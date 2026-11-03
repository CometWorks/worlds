using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage;

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
internal static class StationCharging
{
    private const float Seconds = 1;
    private static DateTime nextTick;

    // The ships whose fuse tripped, by their grid with the smallest entity id, with the station
    // connectors that held them then
    private static readonly Dictionary<long, HashSet<long>> tripped = new();

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
        {
            tripped.Clear();
            return;
        }

        var held = new Dictionary<long, HashSet<long>>();
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (
                MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid)
                && Plugin.Simulated(grid)
            )
                foreach (var ship in Charge(grid))
                    held[ship.Key] = ship.Value;

        foreach (var key in tripped.Keys.ToList())
            if (!held.TryGetValue(key, out var connectors) || !connectors.Overlaps(tripped[key]))
            {
                tripped.Remove(key);
                Plugin.Log.Info($"Station charging: fuse of ship {key} reset, it let go");
            }
    }

    // Charges the ships docked at the station and returns them with the connectors holding them
    private static IEnumerable<KeyValuePair<long, HashSet<long>>> Charge(MyCubeGrid station)
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

        return ships.Select(s => new KeyValuePair<long, HashSet<long>>(s.Key, s.Value.Connectors));
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
