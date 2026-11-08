using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ServerPlugin;

/// <summary>
/// Small changes to a station or shipyard grid of a kept world, made in place by the process that
/// simulates it (SE-1234, policy SE-1238): a block setting, a removed, added or swapped block. The
/// whole patch is checked first (StationPatchPlan.cs), then applied through the game's raze and
/// build success paths the shipyard uses: the station's safe zone refuses the game's own RazeBlock.
/// Each applied patch id goes into the grid's ledger, a storage key saved with the grid, so a patch
/// applies once wherever the grid goes. An admin applies one now with !oct patch apply; the patches
/// in StationPatches of the config are applied by whichever process loads their grid.
/// </summary>
internal static class StationPatches
{
    public static readonly Guid LedgerKey = new("C4D21234-6B0E-4F3A-9E57-2E8B1238A0F1");
    private const int StagedCheckSeconds = 5;
    private const int RetrySeconds = 60;

    private static bool claimed;
    private static DateTime nextCheck;
    private static OctahedronConfig parsedFrom;
    private static List<StationPatch> staged = [];

    // A staged patch refused on a grid: when to try again, and the reason last logged
    private static readonly Dictionary<(string, long), (DateTime Next, string Why)> refused = new();

    public static string Apply(string id, string gridName, string ops, bool apply)
    {
        var patch = PatchFormat.Parse(id, gridName, ops, out var error);
        if (patch == null)
            return $"REFUSED {id}: {error}";
        var grid = Find(patch.Grid, out error);
        return grid == null ? $"NOGRID {patch.Grid}: {error}" : Run(grid, patch, apply);
    }

    public static string Status(string gridName)
    {
        var grid = Find(gridName, out var error);
        if (grid == null)
            return $"NOGRID {gridName}: {error}";
        var model = new GridModel(grid);
        var groups = PatchPlan.Groups(model.Blocks(), model.Neighbours);
        var islands = groups.Count > 1 ? " " + PatchPlan.Describe(groups) : "";
        return $"STATUS {grid.DisplayName} {grid.EntityId} blocks={grid.BlocksCount} hash={Hash(Lines(grid))} "
            + $"ledger={string.Join(",", Ledger(grid))} islands={groups.Count - 1}{islands}";
    }

