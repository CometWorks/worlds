using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace EditProbe;

// Test-only server plugin of the station patch tests (tests/station_patch_cluster.py, SE-1227 and
// SE-1234): reports a station grid's state, and tries the game's own raze. The patches themselves
// go through the Octahedron plugin's !oct patch and its StationPatches config.
//
//   !sedit dump <grid name>             DUMP id blocks=N hash=H ledger=a,b
//   !sedit block <grid name> x,y,z      BLOCK Type/Subtype name enabled owner entity, or NOBLOCK
//   !sedit free <grid name>             FREE x,y,z (up to 8): empty cells outside the hull, next to a block
//   !sedit find <grid name> <subtype or type>   FOUND x,y,z Type/Subtype ..., up to 5
//   !sedit vdel <grid name> x,y,z       the game's RazeBlock, with its safe zone check
//   !sedit storeset <grid name> x,y,z <guid> <value>   sets a mod storage key of a block
//   !sedit give <grid name> x,y,z <Type/Subtype> <amount>   puts items into a block
//
// Lines "?tag<TAB>grid name<TAB>query" in the file EDITPROBE_PENDING are answered by whichever
// process holds the grid, once per tag and grid, as "node<TAB>?tag<TAB>answer" in EDITPROBE_OUT.
// Queries: dump, block:x,y,z, snap:name (the block list to EDITPROBE_OUT.name), islands,
// neighbours:x,y,z, near:meters, prop:x,y,z:PropertyId (a float or bool terminal property),
// text:x,y,z (an LCD's text), storage:x,y,z (the block's mod storage), items:x,y,z:Type/Subtype.
public class Probe : IPlugin
{
    private int ticks;

    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(EditCommands));

    public void Update()
    {
        if (++ticks % 120 != 0 || MySession.Static?.Ready != true)
            return;
        try
        {
            Edits.Answer();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLineAndConsole($"EditProbe queries: {e}");
        }
    }

    public void Dispose() { }
}

