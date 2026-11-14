using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
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
// Gas (StationDockHydrogen): the docked ship's H2 and O2 tanks that are on are filled whenever the
// tanks of either gas are 1 % or more short of full (a bot's service ship: under 90 %), for what the ice would cost at the station's
// Store (DockGasMath). A gas generator makes both gases from the same ice, at its own liters per kg
// of each, so the bill is the ice of the gas needing more. The ship's owner pays the station's
// faction. Short of money, the tanks get what the balance buys. Shipyards are no stations: nothing is refilled there.
internal static class StationCharging
{
    private const float Seconds = 1;
    private static DateTime nextTick;

    // The ships whose fuse tripped, by their grid with the smallest entity id, with the station
    // connectors that held them then
    private static readonly Dictionary<long, HashSet<long>> tripped = new();

    // The ships told they can't pay for hydrogen, until they let go
    private static readonly HashSet<long> broke = new();

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

    // Charges the ships docked at the station, fills their H2 and O2, and returns them with the
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
                Gas(station, entry, factionId, ship.Key, ship.Value.Grids);
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

    // Fills the ship's H2 and O2 tanks for the ice they take at the station's Store price, billed once
    private static void Gas(
        MyCubeGrid station,
        IMyFactionStation entry,
        long factionId,
        long key,
        List<MyCubeGrid> grids
    )
    {
        if (grids.Any(Police.Resupplied))
            return;
        var tanks = grids
            .SelectMany(g => g.GetFatBlocks<MyGasTank>())
            .Where(t => t.IsFunctional && t.Enabled)
            .ToList();
        var main = grids.MaxBy(g => g.BlocksCount);
        var owner = main.BigOwners.FirstOrDefault();
        if (owner == 0 || MySession.Static.Players.IdentityIsNpc(owner))
            return;
        // Filled only once the tanks of a gas lack 1 %, a bot's service ship's under 90 %
        // (DockGasMath.Due), so a ship burning gas while docked isn't billed every few seconds
        var service = ShipyardOrders.IsBot(owner);
        var h2 = Tanks(tanks, Hydrogen, service);
        var o2 = Tanks(tanks, Oxygen, service);
        if (!h2.Due && !o2.Due)
            return;
        var price = IcePrice(entry);
        var balance = MyBankingSystem.Static.TryGetAccountInfo(owner, out var account)
            ? account.Balance
            : 0;
        var (h2PerIce, o2PerIce) = LitersPerIce();
        var (h2Liters, o2Liters, sc) = DockGasMath.Bill(
            h2.Wanted,
            o2.Wanted,
            h2PerIce,
            o2PerIce,
            price,
            balance
        );
        var wanted = Gases(h2.Wanted, o2.Wanted);
        if (sc <= 0 || !MyBankingSystem.ChangeBalance(owner, -sc))
        {
            if (broke.Add(key))
                Tell(
                    station,
                    owner,
                    price <= 0
                        ? $"{station.DisplayName} has no ice for sale: no {Gases(h2.Wanted, o2.Wanted, false)} for {main.DisplayName}."
                        : $"{main.DisplayName} needs {wanted} for "
                            + $"{Sc(DockGasMath.Bill(h2.Wanted, o2.Wanted, h2PerIce, o2PerIce, price, long.MaxValue).Sc)} "
                            + $"({PerTonne(price)} of ice), you have {Sc(balance)}."
                );
            return;
        }
        if (!MyBankingSystem.ChangeBalance(factionId, sc))
            Plugin.Log.Error(
                $"Station charging: {sc} SC taken from {owner} for gas but not paid to faction {factionId}"
            );

        Fill(h2.Tanks, h2Liters);
        Fill(o2.Tanks, o2Liters);

        var partial = h2Liters < h2.Wanted - 0.5 || o2Liters < o2.Wanted - 0.5;
        var text =
            $"{main.DisplayName} got {Gases(h2Liters, o2Liters)} for {Sc(sc)} ({PerTonne(price)} of ice)"
            + (partial ? $", all you could pay for of the {wanted} it needed." : ".");
        Plugin.Log.Info($"Station charging: gas at {station.DisplayName} for {owner}: {text}");
        if (partial)
            broke.Add(key);
        Tell(station, owner, text);
    }

    private const string Hydrogen = "Hydrogen";
    private const string Oxygen = "Oxygen";

    // The tanks of the gas, the liters they lack, and whether their refill is due
    private static (List<MyGasTank> Tanks, double Wanted, bool Due) Tanks(
        List<MyGasTank> all,
        string gas,
        bool service
    )
    {
        var tanks = all.Where(t =>
                t.BlockDefinition is MyGasTankDefinition d && d.StoredGasId.SubtypeName == gas
            )
            .ToList();
        var capacity = tanks.Sum(t => (double)t.Capacity);
        var wanted = tanks.Sum(t => (1 - t.FilledRatio) * t.Capacity);
        return (tanks, wanted, DockGasMath.Due(capacity - wanted, capacity, service));
    }

    private static void Fill(List<MyGasTank> tanks, double liters)
    {
        foreach (var tank in tanks)
        {
            var give = Math.Min(liters, (1 - tank.FilledRatio) * tank.Capacity);
            if (give <= 0)
                continue;
            ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(
                Math.Min(1, tank.FilledRatio + give / tank.Capacity),
                true
            );
            liters -= give;
        }
    }

    private static string Gases(double h2, double o2, bool liters = true)
    {
        var parts = new List<string>();
        if (h2 >= 0.5)
            parts.Add(liters ? $"{h2:N0} L of hydrogen" : "hydrogen");
        if (o2 >= 0.5)
            parts.Add(liters ? $"{o2:N0} L of oxygen" : "oxygen");
        return string.Join(" and ", parts);
    }

    // Liters of H2 and O2 per kg of ice, as the large O2/H2 generator makes them; the shipyard's
    // ShipyardHydrogenLitersPerIce and ShipyardOxygenLitersPerIce without that definition
    private static (double H2, double O2) LitersPerIce()
    {
        double h2 = Plugin.Config.ShipyardHydrogenLitersPerIce,
            o2 = Plugin.Config.ShipyardOxygenLitersPerIce;
        if (
            MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                new MyDefinitionId(typeof(MyObjectBuilder_OxygenGenerator), ""),
                out var block
            ) && block is MyOxygenGeneratorDefinition { ProducedGases: { } gases }
        )
            foreach (var gas in gases)
                if (gas.IceToGasRatio <= 0)
                    continue;
                else if (gas.Id.SubtypeName == Hydrogen)
                    h2 = gas.IceToGasRatio;
                else if (gas.Id.SubtypeName == Oxygen)
                    o2 = gas.IceToGasRatio;
        return (h2, o2);
    }

    private static string PerTonne(double price) => $"{price * 1000:N0} SC per t";

    // What OCT sells a kg of ice for at the station's Store, in SC: the lowest of its offers, a row of
    // lots (MarketLots) counting per kg; 0 when it has none for sale
    private static double IcePrice(IMyFactionStation entry)
    {
        if (entry is not MyFactionStation station || !Stations.TryItemId(Ice, out var id))
            return 0;
        return station
            .StoreItems.Where(i =>
                i.StoreItemType == StoreItemTypes.Offer
                && i.Amount > 0
                && i.Item.HasValue
                && (MyDefinitionId)i.Item.Value == id
            )
            .Select(i => i.PricePerUnit / (double)Market.LotOf(station.Name, i))
            .DefaultIfEmpty(0)
            .Min();
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
