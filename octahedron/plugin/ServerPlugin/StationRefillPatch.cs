using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ServerPlugin;

// Every economy tick the game refills each NPC trade station grid: fuel, batteries, gas, ice,
// turret ammo, and loot in every empty cargo container and cryo chamber, which it also hides
// from inventory screens. This replaces that refill with one whose parts can be switched off
// and whose loot goes only into containers of a configured name.
internal static class StationRefillPatch
{
    private const int IceAmount = 10000;
    private static readonly MyObjectBuilder_Ore Ice =
        MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>("Ice");

    // The generator is internal to the game, so it is reached by name
    private const string GeneratorType = "Sandbox.Game.World.Generator.MyStationResourcesGenerator";
    private static readonly FieldInfo LootTypeField = AccessTools.Field(
        AccessTools.TypeByName(GeneratorType),
        "m_generatedItemsContainerTypeId"
    );
    private static readonly Action<MyCubeGrid, bool> SetGenerated = AccessTools.MethodDelegate<
        Action<MyCubeGrid, bool>
    >(AccessTools.PropertySetter(typeof(MyCubeGrid), nameof(MyCubeGrid.IsGenerated)));
    private static readonly Action<MyGasTank, double> Transfer = AccessTools.MethodDelegate<
        Action<MyGasTank, double>
    >(AccessTools.Method(typeof(MyGasTank), "Transfer"));

    [HarmonyPatch]
    private static class UpdateStation
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                AccessTools.TypeByName(GeneratorType),
                "UpdateStation",
                [typeof(MyCubeGrid)]
            );

        private static bool Prefix(object __instance, MyCubeGrid grid)
        {
            var config = Plugin.Config;
            if (!config.Enabled || !config.StationRefillEnabled)
                return true;

            if (grid.MarkedForClose || grid.Closed)
                return false;

            // Keeps the trash cleaner off the station, as the game does
            SetGenerated(grid, true);
            var loot = config.StationRefillLoot
                ? (MyDefinitionId?)LootTypeField.GetValue(__instance)
                : null;
            foreach (var block in grid.GetFatBlocks())
            {
                if (block.MarkedForClose)
                    continue;
                Refill(block, config, loot);
            }

            return false;
        }
    }

    private static void Refill(MyCubeBlock block, OctahedronConfig config, MyDefinitionId? loot)
    {
        switch (block)
        {
            case MyGasGenerator generator:
                if (config.StationAddIceToGasGenerators)
                    generator.GetInventory().AddItems(IceAmount, Ice);
                break;

            case MyReactor reactor:
                if (config.StationRefuelReactors)
                {
                    var inventory = reactor.GetInventory();
                    var free = inventory.MaxVolume - inventory.CurrentVolume;
                    foreach (var fuel in reactor.BlockDefinition.FuelInfos)
                        inventory.AddItems(
                            (int)((float)free / fuel.FuelDefinition.Volume),
                            fuel.FuelItem
                        );
                }
                break;

            case MyBatteryBlock battery:
                if (config.StationRechargeBatteries)
                    battery.CurrentStoredPower = battery.MaxStoredPower;
                break;

            case MyLargeTurretBase turret:
                if (config.StationReloadTurrets)
                {
                    var inventory = turret.GetInventory();
                    var magazine = turret.GunBase.WeaponProperties.AmmoMagazineDefinition;
                    var count = (int)(
                        (float)(inventory.MaxVolume - inventory.CurrentVolume) / magazine.Volume
                    );
                    inventory.AddItems(
                        count,
                        MyObjectBuilderSerializer.CreateNewObject(
                            turret.GunBase.CurrentAmmoMagazineId
                        )
                    );
                }
                break;

            case MyGasTank tank:
                if (config.StationRefillGasTanks)
                    Transfer(tank, (1.0 - tank.FilledRatio) * tank.Capacity);
                break;

            case MyCargoContainer
            or MyCryoChamber:
                if (block is not MyTerminalBlock terminal || !IsLootContainer(terminal, config))
                    break;
                if (config.StationHideLootContainers)
                    terminal.ShowInInventory = false;
                if (
                    loot.HasValue
                    && block.InventoryCount > 0
                    && block.GetInventory().ItemCount == 0
                )
                {
                    var definition = MyDefinitionManager.Static.GetContainerTypeDefinition(
                        loot.Value
                    );
                    if (definition != null && definition.Items.Length != 0)
                        block.GetInventory().GenerateContent(definition);
                }
                break;
        }
    }

    private static bool IsLootContainer(MyTerminalBlock block, OctahedronConfig config)
    {
        var name = config.StationLootContainerName?.Trim();
        return !string.IsNullOrEmpty(name)
            && string.Equals(
                block.CustomName?.ToString().Trim(),
                name,
                StringComparison.OrdinalIgnoreCase
            );
    }
}
