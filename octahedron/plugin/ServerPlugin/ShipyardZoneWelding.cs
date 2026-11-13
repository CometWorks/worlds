using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;
using VRageMath;

namespace ServerPlugin;

// The station safe zones forbid welding, and each one holds the station's shipyard, whose
// OmniArc beam welders (ToolCore) obey the zone like the game's tools. This lets the shipyard
// grids, named "<station> Shipyard" and owned by the Octahedron Authority, weld and build
// projections in them. Every other tool stays bound by the zone, and grinding stays forbidden
// except for the shipyard's deconstructors in a grind slot, in its build volume (SE-1386).
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
//
// Clients run ToolCore's tool loop too, without this plugin: a tool whose grid the zone holds
// is switched off there, and players see no beams (SE-441). While a build runs, ShowBeams
// tells the clients that the zones don't hold the shipyard grid, so their ToolCore draws the
// beams. The server keeps its own list; it welds as before.
//
// That can't cover a client that gets the shipyard streamed in during a build: it comes back
// in range, joins or crosses to this node (SE-782). The zone it gets holds the grid until the
// next ShowBeams, its ToolCore switches the tools off for good, and only a change of the
// server's switch would tell it otherwise. ToolCore sends the switch only to the clients in
// its lists of each tool's clients, and a cluster handover that brings the shipyard to another
// node takes its players' clients along with their tools, so the new node's lists are empty
// (SE-1115). So at every check of a build, ShowShooting sends the switch and target tracking
// to every client the replication server has given the shipyard, however it got there, and
// adds them to ToolCore's lists. A client whose tool is on takes no notice (SE-1126).
internal static class ShipyardZoneWelding
{
    private const string Owner = "Octahedron Authority";
    private const string NameSuffix = " Shipyard";

    // The inventory of the welder in an IncreaseMountLevel call on this thread
    [ThreadStatic]
    private static MyInventoryBase weldingFrom;

