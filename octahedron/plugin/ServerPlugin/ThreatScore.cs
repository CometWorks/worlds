using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game;
using VRageMath;

namespace ServerPlugin;

// MES's threat score: the sum over every player-owned grid within range of a point, each
// valued by its functional blocks, power output, block count and size (MES
// Entities/EntityEvaluator.cs, GridTargetValue, and Spawning/SpawnConditions.cs,
// GetThreatLevel)
internal static class ThreatScore
{
    public static float Around(Vector3D position, double range)
    {
        var sphere = new BoundingSphereD(position, range);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var total = 0f;
        foreach (var entity in entities)
        {
            if (
                entity is MyCubeGrid grid
                && grid.Physics != null
                && Vector3D.Distance(grid.PositionComp.GetPosition(), position) <= range
                && PlayerOwned(grid)
            )
                total += Grid(grid);
        }
        entities.Clear();
        return total;
    }

    private static bool PlayerOwned(MyCubeGrid grid) =>
        grid.BigOwners.Count > 0 && !MySession.Static.Players.IdentityIsNpc(grid.BigOwners[0]);

    internal static float Grid(MyCubeGrid grid)
    {
        if (grid.BlocksCount <= 1)
            return 0;
        float score = 0;
        foreach (var block in grid.GetFatBlocks())
        {
            if (block is not MyTerminalBlock { IsFunctional: true } terminal)
                continue;
            score += Block(terminal);
        }
        var output = 0f;
        foreach (var producer in grid.GetFatBlocks<MyFunctionalBlock>())
            if (producer is IMyPowerProducer power && producer.IsWorking)
                output += power.MaxOutput;
        score += output / 10;
        score += grid.BlocksCount / 100;
        var box = grid.PositionComp.WorldAABB;
        score += (float)Vector3D.Distance(box.Min, box.Max) / 4;
        if (grid.IsStatic)
            score *= 0.75f;
        score *= grid.GridSizeEnum == MyCubeSize.Large ? 2.5f : 0.5f;
        return score * 0.7f;
    }

    // A block may count in several lists, as in MES's GridEntity
    private static float Block(MyTerminalBlock block)
    {
        var modded = block.BlockDefinition.Context?.IsBaseGame == false;
        float score = 0;
        void Add(float value, float modMultiplier = 2, bool inventory = false)
        {
            var v = value * (modded ? modMultiplier : 1);
            if (inventory && block.HasInventory)
            {
                var inv = block.GetInventory();
                if (inv.MaxVolume > 0)
                    v *= (float)inv.CurrentVolume / (float)inv.MaxVolume + 1;
            }
            score += v;
        }

        if (block is IMyRadioAntenna)
            Add(4);
        if (block is IMyBeacon)
            Add(3);
        if (block is IMyCargoContainer)
            Add(0.5f, inventory: true);
        if (block is IMyShipController)
            Add(0.5f);
        if (block is IMyGravityGeneratorBase or IMyVirtualMass)
            Add(2, 4, true);
        if (block is IMyJumpDrive)
            Add(10);
        if (block is IMyMechanicalConnectionBlock)
            Add(1);
        if (
            block is IMyMedicalRoom
            || block.BlockDefinition.Id.TypeId == typeof(MyObjectBuilder_SurvivalKit)
        )
            Add(10);
        if (block is IMyProductionBlock)
            Add(2, inventory: true);
        if (block is IMyPowerProducer)
            Add(0.5f, inventory: true);
        if (block is IMyThrust)
            Add(1);
        if (block is IMyShipToolBase)
            Add(2, inventory: true);
        if (block is IMyLargeTurretBase)
            Add(7.5f, 4, true);
        else if (block is IMyUserControllableGun)
            Add(5, 4, true);
        return score;
    }
}
