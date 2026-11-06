using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// The station shipyards' OmniArc welders weld ShipyardToolSpeed times as fast as their default,
// and their deconstructors take a block apart in the time those welders build it (SE-1180).
// OmniArc's welder works on 2 blocks per update with a speed of 3, its deconstructor on 1 with
// a speed of 2 (OmniArcBeamWelder.sbc, OmniArcBeamDestro.sbc; the filament tools copy them), and
// the game divides a grinder's work by the block's DisassembleRatio, 3 for heavy armor. So a
// deconstructor works on twice the blocks, and on each with 3/2 the speed times that ratio.
//
// The definitions are shared by every tool of a type, so this patches ToolCore where a tool
// welds or grinds: WeldBlocks and WeldBlocksBulk take the world's welder speed from here, and
// GrindBlocks the number of blocks and each block's grinding. ToolCore is a mod, compiled while
// the world loads, so this patches it by name once the session is ready, as ShipyardOwnBlocks
// does.
//
// Only a tool on a station shipyard grid is faster: one named "<station> Shipyard" that OCT's NPC
// owns blocks of (ShipyardZoneWelding.IsShipyard), and that is neither editable nor destructible.
// The world builder sets those two (build_world.py), and only an admin can change them, so a
// player gets no faster tool by building one, on their grid or the shipyard's, by grinding one
// off, or by taking one over: a player's grid is editable and destructible. A player's turn at a
// shipyard makes its welders theirs (ShipyardTurn), and they stay fast: it is still OCT's shipyard.
internal static class ShipyardToolSpeed
{
    private const float GrindSpeedToWeld = 3f / 2f;
    private const int GrindBlocksToWeld = 2;

    private static bool patched;

    private static AccessTools.FieldRef<object, IMyConveyorSorter> blockTool;
    private static FieldInfo rate;

    private static readonly MethodInfo welderSpeed = AccessTools.PropertyGetter(
        typeof(IMySession),
        nameof(IMySession.WelderSpeedMultiplier)
    );
    private static readonly MethodInfo decreaseMountLevel = AccessTools.Method(
        typeof(IMySlimBlock),
        nameof(IMySlimBlock.DecreaseMountLevel)
    );

    public static void Update()
    {
        if (patched || MySession.Static?.Ready != true)
            return;
        patched = true;
        var utils = AccessTools.TypeByName("ToolCore.GridUtils");
        var comp = AccessTools.TypeByName("ToolCore.Comp.ToolComp");
        var definition = AccessTools.TypeByName("ToolCore.Definitions.ToolDefinition");
        // ShipyardOwnBlocks logs a world without ToolCore
        if (utils == null || comp == null || definition == null)
            return;
        try
        {
            blockTool = AccessTools.FieldRefAccess<IMyConveyorSorter>(comp, "BlockTool");
            rate =
                AccessTools.Field(definition, "Rate")
                ?? throw new MissingFieldException(definition.FullName, "Rate");
            var harmony = new Harmony(Plugin.Name);
            var transpiler = new HarmonyMethod(typeof(ShipyardToolSpeed), nameof(Transpiler));
            foreach (var name in new[] { "WeldBlocks", "WeldBlocksBulk", "GrindBlocks" })
                harmony.Patch(
                    AccessTools.Method(utils, name)
                        ?? throw new MissingMethodException(utils.FullName, name),
                    transpiler: transpiler
                );
            Plugin.Log.Info(
                "Shipyard tools: the station shipyards' welders and deconstructors are faster"
            );
        }
        catch (Exception e)
        {
            Plugin.Log.Error(
                "Shipyard tools: ToolCore can't be patched, some work at their default speed",
                e
            );
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> code,
        MethodBase original
    )
    {
        var grinding = original.Name == "GrindBlocks";
        var found = 0;
        foreach (var instruction in code)
        {
            if (grinding && instruction.Calls(decreaseMountLevel))
            {
                // The block, amount, inventory and flag are on the stack
                found++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return CodeInstruction.Call(typeof(ShipyardToolSpeed), nameof(Grind));
                continue;
            }
            yield return instruction;
            string scale;
            if (!grinding && instruction.Calls(welderSpeed))
                scale = nameof(Weld);
            else if (grinding && instruction.LoadsField(rate))
                scale = nameof(Blocks);
            else
                continue;
            found++;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return CodeInstruction.Call(typeof(ShipyardToolSpeed), scale);
        }
        if (found < (grinding ? 2 : 1))
            throw new InvalidOperationException($"{original.Name} isn't as expected");
    }

    private static float Weld(float multiplier, object comp) =>
        Fast(comp) ? multiplier * (float)Plugin.Config.ShipyardToolSpeed : multiplier;

    // A tool without a WorkRate has int.MaxValue
    private static int Blocks(int count, object comp) =>
        Fast(comp) && count <= int.MaxValue / GrindBlocksToWeld ? count * GrindBlocksToWeld : count;

    private static void Grind(
        IMySlimBlock block,
        float amount,
        IMyInventory inventory,
        bool defaultEfficiency,
        object comp
    )
    {
        if (Fast(comp))
        {
            var slim = (MySlimBlock)block;
            amount *=
                (float)Plugin.Config.ShipyardToolSpeed
                * GrindSpeedToWeld
                * (slim.FatBlock?.DisassembleRatio ?? slim.BlockDefinition.DisassembleRatio);
        }
        block.DecreaseMountLevel(amount, inventory, defaultEfficiency);
    }

    private static bool Fast(object comp) =>
        Plugin.Config.Enabled
        && blockTool(comp)
            is MyCubeBlock { CubeGrid: { Editable: false, DestructibleBlocks: false } } tool
        && ShipyardZoneWelding.IsShipyard(tool);
}
