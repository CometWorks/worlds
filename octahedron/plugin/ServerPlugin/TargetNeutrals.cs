using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Interfaces;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// New weapon blocks start with Target Neutrals off, so that ships and stations don't shoot everyone:
// turrets and custom turret controllers, modded ones too. Searchlights don't shoot and keep theirs.
// A built or pasted block is cleared on the object builder it is created from, so a client building
// it from the same builder gets the same setting. A ship made from a prefab is cleared when it is
// made for a player. A grid loaded from a save or moved between cluster nodes passes none of these,
// so a player who turns the setting on keeps it. Ships NPCs own keep their design.
internal static class TargetNeutrals
{
    internal static void Clear(MyObjectBuilder_CubeBlock block)
    {
        if (!Plugin.Config.Enabled || !Plugin.Config.ClearTargetNeutralsEnabled)
            return;
        var flags = block switch
        {
            MyObjectBuilder_TurretBase turret => turret.Flags ??= DefinitionFlags(turret),
            MyObjectBuilder_TurretControlBlock controller => controller.Flags,
            _ => null,
        };
        if (flags != null)
            flags.TargetNeutrals = false;
    }

    // A whole grid that has just become a player's
    internal static void Clear(MyCubeGrid grid)
    {
        if (!Plugin.Config.Enabled || !Plugin.Config.ClearTargetNeutralsEnabled)
            return;
        foreach (var block in grid.GetFatBlocks())
            if (block is MyLargeTurretBase or MyTurretControlBlock)
                ((IMyTargetingReceiver)block).TargetNeutrals = false;
    }

    private static void Clear(List<MyObjectBuilder_CubeGrid> grids)
    {
        foreach (var grid in grids)
        foreach (var block in grid.CubeBlocks)
            Clear(block);
    }

    // A turret without flags, as the game creates one placed by hand, takes them from its definition
    private static MyObjectBuilder_TargetingFlags DefinitionFlags(MyObjectBuilder_TurretBase turret)
    {
        if (
            !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                turret.GetId(),
                out var definition
            ) || definition is not MyLargeTurretBaseDefinition { EnabledTargetingOptions: var on }
        )
            return null;
        return new MyObjectBuilder_TargetingFlags
        {
            TargetMeteors = (on & MyTurretTargetingOptions.Asteroids) != 0,
            TargetMissiles = (on & MyTurretTargetingOptions.Missiles) != 0,
            TargetCharacters = (on & MyTurretTargetingOptions.Players) != 0,
            TargetSmallGrids = (on & MyTurretTargetingOptions.SmallShips) != 0,
            TargetLargeGrids = (on & MyTurretTargetingOptions.LargeShips) != 0,
            TargetStations = (on & MyTurretTargetingOptions.Stations) != 0,
            TargetFriends = (on & MyTurretTargetingOptions.Friends) != 0,
            TargetEnemies = (on & MyTurretTargetingOptions.Enemies) != 0,
        };
    }

    // Every block built one at a time passes here: placed by hand or as the first block of a new
    // grid, and welded from a projection, by any tool
    [HarmonyPatch]
    private static class Build
    {
        // The game's only build component, internal to its assembly
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                "SpaceEngineers.Game.Entities.MySpaceBuildComponent:BeforeCreateBlock"
            );

        private static void Prefix(MyObjectBuilder_CubeBlock ob) => Clear(ob);
    }

    // Pasting grids from the clipboard (creative tools)
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.TryPasteGrid_Implementation))]
    private static class Paste
    {
        private static void Prefix(MyCubeGrid.MyPasteGridParameters parameters) =>
            Clear(parameters.Entities);
    }

    // Pasting a grid's blocks onto an existing grid (creative tools)
    [HarmonyPatch(typeof(MyCubeGrid), "PasteBlocksToGridServer_Implementation")]
    private static class PasteOnto
    {
        private static void Prefix(List<MyObjectBuilder_CubeGrid> gridsToMerge) =>
            Clear(gridsToMerge);
    }

    // A prefab ship made for a player: every ship a store block sells and every respawn ship is
    // created here with its new owner. This runs on a worker thread and the grids aren't in the
    // world yet, the callbacks run on the game's thread right after they are added.
    [HarmonyPatch(typeof(MyPrefabManager), "CreateGridsFromPrefab")]
    private static class PrefabShip
    {
        private static void Postfix(
            List<MyCubeGrid> results,
            SpawningOptions spawningOptions,
            long ownerId,
            Stack<Action> callbacks
        )
        {
            if (ownerId == 0 || spawningOptions.HasFlag(SpawningOptions.SetOwnerNobody))
                return;
            callbacks.Push(() =>
            {
                if (MySession.Static.Players.IdentityIsNpc(ownerId))
                    return;
                foreach (var grid in results)
                    Clear(grid);
            });
        }
    }
}
