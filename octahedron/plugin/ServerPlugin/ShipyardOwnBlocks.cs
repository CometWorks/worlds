using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// While a player has a station shipyard's welders (ShipyardTurn), they work only on that player's
// blocks and their faction's: a block's owner, or who built it when it has no owner, and for a
// projected block the projector's owner. The order's own projector (the scaffold) is OCT's and
// counts as theirs. Other players' ships in the shipyard are neither targeted nor welded, nor are
// their projections built. Out of a turn the welders are OCT's and work as before.
//
// ToolCore collects the blocks a tool works on in its HitBlockLayers, on a worker thread, and
// hands them over on the game thread: to a turret's targets (OnGetBlockTargetsComplete) or to the
// welding (OnGetBlocksComplete, which adds the WorkSet of earlier hits). Both take the other
// players' blocks out first. ToolCore is a mod, compiled while the world loads, so this patches
// it by name once the session is ready. A ToolCore that renamed them gets no filter and an error
// in the log.
//
// The clients' ToolCore doesn't have this: a client may draw a beam at another player's block
// that the server doesn't weld.
internal static class ShipyardOwnBlocks
{
    private static bool patched;

    private static AccessTools.FieldRef<object, IMyConveyorSorter> blockTool;
    private static AccessTools.FieldRef<object, Dictionary<int, List<IMySlimBlock>>> layers;
    private static AccessTools.FieldRef<object, List<IMySlimBlock>> workSet;

    public static void Update()
    {
        if (patched || MySession.Static?.Ready != true)
            return;
        patched = true;
        var utils = AccessTools.TypeByName("ToolCore.GridUtils");
        var comp = AccessTools.TypeByName("ToolCore.Comp.ToolComp");
        if (utils == null || comp == null)
        {
            Plugin.Log.Info("Shipyard welders: no ToolCore, nothing to filter");
            return;
        }
        try
        {
            blockTool = AccessTools.FieldRefAccess<IMyConveyorSorter>(comp, "BlockTool");
            layers = AccessTools.FieldRefAccess<Dictionary<int, List<IMySlimBlock>>>(
                comp,
                "HitBlockLayers"
            );
            workSet = AccessTools.FieldRefAccess<List<IMySlimBlock>>(comp, "WorkSet");
            var harmony = new Harmony(Plugin.Name);
            var prefix = new HarmonyMethod(typeof(ShipyardOwnBlocks), nameof(Prefix));
            foreach (var name in new[] { "OnGetBlocksComplete", "OnGetBlockTargetsComplete" })
                harmony.Patch(
                    AccessTools.Method(utils, name)
                        ?? throw new MissingMethodException(utils.FullName, name),
                    prefix
                );
            Plugin.Log.Info("Shipyard welders: a player's turn welds only their blocks");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(
                "Shipyard welders: ToolCore can't be patched, a player's turn welds any block",
                e
            );
        }
    }

    private static void Prefix(object comp)
    {
        if (
            !Plugin.Config.Enabled
            || blockTool(comp) is not MyCubeBlock tool
            || tool.OwnerId == 0
            || MySession.Static.Players.IdentityIsNpc(tool.OwnerId)
            || !ShipyardZoneWelding.IsShipyard(tool)
        )
            return;

        var player = tool.OwnerId;
        var faction = MySession.Static.Factions.GetPlayerFaction(player);
        bool Theirs(long identity) =>
            identity == player || faction != null && faction.IsMember(identity);
        bool Other(IMySlimBlock block)
        {
            var slim = (MySlimBlock)block;
            if (slim.CubeGrid.Projector is { } projector)
                return !ShipyardOrders.IsScaffold(projector) && !Theirs(projector.OwnerId);
            return !Theirs(slim.OwnerId != 0 ? slim.OwnerId : slim.BuiltBy);
        }

        foreach (var layer in layers(comp).Values)
            layer.RemoveAll(Other);
        workSet(comp).RemoveAll(Other);
    }
}
