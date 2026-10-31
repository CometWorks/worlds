using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Definitions.SafeZone;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace StationProbe;

// Test-only server plugin of the station tests (tests/station_tests.py): admin commands that set
// up what a test works with, like docked ships and factions, so the client only has to use it.
public class Probe : IPlugin
{
    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update() => ProbeCommands.Update();

    public void Dispose() { }
}

[CommandRoot("sprobe", "Station probe", "Station test commands")]
public sealed class ProbeCommands : CommandModule
{
    private static readonly List<(
        MyShipConnector Ship,
        MyShipConnector Station,
        int Tries
    )> connecting = [];

    [Command(
        "store",
        "Where the station's Store block is: STORE id x y z STAND x y z",
        "!sprobe store <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Store(string station)
    {
        var store = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyStoreBlock>())
            .FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "StoreBlock");
        if (store == null)
            return $"NOSTORE {station}";
        var m = store.WorldMatrix;
        // In front of the block's screen
        var stand = m.Translation + m.Forward * 2.2;
        return $"STORE {store.EntityId} {V(m.Translation)} STAND {V(stand)}";
    }

    [Command(
        "rep",
        "Sets the caller's reputation with a faction: REP tag value",
        "!sprobe rep <tag> <value>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Rep(string tag, int value)
    {
        var faction = MySession.Static.Factions.TryGetFactionByTag(tag);
        if (faction == null)
            return $"NOREP {tag}";
        MySession.Static.Factions.SetReputationBetweenPlayerAndFaction(
            Context.Caller.IdentityId,
            faction.FactionId,
            value,
            VRage.Game.ModAPI.ReputationChangeReason.Admin
        );
        return $"REP {tag} {value}";
    }

    [Command(
        "safezone",
        "The safe zone blocks' upkeep: SAFEZONE subtype chips minutes, one per block",
        "!sprobe safezone"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string SafeZone() =>
        string.Join(
            " ",
            MyDefinitionManager
                .Static.GetAllDefinitions()
                .OfType<MySafeZoneBlockDefinition>()
                .Select(d =>
                    $"SAFEZONE {d.Id.SubtypeName} {d.SafeZoneUpkeep} {d.SafeZoneUpkeepTimeM}"
                )
        );

    [Command(
        "dock",
        "Builds a ship with a cargo container at a free connector of the station and connects it: SHIP grid-id",
        "!sprobe dock <station> <mine|stranger> [Type/Subtype=amount ...]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Dock(
        string station,
        string owner,
        string items = "Ingot/Iron=1000,Component/SteelPlate=50"
    )
    {
        long ownerId =
            owner == "mine"
                ? Context.Caller.IdentityId
                : MySession.Static.Players.CreateNewIdentity("Dock Stranger").IdentityId;
        var free = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .FirstOrDefault(c =>
                !c.Connected && c.Other == null && connecting.All(x => x.Station != c)
            );
        if (free == null)
            return "NOCONNECTOR";

        // The connector's open face: the side whose next two cells of the station grid are empty
        var stationGrid = free.CubeGrid;
        Vector3I? open = null;
        foreach (var d in Base6Directions.EnumDirections)
        {
            var step = Base6Directions.GetIntVector(free.Orientation.TransformDirection(d));
            if (
                !stationGrid.CubeExists(free.Position + step)
                && !stationGrid.CubeExists(free.Position + 2 * step)
            )
            {
                open = step;
                if (d == Base6Directions.Direction.Forward)
                    break;
            }
        }
        if (open == null)
            return "NOOPENFACE";
        var outward = Vector3D.TransformNormal((Vector3D)open.Value, stationGrid.WorldMatrix);
        outward.Normalize();
        var m = free.WorldMatrix;
        var forward = -outward;
        var up = Math.Abs(Vector3D.Dot(m.Up, outward)) < 0.5 ? m.Up : m.Forward;
        var position = m.Translation + outward * stationGrid.GridSize;
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = owner == "mine" ? "Dock Hauler" : "Stranger Hauler",
            PositionAndOrientation = new MyPositionAndOrientation(
                position,
                (Vector3)forward,
                (Vector3)up
            ),
        };
        grid.CubeBlocks.Add(
            Block<MyObjectBuilder_ShipConnector>("Connector", Vector3I.Zero, ownerId)
        );
        grid.CubeBlocks.Add(
            Block<MyObjectBuilder_CargoContainer>(
                "LargeBlockSmallContainer",
                new Vector3I(0, 0, 1),
                ownerId
            )
        );
        // H2=<fill ratio>: a small hydrogen tank, filled that far
        var h2 = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("H2="));
        if (h2 != null)
        {
            var tank = Block<MyObjectBuilder_OxygenTank>(
                "LargeHydrogenTankSmall",
                new Vector3I(0, 0, 2),
                ownerId
            );
            tank.FilledRatio = float.Parse(h2.Substring(3), CultureInfo.InvariantCulture);
            grid.CubeBlocks.Add(tank);
        }
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        var cargo = entity.GetFatBlocks<MyCargoContainer>().First();
        foreach (var pair in items.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("H2="))
                continue;
            var f = pair.Split('=');
            if (!MyDefinitionId.TryParse("MyObjectBuilder_" + f[0], out var id))
                return $"BADITEM {f[0]}";
            cargo
                .GetInventory()
                .AddItems(
                    (MyFixedPoint)decimal.Parse(f[1], CultureInfo.InvariantCulture),
                    MyObjectBuilderSerializer.CreateNewObject(id)
                );
        }
        connecting.Add((entity.GetFatBlocks<MyShipConnector>().First(), free, 0));
        return $"SHIP {entity.EntityId}";
    }

    [Command(
        "docked",
        "Whether the ship is connected: DOCKED grid-id yes|no",
        "!sprobe docked <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Docked(string gridId)
    {
        if (
            !long.TryParse(gridId, out long id)
            || !MyEntities.TryGetEntityById(id, out MyCubeGrid grid)
        )
            return $"DOCKED {gridId} gone";
        var c = grid.GetFatBlocks<MyShipConnector>().First();
        var status = ((Sandbox.ModAPI.Ingame.IMyShipConnector)c).Status;
        return $"DOCKED {gridId} {(c.Connected ? "yes" : "no")} status {status} at {V(c.PositionComp.GetPosition())}";
    }

    [Command(
        "gas",
        "The hydrogen in the H2 tanks of a grid and its subgrids: GAS grid-id liters tanks",
        "!sprobe gas <grid id|grid name>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Gas(string grid)
    {
        var found = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g => g.EntityId.ToString() == grid || g.DisplayName == grid);
        if (found == null)
            return $"NOGRID {grid}";
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(found);
        var grids = group == null ? [found] : group.Nodes.Select(n => n.NodeData).ToList();
        var tanks = grids
            .SelectMany(g => g.GetFatBlocks<MyGasTank>())
            .Where(t => t.BlockDefinition.Id.SubtypeName.Contains("Hydrogen"))
            .ToList();
        var liters = tanks.Sum(t => t.FilledRatio * t.Capacity);
        return string.Format(
            CultureInfo.InvariantCulture,
            "GAS {0} {1:0} {2}",
            found.EntityId,
            liters,
            tanks.Count
        );
    }

    [Command(
        "pay",
        "Adds money to a faction's account, by its tag: PAID tag balance",
        "!sprobe pay <tag> <SC>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Pay(string tag, long amount)
    {
        long id =
            tag == "me"
                ? Context.Caller.IdentityId
                : MySession.Static.Factions.TryGetFactionByTag(tag)?.FactionId ?? 0;
        if (id == 0 || !MyBankingSystem.ChangeBalance(id, amount))
            return $"NOPAY {tag}";
        MyBankingSystem.Static.TryGetAccountInfo(id, out var account);
        return $"PAID {tag} {account.Balance}";
    }

    [Command(
        "buttons",
        "The first toolbar slot of the station's button panels: BUTTON id name slot0",
        "!sprobe buttons <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Buttons(string station) =>
        string.Join(
            " ",
            StationGrids(station)
                .SelectMany(g =>
                    g.GetFatBlocks<SpaceEngineers.Game.Entities.Blocks.MyButtonPanel>()
                )
                .Select(b =>
                    $"BUTTON {b.EntityId} '{b.CustomName}' {b.Toolbar.GetItemAtIndex(0)?.GetType().Name ?? "empty"}"
                )
        );

    [Command("undock", "Disconnects and deletes the probe's ships", "!sprobe undock")]
    [Permission(MyPromoteLevel.Admin)]
    public string Undock()
    {
        int count = 0;
        foreach (
            var grid in MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(g => g.DisplayName is "Dock Hauler" or "Stranger Hauler")
                .ToList()
        )
        {
            foreach (var c in grid.GetFatBlocks<MyShipConnector>())
                c.TryDisconnect();
            grid.Close();
            count++;
        }
        return $"UNDOCKED {count}";
    }

    [Command(
        "faction",
        "Puts the caller into the faction with the tag at the rank, creating it with an NPC founder; founder makes a faction of the caller's own; none leaves",
        "!sprobe faction <tag> <founder|leader|member|none>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Faction(string tag, string rank)
    {
        var factions = MyAPIGateway.Session.Factions;
        long me = Context.Caller.IdentityId;
        var current = factions.TryGetPlayerFaction(me);
        if (current != null && (rank == "none" || current.Tag != tag || rank == "founder"))
        {
            if (current.FounderId == me)
                factions.RemoveFaction(current.FactionId);
            else
                factions.KickMember(current.FactionId, me);
        }
        if (rank == "none")
            return "FACTION none";
        var faction = factions.TryGetFactionByTag(tag);
        if (rank == "founder")
        {
            if (faction != null)
                return $"FACTION {tag} taken";
            factions.CreateFaction(me, tag, tag + " Station Testers", "", "", MyFactionTypes.None);
            return "FACTION " + tag + " founder";
        }
        if (faction == null)
        {
            long npc = MySession.Static.Players.CreateNewIdentity(tag + " Founder").IdentityId;
            factions.CreateFaction(npc, tag, tag + " Station Testers", "", "", MyFactionTypes.None);
            faction = factions.TryGetFactionByTag(tag);
            if (faction == null)
                return "FACTION " + tag + " not created";
        }
        if (!faction.IsMember(me))
        {
            factions.SendJoinRequest(faction.FactionId, me);
            factions.AcceptJoin(faction.FactionId, me);
        }
        if (rank == "leader" && !faction.IsLeader(me))
            factions.PromoteMember(faction.FactionId, me);
        if (rank == "member" && faction.IsLeader(me))
            factions.DemoteMember(faction.FactionId, me);
        return "FACTION " + tag + " " + rank;
    }

    [Command("whoami", "The caller's faction and rank: ME tag rank", "!sprobe whoami")]
    [Permission(MyPromoteLevel.Admin)]
    public string WhoAmI()
    {
        long me = Context.Caller.IdentityId;
        var f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(me);
        return f == null
            ? "ME none"
            : $"ME {f.Tag} {(f.FounderId == me ? "founder" : f.IsLeader(me) ? "leader" : "member")}";
    }

    // Connectors lock once the new grid's physics is in
    public static void Update()
    {
        for (int i = connecting.Count - 1; i >= 0; i--)
        {
            var (ship, station, tries) = connecting[i];
            if (ship.Closed || ship.Connected || tries > 600)
            {
                connecting.RemoveAt(i);
                continue;
            }
            if (tries % 10 == 0)
                ship.TryConnect();
            connecting[i] = (ship, station, tries + 1);
        }
    }

    private static T Block<T>(string subtype, Vector3I min, long owner)
        where T : MyObjectBuilder_TerminalBlock, new()
    {
        var block = new T
        {
            SubtypeName = subtype,
            Min = min,
            BlockOrientation = new SerializableBlockOrientation(
                Base6Directions.Direction.Forward,
                Base6Directions.Direction.Up
            ),
            Owner = owner,
            BuiltBy = owner,
            ShareMode = MyOwnershipShareModeEnum.None,
        };
        return block;
    }

    private static IEnumerable<MyCubeGrid> StationGrids(string station)
    {
        foreach (var faction in MySession.Static.Factions)
        foreach (var entry in faction.Value.Stations)
            if (
                entry.Name != null
                && entry.Name.StartsWith(station, StringComparison.OrdinalIgnoreCase)
                && MyEntities.TryGetEntityById(entry.StationEntityId, out MyCubeGrid grid)
            )
            {
                var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
                return group == null ? [grid] : group.Nodes.Select(n => n.NodeData).ToList();
            }
        return [];
    }

    private static string V(Vector3D v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", v.X, v.Y, v.Z);
}
