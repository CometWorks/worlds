using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace LeaderboardProbe;

/// <summary>
/// Test hook of the leaderboard cluster test (tests/leaderboard/leaderboard_cluster.py): runs the chat
/// command lines of the file LBPROBE_PLAN as the server console, owner level, once the session is
/// ready, on the node LBPROBE_NODE (CLUSTER_NODE_ID) only. Lines appended later run as they arrive.
/// Every reply goes to LBPROBE_OUT as "STEP\tTEXT", STEP counting the plan's command lines from 0.
/// The plan needs a host that runs commands for plugins (ServerCommands.Execute, called by reflection
/// so the probe's commands also load on an older Magnetar, as tests/test_leaderboard.py uses them).
///   wait SECONDS     let the game run
///   anything else    a chat command line, for example "!oct leaderboard"
/// </summary>
public class Probe : IPlugin, ICommandResponder
{
    private string planPath;
    private string[] plan = [];
    private long planLength = -1;
    private int line;
    private int step;
    private DateTime resumeAt = DateTime.MaxValue;
    private StreamWriter output;
    private int replying;

    private static readonly MethodInfo Execute = typeof(ServerCommands).GetMethod("Execute");

    private static readonly CommandCaller Console = new(
        0UL,
        0L,
        "LeaderboardProbe",
        MyPromoteLevel.Owner,
        isConsole: true
    );

    public void Init(object gameInstance)
    {
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));
        var path = Environment.GetEnvironmentVariable("LBPROBE_PLAN");
        var outPath = Environment.GetEnvironmentVariable("LBPROBE_OUT");
        var node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
        var only = Environment.GetEnvironmentVariable("LBPROBE_NODE");
        if (path == null || outPath == null || (only != null && node != only) || Execute == null)
            return;
        planPath = path;
        output = new StreamWriter(outPath, append: true) { AutoFlush = true };
    }

    public void Update()
    {
        if (planPath == null || MySession.Static is not { Ready: true })
            return;
        if (line >= plan.Length)
        {
            long length = File.Exists(planPath) ? new FileInfo(planPath).Length : 0;
            if (length == planLength)
                return;
            planLength = length;
            plan = File.ReadAllLines(planPath)
                .Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))
                .ToArray();
            if (line >= plan.Length)
                return;
        }
        if (resumeAt == DateTime.MaxValue)
            resumeAt = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow < resumeAt)
            return;

        string next = plan[line++].Trim();
        if (next.StartsWith("wait "))
        {
            resumeAt = DateTime.UtcNow.AddSeconds(
                double.Parse(next.Substring(5), CultureInfo.InvariantCulture)
            );
            return;
        }
        replying = step++;
        try
        {
            if (!(bool)Execute.Invoke(null, [next, Console, this]))
                Write(replying, "not a command");
        }
        catch (Exception e)
        {
            Write(replying, "error " + (e.InnerException ?? e).Message);
        }
    }

    // A later reply belongs to the command whose step was current when it was sent; the test waits
    // for each answer before the next command
    public void Send(in CommandReply reply, in CommandCaller caller) => Write(replying, reply.Text);

    private void Write(int index, string text) =>
        output.WriteLine(index + "\t" + (text ?? "").Replace("\n", " | "));

    public void Dispose() => output?.Dispose();
}

[CommandRoot("lbprobe", "Leaderboard probe", "Leaderboard test commands")]
public sealed class ProbeCommands : CommandModule
{
    private const string PanelName = "Curved LCD Panel - Station Lounge Leaderboard";

