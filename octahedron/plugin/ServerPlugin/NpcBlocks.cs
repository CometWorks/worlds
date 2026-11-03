using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.World;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Sync;

namespace ServerPlugin;

// Players use what an NPC shares with everyone but don't change it (SE-936). The game's ownership
// check guards a block's settings as well as its use, and sharing with everyone lets anyone past
// it. build_world.py keeps the blocks that have "Anyone can use" (store, contracts, doors, buttons)
// with the station's faction instead. The medical room (MyMedicalRoom.RequestSupport), the
// hologram table (MyProjectorBase.OnNewBlueprintSuccess), seats (MyUseObjectCockpitDoor) and the
// Free Tools boxes' inventories (MyInventoryReplicable.HasRights) take the same check as the
// settings, so they stay shared, and with NpcBlocksLocked the server refuses the changes from a
// player who isn't in the NPC's faction, unless an admin with "Use terminals" on, as the game's
// own check would for a block not shared. Inventories, the uses below, switching a hologram table
// on or off and whatever the game checks for access alone stay open.
internal static class NpcBlocks
{
    // The game checks these for ownership too, but they use the block rather than change it
    private static readonly HashSet<string> Uses =
    [
        // A seat
        "AttachPilotEvent",
        "SyncMoveIndicator",
        "sync_ControlledEntity_Used",
        "OnSwitchHelmet",
        "SwitchToWeaponMessage",
        "OnSwitchAmmoMagazineRequest",
        "ShootBeginCallback",
        "ShootEndCallback",
        // The medical room's wardrobe
        "UseWardrobeSync",
        "StopUsingWardrobeSync",
        // The store lists the buyer's gas tanks
        "GetGasTanks",
        // A blueprint into a station's hologram table, which hands the table to the player
        "OnNewBlueprintSuccess",
    ];

    // A hologram table starts switched off, and a player switches it on to load a blueprint
    private static readonly AccessTools.FieldRef<
        MyFunctionalBlock,
        Sync<bool, SyncDirection.BothWays>
    > OnOff = AccessTools.FieldRefAccess<MyFunctionalBlock, Sync<bool, SyncDirection.BothWays>>(
        "m_enabled"
    );

    private static readonly HashSet<(ulong, long, string)> logged = [];

    private static bool Locked(MyCubeBlock block, ulong steamId)
    {
        if (
            !Plugin.Config.NpcBlocksLocked
            || block?.IDModule == null
            || block.IDModule.ShareMode != MyOwnershipShareModeEnum.All
        )
            return false;
        return Outsider(block.OwnerId, steamId);
    }

    private static bool Outsider(long npc, ulong steamId)
    {
        var session = MySession.Static;
        if (!session.Players.IdentityIsNpc(npc))
            return false;
        // Not the NPC's faction: every player is in OCT's (OctFaction)
        return !(
            session.RemoteAdminSettings.TryGetValue(steamId, out var admin)
            && admin.HasFlag(AdminSettingsEnum.UseTerminals)
        );
    }

    private static void Refused(MyEntity entity, string what, ulong steamId)
    {
        if (logged.Add((steamId, entity.EntityId, what)))
            Plugin.Log.Info(
                $"NPC block '{entity.DisplayNameText}' ({entity.EntityId}): {what} from {steamId} refused"
            );
    }

    // A block's event that the game checks for ownership, and a block group on an NPC's grid
    [HarmonyPatch(
        typeof(MyReplicationServer),
        "OnEvent",
        [
            typeof(MyPacketDataBitStreamBase),
            typeof(CallSite),
            typeof(object),
            typeof(IMyNetObject),
            typeof(VRageMath.Vector3D?),
            typeof(EndpointId),
        ]
    )]
    private static class Events
    {
        private static bool Prefix(
            MyPacketDataBitStreamBase data,
            CallSite site,
            object obj,
            EndpointId source
        )
        {
            var name = site.MethodInfo.Name;
            MyEntity refused = null;
            if (obj is MyCubeGrid grid)
            {
                if (
                    name == "OnModifyGroupSuccess"
                    && Plugin.Config.NpcBlocksLocked
                    && grid.BigOwners.Any(owner => Outsider(owner, source.Value))
                )
                    refused = grid;
            }
            else if (
                obj is not MyInventoryBase
                && site.ValidationFlags.HasFlag(ValidationType.Ownership)
                && !Uses.Contains(name)
            )
            {
                var block =
                    obj as MyCubeBlock ?? (obj as MyEntityComponentBase)?.Entity as MyCubeBlock;
                if (Locked(block, source.Value))
                    refused = block;
            }
            if (refused == null)
                return true;
            data.Return();
            Refused(refused, name, source.Value);
            return false;
        }
    }

    // A block's setting: the sender gets the block's own value back, stamped no older than the
    // change, or its client would keep its own
    [HarmonyPatch(typeof(MyPropertySyncStateGroup), "SyncPropertyChanged_Implementation")]
    private static class Properties
    {
        private static bool Prefix(
            MyPropertySyncStateGroup __instance,
            byte propertyIndex,
            double propertyTimestampMs,
            ListReader<SyncBase> ___m_properties,
            List<MyTimeSpan> ___m_propertyTimestamps
        )
        {
            var context = MyEventContext.Current;
            if (
                context.IsLocallyInvoked
                || (__instance.Owner as MyExternalReplicable<MySyncedBlock>)?.Instance
                    is not { } block
                // What the game doesn't check for anyone, like a vending machine's selection
                || propertyIndex >= ___m_properties.Count
                || !___m_properties[propertyIndex].ShouldValidate
                || block is MyProjectorBase table
                    && ReferenceEquals(___m_properties[propertyIndex], OnOff(table))
                || !Locked(block, context.Sender.Value)
            )
                return true;
            var sent = MyTimeSpan.FromMilliseconds(propertyTimestampMs);
            var now = MyMultiplayer.Static.ReplicationLayer.GetSimulationUpdateTime();
            ___m_propertyTimestamps[propertyIndex] = sent > now ? sent : now;
            __instance.MarkDirty();
            Refused(block, ___m_properties[propertyIndex].DebugName, context.Sender.Value);
            return false;
        }
    }
}
