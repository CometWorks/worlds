using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;

namespace ServerPlugin;

// A ship docked at a station's trading connector charges its batteries from the station. The game
// passes no power there: the resource distributor belongs to the grids' Logical group, which a
// trading connection leaves out, and linking it would open the station's conveyors and terminal
// too. So the plugin moves the energy itself, every second: each battery of the docked ship that
// is on and not set to discharge gets up to its input power, and the station's reactors give up
// the uranium for it, its batteries once they run dry.
// Fuse: while the docked ships want more than the station has to spare, the one drawing the most
// stops charging until the connectors it was docked to let go of it. Each cluster node charges at
// the stations it simulates.
// Hydrogen (StationDockHydrogen): the docked ship's H2 tanks that are on are filled whenever they are
// 1 % or more short of full, for what the ice would cost at the station's Store
// (DockHydrogenMath), paid by the ship's owner to the station's faction. Short of money, the tanks
// get what the balance buys. Shipyards are no stations: nothing is refilled there.
internal static class StationCharging
{
    private const float Seconds = 1;
    private static DateTime nextTick;

    // The ships whose fuse tripped, by their grid with the smallest entity id, with the station
    // connectors that held them then
    private static readonly Dictionary<long, HashSet<long>> tripped = new();

    // The ships told they can't pay for hydrogen, until they let go
    private static readonly HashSet<long> broke = new();

    // Fill the H2 tanks only once they lack this share, so a ship burning hydrogen while docked
    // isn't billed every second
    private const double HydrogenShortRatio = 0.01;
    private const string Ice = "Ore/Ice";