[CommandRoot("sedit", "Station edit probe", "Station patch test queries")]
public sealed class EditCommands : CommandModule
{
    [Command("dump", "DUMP id blocks=N hash=H ledger=a,b", "!sedit dump <grid>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Dump(string grid) => Edits.Grid(grid) is { } g ? Edits.Dump(g) : $"NOGRID {grid}";

    [Command("block", "BLOCK Type/Subtype name enabled owner entity", "!sedit block <grid> x,y,z")]
    [Permission(MyPromoteLevel.Admin)]
    public string Block(string grid, string at)
    {
        var found = Edits.Grid(grid);
        if (found == null)
            return $"NOGRID {grid}";
        var slim = found.GetCubeBlock(Edits.Cell(at));
        return slim == null ? $"NOBLOCK {at}" : "BLOCK " + Edits.Describe(slim);
    }

    [Command("free", "FREE x,y,z", "!sedit free <grid>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Free(string grid) => Edits.Grid(grid) is { } g ? Edits.Free(g) : $"NOGRID {grid}";

    [Command(
        "find",
        "FOUND x,y,z Type/Subtype name, up to 5",
        "!sedit find <grid> <subtype or type>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Find(string grid, string what)
    {
        var found = Edits.Grid(grid);
        if (found == null)
            return $"NOGRID {grid}";
        var hits = found
            .GetBlocks()
            .Where(b =>
                b.BlockDefinition.Id.SubtypeName == what
                || b.BlockDefinition.Id.TypeId.ToString() == "MyObjectBuilder_" + what
            )
            .OrderBy(b => b.Min.X)
            .ThenBy(b => b.Min.Y)
            .ThenBy(b => b.Min.Z)
            .Take(5)
            .Select(b => $"FOUND {b.Min.X},{b.Min.Y},{b.Min.Z} {Edits.Describe(b)}");
        return hits.Any() ? string.Join("\n", hits) : "NOTFOUND";
    }

    [Command(
        "storeset",
        "Sets a mod storage key of a block",
        "!sedit storeset <grid> x,y,z <guid> <value>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string StoreSet(string grid, string at, string key, string value)
    {
        var block = Edits.Grid(grid)?.GetCubeBlock(Edits.Cell(at))?.FatBlock;
        if (block == null)
            return $"NOBLOCK {at}";
        block.Storage ??= new MyModStorageComponent();
        block.Storage[Guid.Parse(key)] = value;
        return $"STORESET {at}";
    }

    [Command("give", "Puts items into a block", "!sedit give <grid> x,y,z <Type/Subtype> <amount>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Give(string grid, string at, string item, int amount)
    {
        var block = Edits.Grid(grid)?.GetCubeBlock(Edits.Cell(at))?.FatBlock;
        if (block?.GetInventory() is not { } inventory)
            return $"NOINVENTORY {at}";
        var id = MyDefinitionId.Parse("MyObjectBuilder_" + item);
        inventory.AddItems(
            amount,
            (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(id)
        );
        return $"GIVEN {at} {Edits.Items(block)}";
    }

    [Command("vdel", "The game's RazeBlock of one block", "!sedit vdel <grid> x,y,z")]
    [Permission(MyPromoteLevel.Admin)]
    public string VanillaDelete(string grid, string at)
    {
        var found = Edits.Grid(grid);
        if (found == null)
            return $"NOGRID {grid}";
        found.RazeBlock(Edits.Cell(at));
        return $"VDEL {at}";
    }
}

internal static class Edits
{
    // The Octahedron plugin's ledger (ServerPlugin/StationPatches.cs)
    private static readonly Guid Key = new("C4D21234-6B0E-4F3A-9E57-2E8B1238A0F1");
    private static readonly string Node =
        Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? "server";

    // Underscores stand for spaces, so a name is one word in chat
    public static MyCubeGrid Grid(string name)
    {
        name = name.Replace('_', ' ');
        return MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g =>
                !g.Closed && (g.DisplayName == name || g.EntityId.ToString() == name)
            );
    }

    public static Vector3I Cell(string text)
    {
        var p = text.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return new Vector3I(p[0], p[1], p[2]);
    }

    public static string Describe(MySlimBlock slim)
    {
        var fat = slim.FatBlock as MyTerminalBlock;
        var enabled = slim.FatBlock is MyFunctionalBlock f ? (f.Enabled ? "on" : "off") : "-";
        return $"{slim.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")}/{slim.BlockDefinition.Id.SubtypeName} "
            + $"{(fat?.CustomName?.ToString() ?? "-").Replace(' ', '_')} {enabled} {slim.OwnerId} {slim.FatBlock?.EntityId ?? 0}";
    }

    public static string Dump(MyCubeGrid grid)
    {
        var hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Lines(grid))))
        )[..12];
        var ledger =
            grid.Storage != null && grid.Storage.TryGetValue(Key, out var text) ? text : "";
        return $"DUMP {grid.EntityId} blocks={grid.BlocksCount} hash={hash} ledger={ledger} static={grid.IsStatic} destructible={grid.DestructibleBlocks} immune={grid.Immune}";
    }

    private static List<string> Lines(MyCubeGrid grid) =>
        grid.GetBlocks()
            .Select(b =>
            {
                var d = Describe(b).Split(' ');
                return $"{b.Min}|{b.Orientation}|{d[0]}|{d[1]}|{d[2]}|{d[3]}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    // Cells outside the hull next to a block. Whether a block there mounts is for !oct patch check.
    public static string Free(MyCubeGrid grid)
    {
        var found = new List<string>();
        // Bounded: a 3,350 block shipyard took the game thread long enough to drop the client's chat
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (
            var slim in grid.GetBlocks()
                .OrderBy(b => b.Position.X)
                .ThenBy(b => b.Position.Y)
                .ThenBy(b => b.Position.Z)
        )
        {
            if (clock.ElapsedMilliseconds > 300)
                break;
            foreach (var step in Base6Directions.IntDirections)
            {
                var cell = slim.Position + step;
                // Outside the hull: nothing of the grid from this cell out past the grid's bounds
                bool open = true;
                for (
                    var at = cell;
                    open
                        && Vector3I.Min(at, grid.Min) == grid.Min
                        && Vector3I.Max(at, grid.Max) == grid.Max;
                    at += step
                )
                    open = !grid.CubeExists(at);
                if (!open)
                    continue;
                var world = grid.GridIntegerToWorld(cell);
                var box = new BoundingBoxD(world - 1.5, world + 1.5);
                if (MyEntities.GetEntitiesInAABB(ref box).OfType<MyCubeGrid>().Any(g => g != grid))
                    continue;
                var text = $"FREE {cell.X},{cell.Y},{cell.Z}";
                if (!found.Contains(text))
                    found.Add(text);
                if (found.Count == 8)
                    return string.Join(" ", found);
            }
        }
        return found.Count > 0 ? string.Join(" ", found) : "NOFREE";
    }

    private static string Query(MyCubeGrid grid, string query)
    {
        var a = query.Split(':');
        switch (a[0])
        {
            case "dump":
                return Dump(grid);
            case "snap":
                File.WriteAllLines(
                    Environment.GetEnvironmentVariable("EDITPROBE_OUT") + "." + a[1],
                    Lines(grid)
                );
                return $"SNAP {a[1]} {grid.BlocksCount}";
            case "neighbours":
            {
                var slim = grid.GetCubeBlock(Cell(a[1]));
                return slim == null
                    ? $"NOBLOCK {a[1]}"
                    : $"NEIGHBOURS {a[1]} {slim.Neighbours.Count}";
            }
            case "islands":
                return Islands(grid);
            case "prop":
            {
                if (grid.GetCubeBlock(Cell(a[1]))?.FatBlock is not IMyTerminalBlock block)
                    return $"NOBLOCK {a[1]}";
                var property = block.GetProperty(a[2]);
                return property?.TypeName switch
                {
                    "Single" =>
                        $"PROP {a[2]}={property.AsFloat().GetValue(block).ToString(CultureInfo.InvariantCulture)}",
                    "Boolean" => $"PROP {a[2]}={property.AsBool().GetValue(block)}",
                    _ => $"NOPROP {a[2]}",
                };
            }
            case "text":
                return
                    grid.GetCubeBlock(Cell(a[1]))?.FatBlock
                        is Sandbox.ModAPI.Ingame.IMyTextSurface surface
                    ? "TEXT " + surface.GetText().Replace('\n', '|')
                    : $"NOTEXT {a[1]}";
            case "storage":
            {
                var block = grid.GetCubeBlock(Cell(a[1]))?.FatBlock;
                if (block == null)
                    return $"NOBLOCK {a[1]}";
                var items =
                    block.Storage?.Select(kv =>
                        $"{kv.Key}={kv.Value.Replace(' ', '_').Replace('\n', '_')}"
                    )
                    ?? [];
                return $"STORAGE {a[1]} {string.Join(" ", items.OrderBy(t => t, StringComparer.Ordinal))}";
            }
            case "items":
            {
                var block = grid.GetCubeBlock(Cell(a[1]))?.FatBlock;
                if (block == null)
                    return $"NOBLOCK {a[1]}";
                var amount = Enumerable
                    .Range(0, block.InventoryCount)
                    .Sum(i =>
                        (double)
                            block
                                .GetInventory(i)
                                .GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_" + a[2]))
                    );
                return $"ITEMS {a[1]} {amount.ToString(CultureInfo.InvariantCulture)} entity={block.EntityId}";
            }
            case "near":
            {
                var sphere = new BoundingSphereD(
                    grid.PositionComp.WorldAABB.Center,
                    double.Parse(a[1], CultureInfo.InvariantCulture)
                );
                var near = MyEntities
                    .GetEntitiesInSphere(ref sphere)
                    .OfType<MyCubeGrid>()
                    .Where(g => g != grid)
                    .Select(g =>
                        $"{g.DisplayName.Replace(' ', '_')}/{g.BlocksCount}/{(g.IsStatic ? "static" : "dynamic")}/{g.EntityId}"
                    )
                    .OrderBy(t => t, StringComparer.Ordinal);
                return $"NEAR {string.Join(" ", near)}";
            }
            case "block":
            {
                var slim = grid.GetCubeBlock(Cell(a[1]));
                return slim == null ? $"NOBLOCK {a[1]}" : "BLOCK " + Describe(slim);
            }
        }
        return $"UNKNOWN {query}";
    }

    // The game's own test (MyDisconnectHelper in test mode) next to a flood fill over
    // MySlimBlock.Neighbours: every group but the largest splits off at the next block removal
    private static string Islands(MyCubeGrid grid)
    {
        var left = new HashSet<MySlimBlock>(grid.GetBlocks());
        var groups = new List<List<MySlimBlock>>();
        while (left.Count > 0)
        {
            var group = new List<MySlimBlock>();
            var todo = new Stack<MySlimBlock>();
            var first = left.First();
            left.Remove(first);
            todo.Push(first);
            while (todo.Count > 0)
            {
                var block = todo.Pop();
                group.Add(block);
                foreach (var next in block.Neighbours)
                    if (left.Remove(next))
                        todo.Push(next);
            }
            groups.Add(group);
        }
        var game = new MyDisconnectHelper().Disconnect(
            grid,
            MyCubeGrid.MyTestDisconnectsReason.NoReason,
            null,
            true
        );
        var small = groups
            .OrderByDescending(g => g.Count)
            .Skip(1)
            .Select(g =>
                $"{g.Count}@{g[0].Min.X},{g[0].Min.Y},{g[0].Min.Z}:{g[0].BlockDefinition.Id.SubtypeName}"
            );
        return $"ISLANDS {groups.Count - 1} largest={groups.Max(g => g.Count)} hasislands={game} {string.Join(" ", small)}";
    }

    public static string Items(MyEntity block) =>
        Enumerable
            .Range(0, block.InventoryCount)
            .Sum(i => (double)block.GetInventory(i).CurrentMass)
            .ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly HashSet<string> answered = [];

    public static void Answer()
    {
        var pending = Environment.GetEnvironmentVariable("EDITPROBE_PENDING");
        var output = Environment.GetEnvironmentVariable("EDITPROBE_OUT");
        if (string.IsNullOrEmpty(pending) || output == null || !File.Exists(pending))
            return;
        foreach (var line in File.ReadAllLines(pending))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || !parts[0].StartsWith('?'))
                continue;
            var grid = Grid(parts[1]);
            if (grid != null && answered.Add(parts[0] + "|" + grid.EntityId))
                File.AppendAllText(output, $"{Node}\t{parts[0]}\t{Query(grid, parts[2])}\n");
        }
    }
}
