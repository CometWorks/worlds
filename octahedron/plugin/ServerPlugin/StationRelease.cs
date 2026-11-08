using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;

namespace ServerPlugin;

// When the stations' connectors let go of a docked ship (SE-1237). The game let go of every ship 10
// minutes after it docked, whoever was aboard, counting from a frame number of the node that docked
// it. The plugin turns that timer off on the stations' connectors and checks every minute instead:
// - While one of the ship's biggest owners, or a player in a biggest owner's faction (not OCT,
//   which every player is in), is online in the station's safe zone, the ship stays.
// - Otherwise a 10 minute countdown starts, and the ship is let go at its end. One of them coming
//   back stops it, and it starts over the next time they leave.
// - While no connector of the station is free, the ship docked there the longest gets the countdown
//   even with its owners there, and they are told by chat, so no faction can hold every connector
//   of a station. A connector freeing up stops it again.
// The docked time and the countdown are kept in the connector's storage, so they are saved with
// the world and go with the station to another cluster node. Both count simulation time: each node
// adds the frames it simulated since it last checked the connector.
internal static class StationRelease
{
    public static readonly Guid StorageKey = new("3E7B5D21-9C4A-4F86-B1D2-6A0C8E5F7B13");
    private const int FramesPerSecond = 60;
    private static bool claimed;
    private static int nextCheck;

    // The frame each connector was last checked on this node
    private static Dictionary<long, int> seen = new();

    private sealed class Dock
    {
        public long Ship;
        public double Docked;
        public double Left = -1;
        public bool Full;
    }

