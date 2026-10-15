using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Network;

namespace ServerPlugin;

// Caps the PCU of a mechanical grid group: the grids joined by rotors, pistons and hinges, not
// by connectors. The game has no such cap. A group grows when a block is placed, a projected
// block is built, a block is welded past its functional level, a top part is added or
// attached, two grids merge, or a blueprint is pasted; each is refused over GroupPcuLimit. Blocks built by nobody or by an NPC, and
// admins who ignore PCU limits, aren't capped.
internal static class GroupPcuLimit
{
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<(long, string), DateTime> lastNotified = new();

    // The PCU the game counts on the grids of the group, both groups if two grids are given
    public static int Pcu(MyCubeGrid grid, MyCubeGrid other = null)
    {
        var pcu = GroupPcu(grid);
        if (other != null && !MyCubeGridGroups.Static.Mechanical.HasSameGroup(grid, other))
            pcu += GroupPcu(other);
        return pcu;
    }

    private static int GroupPcu(MyCubeGrid grid)
    {
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        if (group == null)
            return grid.BlocksPCU;
        var pcu = 0;
        foreach (var node in group.Nodes)
            pcu += node.NodeData.BlocksPCU;
        return pcu;
    }

    // True if adding pcu to the group of grid (joined with the group of other) stays within the
    // cap, or the group doesn't grow. Tells the player when it doesn't fit.
    public static bool Allows(long builtBy, MyCubeGrid grid, int pcu, MyCubeGrid other = null) =>
        (
            pcu <= 0
            && (other == null || MyCubeGridGroups.Static.Mechanical.HasSameGroup(grid, other))
        ) || Within(builtBy, Pcu(grid, other) + pcu);

    // True if a group of this much PCU is allowed, or the cap doesn't apply to builtBy
    private static bool Within(long builtBy, int pcu)
    {
        var limit = Plugin.Config.GroupPcuLimit;
        if (limit <= 0 || pcu <= limit || !Plugin.Config.Enabled || Exempt(builtBy))
            return true;
        Notify(
            builtBy,
            $"Not built: a ship or station may have at most {limit:N0} PCU, this one would have {pcu:N0}."
        );
        return false;
    }

    private static bool Exempt(long identityId)
    {
        var session = MySession.Static;
        if (identityId == 0 || session.Players.IdentityIsNpc(identityId))
            return true;
        var steamId = session.Players.TryGetSteamId(identityId);
        return session.IsUserAdmin(steamId)
            && session.RemoteAdminSettings.TryGetValue(steamId, out var settings)
            && settings.HasFlag(AdminSettingsEnum.IgnorePcu);
    }

    // A tool or a merge block retries every second or so, the player hears the same message
    // every NotifyInterval
    internal static void Notify(long identityId, string message)
    {
        var now = DateTime.UtcNow;
        if (
            lastNotified.TryGetValue((identityId, message), out var last)
            && now - last < NotifyInterval
        )
            return;
        lastNotified[(identityId, message)] = now;
        MyVisualScriptLogicProvider.ShowNotification(message, 5000, "Red", identityId);
        var name = MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName;
        Plugin.Log.Info($"{name} ({identityId}): {message}");
    }

    // Placing blocks by hand, one or an area of them; the game checks the world limits here
    [HarmonyPatch(typeof(MyCubeGrid), "IsWithinWorldLimits")]
    private static class Place
    {
        private static void Postfix(MyCubeGrid __instance, long ownerID, int pcu, ref bool __result)
        {
            if (__result)
                __result = Allows(ownerID, __instance, pcu);
        }
    }

