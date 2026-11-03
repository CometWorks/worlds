using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// SE-953: on a big ship the welders stop with blocks left that they could build, the
// components in the containers. When a build stalls so, the shipyard finishes those blocks
// itself, as it finishes the support arm: it welds the built ones and places the projected ones
// that pass the build check, FinishBlocks per check, and the welders go on with what is behind
// them. What it can't finish decides the ship as before (Stall).
internal static partial class ShipyardOrders
{
    private const int FinishBlocks = 200;

    // The blocks the shipyard finished so far, by order
    private static readonly Dictionary<Order, int> finished = [];

    // The blocks the welders left, as far as the containers have their components. False if there
    // were none.
    private static bool Finish(MyCubeGrid shipyard, Order order)
    {
        var watch = Stopwatch.StartNew();
        var welded = 0;
        var placed = 0;
        var left = 0;
        foreach (var block in ShipSoFar(shipyard, order).Where(b => !b.IsFullIntegrity).ToList())
        {
            if (welded >= FinishBlocks)
                break;
            var parts = new Dictionary<string, int>();
            block.GetMissingComponents(parts);
            if (Short(shipyard, parts) != null)
            {
                left++;
                continue;
            }
            var integrity = block.Integrity;
            Weld(shipyard, order, [block]);
            if (block.Integrity > integrity)
                welded++;
        }
        foreach (var (_, blueprint, preview, built) in Projected(order.Projector))
        {
            if (built == null)
                continue;
            foreach (var block in preview.GetBlocks().ToList())
            {
                if (welded + placed >= FinishBlocks)
                    break;
                if (
                    ShipyardZoneWelding.Unzoned(() => order.Projector.CanBuild(block, true))
                    != BuildCheckResult.OK
                )
                    continue;
                // A definition may list a component more than once
                var parts = block
                    .BlockDefinition.Components.GroupBy(c => c.Definition.Id.SubtypeName)
                    .ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
                if (Short(shipyard, parts) != null)
                {
                    left++;
                    continue;
                }
                if (Place(order, blueprint, preview, built, block) is not { } slim)
                    continue;
                Weld(shipyard, order, [slim]);
                placed++;
            }
        }
        if (welded + placed == 0)
            return false;
        if (!finished.TryGetValue(order, out var before))
            foreach (var beam in Beams(shipyard))
                Plugin.Log.Info(
                    $"Shipyard order {order.Id} stalled first: welder {beam.Position}: {AimOf(beam)}"
                );
        finished[order] = before + welded + placed;
        // The welders' progress decides the next stall, not this: a check without it finishes more
        order.Progress = ShipSoFar(shipyard, order).Sum(b => b.Integrity);
        order.Idle = StallChecks - 1;
        Plugin.Log.Info(
            $"Shipyard order {order.Id} at {shipyard.DisplayName}: the welders stalled, finished "
                + $"{welded} built and {placed} projected blocks in {watch.Elapsed.TotalMilliseconds:0} ms"
                + (left > 0 ? $", {left} short of components" : "")
                + $"; {finished[order]} so far"
        );
        return true;
    }

    // The order's projected grids by Multigrid Projector: the subgrid index, its blueprint, preview
    // and built grid, null for a subgrid not started yet. None without the plugin.
    private static IEnumerable<(
        int Index,
        MyObjectBuilder_CubeGrid Blueprint,
        MyCubeGrid Preview,
        MyCubeGrid Built
    )> Projected(MyProjectorBase projector)
    {
        var api = MgpApi();
        if (api == null)
            yield break;
        var count = ((Func<long, int>)api[1])(projector.EntityId);
        var builders = ((Func<long, List<MyObjectBuilder_CubeGrid>>)api[2])(projector.EntityId);
        for (var i = 0; i < count && i < (builders?.Count ?? 0); i++)
            if (((Func<long, int, IMyCubeGrid>)api[3])(projector.EntityId, i) is MyCubeGrid preview)
                yield return (
                    i,
                    builders[i],
                    preview,
                    ((Func<long, int, IMyCubeGrid>)api[4])(projector.EntityId, i) as MyCubeGrid
                );
    }
}