    // The inventory of the grinder in a DecreaseMountLevel call on this thread
    [ThreadStatic]
    private static MyInventoryBase grindingFrom;

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
        foreach (var owner in Owners(grid))
            if (players.IdentityIsNpc(owner) && players.TryGetIdentity(owner)?.DisplayName == Owner)
                return true;
        return false;
    }

    /// <summary>
    /// Everyone who owns a block of the grid. During a player's turn the player may own more of
    /// a shipyard's blocks than OCT and be its only big owner: the Moon Shipyard's lights have no
    /// owner, and OCT keeps only its reactors, deconstructors and arm rotors (SE-1069).
    /// </summary>
    internal static IEnumerable<long> Owners(MyCubeGrid grid) =>
        grid.BigOwners.Concat(grid.SmallOwners);

    private static readonly System.Reflection.MethodInfo removeEntity = AccessTools.Method(
        typeof(MySafeZone),
        "RemoveEntity_Implementation"
    );
    private static readonly System.Reflection.MethodInfo insertEntity = AccessTools.Method(
        typeof(MySafeZone),
        "InsertEntity_Implementation"
    );

    // Events by private methods: the replication layer takes the method of the returned delegate
    private static readonly Func<MySafeZone, Action<long, bool>> removeEvent = z =>
        (Action<long, bool>)Delegate.CreateDelegate(typeof(Action<long, bool>), z, removeEntity);
    private static readonly Func<MySafeZone, Action<long>> insertEvent = z =>
        (Action<long>)Delegate.CreateDelegate(typeof(Action<long>), z, insertEntity);

    /// <summary>
    /// Takes the shipyard grid out of the zones around it on the clients, or puts it back.
    /// A client that joins gets the zones' lists again, so a running build sends it again.
    /// </summary>
    internal static void ShowBeams(MyCubeGrid shipyard, bool on)
    {
        var box = shipyard.PositionComp.WorldAABB;
        foreach (var zone in MySessionComponentSafeZones.SafeZones)
        {
            if (!zone.Enabled || !zone.PositionComp.WorldAABB.Intersects(box))
                continue;
            ShowOutside(zone, shipyard.EntityId, on);
        }
    }

    /// <summary>
    /// Tells the clients that the zone doesn't hold the entity, or that it does again.
    /// </summary>
    internal static void ShowOutside(MySafeZone zone, long entityId, bool outside)
    {
        if (outside)
            MyMultiplayer.RaiseEvent(zone, removeEvent, entityId, true);
        else
            MyMultiplayer.RaiseEvent(zone, insertEvent, entityId);
    }

    // ToolCore is internal to the mod, so this goes by name
    private sealed class ToolCore(Type session)
    {
        internal readonly FieldInfo Instance = AccessTools.Field(session, "Instance");
        internal readonly FieldInfo Tools = AccessTools.Field(session, "ToolMap");
        internal readonly FieldInfo Networking = AccessTools.Field(session, "Networking");
        internal readonly FieldInfo Clients = AccessTools.Field(
            AccessTools.TypeByName("ToolCore.Comp.ToolComp"),
            "ReplicatedClients"
        );
        internal readonly MethodInfo Send = AccessTools.Method(
            AccessTools.TypeByName("ToolCore.Session.Networking"),
            "SendPacketToClients"
        );
        internal readonly Type Packet = AccessTools.TypeByName("ToolCore.Session.BoolUpdatePacket");
        internal readonly object Activated = Enum.Parse(
            AccessTools.TypeByName("ToolCore.Session.FieldType"),
            "Activated"
        );
        internal readonly object TrackTargets = Enum.Parse(
            AccessTools.TypeByName("ToolCore.Session.FieldType"),
            "TrackTargets"
        );
    }

    private static ToolCore toolCore;

    // The replication server's clients: the field and their class are internal
    private static readonly FieldInfo clientStates = AccessTools.Field(
        typeof(MyReplicationServer),
        "m_clientStates"
    );
    private static readonly MethodInfo replicableReady = AccessTools.Method(
        AccessTools.TypeByName("VRage.Network.MyClient"),
        "IsReplicableReady"
    );

    // Per call, on the game thread
    private static readonly List<ulong> toTell = [];

    /// <summary>
    /// Sends the tools' switch and target tracking to every client that has the shipyard, and
    /// adds those clients to ToolCore's lists of the tools' clients. Does nothing as the build
    /// ends, or without ToolCore.
    /// </summary>
    internal static void ShowShooting(IEnumerable<MyFunctionalBlock> tools, bool on)
    {
        if (!on)
            return;
        var session = AccessTools.TypeByName("ToolCore.Session.ToolSession");
        if (session == null)
            return;
        var core = toolCore ??= new ToolCore(session);
        var instance = core.Instance.GetValue(null);
        if (instance == null)
            return;
        var map = (IDictionary)core.Tools.GetValue(instance);
        var networking = core.Networking.GetValue(instance);
        MyCubeGrid grid = null;
        foreach (var tool in tools)
        {
            if (map[tool.EntityId] is not { } comp)
                continue;
            if (grid != tool.CubeGrid)
                Holders(grid = tool.CubeGrid);
            if (toTell.Count == 0)
                continue;
            var clients = (List<ulong>)core.Clients.GetValue(comp);
            foreach (var client in toTell)
                if (!clients.Contains(client))
                    clients.Add(client);
            Send(core, networking, tool, core.TrackTargets);
            Send(core, networking, tool, core.Activated);
        }
    }

    // ToolCore's switch of the field on, to toTell
    private static void Send(
        ToolCore core,
        object networking,
        MyFunctionalBlock tool,
        object field
    ) =>
        core.Send.Invoke(
            networking,
            [Activator.CreateInstance(core.Packet, tool.EntityId, field, true), toTell, 0UL]
        );

    // The clients that have the grid by the replication server: created there and not still
    // streaming. A client's ToolCore logs a packet for a tool it doesn't have as an error.
    private static void Holders(MyCubeGrid grid)
    {
        toTell.Clear();
        if (
            MyMultiplayer.Static?.ReplicationLayer is not MyReplicationServer server
            || MyExternalReplicable.FindByObject(grid) is not { } replicable
        )
            return;
        foreach (DictionaryEntry pair in (IDictionary)clientStates.GetValue(server))
            if ((bool)replicableReady.Invoke(pair.Value, [replicable]))
                toTell.Add(((Endpoint)pair.Key).Id.Value);
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
            if (__result || !Plugin.Config.Enabled)
                return;
            // A grind slot's deconstructors: ToolCore asks about their grid (SE-1386)
            if (action == MySafeZoneAction.Grinding)
            {
                __result =
                    (entity as MyCubeGrid ?? (entity as MyCubeBlock)?.CubeGrid) is { } grid
                    && IsShipyard(grid)
                    && ShipyardOrders.Grinding(grid);
                return;
            }
            if (action is not (MySafeZoneAction.Welding or MySafeZoneAction.BuildingProjections))
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

    // Grinding a block asks the zone about the block's center, naming nobody: the deconstructor
    // whose inventory takes the components may grind in its slot's volume (SE-1386)
    [HarmonyPatch(
        typeof(MySessionComponentSafeZones),
        nameof(MySessionComponentSafeZones.IsActionAllowed),
        typeof(Vector3D),
        typeof(MySafeZoneAction),
        typeof(long),
        typeof(ulong)
    )]
    private static class PointCheck
    {
        private static void Postfix(ref bool __result, Vector3D point, MySafeZoneAction action)
        {
            if (
                !__result
                && action == MySafeZoneAction.Grinding
                && grindingFrom?.Entity is MyEntity tool
                && Plugin.Config.Enabled
            )
                __result = ShipyardOrders.GrindsHere(tool, point);
        }
    }

    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.DecreaseMountLevel))]
    private static class Grinding
    {
        private static void Prefix(
            MySlimBlock __instance,
            MyInventoryBase outputInventory,
            out float __state
        )
        {
            grindingFrom = outputInventory;
            __state = __instance.Integrity;
        }

        private static void Postfix(
            MySlimBlock __instance,
            MyInventoryBase outputInventory,
            float __state
        )
        {
            if (__instance.Integrity < __state)
                ShipyardOrders.Worked(outputInventory?.Entity as MyEntity);
        }

        private static void Finalizer() => grindingFrom = null;
    }

    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.IncreaseMountLevel))]
    private static class Welding
    {
        private static void Prefix(
            MySlimBlock __instance,
            MyInventoryBase outputInventory,
            out float __state
        )
        {
            weldingFrom = outputInventory;
            __state = __instance.Integrity;
        }

        private static void Postfix(
            MySlimBlock __instance,
            MyInventoryBase outputInventory,
            float __state
        )
        {
            if (__instance.Integrity > __state)
                ShipyardOrders.Worked(outputInventory?.Entity as MyEntity);
        }

        private static void Finalizer() => weldingFrom = null;
    }

    // The game's own welders ask the projector directly and stay bound by its box check
    [HarmonyPatch(typeof(MyProjectorBase), "Sandbox.ModAPI.IMyProjector.CanBuild")]
    private static class ModCanBuild
    {
        private static void Prefix() => modCanBuild = true;

        private static void Finalizer() => modCanBuild = false;
    }

    // A check of the shipyard's own, past the zone's box check as the shipyard's welders are
    internal static T Unzoned<T>(Func<T> check)
    {
        modCanBuild = true;
        try
        {
            return check();
        }
        finally
        {
            modCanBuild = false;
        }
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
            if (__runOriginal && isProjection && __instance.BlocksCount > __state)
                ShipyardOrders.Worked(MyEntities.GetEntityById(builderEntityId));
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