    // The staged patches: each process applies those its grids' ledgers lack, in the list's order.
    // A refused one holds back the later ones of its grid, and is tried again a minute later.
    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextCheck || MySession.Static?.Ready != true)
            return;
        nextCheck = now.AddSeconds(StagedCheckSeconds);
        Claim();
        if (parsedFrom != Plugin.Config)
            Parse(Plugin.Config);
        if (staged.Count == 0)
            return;

        var names = staged.Select(p => p.Grid).ToHashSet();
        var grids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.Closed && names.Contains(g.DisplayName) && Patchable(g))
            .Where(Plugin.Simulated)
            .ToList();
        foreach (var grid in grids)
        {
            var ledger = Ledger(grid);
            foreach (var patch in staged.Where(p => p.Grid == grid.DisplayName))
            {
                if (ledger.Contains(patch.Id))
                    continue;
                var key = (patch.Id, grid.EntityId);
                if (refused.TryGetValue(key, out var last) && now < last.Next)
                    break;
                string result;
                try
                {
                    result = Run(grid, patch, true, staged: true);
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"Station patch {patch.Id} on {grid.DisplayName} failed", e);
                    result = $"FAILED {patch.Id} on {grid.DisplayName}: {e.Message}";
                }
                if (result.StartsWith("APPLIED"))
                {
                    refused.Remove(key);
                    continue;
                }
                if (last.Why != result)
                    Plugin.Log.Warning($"Station patch staged: {result}");
                refused[key] = (now.AddSeconds(RetrySeconds), result);
                break;
            }
        }
    }

    private static void Parse(OctahedronConfig config)
    {
        parsedFrom = config;
        staged = [];
        refused.Clear();
        foreach (var entry in config.StationPatches ?? [])
        {
            var patch = PatchFormat.Parse(entry.Id, entry.Grid, entry.Ops, out var error);
            if (patch == null)
                Plugin.Log.Error($"Station patch '{entry.Id}' in the config is left out: {error}");
            else
                staged.Add(patch);
        }
    }

    private static void Claim()
    {
        if (claimed)
            return;
        claimed = true;
        EncounterData.ClaimStorage("OctahedronStationPatches", LedgerKey);
    }

    /// <summary>A station or shipyard grid of this name that this process simulates.</summary>
    private static MyCubeGrid Find(string name, out string error)
    {
        error = null;
        var found = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.Closed && g.DisplayName == name && Patchable(g))
            .ToList();
        if (found.Count > 1)
            error = $"{found.Count} station or shipyard grids have that name";
        else if (found.Count == 0)
            error = "no station or shipyard grid of that name on this process";
        else if (!Plugin.Simulated(found[0]))
            error = "another node simulates it";
        return error == null ? found[0] : null;
    }

    private static bool Patchable(MyCubeGrid grid) =>
        Stations.StationOf(grid) != null || ShipyardZoneWelding.IsShipyard(grid);

    private static string Run(MyCubeGrid grid, StationPatch patch, bool apply, bool staged = false)
    {
        var where = $"{patch.Id} on {grid.DisplayName}";
        if (Ledger(grid).Contains(patch.Id))
            return $"SKIP {where}: the grid's ledger has it";
        var steps = PatchPlan.Check(patch, new GridModel(grid), out var error);
        if (steps == null)
            return $"REFUSED {where}: {error}";
        if (!apply)
            return $"CHECKED {where}: {steps.Count} ops would apply";

        Claim();
        var before = Lines(grid);
        var done = 0;
        foreach (var step in steps)
        {
            var failed = Do(grid, step);
            if (failed != null)
            {
                // The checks passed, so the game refused something they don't know about
                var text =
                    $"FAILED {where} at op '{step.Op.Text}' after {done} of {steps.Count}: {failed}. "
                    + "Not in the ledger; the ops before it stay applied.";
                Plugin.Log.Error("Station patch " + text);
                return text;
            }
            done++;
        }
        grid.Storage ??= new MyModStorageComponent();
        grid.Storage[LedgerKey] = string.Join(",", Ledger(grid).Append(patch.Id));

        var after = Lines(grid);
        var gone = before.Except(after).ToList();
        var added = after.Except(before).ToList();
        var head =
            $"APPLIED {where}{(staged ? " (staged)" : "")}: {steps.Count} ops, blocks {before.Count} -> {after.Count}, "
            + $"hash {Hash(before)} -> {Hash(after)}";
        var diff = gone.Take(20).Select(l => "- " + l).Concat(added.Take(20).Select(l => "+ " + l));
        Plugin.Log.Info($"Station patch {head}; {string.Join("; ", diff)}");
        return string.Join("\n", diff.Prepend(head));
    }

    private static string Do(MyCubeGrid grid, PatchStep step)
    {
        var old = step.Old?.Game as MySlimBlock;
        var op = step.Op;
        if (op.Kind == PatchOpKind.Set)
        {
            var block = (MyTerminalBlock)old.FatBlock;
            if (op.Key == "enabled")
                ((MyFunctionalBlock)block).Enabled = op.Value == "1";
            else if (op.Key == "name")
                block.SetCustomName(op.Value);
            else
                block.CustomData = op.Value;
            return null;
        }
        if (old != null)
        {
            ShipyardOrders.Raze(grid, [old.Min]);
            if (grid.GetCubeBlock(old.Min) != null)
                return $"{step.Old} is still there";
        }
        if (step.New?.Game is not NewBlock made)
            return null;
        var builder = (MyObjectBuilder_CubeBlock)made.Builder.Clone();
        builder.EntityId = VRage.MyEntityIdentifier.AllocateId();
        made.Orientation.GetQuaternion(out var rotation);
        var location = new MyCubeGrid.MyBlockLocation(
            made.Definition.Id,
            builder.Min,
            made.Max,
            made.Position,
            rotation,
            builder.EntityId,
            builder.Owner
        );
        var visuals = new MyCubeGrid.MyBlockVisuals(
            ((Vector3)builder.ColorMaskHSV).PackHSVToUint(),
            MyStringHash.GetOrCompute(builder.SkinSubtypeId)
        );
        ShipyardArm.Build(grid, location, visuals, builder, builder.Owner);
        var placed = grid.GetCubeBlock(builder.Min);
        return placed?.BlockDefinition.Id == made.Definition.Id
            ? null
            : $"placing {step.New} failed";
    }

    public static List<string> Ledger(MyCubeGrid grid) =>
        grid.Storage != null
        && grid.Storage.TryGetValue(LedgerKey, out var text)
        && !string.IsNullOrEmpty(text)
            ? text.Split(',').ToList()
            : [];

    // The grid's blocks, one line each, sorted: what a patch's diff and the hash compare
    private static List<string> Lines(MyCubeGrid grid) =>
        grid.GetBlocks()
            .Select(b =>
            {
                var name = (b.FatBlock as MyTerminalBlock)?.CustomName?.ToString();
                var on = b.FatBlock is MyFunctionalBlock f ? (f.Enabled ? " on" : " off") : "";
                return $"{b.Min.X},{b.Min.Y},{b.Min.Z} {TypeName(b.BlockDefinition.Id)} {b.Orientation.Forward}/{b.Orientation.Up}"
                    + $"{(name != null ? $" '{name}'" : "")}{on} owner={b.OwnerId}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static string Hash(List<string> lines) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..12];

    private static string TypeName(MyDefinitionId id) =>
        id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + id.SubtypeName;

    private static Cell C(Vector3I v) => new(v.X, v.Y, v.Z);

    private static Vector3I V(Cell c) => new(c.X, c.Y, c.Z);

    /// <summary>What the game needs to build a block a patch adds.</summary>
    private sealed class NewBlock
    {
        public MyCubeBlockDefinition Definition;
        public MyBlockOrientation Orientation;
        public MyObjectBuilder_CubeBlock Builder;
        public Vector3I Max;
        public Vector3I Position;
    }

    /// <summary>A grid as the patch checks see it.</summary>
    private sealed class GridModel(MyCubeGrid grid) : IPatchGrid
    {
        private readonly Dictionary<MySlimBlock, PatchBlock> known = new();

        private PatchBlock Wrap(MySlimBlock slim)
        {
            if (slim == null)
                return null;
            if (known.TryGetValue(slim, out var block))
                return block;
            return known[slim] = new PatchBlock
            {
                Min = C(slim.Min),
                Cells = Cells(slim.Min, slim.Max),
                Type = TypeName(slim.BlockDefinition.Id),
                Terminal = slim.FatBlock is MyTerminalBlock,
                Functional = slim.FatBlock is MyFunctionalBlock,
                Game = slim,
            };
        }

        private static List<Cell> Cells(Vector3I min, Vector3I max)
        {
            var cells = new List<Cell>();
            for (var it = new Vector3I_RangeIterator(ref min, ref max); it.IsValid(); it.MoveNext())
                cells.Add(C(it.Current));
            return cells;
        }

        public PatchBlock BlockAt(Cell cell) => Wrap(grid.GetCubeBlock(V(cell)));

        public IEnumerable<PatchBlock> Blocks() => grid.GetBlocks().Select(Wrap);

        public IEnumerable<PatchBlock> Neighbours(PatchBlock block) =>
            ((MySlimBlock)block.Game).Neighbours.Select(Wrap);

        public PatchBlock NewBlock(PatchOp op, PatchBlock replaced, out string error)
        {
            error = null;
            if (
                !MyDefinitionId.TryParse("MyObjectBuilder_" + op.Block, out var id)
                || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out var definition)
            )
            {
                error = $"no block definition {op.Block}";
                return null;
            }
            if (definition.CubeSize != grid.GridSizeEnum)
            {
                error = $"{op.Block} is a {definition.CubeSize} grid block";
                return null;
            }
            var old = replaced?.Game as MySlimBlock;
            var orientation =
                old?.Orientation
                ?? new MyBlockOrientation(
                    Enum.Parse<Base6Directions.Direction>(op.Forward),
                    Enum.Parse<Base6Directions.Direction>(op.Up)
                );
            var min = old?.Min ?? V(op.At);
            var turn = new MatrixI(orientation);
            var max = min + Vector3I.Abs(Vector3I.TransformNormal(definition.Size - 1, ref turn));
            var owner =
                old != null && old.OwnerId != 0 ? old.OwnerId : grid.BigOwners.FirstOrDefault();
            var builder = (MyObjectBuilder_CubeBlock)
                MyObjectBuilderSerializer.CreateNewObject(definition.Id);
            builder.SubtypeName = definition.Id.SubtypeName;
            builder.Min = min;
            builder.BlockOrientation = orientation;
            builder.Owner = builder.BuiltBy = owner;
            builder.ShareMode = old?.FatBlock?.IDModule?.ShareMode ?? Share(owner);
            builder.IntegrityPercent = builder.BuildPercent = 1f;
            builder.ColorMaskHSV = (old ?? Beside(min) ?? grid.GetBlocks().First()).ColorMaskHSV;
            builder.SkinSubtypeId = (old?.SkinSubtypeId ?? MyStringHash.NullOrEmpty).String;
            var name =
                old != null ? (old.FatBlock as MyTerminalBlock)?.CustomName?.ToString() : op.Name;
            if (name != null && builder is MyObjectBuilder_TerminalBlock terminal)
                terminal.CustomName = name;
            return new PatchBlock
            {
                Min = C(min),
                Cells = Cells(min, max),
                Type = TypeName(definition.Id),
                Game = new NewBlock
                {
                    Definition = definition,
                    Orientation = orientation,
                    Builder = builder,
                    Max = max,
                    Position = MySlimBlock.ComputePositionInGrid(turn, definition, min),
                },
            };
        }

        // An added block takes the color of a block next to it
        private MySlimBlock Beside(Vector3I cell) =>
            Base6Directions
                .IntDirections.Select(d => grid.GetCubeBlock(cell + d))
                .FirstOrDefault(b => b != null);

        // An added block is shared like the owner's other blocks of the grid
        private MyOwnershipShareModeEnum Share(long owner) =>
            grid.GetFatBlocks()
                .Select(b => b.IDModule)
                .FirstOrDefault(m => m != null && m.Owner == owner)
                ?.ShareMode
            ?? MyOwnershipShareModeEnum.None;

        public bool Mounts(PatchBlock added, PatchBlock other)
        {
            var made = (NewBlock)added.Game;
            made.Orientation.GetQuaternion(out var rotation);
            var position = made.Position;
            var test = new OneBlock(Connectivity(other), other.Cells.Select(V).ToHashSet());
            return MyCubeGrid.CheckConnectivity(
                test,
                made.Definition,
                made.Definition.GetBuildProgressModelMountPoints(1f),
                ref rotation,
                ref position
            );
        }

        private static ConnectivityResult Connectivity(PatchBlock block) =>
            block.Game is MySlimBlock slim
                ? new ConnectivityResult
                {
                    Definition = slim.BlockDefinition,
                    FatBlock = slim.FatBlock,
                    Orientation = slim.Orientation,
                    Position = slim.Position,
                }
                : new ConnectivityResult
                {
                    Definition = ((NewBlock)block.Game).Definition,
                    Orientation = ((NewBlock)block.Game).Orientation,
                    Position = ((NewBlock)block.Game).Position,
                };

        public string Veto(PatchOp op, PatchBlock block)
        {
            var slim = block.Game as MySlimBlock;
            if (
                op.Kind is PatchOpKind.Del or PatchOpKind.Swap
                && slim?.FatBlock is MyShipConnector { Other: not null }
            )
                return "a ship is docked to the connector";
            if (
                ShipyardZoneWelding.IsShipyard(grid)
                && ShipyardOrders.Busy(grid.EntityId)
                && Machinery(slim?.BlockDefinition ?? ((NewBlock)block.Game).Definition)
            )
                return "the shipyard has an order, and the patch changes its welders, containers, connectors or arm";
            return null;
        }

        private static bool Machinery(MyCubeBlockDefinition definition) =>
            definition.Id.TypeId == typeof(MyObjectBuilder_CargoContainer)
            || definition.Id.TypeId == typeof(MyObjectBuilder_ShipConnector)
            || definition.Id.TypeId == typeof(MyObjectBuilder_MotorStator)
            || definition.Id.TypeId == typeof(MyObjectBuilder_MotorAdvancedStator)
            || ShipyardOrders.WelderSubtypes.Contains(definition.Id.SubtypeName);
    }

    /// <summary>One block as the game's connectivity test sees a grid: the mounts of a new block with it alone.</summary>
    private sealed class OneBlock(ConnectivityResult block, HashSet<Vector3I> cells)
        : IMyGridConnectivityTest
    {
        public void GetConnectedBlocks(
            Vector3I minI,
            Vector3I maxI,
            Dictionary<Vector3I, ConnectivityResult> found
        )
        {
            for (
                var it = new Vector3I_RangeIterator(ref minI, ref maxI);
                it.IsValid();
                it.MoveNext()
            )
                if (cells.Contains(it.Current))
                {
                    found[block.Position] = block;
                    return;
                }
        }
    }
}
