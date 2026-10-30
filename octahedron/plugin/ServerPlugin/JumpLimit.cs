using System;
using System.Globalization;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace ServerPlugin;

// Jump drives obey the world edge (WorldEdge.cs): a jump whose target lies beyond the braking
// radius is shortened to where its line crosses that sphere. A ship already beyond it may jump
// sideways or inward, but never farther out than it is; a jump straight outward from there is
// refused. The server picks the jump target, so this works on a single server and on every
// cluster node alike.
internal static class JumpLimit
{
    private static readonly Func<MyGridJumpDriveSystem, MyCubeGrid> GridOf =
        AccessTools.MethodDelegate<Func<MyGridJumpDriveSystem, MyCubeGrid>>(
            AccessTools.PropertyGetter(typeof(MyGridJumpDriveSystem), "Grid")
        );

    // The target moved inside the limit, unchanged when the jump stays inside
    internal static Vector3D Clamp(Vector3D start, Vector3D target, OctahedronConfig config)
    {
        var p = start - config.WorldEdgeCenter;
        var d = target - start;
        var t = JumpLimitMath.Fraction(
            p.LengthSquared(),
            Vector3D.Dot(p, d),
            d.LengthSquared(),
            config.WorldEdgeBrakingRadius
        );
        return t >= 1 ? target : start + d * t;
    }

    private static bool Active => Plugin.Config is { Enabled: true, WorldEdgeEnabled: true };

    // The player's jump request: the shortened target goes to every client with the jump
    [HarmonyPatch(typeof(MyGridJumpDriveSystem), "OnRequestJumpFromClient")]
    private static class Request
    {
        private static void Prefix(
            MyGridJumpDriveSystem __instance,
            ref Vector3D jumpTarget,
            long userId
        )
        {
            if (!Active)
                return;
            var config = Plugin.Config;
            var grid = GridOf(__instance);
            var start = grid.WorldMatrix.Translation;
            var clamped = Clamp(start, jumpTarget, config);
            if (clamped == jumpTarget)
                return;

            var asked = Vector3D.Distance(start, jumpTarget);
            var left = Vector3D.Distance(start, clamped);
            var limit = Km(config.WorldEdgeBrakingRadius);
            string text;
            // The game drops jumps this short without a word
            if (left < __instance.GetMinJumpDistance(userId) - 200)
                text =
                    $"Jump refused: it would take {grid.DisplayName} farther out than the {limit} world limit.";
            else
                text =
                    $"Jump shortened from {Km(asked)} to {Km(left)}: the target is beyond the {limit} world limit.";
            Plugin.Log.Info(
                $"Jump limit: {grid.DisplayName} ({grid.EntityId}) of {userId}, {Km(asked)} asked, {Km(left)} allowed"
            );
            MyVisualScriptLogicProvider.SendChatMessage(text, "Jump drive", userId);
            jumpTarget = clamped;
        }
    }

    // Where the ship lands: covers jumps started by scripts and mods, which skip the request
    [HarmonyPatch(typeof(MyGridJumpDriveSystem), "FindSuitableJumpLocation")]
    private static class Location
    {
        private static void Prefix(MyGridJumpDriveSystem __instance, ref Vector3D desiredLocation)
        {
            if (Active)
                desiredLocation = Clamp(
                    GridOf(__instance).WorldMatrix.Translation,
                    desiredLocation,
                    Plugin.Config
                );
        }

        // The game moves the location off what is there, which can push it back over the limit.
        // No location, as when the game finds none: the jump doesn't happen.
        private static void Postfix(
            MyGridJumpDriveSystem __instance,
            Vector3D desiredLocation,
            ref Vector3D? __result
        )
        {
            if (!Active || __result is not { } location || location == desiredLocation)
                return;
            var start = GridOf(__instance).WorldMatrix.Translation;
            if (Clamp(start, location, Plugin.Config) != location)
                __result = null;
        }
    }

    private static string Km(double meters) =>
        (meters / 1000).ToString("N1", CultureInfo.InvariantCulture) + " km";
}
