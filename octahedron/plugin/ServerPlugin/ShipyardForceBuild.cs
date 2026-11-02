using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ServerPlugin;

// SE-940: welders place a projected block only where its hitbox is clear, so a part of a ship
// overlapping another, often a subgrid's block in its parent grid, is never placed, and every
// subgrid behind it is left out. When a build stalls with such blocks, the shipyard places them
// itself, as the welders would but without the collision check, and welds them from its
// containers. The welders rest while it watches the physics for TrialTicks: if the overlap makes
// the ship's parts shake (clang), the ship is removed and refunded, else the build goes on.
internal static partial class ShipyardOrders
{
    private sealed class Trial
    {
        public MyCubeGrid Shipyard;
        public string Blocks;
        public int Ticks;
        public double Baseline;
        public double Steps;
        public float Speed;
        public float Spin;

        // The ship's grids since the blocks were placed, a part that broke off included
        public HashSet<MyCubeGrid> Grids;
    }

    private static readonly Dictionary<Order, Trial> trials = [];

    // The blocks placed by force so far, by order: subgrid index and cell on the preview grid
    private static readonly Dictionary<Order, HashSet<(int, Vector3I)>> forced = [];

    private const int TrialTicks = 600;

    // Clang: one of the ship's moving grids this fast, or the average physics step of the trial
    // twice as long as before it and this much longer. Rotors and hinges turn at 6.3 rad/s at
    // most, pistons move at 5 m/s. The step time is the whole server's, so it takes a big jump:
    // the shaking subgrid of a small ship hardly shows in it, its speed does.
    private const double ClangStepMs = 5;
    private const float ClangSpeed = 20;
    private const float ClangSpin = 10;

    // Places the blocks that only a collision keeps the welders from, as far as the containers
    // have their components. False if there were none.
    private static bool Force(MyCubeGrid shipyard, Order order)
    {
        var api = MgpApi();
        if (api == null)
            return false;
        var projector = order.Projector;
        var count = ((Func<long, int>)api[1])(projector.EntityId);
        var builders = ((Func<long, List<MyObjectBuilder_CubeGrid>>)api[2])(projector.EntityId);
        if (count == 0 || builders == null)
            return false;
        if (!forced.TryGetValue(order, out var tried))
            forced[order] = tried = [];
        var parts = new Dictionary<string, int>();
        var placed = new List<MySlimBlock>();
        var why = new Dictionary<string, int>();
        var unbuilt = 0;
        for (var i = 0; i < count && i < builders.Count; i++)
        {
            var preview =
                ((Func<long, int, IMyCubeGrid>)api[3])(projector.EntityId, i) as MyCubeGrid;
            var built = ((Func<long, int, IMyCubeGrid>)api[4])(projector.EntityId, i) as MyCubeGrid;
            if (preview == null)
                continue;
            if (built == null)
            {
                unbuilt++;
                continue;
            }
            foreach (var block in preview.GetBlocks().ToList())
            {
                var result = ShipyardZoneWelding.Unzoned(() => projector.CanBuild(block, true));
                if (result == BuildCheckResult.AlreadyBuilt)
                    continue;
                var clear = projector.CanBuild(block, false);
                var key = $"{result}/{clear}";
                why[key] = (why.TryGetValue(key, out var n) ? n : 0) + 1;
                if (
                    tried.Contains((i, block.Min))
                    || result != BuildCheckResult.IntersectedWithSomethingElse
                    || clear != BuildCheckResult.OK
                )
                    continue;
                foreach (var c in block.BlockDefinition.Components)
                    parts[c.Definition.Id.SubtypeName] =
                        (parts.TryGetValue(c.Definition.Id.SubtypeName, out var had) ? had : 0)
                        + c.Count;
                if (Short(shipyard, parts) != null)
                {
                    foreach (var c in block.BlockDefinition.Components)
                        parts[c.Definition.Id.SubtypeName] -= c.Count;
                    continue;
                }
                tried.Add((i, block.Min));
                if (Place(order, builders[i], preview, built, block) is { } slim)
                    placed.Add(slim);
            }
        }
        if (placed.Count == 0)
        {
            Plugin.Log.Info(
                $"Shipyard order {order.Id}: nothing to place by force; {unbuilt} subgrids not started, "
                    + "the rest by build check with/without collisions: "
                    + string.Join(", ", why.Select(w => $"{w.Key} {w.Value}"))
            );
            return false;
        }
        Weld(shipyard, order, placed);
        Welders(shipyard, false);
        var names = string.Join(
            ", ",
            placed
                .GroupBy(b => b.BlockDefinition.DisplayNameText)
                .Select(g => $"{g.Count()} {g.Key}")
        );
        trials[order] = new Trial
        {
            Shipyard = shipyard,
            Blocks = names,
            Baseline = stepAverage,
            Grids = [],
        };
        order.Idle = 0;
        Plugin.Log.Info(
            $"Shipyard order {order.Id} at {shipyard.DisplayName}: placed {names} the welders couldn't for "
                + $"a collision, on {string.Join(", ", placed.Select(b => b.CubeGrid.DisplayName).Distinct())}; "
                + $"watching the physics for {TrialTicks / 60} s"
        );
        return true;
    }

