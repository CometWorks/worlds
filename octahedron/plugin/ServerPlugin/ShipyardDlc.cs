using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;

namespace ServerPlugin;

/// <summary>
/// The shipyard builds only the blocks of the DLCs the ordering player owns. That is checked at
/// the press, while the server knows what they own: the other blocks are left out of the order's
/// blueprint. Its welders are the station's, the game's DLC checks would refuse them every DLC
/// block, and ToolCore would lose a component on each try. So an order's projector, which holds
/// nothing else, passes those checks; any other projector doesn't.
/// </summary>
internal static class ShipyardDlc
{
    [ThreadStatic]
    private static bool building;

    /// <summary>Removes the blocks of DLCs the player doesn't own; what it removed by DLC, or null.</summary>
    public static string LeaveOut(List<MyObjectBuilder_CubeGrid> grids, ulong steamId)
    {
        var dlcs = MySession.Static.GetComponent<MySessionComponentDLC>();
        var left = new Dictionary<string, int>();
        foreach (var grid in grids)
            grid.CubeBlocks.RemoveAll(block =>
            {
                if (
                    !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                        block.GetId(),
                        out var definition
                    )
                )
                    return false;
                var dlc = dlcs.GetFirstMissingDefinitionDLC(definition, steamId);
                if (dlc == null)
                    return false;
                var name = MyTexts.GetString(dlc.DisplayName);
                left[name] = (left.TryGetValue(name, out var count) ? count : 0) + 1;
                return true;
            });
        grids.RemoveAll(g => g.CubeBlocks.Count == 0);
        return left.Count == 0
            ? null
            : string.Join(", ", left.OrderBy(l => l.Key).Select(l => $"{l.Value} of {l.Key}"));
    }

    [HarmonyPatch(typeof(MyProjectorBase), nameof(MyProjectorBase.Build))]
    private static class BuildPatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(MyProjectorBase __instance) =>
            building = ShipyardOrders.IsScaffold(__instance);

        // ReSharper disable once UnusedMember.Local
        private static void Finalizer() => building = false;
    }

    [HarmonyPatch(
        typeof(MySessionComponentDLC),
        nameof(MySessionComponentDLC.HasDLC),
        typeof(string),
        typeof(ulong)
    )]
    private static class HasDlcPatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(ref bool __result)
        {
            if (building)
                __result = true;
        }
    }
}
