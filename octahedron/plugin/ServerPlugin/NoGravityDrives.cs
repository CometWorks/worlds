using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;

namespace ServerPlugin;

// Artificial mass and space balls don't react to gravity generators, which is what makes a gravity
// drive. The game's Warfare 3 update brings this as a world setting. Until then a generator keeps these
// blocks out of the list of entities its field pulls, and the server turns them off on a ship with one
// in a field: a client has no patch, and would otherwise fly its own copy of the ship while the server's
// grid sleeps. Only for game version 1.210.*: on any other the patches aren't applied at all, so a game
// update can't break the plugin here. SE-882 removes this file.
internal static class NoGravityDrives
{
    private const int GameMajor = 1;
    private const int GameMinor = 210;
    private const int CheckFrames = 10;
    private const int WakeFrames = 300;

    // Read once: Harmony asks Prepare again for each method it patches
    private static readonly bool Applies = CheckGameVersion();

    private static readonly ConcurrentDictionary<MyFunctionalBlock, byte> Blocks = new();
    private static readonly Dictionary<MyCubeGrid, int> Waking = new();
    private static bool seenLoaded;
    private static int frame;

    private static bool Active =>
        Applies && Plugin.Config is { Enabled: true, NoGravityDrivesEnabled: true };

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

    internal static void Update()
    {
        if (!Active || ++frame % CheckFrames != 0)
            return;
        // A server's plugins load after its world, so these came before the patch that records them
        if (!seenLoaded)
        {
            seenLoaded = true;
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
            foreach (var block in Drives(grid))
                Blocks.TryAdd(block, 0);
        }
        foreach (var block in Blocks.Keys)
        {
            if (!block.InScene)
            {
                Blocks.TryRemove(block, out _);
                continue;
            }
            var grid = block.CubeGrid;
            if (
                block.IsWorking
                && !grid.IsStatic
                && MyGravityProviderSystem
                    .CalculateArtificialGravityInPoint(block.WorldMatrix.Translation)
                    .LengthSquared() > 0
                && Plugin.Simulated(grid)
            )
                TurnOff(grid);
        }
        foreach (var (grid, until) in Waking.ToList())
        {
            if (frame > until || grid.Closed)
                Waking.Remove(grid);
            else
                grid.Physics?.RigidBody?.Activate();
        }
    }

    private static IEnumerable<MyFunctionalBlock> Drives(MyCubeGrid grid) =>
        grid.GetFatBlocks<MyFunctionalBlock>().Where(b => b is IMyVirtualMass);

    private static void TurnOff(MyCubeGrid grid)
    {
        foreach (var block in Drives(grid))
            block.Enabled = false;
        // A sleeping grid sends no states, and those correct what a client's copy did before this
        Waking[grid] = frame + WakeFrames;
        Plugin.Log.Info(
            $"Gravity drive on {grid.DisplayName}: artificial mass and space balls off"
        );
    }

    [HarmonyPatch(typeof(MyGravityGeneratorBase), "phantom_Enter")]
    private static class Field
    {
        private static bool Prepare() => Applies;

        private static bool Prefix(HkRigidBody body) =>
            !(Active && body.GetEntity(0u) is IMyVirtualMass);
    }

    [HarmonyPatch]
    private static class Added
    {
        private static bool Prepare() => Applies;

        private static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                AccessTools.Method(typeof(MyVirtualMass), nameof(MyVirtualMass.OnAddedToScene)),
                AccessTools.Method(typeof(MySpaceBall), nameof(MySpaceBall.OnAddedToScene)),
            };

        private static void Postfix(MyFunctionalBlock __instance) => Blocks.TryAdd(__instance, 0);
    }
}
