using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(7);
    private static DateTime nextPurge;

    // The blueprint of the item's current version from the cache shared by the servers on this
    // host, else downloaded and put there. A hidden item has no public version: it is downloaded
    // every time. Blocks the calling thread.
    private static MyObjectBuilder_ShipBlueprintDefinition Download(ulong id, out string error)
    {
        var version = WorkshopVersion(id);
        var entry = version == null ? null : Path.Combine(CacheFolder(), $"{id}-{version}");
        byte[] xml;
        string how;
        if (entry != null && File.Exists(Path.Combine(entry, "bp.sbc")))
        {
            xml = File.ReadAllBytes(Path.Combine(entry, "bp.sbc"));
            // Kept CacheRetention from the last use
            Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
            how = "from the cache";
        }
        else
        {
            xml = SteamCmdDownload(id, out error);
            if (xml == null)
                return null;
            if (entry != null)
                Store(entry, xml);
            how = "downloaded with SteamCMD";
        }
        MyObjectBuilderSerializerKeen.DeserializeXML(
            new MemoryStream(xml),
            out MyObjectBuilder_Definitions definitions
        );
        var blueprint = definitions?.ShipBlueprints?.FirstOrDefault();
        if (blueprint?.CubeGrids == null || blueprint.CubeGrids.Length == 0)
        {
            error = $"Workshop item {id} is not a ship blueprint.";
            return null;
        }
        Plugin.Log.Info(
            $"Workshop blueprint {id} version {version ?? "unknown"} '{blueprint.CubeGrids[0].DisplayName}' {how}: "
                + $"{blueprint.CubeGrids.Length} grids"
        );
        error = null;
        return blueprint;
    }

    // Another server on this host may store the same version meanwhile: a whole folder or none
    private static void Store(string entry, byte[] xml)
    {
        var staging = Path.Combine(Path.GetDirectoryName(entry), $".{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "bp.sbc"), xml);
        try
        {
            Directory.Move(staging, entry);
        }
        catch (IOException)
        {
            Directory.Delete(staging, true);
        }
    }

    // The item's time_updated from the public Steam Web API, null for a hidden item or no answer
    private static string WorkshopVersion(ulong id)
    {
        try
        {
            var answer = http.PostAsync(
                    "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                    new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["itemcount"] = "1",
                            ["publishedfileids[0]"] = id.ToString(),
                        }
                    )
                )
                .Result.Content.ReadAsStringAsync()
                .Result;
            return Regex.Match(answer, @"""time_updated"":(\d+)") is { Success: true } m
                ? m.Groups[1].Value
                : null;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(
                $"Workshop blueprint {id}: no version from the Steam Web API: {e.Message}"
            );
            return null;
        }
    }

    // bp.sbc of the item, downloaded anonymously. The cluster's nodes have no Steam client or game
    // server of their own, so the game's Workshop calls fail there.
    private static byte[] SteamCmdDownload(ulong id, out string error)
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
        var workshop = new[]
        {
            root,
            Path.Combine(home, ".steam", "steamcmd"),
            Path.GetDirectoryName(steamCmd),
        }
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => Path.Combine(r, "steamapps", "workshop"))
            .FirstOrDefault(w =>
                Directory.Exists(Path.Combine(w, "content", WorkshopAppId, id.ToString()))
            );
        var xml =
            workshop == null
                ? null
                : BlueprintXml(Path.Combine(workshop, "content", WorkshopAppId, id.ToString()));
        if (!output.Contains("Success. Downloaded item") || xml == null)
        {
            var why = output.Split('\n').LastOrDefault(l => l.Contains("ERROR"))?.Trim();
            Plugin.Log.Warning($"Workshop blueprint {id}: {steamCmd} failed: {why ?? output}");
            error =
                xml == null && workshop != null
                    ? $"Workshop item {id} is not a ship blueprint."
                    : $"The Workshop has no public item {id}" + (why == null ? "." : $": {why}");
            return null;
        }
        error = null;
        return xml;
    }

    private static string SteamCmd() =>
        !string.IsNullOrWhiteSpace(Config.ShipyardSteamCmd) ? Config.ShipyardSteamCmd
        : QuasarTools() is { } tools
            ? Path.Combine(
                tools,
                "SteamCMD",
                Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? "steamcmd.exe"
                    : "steamcmd.sh"
            )
        : "steamcmd";

    private static string CacheFolder() =>
        !string.IsNullOrWhiteSpace(Config.ShipyardWorkshopCache) ? Config.ShipyardWorkshopCache
        : QuasarTools() is { } tools ? Path.Combine(tools, "OctahedronBlueprints")
        : Path.Combine(MyFileSystem.UserDataPath, "Octahedron", "Blueprints");

    // Quasar's ManagedRuntime/Tools above this server's data folder, shared by its servers
    private static string QuasarTools()
    {
        for (var dir = new DirectoryInfo(MyFileSystem.UserDataPath); dir != null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "ManagedRuntime", "Tools");
            if (Directory.Exists(Path.Combine(tools, "SteamCMD")))
                return tools;
        }
        return null;
    }

    // Once a day: cached blueprints unused for CacheRetention go
    internal static void PurgeCache()
    {
        if (DateTime.UtcNow < nextPurge)
            return;
        nextPurge = DateTime.UtcNow.AddDays(1);
        var cache = CacheFolder();
        Task.Run(() =>
        {
            if (!Directory.Exists(cache))
                return;
            foreach (var entry in Directory.GetDirectories(cache))
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(entry) > CacheRetention)
                    {
                        Directory.Delete(entry, true);
                        Plugin.Log.Info(
                            $"Workshop blueprint cache: {Path.GetFileName(entry)} removed"
                        );
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Another server on this host removed it first
                }
        });
    }

    // bp.sbc in the item's folder, or in the zip of an item from before the Workshop's UGC
    private static byte[] BlueprintXml(string folder)
    {
        var file = Path.Combine(folder, "bp.sbc");
        if (File.Exists(file))
            return File.ReadAllBytes(file);
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
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
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