    [Command(
        "faction",
        "Makes a faction of a new identity: FACTION tag faction-id founder-id, or FACTION tag requested",
        "!lbprobe faction <tag>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Faction(string tag)
    {
        var factions = MyAPIGateway.Session.Factions;
        if (factions.TryGetFactionByTag(tag) is { } had)
            return $"FACTION {tag} {had.FactionId} {had.FounderId}";
        long founder = MySession.Static.Players.CreateNewIdentity(tag + " Founder").IdentityId;
        factions.CreateFaction(
            founder,
            tag,
            tag + " Leaderboard Testers",
            "",
            "",
            MyFactionTypes.None
        );
        var faction = factions.TryGetFactionByTag(tag);
        // On a cluster node the World Authority makes it, a moment later
        return faction == null
            ? $"FACTION {tag} requested"
            : $"FACTION {tag} {faction.FactionId} {founder}";
    }

    [Command("pay", "Pays into the faction's account: PAID tag SC", "!lbprobe pay <tag> <SC>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Pay(string tag, long amount)
    {
        var faction = MySession.Static.Factions.TryGetFactionByTag(tag);
        return faction != null && MyBankingSystem.ChangeBalance(faction.FactionId, amount)
            ? $"PAID {tag} {amount}"
            : $"NOPAY {tag}";
    }

    [Command(
        "ship",
        "Builds a ship of the faction's founder off the station, a connector and a small cargo container with the items: SHIP grid-id",
        "!lbprobe ship <tag> <station> [Type/Subtype=amount,...] [meters to the station's right, 200]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Ship(string tag, string station, string items = "", double offset = 200)
    {
        var faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tag);
        if (faction == null)
            return "NOFACTION";
        if (StationGrid(station) is not { } grid)
            return "NOSTATION";
        long owner = faction.FounderId;
        var position = grid.PositionComp.WorldAABB.Center + grid.WorldMatrix.Right * offset;
        var ship = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = tag + " Probe Ship",
            PositionAndOrientation = new MyPositionAndOrientation(
                position,
                Vector3.Forward,
                Vector3.Up
            ),
        };
        ship.CubeBlocks.Add(
            Block<MyObjectBuilder_ShipConnector>("Connector", Vector3I.Zero, owner)
        );
        ship.CubeBlocks.Add(
            Block<MyObjectBuilder_CargoContainer>(
                "LargeBlockSmallContainer",
                new Vector3I(0, 0, 1),
                owner
            )
        );
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(ship);
        var cargo = entity.GetFatBlocks<MyCargoContainer>().First();
        foreach (var pair in items.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = pair.Split('=');
            if (!MyDefinitionId.TryParse("MyObjectBuilder_" + f[0], out var id))
                return $"BADITEM {f[0]}";
            cargo
                .GetInventory()
                .AddItems(
                    (MyFixedPoint)decimal.Parse(f[1], CultureInfo.InvariantCulture),
                    MyObjectBuilderSerializer.CreateNewObject(id)
                );
        }
        return $"SHIP {entity.EntityId}";
    }

    [Command(
        "grids",
        "The grids whose biggest owner is in the faction: GRID id name blocks x y z",
        "!lbprobe grids <tag>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Grids(string tag)
    {
        var lines = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g =>
                g.BigOwners.Count > 0
                && MySession.Static.Factions.GetPlayerFaction(g.BigOwners[0])?.Tag == tag
            )
            .Select(g =>
                $"GRID {g.EntityId} {g.DisplayName} {g.BlocksCount} {V(g.PositionComp.GetPosition())}"
            )
            .ToList();
        return lines.Count == 0 ? "GRIDS none" : string.Join("\n", lines);
    }

    [Command(
        "panel",
        "The text of the station's first leaderboard panel: PANEL count content font AT x y z FORWARD x y z | text",
        "!lbprobe panel <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Panel(string station)
    {
        if (StationGrid(station) is not { } grid)
            return "NOSTATION";
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        var panels = (group == null ? [grid] : group.Nodes.Select(n => n.NodeData))
            .SelectMany(g => g.GetFatBlocks<MyTextPanel>())
            .Where(p => p.CustomName?.ToString() == PanelName)
            .ToList();
        if (panels.Count == 0)
            return "PANEL 0";
        // The text the surface shows, as the server sent it to the clients
        var surface = panels[0].PanelComponent;
        var m = panels[0].WorldMatrix;
        return $"PANEL {panels.Count} {surface.ContentType} {surface.Font} AT {V(m.Translation)} FORWARD {V(m.Forward)}\n{surface.Text}";
    }

    private static MyCubeGrid StationGrid(string station)
    {
        foreach (var faction in MySession.Static.Factions)
        foreach (var entry in faction.Value.Stations)
            if (
                entry.Name != null
                && entry.Name.StartsWith(station, StringComparison.OrdinalIgnoreCase)
                && MyEntities.TryGetEntityById(entry.StationEntityId, out MyCubeGrid grid)
            )
                return grid;
        return null;
    }

    private static string V(Vector3D v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", v.X, v.Y, v.Z);

    private static T Block<T>(string subtype, Vector3I min, long owner)
        where T : MyObjectBuilder_TerminalBlock, new() =>
        new()
        {
            SubtypeName = subtype,
            Min = min,
            BlockOrientation = new SerializableBlockOrientation(
                Base6Directions.Direction.Forward,
                Base6Directions.Direction.Up
            ),
            Owner = owner,
            BuiltBy = owner,
            ShareMode = MyOwnershipShareModeEnum.None,
        };
}
