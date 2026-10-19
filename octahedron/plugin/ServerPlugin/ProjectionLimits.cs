using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ServerPlugin;

// The server welds a projected block without checking the world's block limits: the game's
// welders check them before they ask, ToolCore's (OmniArc) don't. This checks them on the
// server for the player the new block will count toward, its BuiltBy, and refuses a block over
// any of them: block type or tag limit, PCU, blocks per player, grid size. Multigrid Projector
// builds projected blocks through the same request, so this covers its projections as well.
[HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlockRequestInternal))]
internal static class ProjectionLimits
{
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<long, DateTime> lastNotified = new();

    private static bool Prefix(
        MyCubeGrid __instance,
        MyCubeGrid.MyBlockLocation location,
        MyObjectBuilder_CubeBlock blockObjectBuilder,
        long builderEntityId,
        bool instantBuild,
        bool isProjection
    )
    {
        var config = Plugin.Config;
        if (!isProjection || !config.Enabled || !config.ProjectionLimitsEnabled)
            return true;

        // Blocks built by nobody or by an NPC, like those an NPC station's welders weld, count
        // toward no player
        var session = MySession.Static;
        var builtBy = blockObjectBuilder?.BuiltBy ?? 0;
        if (builtBy == 0 || session.Players.IdentityIsNpc(builtBy))
            return true;

        if (
            !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                location.BlockDefinition,
                out var definition
            )
        )
            return true;

        // A block starts as a construction stage of 1 PCU, unless it is built complete at once
        var pcu =
            session.CreativeMode || instantBuild
                ? definition.PCU
                : MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST;
        var limitType = definition.GetLimitType();
        var result = session.IsWithinWorldLimits(
            out _,
            builtBy,
            limitType,
            pcu,
            blocksToBuild: 1,
            blocksCount: __instance.BlocksCount
        );
        if (result == MySession.LimitResult.Passed)
            return true;

        Refund(builderEntityId, definition);
        Notify(builtBy, definition, limitType, result);
        return false;
    }

    // The game takes a new block's first component from the welding character or ship welder
    // after it built the block. ToolCore's block tools take it from their own inventory before
    // they ask, so they get it back; an OmniArc welder would lose one at every retry otherwise.
    private static void Refund(long builderEntityId, MyCubeBlockDefinition definition)
    {
        if (MySession.Static.CreativeMode || definition.Components.Length == 0)
            return;
        if (
            !MyEntities.TryGetEntityById(builderEntityId, out var builder)
            || builder is MyCharacter or MyShipWelder
        )
            return;

        builder
            .GetInventory()
            ?.AddItems(
                1,
                MyObjectBuilderSerializer.CreateNewObject(definition.Components[0].Definition.Id)
            );
    }

    // A tool retries every second or so, the player hears about it every NotifyInterval
    private static void Notify(
        long identityId,
        MyCubeBlockDefinition definition,
        string limitType,
        MySession.LimitResult result
    )
    {
        var now = DateTime.UtcNow;
        if (lastNotified.TryGetValue(identityId, out var last) && now - last < NotifyInterval)
            return;
        lastNotified[identityId] = now;

        var reason = result switch
        {
            MySession.LimitResult.BlockTypeLimit =>
                $"you reached the limit of {MySession.Static.GetBlockTypeLimit(limitType)} {limitType} blocks",
            MySession.LimitResult.PCU => "you have no PCU left",
            MySession.LimitResult.MaxBlocksPerPlayer =>
                "you reached the limit of blocks per player",
            MySession.LimitResult.MaxGridSize => "the grid reached its size limit",
            MySession.LimitResult.NoFaction => "join a faction to build",
            _ => result.ToString(),
        };
        var message = $"Projected {definition.DisplayNameText} not built: {reason}.";
        MyVisualScriptLogicProvider.ShowNotification(message, 5000, "Red", identityId);
        var name = MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName;
        Plugin.Log.Info($"{name} ({identityId}): {message}");
    }
}
