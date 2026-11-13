using System;
using HarmonyLib;
using Sandbox.Game.Entities;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace ServerPlugin;

// The station safe zones forbid landing gear and magnetic plate locks (SE-974), which kept the
// janitor bots from towing garbage away from inside a zone. A gear owned by a bot
// (ShipyardBotSteamIds) locks in them anyway (SE-1466). Players' gears stay forbidden: the bot
// list goes by the owner's Steam ID, so naming a grid like a janitor's gets a player nothing.
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
            botLocks = enable && Plugin.Config.Enabled && ShipyardOrders.IsBot(__instance.OwnerId);

        private static void Finalizer() => botLocks = false;
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
