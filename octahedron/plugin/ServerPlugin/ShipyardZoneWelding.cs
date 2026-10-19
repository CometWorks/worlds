using System;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace ServerPlugin;

// The station safe zones forbid welding, and each one holds the station's shipyard, whose
// OmniArc beam welders (ToolCore) obey the zone like the game's tools. This lets the shipyard
// grids, named "<station> Shipyard" and owned by the Octahedron Authority, weld and build
// projections in them. Every other tool stays bound by the zone, and grinding stays forbidden.
//
// The game asks a zone either about an entity, sometimes naming who acts on it, or about a
// box, naming nobody:
// - ToolCore asks about the tool's grid once a tick, or about the tool block when shooting;
// - building a projected block asks about the projector's grid, with the tool as the source,
//   and then about the new block's box; BuildRequest tells the box check who builds;
// - welding a block asks about the welded block's grid without naming the welder, so Welding
//   remembers the inventory the components come from;
// - ToolCore first asks the projector whether it can build the block, which asks about the
//   block's box; ModCanBuild lets every mod past that, as the build request decides.
// The shipyard grid itself is indestructible and not editable, so letting anyone "weld" it
// changes nothing.
internal static class ShipyardZoneWelding
{
    private const string Owner = "Octahedron Authority";
    private const string NameSuffix = " Shipyard";

    // The inventory of the welder in an IncreaseMountLevel call on this thread
    [ThreadStatic]
    private static MyInventoryBase weldingFrom;

    // A mod asks a projector whether it can build a block, on this thread
    [ThreadStatic]
    private static bool modCanBuild;

    // A shipyard tool asks to build a projected block, on this thread
    [ThreadStatic]
    private static bool shipyardBuilds;

    internal static bool IsShipyard(MyEntity entity)
    {
        var grid = entity as MyCubeGrid ?? (entity as MyCubeBlock)?.CubeGrid;
        if (grid?.DisplayName?.EndsWith(NameSuffix) != true)
            return false;
        var players = MySession.Static.Players;
        foreach (var owner in grid.BigOwners)
            if (players.IdentityIsNpc(owner) && players.TryGetIdentity(owner)?.DisplayName == Owner)
                return true;
        return false;
    }

    private static bool IsShipyard(long entityId) =>
        entityId != 0
        && MyEntities.TryGetEntityById(entityId, out var entity)
        && IsShipyard(entity);

    // The postfixes act only on a refusal of welding, so the game's many other checks pay a
    // comparison or two
    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.IsActionAllowed),
        typeof(MyEntity),
        typeof(MySafeZoneAction),
        typeof(long),
        typeof(ulong)
    )]
    private static class EntityCheck
    {
        private static void Postfix(
            ref bool __result,
            MyEntity entity,
            MySafeZoneAction action,
            long sourceEntityId
        )
        {
            if (
                __result
                || action is not (MySafeZoneAction.Welding or MySafeZoneAction.BuildingProjections)
                || !Plugin.Config.Enabled
            )
                return;

            __result =
                IsShipyard(entity)
                || IsShipyard(sourceEntityId)
                || action == MySafeZoneAction.Welding
                    && IsShipyard(weldingFrom?.Entity as MyEntity);
        }
    }

    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.IsActionAllowed),
        typeof(BoundingBoxD),
        typeof(MySafeZoneAction),
        typeof(long),
        typeof(ulong)
    )]
    private static class BoxCheck
    {
        private static void Postfix(ref bool __result, MySafeZoneAction action)
        {
            if (
                !__result
                && action == MySafeZoneAction.BuildingProjections
                && (modCanBuild || shipyardBuilds)
                && Plugin.Config.Enabled
            )
                __result = true;
        }
    }

    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.IncreaseMountLevel))]
    private static class Welding
    {
        private static void Prefix(MyInventoryBase outputInventory) =>
            weldingFrom = outputInventory;

        private static void Finalizer() => weldingFrom = null;
    }

    // The game's own welders ask the projector directly and stay bound by its box check
    [HarmonyPatch(typeof(MyProjectorBase), "Sandbox.ModAPI.IMyProjector.CanBuild")]
    private static class ModCanBuild
    {
        private static void Prefix() => modCanBuild = true;

        private static void Finalizer() => modCanBuild = false;
    }

    // A mod tool past ModCanBuild may still be refused here, by the zone among others, and
    // ToolCore took the block's first component before it asked: it gets the component back,
    // as in ProjectionLimits. Runs last, so a request ProjectionLimits refused (and refunded)
    // is left alone.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlockRequestInternal))]
    private static class BuildRequest
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(
            bool __runOriginal,
            MyCubeGrid __instance,
            long builderEntityId,
            bool isProjection,
            out int __state
        )
        {
            __state = __instance.BlocksCount;
            shipyardBuilds = __runOriginal && isProjection && IsShipyard(builderEntityId);
        }

        private static void Postfix(
            bool __runOriginal,
            MyCubeGrid __instance,
            MyCubeGrid.MyBlockLocation location,
            long builderEntityId,
            bool isProjection,
            int __state
        )
        {
            if (
                __runOriginal
                && isProjection
                && __instance.BlocksCount == __state
                && MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                    location.BlockDefinition,
                    out var definition
                )
            )
                ProjectionLimits.Refund(builderEntityId, definition);
        }

        private static void Finalizer() => shipyardBuilds = false;
    }
}