    // The block on the built grid, where the projection has it, from the blueprint's own builder
    // as Multigrid Projector would build it, at the integrity of a new block
    private static MySlimBlock Place(
        Order order,
        MyObjectBuilder_CubeGrid blueprint,
        MyCubeGrid preview,
        MyCubeGrid built,
        MySlimBlock block
    )
    {
        var source =
            blueprint.CubeBlocks.FirstOrDefault(b =>
                (Vector3I)b.Min == block.Min && b.GetId() == block.BlockDefinition.Id
            ) ?? block.GetObjectBuilder();
        var builder = (MyObjectBuilder_CubeBlock)source.Clone();
        if (
            builder is MyObjectBuilder_MechanicalConnectionBlock mechanical
            && builder is not MyObjectBuilder_MotorSuspension
        )
            mechanical.TopBlockId = null;
        if (builder.EntityId == 0 || VRage.MyEntityIdentifier.ExistsById(builder.EntityId))
            builder.EntityId = VRage.MyEntityIdentifier.AllocateId();
        builder.ConstructionInventory = null;
        builder.ConstructionStockpile = null;
        builder.IntegrityPercent = builder.BuildPercent = MyComponentStack.NewBlockIntegrity;
        builder.Owner = builder.BuiltBy = order.Player;
        if (
            builder is MyObjectBuilder_BatteryBlock battery
            && block.BlockDefinition is MyBatteryBlockDefinition definition
        )
            battery.CurrentStoredPower =
                definition.InitialStoredPowerRatio * definition.MaxStoredPower;

        var a = built.WorldToGridInteger(preview.GridIntegerToWorld(block.Min));
        var b = built.WorldToGridInteger(preview.GridIntegerToWorld(block.Max));
        var center = built.WorldToGridInteger(preview.GridIntegerToWorld(block.Position));
        block.Orientation.GetMatrix(out var local);
        var turn =
            local
            * (Matrix)preview.WorldMatrix.GetOrientation()
            * Matrix.Transpose((Matrix)built.WorldMatrix.GetOrientation());
        var location = new MyCubeGrid.MyBlockLocation(
            block.BlockDefinition.Id,
            Vector3I.Min(a, b),
            Vector3I.Max(a, b),
            center,
            Quaternion.CreateFromRotationMatrix(turn),
            builder.EntityId,
            order.Player
        );
        var visuals = new MyCubeGrid.MyBlockVisuals(
            block.ColorMaskHSV.PackHSVToUint(),
            block.SkinSubtypeId
        );
        Plugin.Log.Info(
            $"Shipyard order {order.Id}: placing {block.BlockDefinition.Id.SubtypeName} on {built.DisplayName} "
                + $"by force, into {Overlapped(block, built)}"
        );
        ShipyardArm.Build(built, location, visuals, builder, order.Player);
        var slim = built.GetCubeBlock(center);
        if (slim?.BlockDefinition.Id != block.BlockDefinition.Id)
        {
            Plugin.Log.Warning(
                $"Shipyard order {order.Id}: placing {block.BlockDefinition.Id.SubtypeName} at {center} on {built.DisplayName} failed"
            );
            return null;
        }
        // A rotor, hinge or piston gets its top from the projection, as when welders place it
        slim.FatBlock?.OnBuildSuccess(order.Player, false);
        return slim;
    }

    // The other grids' blocks in the cells of a preview block, for the log
    private static string Overlapped(MySlimBlock block, MyCubeGrid built)
    {
        block.GetWorldBoundingBox(out var box, true);
        box.Inflate(-0.1);
        var near = new List<MyEntity>();
        MyEntities.GetTopMostEntitiesInBox(ref box, near);
        var found = new List<string>();
        foreach (var grid in near.OfType<MyCubeGrid>())
        {
            if (grid == built || grid == block.CubeGrid || grid.Physics == null)
                continue;
            var corners = box.GetCorners().Select(grid.WorldToGridInteger).ToList();
            var min = corners.Aggregate(Vector3I.Min);
            var max = corners.Aggregate(Vector3I.Max);
            var blocks = new HashSet<MySlimBlock>();
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
                if (grid.GetCubeBlock(new Vector3I(x, y, z)) is { } other)
                    blocks.Add(other);
            found.AddRange(
                blocks.Select(b => $"{b.BlockDefinition.DisplayNameText} on {grid.DisplayName}")
            );
        }
        return found.Count == 0 ? "no block of another grid" : string.Join(", ", found.Take(5));
    }

