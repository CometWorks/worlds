using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ServerPlugin;

// Police bots defend the stations (SE-1384). A police officer is a bot (ShipyardBotSteamIds) that
// said "!oct police duty <station>"; it flies a ship named PoliceGridName*. Weapon damage to a grid
// or a character within PoliceAlertRange of a station's center makes the attacker a target of that
// station's police: the grid that owns the weapon, with every grid mechanically connected to it.
// Every PoliceReportSeconds the officers on duty there hear, privately from "Police":
//
//     Target KEY at X Y Z moving VX VY VZ grids ID,ID,... weapons N
//     Clear KEY: WHY
//
// X Y Z and the velocity are the biggest grid's, which the grid list names first. KEY is the
// attacker's grid with the smallest entity id when it was first seen. A target is clear
// once all its grids are beyond PoliceChaseRange of the station, it has no working weapon block
// left, or it is gone. Damage by a police ship, by a station grid (its own turrets) and by hand
// weapons raises no alert.
//
// A police ship of an officer on duty, docked at a station's connector (where they park), gets its
// hydrogen and oxygen tanks filled and its guns and cargo (PoliceCargoAmmo of the volume) filled with the guns' ammo,
// for free, every second it stays there. A later service station is to do this instead.
//
// Officers and targets live in this process: on a cluster each node knows the officers who said
// they are on duty there and the stations it simulates. The bots say it again now and then.
internal static class Police
{
    private const string Author = "Police";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static OctahedronConfig Config => Plugin.Config;
    private static DateTime nextTick,
        nextReport;
    private static MyDamageSystem watched;

    // Officer identity -> the station they are on duty at
    private static readonly Dictionary<long, string> officers = new();

    private sealed class Target
    {
        public string Station;
        public HashSet<long> Grids = new();
    }

    // Attacker key -> target
    private static readonly Dictionary<long, Target> targets = new();

    // Damage seen since the last tick: attacker grid and station. The damage handler may run off
    // the game's thread.
    private static readonly object hitsLock = new();
    private static List<(long Grid, string Station)> hits = new();

    public static string Duty(long identity, string station)
    {
        if (!Config.PoliceEnabled)
            return "The police are off on this server.";
        if (!ShipyardOrders.IsBot(identity))
            return "Only police bots go on duty.";
        if (station.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            officers.Remove(identity);
            return "Off duty.";
        }
        if (!Stations.Find(ref station, out var error))
            return error;
        officers[identity] = station;
        return $"On duty at {station}.";
    }

