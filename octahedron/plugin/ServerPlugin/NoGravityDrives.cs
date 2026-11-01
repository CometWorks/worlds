using System;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;

namespace ServerPlugin;

// Artificial mass and space balls don't react to gravity generators, which is what makes a gravity
// drive. The game's Warfare 3 update brings this as a world setting. Until then a generator keeps these
// blocks out of the list of entities its field pulls, and characters, loose objects and the rest stay
// on that list. Only for game version 1.210.*: on any other the patch isn't applied at all, so a game
// update can't break the plugin here. SE-882 removes this file.
[HarmonyPatch(typeof(MyGravityGeneratorBase), "phantom_Enter")]
internal static class NoGravityDrives
{
    private const int GameMajor = 1;
    private const int GameMinor = 210;

    // Read once: Harmony asks Prepare again for each method it patches
    private static readonly bool Applies = CheckGameVersion();

    private static bool Prepare() => Applies;

    // OCTAHEDRON_GAME_VERSION is for tests of the gate
    private static bool CheckGameVersion()
    {
        var version = Environment.GetEnvironmentVariable("OCTAHEDRON_GAME_VERSION") is { } forced
            ? Version.Parse(forced)
            : (Version)MyFinalBuildConstants.APP_VERSION;
        var applies = version.Major == GameMajor && version.Minor == GameMinor;
        Plugin.Log.Info(
            applies
                ? $"Gravity drive patch applied on game version {version}"
                : $"Gravity drive patch not applied on game version {version}, it is for {GameMajor}.{GameMinor}.*"
        );
        return applies;
    }

    private static bool Prefix(HkRigidBody body) =>
        !(
            Plugin.Config is { Enabled: true, NoGravityDrivesEnabled: true }
            && body.GetEntity(0u) is IMyVirtualMass
        );
}