    // A block gets its full PCU, instead of 1, once welded to its functional level
    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.IncreaseMountLevel))]
    private static class Weld
    {
        private static bool Prefix(
            MySlimBlock __instance,
            float welderMountAmount,
            bool testingMode,
            ref bool __result
        )
        {
            var stack = __instance.ComponentStack;
            if (stack.IsFunctional)
                return true;
            var definition = __instance.BlockDefinition;
            var amount = testingMode
                ? welderMountAmount / definition.IntegrityPointsPerSec
                : welderMountAmount;
            if (
                stack.WillFunctionalityRise(amount) != true
                || Allows(
                    __instance.BuiltBy,
                    __instance.CubeGrid,
                    definition.PCU - MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST
                )
            )
                return true;
            __result = false;
            return false;
        }
    }

    // A rotor, piston, hinge or suspension attaching a top part. The game reattaches the
    // tops it already had, after loading for example, and attaches a new top it just added,
    // with the top's id already in the block's connection state; those aren't checked here.
    [HarmonyPatch(
        typeof(MyMechanicalConnectionBlockBase),
        "Attach",
        [typeof(MyAttachableTopBlockBase), typeof(bool)]
    )]
    private static class Attach
    {
        private static bool Prefix(
            MyMechanicalConnectionBlockBase __instance,
            MyAttachableTopBlockBase topBlock,
            ref bool __result
        )
        {
            if (
                Traverse
                    .Create(__instance)
                    .Field("m_connectionState")
                    .Property("Value")
                    .Field("TopBlockId")
                    .GetValue<long?>() == topBlock.EntityId
                || Allows(__instance.BuiltBy, __instance.CubeGrid, 0, topBlock.CubeGrid)
            )
                return true;
            __result = false;
            return false;
        }
    }

    // Two merge blocks locked together merge their grids once the game's limits allow it
    [HarmonyPatch(typeof(MyShipMergeBlock), "IsWithinWorldLimits", MethodType.Getter)]
    private static class Merge
    {
        private static void Postfix(MyShipMergeBlock __instance, ref bool __result)
        {
            var other = __instance.Other;
            if (__result && other != null)
                __result =
                    Allows(__instance.BuiltBy, __instance.CubeGrid, 0, other.CubeGrid)
                    && Allows(other.BuiltBy, __instance.CubeGrid, 0, other.CubeGrid);
        }
    }

    // A rotor, piston, hinge or suspension adding a new top part, before the top grid is added
    [HarmonyPatch]
    private static class NewTop
    {
        // Not the parameterless CanPlaceTop() of the base class
        private static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                typeof(MyMechanicalConnectionBlockBase),
                typeof(MyPistonBase),
                typeof(MyMotorSuspension),
            }.Select(type =>
                AccessTools.DeclaredMethod(
                    type,
                    "CanPlaceTop",
                    [typeof(MyAttachableTopBlockBase), typeof(long)]
                )
            );

        private static void Postfix(
            MyMechanicalConnectionBlockBase __instance,
            MyAttachableTopBlockBase topBlock,
            long builtBy,
            ref bool __result
        )
        {
            if (__result)
                __result = Allows(builtBy, __instance.CubeGrid, topBlock.CubeGrid.BlocksPCU);
        }
    }

    // A player pasting grids from the clipboard (creative tools); a paste the server makes
    // itself, like a starter ship, isn't checked here
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.TryPasteGrid_Implementation))]
    private static class Paste
    {
        private static bool Prefix(MyCubeGrid.MyPasteGridParameters parameters)
        {
            var context = MyEventContext.Current;
            return context.IsLocallyInvoked
                || Within(
                    MySession.Static.Players.TryGetIdentityId(context.Sender.Value),
                    LargestGroupPcu(parameters.Entities)
                );
        }
    }

    // A player pasting a grid's blocks onto an existing grid (creative tools)
    [HarmonyPatch(typeof(MyCubeGrid), "PasteBlocksToGridServer_Implementation")]
    private static class PasteOnto
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            List<MyObjectBuilder_CubeGrid> gridsToMerge
        )
        {
            var context = MyEventContext.Current;
            return context.IsLocallyInvoked
                || Allows(
                    MySession.Static.Players.TryGetIdentityId(context.Sender.Value),
                    __instance,
                    gridsToMerge[0].CalculatePCUs()
                );
        }
    }

    // The PCU of the largest mechanical group among grids about to be pasted: grids joined
    // through a rotor, piston or hinge whose top block is among them
    private static int LargestGroupPcu(List<MyObjectBuilder_CubeGrid> grids)
    {
        var root = new int[grids.Count];
        var gridOfBlock = new Dictionary<long, int>();
        for (var i = 0; i < grids.Count; i++)
        {
            root[i] = i;
            foreach (var block in grids[i].CubeBlocks)
                gridOfBlock[block.EntityId] = i;
        }
        int Find(int i) => root[i] == i ? i : root[i] = Find(root[i]);
        for (var i = 0; i < grids.Count; i++)
            foreach (var block in grids[i].CubeBlocks)
                if (
                    block is MyObjectBuilder_MechanicalConnectionBlock { TopBlockId: long top }
                    && gridOfBlock.TryGetValue(top, out var j)
                )
                    root[Find(i)] = Find(j);

        var pcu = new int[grids.Count];
        for (var i = 0; i < grids.Count; i++)
            pcu[Find(i)] += grids[i].CalculatePCUs();
        return pcu.Length == 0 ? 0 : pcu.Max();
    }
}