    public static string Describe()
    {
        var lines = officers
            .Select(o =>
                $"{MySession.Static.Players.TryGetIdentity(o.Key)?.DisplayName ?? o.Key.ToString()}: {o.Value}"
            )
            .Concat(
                targets.Select(t =>
                    $"target {t.Key} at {t.Value.Station}: {t.Value.Grids.Count} grids"
                )
            );
        return string.Join("\n", lines.DefaultIfEmpty("No officers on duty."));
    }

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextTick || MySession.Static?.Ready != true)
            return;
        nextTick = now + Tick;
        if (!Config.PoliceEnabled)
        {
            targets.Clear();
            return;
        }
        Watch();
        foreach (var id in officers.Keys.ToList())
            if (!MySession.Static.Players.IsPlayerOnline(id))
                officers.Remove(id);

        List<(long Grid, string Station)> seen;
        lock (hitsLock)
            (seen, hits) = (hits, new());
        foreach (var (grid, station) in seen)
            AddTarget(grid, station);

        var report = now >= nextReport;
        if (report)
            nextReport = now + TimeSpan.FromSeconds(Config.PoliceReportSeconds);
        foreach (var (key, target) in targets.ToList())
        {
            var why = Follow(target, out var grids);
            if (why != null)
            {
                targets.Remove(key);
                TellOfficers(target.Station, $"Clear {key}: {why}");
                Plugin.Log.Info($"Police: target {key} at {target.Station} clear: {why}");
                continue;
            }
            if (report)
                TellOfficers(target.Station, Report(key, grids));
        }
        Resupply();
    }

    public static void Dispose() => watched = null;

    // ------------------------------------------------------------------ damage

    private static void Watch()
    {
        var system = MyDamageSystem.Static;
        if (system == null || system == watched)
            return;
        watched = system;
        system.RegisterAfterDamageHandler(
            0,
            (object target, MyDamageInformation info) =>
            {
                try
                {
                    OnDamage(target, info);
                }
                catch (Exception e)
                {
                    Plugin.Log.Error("Police: damage handler", e);
                }
            }
        );
    }

    private static bool IsWeaponDamage(MyStringHash type) =>
        type == MyDamageType.Bullet
        || type == MyDamageType.Rocket
        || type == MyDamageType.Explosion
        || type == MyDamageType.Weapon
        || type == MyDamageType.Mine;

    private static void OnDamage(object target, MyDamageInformation info)
    {
        if (
            watched == null
            || !Config.PoliceEnabled
            || !IsWeaponDamage(info.Type)
            || info.AttackerId == 0
        )
            return;
        Vector3D? at = target switch
        {
            MySlimBlock block when block.CubeGrid != null => block.CubeGrid.GridIntegerToWorld(
                block.Position
            ),
            MyEntity entity => entity.PositionComp.GetPosition(),
            _ => null,
        };
        if (at == null || AttackerGrid(info.AttackerId) is not { } attacker)
            return;
        if (target is MySlimBlock hit && hit.CubeGrid == attacker)
            return; // its own ammo going off
        if (IsPolice(attacker) || Stations.StationOf(attacker) != null)
            return;
        var station = StationNear(at.Value, Config.PoliceAlertRange);
        if (station == null)
            return;
        lock (hitsLock)
            hits.Add((attacker.EntityId, station));
    }

    // The grid of the weapon that did the damage: a weapon block's, a missile's launcher's. Hand
    // weapons, characters and collisions give none.
    private static MyCubeGrid AttackerGrid(long attackerId)
    {
        if (!MyEntities.TryGetEntityById(attackerId, out MyEntity entity, allowClosed: true))
            return null;
        if (entity is MyMissile missile)
            MyEntities.TryGetEntityById(missile.LauncherId, out entity, allowClosed: true);
        return entity is MyCubeBlock block ? block.CubeGrid : null;
    }

    private static void AddTarget(long gridId, string station)
    {
        if (!MyEntities.TryGetEntityById(gridId, out MyCubeGrid grid) || grid.Closed)
            return;
        var group = Group(grid).Select(g => g.EntityId).ToList();
        if (targets.Values.Any(t => t.Grids.Overlaps(group)))
            return;
        var key = group.Min();
        targets[key] = new Target { Station = station, Grids = group.ToHashSet() };
        Plugin.Log.Info(
            $"Police: alert at {station}, {grid.DisplayName} ({key}) did weapon damage"
        );
    }

    private static List<MyCubeGrid> Group(MyCubeGrid grid) =>
        MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid).Where(g => !g.Closed).ToList();

    // Null while the police keep after the target, else why they stop
    private static string Follow(Target target, out List<MyCubeGrid> grids)
    {
        grids = new();
        foreach (var id in target.Grids)
            if (MyEntities.TryGetEntityById(id, out MyCubeGrid grid) && !grid.Closed)
                foreach (var g in Group(grid))
                    if (!grids.Contains(g))
                        grids.Add(g);
        target.Grids = grids.Select(g => g.EntityId).ToHashSet();
        if (grids.Count == 0)
            return "gone";
        if (!StationPosition(target.Station, out var center))
            return "station not simulated here";
        var range = Config.PoliceChaseRange;
        if (grids.All(g => Vector3D.DistanceSquared(Center(g), center) > range * range))
            return "beyond the chase range";
        if (Weapons(grids) == 0)
            return "no working weapon";
        return null;
    }

    private static int Weapons(List<MyCubeGrid> grids) =>
        grids.Sum(g =>
            g.GetFatBlocks()
                .Count(b => b is MyUserControllableGun { IsFunctional: true, IsWorking: true })
        );

    private static Vector3D Center(MyCubeGrid grid) => grid.PositionComp.WorldAABB.Center;

    private static string Report(long key, List<MyCubeGrid> grids)
    {
        var main = grids.MaxBy(g => g.BlocksCount);
        var at = Center(main);
        var v = main.Physics?.LinearVelocity ?? Vector3.Zero;
        return string.Format(
            CultureInfo.InvariantCulture,
            "Target {0} at {1:0} {2:0} {3:0} moving {4:0.0} {5:0.0} {6:0.0} grids {7} weapons {8}",
            key,
            at.X,
            at.Y,
            at.Z,
            v.X,
            v.Y,
            v.Z,
            string.Join(",", grids.OrderBy(g => g != main).Select(g => g.EntityId)),
            Weapons(grids)
        );
    }

    private static void TellOfficers(string station, string text)
    {
        foreach (var (officer, at) in officers)
            if (at == station)
                MyVisualScriptLogicProvider.SendChatMessage(text, Author, officer);
    }

    // ------------------------------------------------------------------ stations

    private static IEnumerable<(string Name, MyCubeGrid Grid)> SimulatedStations()
    {
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (
                Amounts.ValidName(station.Name)
                && MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid)
                && Plugin.Simulated(grid)
            )
                yield return (station.Name, grid);
    }

    private static bool StationPosition(string name, out Vector3D center)
    {
        foreach (var (n, grid) in SimulatedStations())
            if (n == name)
            {
                center = Center(grid);
                return true;
            }
        center = default;
        return false;
    }

    private static string StationNear(Vector3D at, double range)
    {
        string best = null;
        var bestD = range * range;
        foreach (var (name, grid) in SimulatedStations())
        {
            var d = Vector3D.DistanceSquared(Center(grid), at);
            if (d <= bestD)
                (best, bestD) = (name, d);
        }
        return best;
    }

    // A police ship of an officer on duty: Resupply fills its tanks free, StationCharging doesn't bill it
    public static bool Resupplied(MyCubeGrid grid) =>
        Config.PoliceEnabled && Named(grid) && grid.BigOwners.Any(officers.ContainsKey);

    // A ship named PoliceGridName* that a bot owns: no alert for its damage, whoever it hits
    private static bool IsPolice(MyCubeGrid grid) =>
        Named(grid) && grid.BigOwners.Any(ShipyardOrders.IsBot);

    private static bool Named(MyCubeGrid grid) =>
        !string.IsNullOrEmpty(Config.PoliceGridName)
        && (grid.DisplayName ?? "").StartsWith(
            Config.PoliceGridName,
            StringComparison.OrdinalIgnoreCase
        );

    // ------------------------------------------------------------------ resupply

    // The police ships of the officers on duty, found again every Rescan
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(10);
    private static DateTime nextScan;
    private static readonly HashSet<long> ships = new();

    private static void Resupply()
    {
        if (officers.Count == 0)
            return;
        var now = DateTime.UtcNow;
        if (now >= nextScan)
        {
            nextScan = now + Rescan;
            ships.Clear();
            foreach (var entity in MyEntities.GetEntities())
                if (
                    entity is MyCubeGrid grid
                    && Named(grid)
                    && grid.BigOwners.Any(officers.ContainsKey)
                )
                    ships.Add(grid.EntityId);
        }
        foreach (var id in ships)
            if (
                MyEntities.TryGetEntityById(id, out MyCubeGrid ship)
                && Plugin.Simulated(ship)
                && Docked(ship)
            )
                Fill(ship);
    }

    // Locked on a station's connector
    private static bool Docked(MyCubeGrid ship) =>
        ship.GetFatBlocks<MyShipConnector>()
            .Any(c =>
                c.Connected && c.Other?.CubeGrid is { } other && Stations.StationOf(other) != null
            );

    private static void Fill(MyCubeGrid ship)
    {
        var filled = new List<string>();
        foreach (var tank in ship.GetFatBlocks<MyGasTank>())
            if (tank.IsFunctional && tank.FilledRatio < 0.999)
            {
                ((IMyGasTank)tank).ChangeFilledRatio(1, true);
                filled.Add(
                    tank.BlockDefinition.StoredGasId.SubtypeName == "Hydrogen" ? "H2" : "O2"
                );
            }

        var ammo = new Dictionary<MyDefinitionId, int>();
        foreach (var gun in ship.GetFatBlocks<MyUserControllableGun>())
        {
            if (gun is not IMyGunObject<MyGunBase> { GunBase: { } gunBase } || !gun.IsFunctional)
                continue;
            var magazine = gunBase.CurrentAmmoMagazineId;
            ammo[magazine] = ammo.GetValueOrDefault(magazine) + 1;
            if (gun.GetInventory() is { } inventory && Top(inventory, magazine, 1.0) > 0)
                filled.Add(magazine.SubtypeName);
        }
        if (ammo.Count > 0 && Config.PoliceCargoAmmo > 0)
        {
            var guns = ammo.Values.Sum();
            foreach (var box in ship.GetFatBlocks<MyCargoContainer>())
            {
                if (box.GetInventory() is not { } inventory)
                    continue;
                foreach (var (magazine, count) in ammo)
                    if (Top(inventory, magazine, Config.PoliceCargoAmmo * count / guns) > 0)
                        filled.Add(magazine.SubtypeName);
            }
        }
        if (filled.Count > 0)
            Plugin.Log.Info(
                $"Police: resupplied {ship.DisplayName} ({ship.EntityId}): {string.Join(", ", filled.Distinct())}"
            );
    }

    // Adds magazines until they take up share of the inventory's volume, or it is full: how many
    private static int Top(MyInventory inventory, MyDefinitionId magazine, double share)
    {
        if (
            !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(magazine, out var item)
            || item.Volume <= 0
        )
            return 0;
        var want = (int)Math.Floor((double)inventory.MaxVolume * share / item.Volume);
        var have = (int)inventory.GetItemAmount(magazine);
        var fits = (int)inventory.ComputeAmountThatFits(magazine);
        var add = Math.Min(want - have, fits);
        if (add <= 0)
            return 0;
        inventory.AddItems(
            add,
            (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine)
        );
        return add;
    }
}
