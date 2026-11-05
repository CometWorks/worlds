using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ServerPlugin;

// The game's client damages a blueprint with subgrids on its way to a hologram table: it remaps
// each grid's entity ids on its own, so a rotor, piston or hinge base points at no block of its
// head's grid, and the server can't tell where the subgrids belong. Multigrid Projector on the
// client remaps them together. The shipyard refuses such a blueprint and asks the player for the
// blueprint's Steam Workshop link (!oct workshop), which this server downloads with SteamCMD and
// loads into the table itself, or for the plugin. Players without Pulsar can order ships with subgrids so.
internal static partial class ShipyardOrders
{
    private static readonly Regex WorkshopIdPattern = new(@"(?:\bid=|^)(\d{6,20})\b");

    // Players whose Workshop download is running
    private static readonly HashSet<long> downloading = new();

    internal const string DamagedHint =
        "Paste its Steam Workshop link into chat as !oct workshop <link>, "
        + "or load it again with the Multigrid Projector plugin of Pulsar.";

    /// <summary>A blueprint with subgrids whose mechanical connections point at no block of it.</summary>
    internal static bool Damaged(List<MyObjectBuilder_CubeGrid> blueprint)
    {
        if (blueprint == null || blueprint.Count < 2)
            return false;
        var ids = new HashSet<long>(
            blueprint.SelectMany(g => g.CubeBlocks).Select(b => b.EntityId)
        );
        // Multigrid Projector before 0.9.10 sent every block with EntityId 0: no ids to check
        ids.Remove(0);
        return ids.Count > 0
            && blueprint
                .SelectMany(g => g.CubeBlocks)
                .OfType<MyObjectBuilder_MechanicalConnectionBlock>()
                .Any(b => b.TopBlockId is long top && top != 0 && !ids.Contains(top));
    }

    // !oct workshop: the Workshop blueprint into the nearest table, as if the player loaded it
    public static string Workshop(long identityId, string link, string tableName)
    {
        var match = WorkshopIdPattern.Match(link?.Trim() ?? "");
        if (!match.Success)
            return "Give the blueprint's Steam Workshop link or id: !oct workshop <link>";
        var id = ulong.Parse(match.Groups[1].Value);
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return "Spawn first.";
        var table = StarterShip.FindTable(character, tableName, Config);
        if (table == null || StationOf(table.CubeGrid) == null)
            return "Stand by a station's hologram table.";
        if (Holder(table) is var holder && holder != 0 && holder != identityId)
            return $"{Name(holder)} holds this hologram table.";
        if (!downloading.Add(identityId))
            return "Your last Workshop blueprint is still downloading.";
        var steamId = MySession.Static.Players.TryGetSteamId(identityId);
        Task.Run(() =>
        {
            string error;
            MyObjectBuilder_ShipBlueprintDefinition blueprint = null;
            try
            {
                blueprint = Download(id, out error);
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Workshop blueprint {id}", e);
                error = "The Workshop download failed.";
            }
            MySandboxGame.Static.Invoke(
                () =>
                {
                    downloading.Remove(identityId);
                    Tell(identityId, error ?? Load(identityId, steamId, table, blueprint));
                },
                "Octahedron Workshop blueprint"
            );
        });
        return $"The shipyard downloads Workshop item {id} and answers in chat.";
    }

    private const string WorkshopAppId = "244850";
    private static readonly object steamCmdLock = new();