    private sealed class Ship
    {
        public List<MyCubeGrid> Grids;
        public readonly HashSet<long> Connectors = new();
        public readonly List<(MyBatteryBlock Battery, float Megawatts)> Batteries = new();
        public float Demand;
    }

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextTick || MySession.Static?.Ready != true)
            return;
        nextTick = now.AddSeconds(Seconds);
        if (!Plugin.Config.StationDockCharging)
            tripped.Clear();
        if (!Plugin.Config.StationDockHydrogen)
            broke.Clear();
        if (!Plugin.Config.StationDockCharging && !Plugin.Config.StationDockHydrogen)
            return;

        var held = new Dictionary<long, HashSet<long>>();
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (
                MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid)
                && Plugin.Simulated(grid)
                && !ShipyardZoneWelding.IsShipyard(grid)
            )
                foreach (var ship in Charge(grid, station, faction.Value.FactionId))
                    held[ship.Key] = ship.Value;

        broke.RemoveWhere(key => !held.ContainsKey(key));

        foreach (var key in tripped.Keys.ToList())
            if (!held.TryGetValue(key, out var connectors) || !connectors.Overlaps(tripped[key]))
            {
                tripped.Remove(key);
                Plugin.Log.Info($"Station charging: fuse of ship {key} reset, it let go");
            }
    }

    // Charges the ships docked at the station, fills their hydrogen, and returns them with the
    // connectors holding them
    private static IEnumerable<KeyValuePair<long, HashSet<long>>> Charge(
        MyCubeGrid station,
        IMyFactionStation entry,
        long factionId
    )
    {
        var ships = new Dictionary<long, Ship>();
        foreach (var connector in station.GetFatBlocks<MyShipConnector>())
        {
            if (
                !connector.Connected
                || !connector.TradingEnabled
                || connector.Other?.CubeGrid is not { } other
                || other == station
                || !MySession.Static.Players.IdentityIsNpc(connector.OwnerId)
            )
                continue;
            var grids = MyCubeGridGroups.Static.Logical.GetGroupNodes(other);
            var key = grids.Min(g => g.EntityId);
            if (!ships.TryGetValue(key, out var ship))
                ships[key] = ship = Demand(grids);
            ship.Connectors.Add(connector.EntityId);
        }
        if (ships.Count == 0)
            return [];

        if (Plugin.Config.StationDockHydrogen)
            foreach (var ship in ships)
                Hydrogen(station, entry, factionId, ship.Key, ship.Value.Grids);
        if (Plugin.Config.StationDockCharging)
            Power(station, ships);
        return ships.Select(s => new KeyValuePair<long, HashSet<long>>(s.Key, s.Value.Connectors));
    }

    private static void Power(MyCubeGrid station, Dictionary<long, Ship> ships)
    {
        var distributor = station.GridSystems.ResourceDistributor;
        var electricity = MyResourceDistributorComponent.ElectricityId;
        var spare =
            distributor.MaxAvailableResourceByType(electricity, station)
            - distributor.TotalRequiredInputByType(electricity, station);

        var charging = ships.Where(s => !tripped.ContainsKey(s.Key)).ToList();
        var total = charging.Sum(s => s.Value.Demand);
        while (total > 0 && total > spare)
        {
            var top = charging.MaxBy(s => s.Value.Demand);
            charging.Remove(top);
            total -= top.Value.Demand;
            Trip(station, top.Key, top.Value, spare);
        }

        float energy = 0;
        foreach (var ship in charging)
        foreach (var (battery, megawatts) in ship.Value.Batteries)
        {
            var mwh = megawatts * Seconds / 3600;
            battery.CurrentStoredPower = Math.Min(
                battery.MaxStoredPower,
                battery.CurrentStoredPower + mwh
            );
            energy += mwh;
        }
        Debit(station, energy);
    }

    // Fills the ship's H2 tanks for the ice it takes at the station's Store price
    private static void Hydrogen(
        MyCubeGrid station,
        IMyFactionStation entry,
        long factionId,
        long key,
        List<MyCubeGrid> grids
    )
    {
        var tanks = grids
            .SelectMany(g => g.GetFatBlocks<MyGasTank>())
            .Where(t =>
                t.IsFunctional
                && t.Enabled
                && t.BlockDefinition is MyGasTankDefinition d
                && d.StoredGasId.SubtypeName == "Hydrogen"
            )
            .ToList();
        var capacity = tanks.Sum(t => (double)t.Capacity);
        var wanted = tanks.Sum(t => (1 - t.FilledRatio) * t.Capacity);
        if (capacity <= 0 || wanted < capacity * HydrogenShortRatio)
            return;

        var main = grids.MaxBy(g => g.BlocksCount);
        var owner = main.BigOwners.FirstOrDefault();
        if (owner == 0 || MySession.Static.Players.IdentityIsNpc(owner))
            return;
        var price = IcePrice(entry);
        var balance = MyBankingSystem.Static.TryGetAccountInfo(owner, out var account)
            ? account.Balance
            : 0;
        var (liters, sc) = DockHydrogenMath.Bill(
            wanted,
            price,
            Plugin.Config.ShipyardHydrogenLitersPerIce,
            balance
        );
        if (sc <= 0 || !MyBankingSystem.ChangeBalance(owner, -sc))
        {
            if (broke.Add(key))
                Tell(
                    station,
                    owner,
                    price <= 0
                        ? $"{station.DisplayName} has no ice for sale: no hydrogen for {main.DisplayName}."
                        : $"{main.DisplayName} needs {wanted:N0} L of hydrogen for {Sc(Bill(wanted, price))} "
                            + $"({price:N0} SC per kg of ice), you have {Sc(balance)}."
                );
            return;
        }
        if (!MyBankingSystem.ChangeBalance(factionId, sc))
            Plugin.Log.Error(
                $"Station charging: {sc} SC taken from {owner} for hydrogen but not paid to faction {factionId}"
            );

        var left = liters;
        foreach (var tank in tanks)
        {
            var give = Math.Min(left, (1 - tank.FilledRatio) * tank.Capacity);
            if (give <= 0)
                continue;
            ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(
                Math.Min(1, tank.FilledRatio + give / tank.Capacity),
                true
            );
            left -= give;
        }

        var text =
            $"{main.DisplayName} got {liters:N0} L of hydrogen for {Sc(sc)} ({price:N0} SC per kg of ice)"
            + (
                liters < wanted - 0.5
                    ? $", all you could pay for of the {wanted:N0} L it needed."
                    : "."
            );
        Plugin.Log.Info($"Station charging: hydrogen at {station.DisplayName} for {owner}: {text}");
        if (liters < wanted - 0.5)
            broke.Add(key);
        Tell(station, owner, text);
    }

    private static long Bill(double liters, long price) =>
        DockHydrogenMath
            .Bill(liters, price, Plugin.Config.ShipyardHydrogenLitersPerIce, long.MaxValue)
            .Sc;

    // What OCT sells a kg of ice for at the station's Store, in SC; 0 when it has none for sale
    private static long IcePrice(IMyFactionStation entry)
    {
        if (entry is not MyFactionStation station || !Stations.TryItemId(Ice, out var id))
            return 0;
        return station
                .StoreItems.FirstOrDefault(i =>
                    i.StoreItemType == StoreItemTypes.Offer
                    && i.Amount > 0
                    && i.Item.HasValue
                    && (MyDefinitionId)i.Item.Value == id
                )
                ?.PricePerUnit
            ?? 0;
    }

    private static string Sc(long amount) => $"{amount:N0} SC";

    private static void Tell(MyCubeGrid station, long owner, string text)
    {
        if (MySession.Static.Players.IsPlayerOnline(owner))
            MyVisualScriptLogicProvider.SendChatMessage(text, station.DisplayName, owner);
    }

    private static Ship Demand(List<MyCubeGrid> grids)
    {
        var ship = new Ship { Grids = grids };
        foreach (var battery in grids.SelectMany(g => g.GetFatBlocks<MyBatteryBlock>()))
        {
            if (
                !battery.IsFunctional
                || !battery.Enabled
                || battery.ChargeMode == ChargeMode.Discharge
            )
                continue;
            var room = battery.MaxStoredPower - battery.CurrentStoredPower;
            if (room <= 0)
                continue;
            var megawatts = Math.Min(
                battery.BlockDefinition.RequiredPowerInput,
                room * 3600 / Seconds
            );
            ship.Batteries.Add((battery, megawatts));
            ship.Demand += megawatts;
        }
        return ship;
    }

    private static void Trip(MyCubeGrid station, long key, Ship ship, float spare)
    {
        tripped[key] = ship.Connectors;
        var main = ship.Grids.MaxBy(g => g.BlocksCount);
        var text =
            $"{main.DisplayName} wants {ship.Demand:0.##} MW, more than the {Math.Max(0, spare):0.##} MW "
            + $"{station.DisplayName} has to spare: the station's fuse cut its charging. Undock and dock again to charge.";
        Plugin.Log.Info($"Station charging: fuse tripped for ship {key}: {text}");
        var owner = main.BigOwners.FirstOrDefault();
        if (owner != 0 && MySession.Static.Players.IsPlayerOnline(owner))
            MyVisualScriptLogicProvider.SendChatMessage(text, station.DisplayName, owner);
    }

    // Takes the energy from the station: uranium from its working reactors at their own rate, the
    // rest from its batteries
    private static void Debit(MyCubeGrid station, float mwh)
    {
        foreach (var reactor in station.GetFatBlocks<MyReactor>())
        {
            if (mwh <= 0 || !reactor.IsWorking)
                continue;
            var definition = reactor.BlockDefinition;
            var fuel = definition.FuelInfos[0];
            var perMwh = fuel.ConsumptionPerSecond_Items * 3600 / definition.MaxPowerOutput;
            var inventory = reactor.GetInventory();
            var take = MyFixedPoint.Min(
                inventory.GetItemAmount(fuel.FuelId),
                (MyFixedPoint)(mwh * perMwh)
            );
            if (take <= 0)
                continue;
            inventory.RemoveItemsOfType(take, fuel.FuelId);
            mwh -= (float)take / perMwh;
        }
        foreach (var battery in station.GetFatBlocks<MyBatteryBlock>())
        {
            if (mwh <= 0)
                return;
            var take = Math.Min(mwh, battery.CurrentStoredPower);
            battery.CurrentStoredPower -= take;
            mwh -= take;
        }
    }
}
