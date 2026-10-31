using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ServerPlugin;

// Every economy tick the game refills each NPC trade station grid: fuel, batteries, gas, ice,
// turret ammo, and loot in every empty cargo container and cryo chamber, which it also hides
// from inventory screens. This replaces that refill with one whose parts can be switched off
// and whose loot goes only into containers of a configured name.
// On a cluster the economy tick runs only on the World Authority, which simulates no grids, so
// the game refills no station there. Each node refills the stations it simulates instead.
internal static class StationRefillPatch
{
    private const int IceAmount = 10000;
    private const int NodeCheckSeconds = 5;
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

    private static readonly Action<MyFactionStation, MyFaction> UpdateFactionStation =
        AccessTools.MethodDelegate<Action<MyFactionStation, MyFaction>>(
            AccessTools.Method(typeof(MyFactionStation), "Update")
        );
    private static readonly HashSet<long> nodeStations = new();
    private static DateTime nextNodeCheck;
    private static DateTime nextNodeRefill;

    // Runs the game's refill of the stations this node simulates on a timer of the economy tick's
    // length, on a cluster only. A station that comes to this node is refilled at once, whether
    // it was there at the start or its partition came later, so frequent restarts can't hold the
    // refill back and a new node doesn't wait a whole tick for its first loot.
    public static void UpdateNode()
    {
        var session = MySession.Static;
        var now = DateTime.UtcNow;
        if (
            !PluginCluster.IsClusterProcess
            || now < nextNodeCheck
            || session?.Ready != true
            || !session.Settings.EnableEconomy
        )
            return;
        nextNodeCheck = now.AddSeconds(NodeCheckSeconds);

        var tick = now >= nextNodeRefill;
        if (tick)
            nextNodeRefill = now.AddSeconds(Math.Max(1, session.Settings.EconomyTickInSeconds));

        foreach (var faction in session.Factions)
        foreach (var station in faction.Value.Stations)
        {
            if (station is not MyFactionStation factionStation)
                continue;
            var id = factionStation.StationEntityId;
            if (!MyEntities.TryGetEntityById(id, out MyCubeGrid grid) || !Plugin.Simulated(grid))
            {
                nodeStations.Remove(id);
                continue;
            }
            var arrived = nodeStations.Add(id);
            if (arrived)
                Plugin.Log.Info($"Refilling station {factionStation.Name} as it came to this node");
            if (arrived || tick)
                UpdateFactionStation(factionStation, faction.Value);
        }
    }

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
            if (!config.Enabled)
                return true;
            if (!Plugin.Simulated(grid))
                return false;
            if (!config.StationRefillEnabled)
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
