using System;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace ServerPlugin;

// The station safe zones forbid landing gear and magnetic plate locks (SE-974), which kept the
// janitor bots from towing garbage away from inside a zone. A gear on a bot's ship locks in them
// anyway (SE-1466): one whose owner, or, as gears have no computer and so no owner, whose grid's
// owners are all bots (ShipyardBotSteamIds). Players' gears stay forbidden: the bot list goes by
// Steam ID, so naming a grid like a janitor's gets a player nothing.
//
// The server checks a lock in AttachRequest, asking the zones about the gear's grid box without
// naming the gear. A client's lock request and the server's own autolock both go through it, so
// the prefix there marks the gear and the box check's postfix lets the lock past for it.
internal static class BotGearLocks
{
    // A bot's gear asks to lock, on this thread
    [ThreadStatic]
    private static bool botLocks;

    [HarmonyPatch(typeof(MyLandingGear), "AttachRequest")]
    private static class Request
    {
        private static void Prefix(MyLandingGear __instance, bool enable) =>
            botLocks = enable && Plugin.Config.Enabled && IsBots(__instance);

        private static void Finalizer() => botLocks = false;
    }

    internal static bool IsBots(MyLandingGear gear)
    {
        if (gear.OwnerId != 0)
            return ShipyardOrders.IsBot(gear.OwnerId);
        var owners = gear.CubeGrid.BigOwners;
        return owners.Count > 0 && owners.All(ShipyardOrders.IsBot);
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
            if (!__result && botLocks && action == MySafeZoneAction.LandingGearLock)
                __result = true;
        }
    }
}