    public static void Update()
    {
        if (MySession.Static?.Ready != true || !Plugin.Config.StationConnectorRelease)
            return;
        var frame = MySession.Static.GameplayFrameCounter;
        if (frame < nextCheck)
            return;
        nextCheck =
            frame + Math.Max(1, Plugin.Config.StationConnectorCheckSeconds) * FramesPerSecond;
        if (!claimed)
        {
            claimed = true;
            EncounterData.ClaimStorage("OctahedronStationRelease", StorageKey);
        }

        var now = new Dictionary<long, int>();
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (
                MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid)
                && Plugin.Simulated(grid)
            )
                Check(grid, frame, now);
        seen = now;
    }

    private static void Check(MyCubeGrid station, int frame, Dictionary<long, int> now)
    {
        var connectors = station
            .GetFatBlocks<MyShipConnector>()
            .Where(c => MySession.Static.Players.IdentityIsNpc(c.OwnerId))
            .ToList();
        // A zone of a safe zone block is a player's, not the station's
        var zones = MySessionComponentSafeZones
            .SafeZones.Where(z =>
                z.Enabled
                && !z.MarkedForClose
                && z.SafeZoneBlockId == 0
                && connectors.Any(c => z.Contains(c.PositionComp.GetPosition()))
            )
            .ToList();
        var inZone = MySession
            .Static.Players.GetOnlinePlayers()
            .Where(p =>
                p.Character is { IsDead: false } character
                && zones.Any(z => z.Contains(character.PositionComp.GetPosition()))
            )
            .Select(p => p.Identity.IdentityId)
            .ToList();

        var docks = new List<(MyShipConnector Connector, Dock Dock, double Elapsed)>();
        foreach (var connector in connectors)
        {
            if (connector.AutoUnlockTime > 0)
                connector.AutoUnlockTime.Value = 0;
            var dock = Read(connector);
            if (
                !connector.Connected
                || connector.Other?.CubeGrid is not { } ship
                || ship == station
            )
            {
                if (dock != null)
                    connector.Storage.RemoveValue(StorageKey);
                continue;
            }
            double elapsed = seen.TryGetValue(connector.EntityId, out var last)
                ? (double)(frame - last) / FramesPerSecond
                : 0;
            if (dock == null || dock.Ship != ship.EntityId)
                dock = new Dock { Ship = ship.EntityId };
            else
                dock.Docked += elapsed;
            now[connector.EntityId] = frame;
            docks.Add((connector, dock, elapsed));
        }

        // While no connector is free, the ship docked the longest
        var full = !connectors.Any(c => !c.Connected && c.IsWorking);
        var longest = full
            ? docks
                .OrderByDescending(d => d.Dock.Docked)
                .ThenBy(d => d.Connector.EntityId)
                .FirstOrDefault()
                .Dock
            : null;

        var released = new HashSet<long>();
        foreach (var (connector, dock, elapsed) in docks)
        {
            if (released.Contains(dock.Ship))
                continue;
            var ship = connector.Other.CubeGrid;
            var owners = Owners(ship);
            var present = inZone.Any(p =>
                owners.Contains(p) || (OctFaction.Team(p) is { } team && owners.Any(team.IsMember))
            );
            var name = $"{ship.DisplayName} ({ship.EntityId}) at {station.DisplayName}";
            if (present && dock != longest)
            {
                if (dock.Left >= 0)
                    Plugin.Log.Info(
                        $"Station release: countdown of {name} stopped, its owners are there and a connector is free"
                    );
                dock.Left = -1;
            }
            else if (dock.Left < 0)
            {
                dock.Left = Plugin.Config.StationConnectorCountdownSeconds;
                Plugin.Log.Info(
                    $"Station release: countdown of {name} started, {dock.Left:0} s, "
                        + (present ? "the station is full" : "none of its owners is there")
                );
            }
            else if ((dock.Left -= elapsed) <= 0)
            {
                Plugin.Log.Info($"Station release: {name} let go after {dock.Docked:0} s docked");
                Release(station, ship);
                released.Add(dock.Ship);
                continue;
            }
            if (dock == longest && !dock.Full)
                Tell(station, ship, owners, dock.Left);
            dock.Full = dock == longest;
            connector.Storage ??= new MyModStorageComponent();
            connector.Storage[StorageKey] = string.Join(
                " ",
                dock.Ship,
                dock.Docked.ToString("0.###", CultureInfo.InvariantCulture),
                dock.Left.ToString("0.###", CultureInfo.InvariantCulture),
                dock.Full ? 1 : 0
            );
        }
    }

    private static Dock Read(MyShipConnector connector)
    {
        if (connector.Storage?.TryGetValue(StorageKey, out var text) != true)
            return null;
        var f = text.Split(' ');
        return new Dock
        {
            Ship = long.Parse(f[0], CultureInfo.InvariantCulture),
            Docked = double.Parse(f[1], CultureInfo.InvariantCulture),
            Left = double.Parse(f[2], CultureInfo.InvariantCulture),
            Full = f[3] == "1",
        };
    }

    // The biggest owners of the ship's largest grid, of the grids joined by rotors, pistons and hinges
    private static List<long> Owners(MyCubeGrid ship) =>
        MyCubeGridGroups
            .Static.Mechanical.GetGroupNodes(ship)
            .MaxBy(g => g.BlocksCount)
            .BigOwners.Where(o => o != 0)
            .ToList();

    // Every connector of the station holding the ship lets go of it
    private static void Release(MyCubeGrid station, MyCubeGrid ship)
    {
        var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
        foreach (var connector in station.GetFatBlocks<MyShipConnector>())
            if (connector.Connected && grids.Contains(connector.Other?.CubeGrid))
            {
                ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Disconnect();
                connector.Storage?.RemoveValue(StorageKey);
            }
    }

    private static void Tell(MyCubeGrid station, MyCubeGrid ship, List<long> owners, double left)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, left));
        var text =
            $"Every connector of {station.DisplayName} is in use, and {ship.DisplayName} has been docked there the longest: "
            + $"it is let go in {(int)time.TotalMinutes}:{time.Seconds:00} unless a connector frees up first.";
        Plugin.Log.Info($"Station release: told the owners of {ship.EntityId}: {text}");
        foreach (var owner in owners)
            if (MySession.Static.Players.IsPlayerOnline(owner))
                MyVisualScriptLogicProvider.SendChatMessage(text, station.DisplayName, owner);
    }
}