    // Blocks the calling thread while SteamCMD downloads the item anonymously. The cluster's nodes
    // have no Steam client or game server of their own, so the game's Workshop calls fail there.
    private static MyObjectBuilder_ShipBlueprintDefinition Download(ulong id, out string error)
    {
        var root = Path.Combine(MyFileSystem.UserDataPath, "Octahedron", "Workshop");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var steamCmd = SteamCmd();
        var start = new ProcessStartInfo(steamCmd)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var arg in new[]
            {
                "+force_install_dir",
                root,
                "+login",
                "anonymous",
                "+workshop_download_item",
                WorkshopAppId,
                id.ToString(),
                "+quit",
            }
        )
            start.ArgumentList.Add(arg);
        // SteamCMD keeps its login and cache in HOME: ours, not that of the user running Quasar
        start.Environment["HOME"] = home;
        string output;
        // One SteamCMD at a time in that HOME
        lock (steamCmdLock)
        {
            using var process = Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!stdout.Wait(TimeSpan.FromMinutes(5)))
            {
                process.Kill();
                error = "The Workshop download took too long.";
                return null;
            }
            output = stdout.Result + stderr.Result;
        }
        // SteamCMD puts the item into the install folder, or into its own or HOME's Steam folder
        var folder = new[]
        {
            root,
            Path.Combine(home, ".steam", "steamcmd"),
            Path.GetDirectoryName(steamCmd),
        }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r =>
                Path.Combine(r, "steamapps", "workshop", "content", WorkshopAppId, id.ToString())
            )
            .FirstOrDefault(Directory.Exists);
        if (!output.Contains("Success. Downloaded item") || folder == null)
        {
            var why = output.Split('\n').LastOrDefault(l => l.Contains("ERROR"))?.Trim();
            Plugin.Log.Warning($"Workshop blueprint {id}: {steamCmd} failed: {why ?? output}");
            error = $"The Workshop has no public item {id}" + (why == null ? "." : $": {why}");
            return null;
        }
        var blueprint = ReadBlueprint(folder)?.ShipBlueprints?.FirstOrDefault();
        if (blueprint?.CubeGrids == null || blueprint.CubeGrids.Length == 0)
        {
            error = $"Workshop item {id} is not a ship blueprint.";
            return null;
        }
        Plugin.Log.Info(
            $"Workshop blueprint {id} '{blueprint.CubeGrids[0].DisplayName}' downloaded: {blueprint.CubeGrids.Length} grids"
        );
        error = null;
        return blueprint;
    }

    private static string SteamCmd()
    {
        if (!string.IsNullOrWhiteSpace(Config.ShipyardSteamCmd))
            return Config.ShipyardSteamCmd;
        var name =
            Environment.OSVersion.Platform == PlatformID.Win32NT ? "steamcmd.exe" : "steamcmd.sh";
        for (var dir = new DirectoryInfo(MyFileSystem.UserDataPath); dir != null; dir = dir.Parent)
        {
            var quasar = Path.Combine(dir.FullName, "ManagedRuntime", "Tools", "SteamCMD", name);
            if (File.Exists(quasar))
                return quasar;
        }
        return "steamcmd";
    }

    // bp.sbc in the item's folder, or in the zip of an item from before the Workshop's UGC
    private static MyObjectBuilder_Definitions ReadBlueprint(string folder)
    {
        MyObjectBuilder_Definitions definitions = null;
        var file = Path.Combine(folder, "bp.sbc");
        if (File.Exists(file))
        {
            MyObjectBuilderSerializerKeen.DeserializeXML(file, out definitions);
            return definitions;
        }
        var archive = Directory
            .GetFiles(folder)
            .FirstOrDefault(f => f.EndsWith(".bin") || f.EndsWith(".sbb"));
        if (archive == null)
            return null;
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "bp.sbc", StringComparison.OrdinalIgnoreCase)
        );
        if (entry == null)
            return null;
        using var stream = entry.Open();
        MyObjectBuilderSerializerKeen.DeserializeXML(stream, out definitions);
        return definitions;
    }

    private static string Load(
        long identityId,
        ulong steamId,
        MyProjectorBase table,
        MyObjectBuilder_ShipBlueprintDefinition blueprint
    )
    {
        if (table.MarkedForClose || !table.IsWorking)
            return "The hologram table is gone or off.";
        if (Holder(table) is var holder && holder != 0 && holder != identityId)
            return $"{Name(holder)} took the hologram table meanwhile.";
        var grids = blueprint.CubeGrids.ToList();
        // What the game does to a client's blueprint on the server
        if (!MySession.Static.IsUserScripter(steamId))
            foreach (var block in grids.SelectMany(g => g.CubeBlocks))
                if (block is MyObjectBuilder_MyProgrammableBlock pb)
                    pb.Program = null;
        // As Multigrid Projector sends it: one id map for all grids, the main grid at the origin
        MyEntities.RemapObjectBuilderCollection(grids);
        if (grids[0].PositionAndOrientation is { } main)
        {
            var origin = MatrixD.Invert(main.GetMatrix());
            foreach (var grid in grids.Where(g => g.PositionAndOrientation.HasValue))
                grid.PositionAndOrientation = new MyPositionAndOrientation(
                    grid.PositionAndOrientation.Value.GetMatrix() * origin
                );
        }
        SendNewBlueprint(table, grids);
        loaded[table.EntityId] = identityId;
        loadedAt[table.EntityId] = DateTime.UtcNow;
        Hold(table, identityId);
        Plugin.Log.Info(
            $"Hologram table {table.EntityId}: Workshop blueprint '{grids[0].DisplayName}' loaded for {Name(identityId)}"
        );
        return $"'{grids[0].DisplayName}' is on the hologram table: read the quote, then press the button.";
    }

    // Sets the blueprint on the server and sends it to the clients, as a client's load does
    private static readonly Action<
        MyProjectorBase,
        List<MyObjectBuilder_CubeGrid>
    > SendNewBlueprint = AccessTools.MethodDelegate<
        Action<MyProjectorBase, List<MyObjectBuilder_CubeGrid>>
    >(AccessTools.Method(typeof(MyProjectorBase), "SendNewBlueprint"));
}