    // Welds the blocks with the components in the shipyard's containers
    private static void Weld(MyCubeGrid shipyard, Order order, List<MySlimBlock> blocks)
    {
        var containers = Containers(shipyard).ToList();
        foreach (var block in blocks)
        {
            foreach (var inventory in containers)
                block.MoveItemsToConstructionStockpile(inventory);
            block.IncreaseMountLevel(
                block.MaxIntegrity / block.BlockDefinition.IntegrityPointsPerSec,
                order.Player,
                containers.FirstOrDefault(),
                isHelping: true
            );
        }
    }

    // Every tick: the physics of the orders on trial
    private static void WatchTrials()
    {
        foreach (var (order, trial) in trials.ToList())
        {
            // The station went to another node, or out of the simulation
            if (!orders.ContainsValue(order))
            {
                trials.Remove(order);
                forced.Remove(order);
                continue;
            }
            trial.Steps += lastStep;
            var own = Own(trial.Shipyard);
            trial.Grids.UnionWith(Grids(order.Projector.CubeGrid).Where(g => !own.Contains(g)));
            foreach (var grid in trial.Grids)
            {
                if (grid.Closed || grid.IsStatic || grid.Physics == null)
                    continue;
                trial.Speed = Math.Max(trial.Speed, grid.Physics.LinearVelocity.Length());
                trial.Spin = Math.Max(trial.Spin, grid.Physics.AngularVelocity.Length());
            }
            // Shaking ends the trial at once, before it breaks parts off
            var shakes = trial.Speed > ClangSpeed || trial.Spin > ClangSpin;
            if (++trial.Ticks < TrialTicks && !shakes)
                continue;
            trials.Remove(order);
            var steps = trial.Steps / trial.Ticks;
            var clang =
                shakes || steps > Math.Max(2 * trial.Baseline, trial.Baseline + ClangStepMs);
            Plugin.Log.Info(
                $"Shipyard order {order.Id} at {trial.Shipyard.DisplayName}: after placing {trial.Blocks}, "
                    + $"{trial.Ticks} ticks: physics steps {steps:0.00} ms (before {trial.Baseline:0.00} ms), "
                    + $"top speed {trial.Speed:0.0} m/s, spin {trial.Spin:0.0} rad/s: "
                    + (clang ? "clang, the ship is removed" : "the build goes on")
            );
            if (!clang)
            {
                Welders(trial.Shipyard, true);
                continue;
            }
            order.Clang = trial.Blocks;
            order.Refund = true;
            var loose = trial
                .Grids.Where(g =>
                    !g.MarkedForClose
                    && !MyCubeGridGroups.Static.Mechanical.HasSameGroup(g, order.Projector.CubeGrid)
                )
                .ToList();
            if (!order.Free)
                KeepParts(trial.Shipyard, order, loose.SelectMany(g => g.GetBlocks()));
            Scrap(trial.Shipyard, order);
            foreach (var grid in loose)
                grid.Close();
            End(trial.Shipyard, order, "clang");
        }
    }

    // The physics step in ms: the last one, and the average over about the last 100
    private static double lastStep;
    private static double stepAverage;

    [HarmonyPatch(typeof(MyPhysics), "StepWorlds")]
    private static class StepWorlds_
    {
        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(long __state)
        {
            lastStep = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            stepAverage = stepAverage * 0.99 + lastStep * 0.01;
        }
    }

    // Multigrid Projector's mod API, as a mod gets it: null without the plugin
    private const long MgpRequest = 2415983416L * 1000;
    private static object[] mgpApi;

    private static object[] MgpApi()
    {
        if (mgpApi != null)
            return mgpApi;
        Action<object> handler = o => mgpApi = o as object[];
        MyAPIGateway.Utilities.RegisterMessageHandler(MgpRequest + 1, handler);
        MyAPIGateway.Utilities.SendModMessage(MgpRequest, null);
        MyAPIGateway.Utilities.UnregisterMessageHandler(MgpRequest + 1, handler);
        return mgpApi;
    }
}
