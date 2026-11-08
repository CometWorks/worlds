using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.Definitions.SafeZone;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
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
        "!sprobe dock <station> <mine|stranger|mate> [Type/Subtype=amount,...,AT=x;y;z]"
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
        // mate: a stranger in the caller's faction
        if (owner == "mate")
        {
            var faction = MySession.Static.Factions.GetPlayerFaction(Context.Caller.IdentityId);
            if (faction == null)
                return "NOFACTION";
            MyAPIGateway.Session.Factions.SendJoinRequest(faction.FactionId, ownerId);
            MyAPIGateway.Session.Factions.AcceptJoin(faction.FactionId, ownerId);
        }
        // AT=x,y,z: the station connector at that cell, else the first free one
        var at = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("AT="));
        var cell = at == null ? null : at.Substring(3).Replace(';', ',');
        var free = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .FirstOrDefault(c =>
                !c.Connected
                && c.Other == null
                && connecting.All(x => x.Station != c)
                && (cell == null || $"{c.Min.X},{c.Min.Y},{c.Min.Z}" == cell)
            );
        if (free == null)
            return "NOCONNECTOR";
        return Hauler(free, ownerId, owner == "mine" ? "Dock Hauler" : "Stranger Hauler", items);
    }

    // A ship with a connector and a cargo container at the free connector, connecting
    private string Hauler(MyShipConnector free, long ownerId, string name, string items)
    {
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
            DisplayName = name,
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
            // Stockpiling, it takes hydrogen from any tank or generator it reaches too. Laid along
            // the ship, its port at its base faces the container, so conveyors reach it.
            if (items.Contains("STOCKPILE"))
            {
                tank.IsStockpiling = true;
                tank.BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Up,
                    Base6Directions.Direction.Backward
                );
            }
            grid.CubeBlocks.Add(tank);
        }
        // O2=<fill ratio>: a large grid's small oxygen tank, filled that far
        var o2 = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("O2="));
        if (o2 != null)
        {
            var tank = Block<MyObjectBuilder_OxygenTank>("", new Vector3I(-1, 0, 1), ownerId);
            tank.FilledRatio = float.Parse(o2.Substring(3), CultureInfo.InvariantCulture);
            grid.CubeBlocks.Add(tank);
        }
        // BAT=<MWh>: a battery holding that much, the ship's only power. BATS=<n>: n of them in a row.
        var bat = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("BAT="));
        var bats = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("BATS="));
        for (int i = 0; bat != null && i < (bats == null ? 1 : int.Parse(bats.Substring(5))); i++)
        {
            var battery = Block<MyObjectBuilder_BatteryBlock>(
                "LargeBlockBatteryBlock",
                new Vector3I(1, 0, 1 + i),
                ownerId
            );
            battery.CurrentStoredPower = float.Parse(
                bat.Substring(4),
                CultureInfo.InvariantCulture
            );
            grid.CubeBlocks.Add(battery);
        }
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        var cargo = entity.GetFatBlocks<MyCargoContainer>().First();
        foreach (var pair in items.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (
                pair.StartsWith("H2=")
                || pair.StartsWith("O2=")
                || pair.StartsWith("BAT=")
                || pair.StartsWith("BATS=")
                || pair.StartsWith("AT=")
                || pair == "STOCKPILE"
            )
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

    private static MyCubeGrid Yard(string station) =>
        MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .First(g =>
                g.DisplayName.StartsWith(station + " ", StringComparison.OrdinalIgnoreCase)
                && g.DisplayName.EndsWith(" Shipyard")
            );

    private static IEnumerable<MyCubeBlock> Beams(MyCubeGrid yard) =>
        yard.GetFatBlocks<MyCubeBlock>()
            .Where(b =>
                b.BlockDefinition.Id.SubtypeName is "OmniArcBeam" or "OctahedronFilamentWelder"
            );

    private static IEnumerable<MyCubeBlock> Deconstructors(MyCubeGrid yard) =>
        yard.GetFatBlocks<MyCubeBlock>()
            .Where(b =>
                b.BlockDefinition.Id.SubtypeName
                    is "OmniArcDestro"
                        or "OctahedronFilamentDeconstructor"
            );

    // ToolCore's switch; false without ToolCore
    private static bool Shooting(MyCubeBlock block)
    {
        var terminal = (IMyTerminalBlock)block;
        return terminal.GetProperty("ToolCore_Shoot") != null
            && terminal.GetValueBool("ToolCore_Shoot");
    }

    [Command(
        "yardship",
        "Builds a ship with a cargo container at a free connector of the station's shipyard and connects it: SHIP grid-id",
        "!kprobe yardship <station> [Type/Subtype=amount ...]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardShip(string station, string items = "Component/SteelPlate=2000")
    {
        var free = Yard(station)
            .GetFatBlocks<MyShipConnector>()
            .FirstOrDefault(c => !c.Connected && connecting.All(x => x.Station != c));
        return free == null
            ? "NOCONNECTOR"
            : Hauler(free, Context.Caller.IdentityId, "Dock Hauler", items);
    }

    [Command(
        "yarddock",
        "Builds a ship 150 m long along the shipyard's wall at a free connector of it and connects it: a powered one or, with pod, one without power or a cockpit: SHIP grid-id",
        "!sprobe yarddock <station> <name> [pod]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardDock(string station, string name, string kind = "")
    {
        var yard = Yard(station);
        var free = yard.GetFatBlocks<MyShipConnector>()
            .FirstOrDefault(c => !c.Connected && connecting.All(x => x.Station != c));
        if (free == null)
            return "NOCONNECTOR";
        long me = Context.Caller.IdentityId;
        var outward = free.WorldMatrix.Forward;
        var blocks = new List<MyObjectBuilder_CubeBlock>
        {
            Block<MyObjectBuilder_ShipConnector>("Connector", Vector3I.Zero, me),
            Block<MyObjectBuilder_CargoContainer>(
                "LargeBlockSmallContainer",
                new Vector3I(0, 0, 1),
                me
            ),
        };
        blocks.AddRange(Row(new Vector3I(0, 0, 2)));
        if (kind != "pod")
            blocks.AddRange(Power(new Vector3I(0, 0, 3), me));
        var entity = (MyCubeGrid)
            MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(
                Ship(
                    name,
                    free.WorldMatrix.Translation + outward * yard.GridSize,
                    -outward,
                    yard.WorldMatrix.Forward,
                    blocks
                )
            );
        connecting.Add((entity.GetFatBlocks<MyShipConnector>().First(), free, 0));
        return $"SHIP {entity.EntityId}";
    }

    [Command(
        "yardat",
        "Builds a ship at a cell of the shipyard's grid, along the shipyard: a cockpit there and a battery one cell toward its +Z. With turned, across: the battery toward -X. With pod: a row of armor 150 m long and a container, no power or cockpit: SHIP grid-id",
        "!sprobe yardat <station> <name> <x,y,z> [turned|pod]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardAt(string station, string name, string cell, string kind = "")
    {
        var yard = Yard(station);
        var c = cell.Split(',').Select(int.Parse).ToArray();
        var m = yard.WorldMatrix;
        var position = Vector3D.Transform(new Vector3D(c[0], c[1], c[2]) * yard.GridSize, m);
        long me = Context.Caller.IdentityId;
        MyObjectBuilder_CubeGrid grid;
        if (kind == "pod")
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>
            {
                Block<MyObjectBuilder_CargoContainer>(
                    "LargeBlockSmallContainer",
                    Vector3I.Zero,
                    me
                ),
            };
            blocks.AddRange(Row(new Vector3I(0, 0, 1)));
            grid = Ship(name, position, m.Right, m.Forward, blocks);
        }
        else
            grid = Ship(
                name,
                position,
                kind == "turned" ? m.Right : m.Forward,
                m.Up,
                Power(Vector3I.Zero, me)
            );
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        return $"SHIP {entity.EntityId}";
    }

    [Command(
        "yardspace",
        "The shipyard's build space in meters and how many of its connectors and conveyors are in it: SPACE width height length connectors=count in=count conveyors=count in=count",
        "!sprobe yardspace <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardSpace(string station)
    {
        var yard = Yard(station);
        var space = (MyOrientedBoundingBoxD)
            AppDomain
                .CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("ServerPlugin.ShipyardOrders"))
                .First(t => t != null)
                .GetMethod("FreeSpace", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, [yard]);
        var connectors = yard.GetFatBlocks<MyShipConnector>().Cast<MyCubeBlock>().ToList();
        var conveyors = yard.GetFatBlocks<MyCubeBlock>()
            .Where(b => b is MyConveyor or MyConveyorConnector)
            .ToList();
        int In(List<MyCubeBlock> blocks) =>
            blocks.Count(b =>
            {
                var position = b.PositionComp.GetPosition();
                return space.Contains(ref position);
            });
        var size = space.HalfExtent * 2;
        return string.Format(
            CultureInfo.InvariantCulture,
            "SPACE {0} {1} {2} connectors={3} in={4} conveyors={5} in={6}",
            size.X,
            size.Y,
            size.Z,
            connectors.Count,
            In(connectors),
            conveyors.Count,
            In(conveyors)
        );
    }

    private static MyObjectBuilder_CubeGrid Ship(
        string name,
        Vector3D position,
        Vector3D forward,
        Vector3D up,
        List<MyObjectBuilder_CubeBlock> blocks
    ) =>
        new()
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = name,
            PositionAndOrientation = new MyPositionAndOrientation(
                position,
                (Vector3)forward,
                (Vector3)up
            ),
            CubeBlocks = blocks,
        };

    // A cockpit and, a cell toward the grid's +Z, a charged battery
    private static List<MyObjectBuilder_CubeBlock> Power(Vector3I at, long owner)
    {
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            at + new Vector3I(0, 0, 1),
            owner
        );
        battery.CurrentStoredPower = 3;
        return [Block<MyObjectBuilder_Cockpit>("LargeBlockCockpit", at, owner), battery];
    }

    // Armor around the cell along the grid's Y axis, 150 m in all
    private static IEnumerable<MyObjectBuilder_CubeBlock> Row(Vector3I around) =>
        Enumerable
            .Range(-30, 61)
            .Select(y => new MyObjectBuilder_CubeBlock
            {
                SubtypeName = "LargeBlockArmorBlock",
                Min = around + new Vector3I(0, y, 0),
                BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Forward,
                    Base6Directions.Direction.Up
                ),
            });

    private static IEnumerable<MyInventory> YardInventories(MyCubeGrid yard) =>
        yard.GetFatBlocks<MyCargoContainer>()
            .Cast<MyCubeBlock>()
            .Concat(Beams(yard))
            .Concat(yard.GetFatBlocks<MyShipConnector>())
            .Select(b => b.GetInventory());

    [Command(
        "yardload",
        "Puts items into the shipyard's cargo containers, as a player would: LOADED amount",
        "!kprobe yardload <station> <Type/Subtype> <amount>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardLoad(string station, string item, string amount)
    {
        if (!MyDefinitionId.TryParse("MyObjectBuilder_" + item, out var id))
            return $"BADITEM {item}";
        var left = (MyFixedPoint)decimal.Parse(amount, CultureInfo.InvariantCulture);
        foreach (var cargo in Yard(station).GetFatBlocks<MyCargoContainer>())
        {
            var inventory = cargo.GetInventory();
            var fits = MyFixedPoint.Min(left, inventory.ComputeAmountThatFits(id));
            if (fits <= 0)
                continue;
            inventory.AddItems(fits, MyObjectBuilderSerializer.CreateNewObject(id));
            left -= fits;
        }
        return $"LOADED {amount} left {left}";
    }

    [Command(
        "yardtake",
        "Empties the inventories of the shipyard's containers, welders and connectors, as a player moving it all to their ship: TOOK items",
        "!kprobe yardtake <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardTake(string station)
    {
        var count = 0;
        foreach (var inventory in YardInventories(Yard(station)))
            for (var i = inventory.GetItems().Count - 1; i >= 0; i--)
            {
                inventory.RemoveItemsAt(i);
                count++;
            }
        return $"TOOK {count}";
    }

    [Command(
        "yardstock",
        "What the shipyard's containers, welders and connectors hold: STOCK item=amount ... ROOM m3",
        "!kprobe yardstock <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardStock(string station)
    {
        var yard = Yard(station);
        var stock = YardInventories(yard)
            .SelectMany(i => i.GetItems())
            .GroupBy(i => i.Content.GetObjectId().ToString().Replace("MyObjectBuilder_", ""))
            .Select(g =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{g.Key}={g.Sum(i => (double)i.Amount):0.###}"
                )
            );
        var room = yard.GetFatBlocks<MyCargoContainer>()
            .Sum(c => (double)(c.GetInventory().MaxVolume - c.GetInventory().CurrentVolume));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"STOCK {string.Join(" ", stock)} ROOM {room:0.###}"
        );
    }

    [Command(
        "yardoff",
        "Switches the shipyard's connectors off: OFF count",
        "!kprobe yardoff <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardOff(string station)
    {
        var connectors = Yard(station).GetFatBlocks<MyShipConnector>().ToList();
        foreach (var connector in connectors)
            connector.Enabled = false;
        return $"OFF {connectors.Count}";
    }

    [Command(
        "yardon",
        "Switches the shipyard's connectors and welders on, as a world from before they start off has them: ON count",
        "!kprobe yardon <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardOn(string station)
    {
        var yard = Yard(station);
        var blocks = yard.GetFatBlocks<MyShipConnector>()
            .Cast<MyCubeBlock>()
            .Concat(Beams(yard))
            .Cast<MyFunctionalBlock>()
            .ToList();
        foreach (var block in blocks)
            block.Enabled = true;
        return $"ON {blocks.Count}";
    }

    [Command("nodlc", "The server forgets the caller's DLCs: NODLC steam-id", "!sprobe nodlc")]
    [Permission(MyPromoteLevel.Admin)]
    public string NoDlc()
    {
        var owned =
            (Dictionary<ulong, HashSet<uint>>)
                typeof(MySessionComponentDLC)
                    .GetField(
                        "m_clientAvailableDLCs",
                        BindingFlags.Instance | BindingFlags.NonPublic
                    )
                    .GetValue(MySession.Static.GetComponent<MySessionComponentDLC>());
        var steamId = MySession.Static.Players.TryGetSteamId(Context.Caller.IdentityId);
        owned[steamId] = [];
        return $"NODLC {steamId}";
    }

    [Command(
        "dlc",
        "How many of the game's DLCs the server says a player owns: DLC owned total",
        "!sprobe dlc <player name>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Dlc(string player)
    {
        var steamId = MySession.Static.Players.TryGetSteamId(
            MySession
                .Static.Players.GetAllIdentities()
                .First(i => i.DisplayName == player)
                .IdentityId
        );
        var dlcs = MySession.Static.GetComponent<MySessionComponentDLC>();
        var names = MyDLCs.DLCs.Values.Select(d => d.Name).ToList();
        return $"DLC {names.Count(n => dlcs.HasDLC(n, steamId))} {names.Count}";
    }

    [Command(
        "removegrid",
        "Removes the grids of this name: REMOVED count",
        "!kprobe removegrid <name with _ for spaces>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string RemoveGrid(string name)
    {
        name = name.Replace('_', ' ');
        var grids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => g.DisplayName == name)
            .ToList();
        foreach (var grid in grids)
            grid.Close();
        return $"REMOVED {grids.Count}";
    }

    [Command(
        "movegrid",
        "Moves the grids of this name along their up by this many meters: MOVED count",
        "!kprobe movegrid <name with _ for spaces> <meters>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string MoveGrid(string name, double meters)
    {
        name = name.Replace('_', ' ');
        var grids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => g.DisplayName == name)
            .ToList();
        foreach (var grid in grids)
        {
            var matrix = grid.WorldMatrix;
            matrix.Translation += matrix.Up * meters;
            grid.Teleport(matrix);
        }
        return $"MOVED {grids.Count}";
    }

    [Command(
        "yardblocker",
        "Builds a powered ship of the caller's with a cockpit in the middle of the shipyard, which the sweep leaves alone: BLOCKER grid-id",
        "!kprobe yardblocker <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardBlocker(string station)
    {
        var yard = Yard(station);
        var beams = Beams(yard).ToList();
        var center = Vector3D.Zero;
        foreach (var beam in beams)
            center += beam.PositionComp.GetPosition();
        center /= beams.Count;
        long me = Context.Caller.IdentityId;
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            new Vector3I(0, 0, 1),
            me
        );
        battery.CurrentStoredPower = 3;
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = "Blocker",
            PositionAndOrientation = new MyPositionAndOrientation(
                center,
                (Vector3)yard.WorldMatrix.Forward,
                (Vector3)yard.WorldMatrix.Up
            ),
            CubeBlocks =
            [
                Block<MyObjectBuilder_Cockpit>("LargeBlockCockpit", Vector3I.Zero, me),
                battery,
            ],
        };
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        return $"BLOCKER {entity.EntityId}";
    }

    [Command(
        "spotship",
        "Builds a powered ship of the caller's with a cockpit at a point: SPOTSHIP entity id",
        "!kprobe spotship <name with _ for spaces> <x> <y> <z>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string SpotShip(string name, double x, double y, double z)
    {
        long me = Context.Caller.IdentityId;
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            new Vector3I(0, 0, 1),
            me
        );
        battery.CurrentStoredPower = 3;
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = name.Replace('_', ' '),
            PositionAndOrientation = new MyPositionAndOrientation(
                new Vector3D(x, y, z),
                Vector3.Forward,
                Vector3.Up
            ),
            CubeBlocks =
            [
                Block<MyObjectBuilder_Cockpit>("LargeBlockCockpit", Vector3I.Zero, me),
                battery,
            ],
        };
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        return $"SPOTSHIP {entity.EntityId}";
    }

    [Command(
        "sphere",
        "The bounding sphere of a grid on this node and the grids whose spheres meet it: "
            + "SPHERE x y z radius meets=a,b, or NOGRID",
        "!kprobe sphere <name with _ for spaces>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Sphere(string name)
    {
        name = name.Replace('_', ' ');
        var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList();
        var grid = grids.FirstOrDefault(g => g.DisplayName == name);
        if (grid == null)
            return "NOGRID";
        var sphere = grid.PositionComp.WorldVolume;
        var meets = grids.Where(g => g != grid && g.PositionComp.WorldVolume.Intersects(sphere));
        var culture = CultureInfo.InvariantCulture;
        return string.Format(
            culture,
            "SPHERE {0:F2} {1:F2} {2:F2} {3:F2} meets={4}",
            sphere.Center.X,
            sphere.Center.Y,
            sphere.Center.Z,
            sphere.Radius,
            string.Join(",", meets.Select(g => g.DisplayName))
        );
    }

    [Command(
        "yardwreck",
        "Builds a wreck by the shipyard: 3 armor blocks, one at half its build, and a cargo container with the items: WRECK grid-id",
        "!kprobe yardwreck <station> [Type/Subtype=amount,...] [in: in the build space | over: with more stone than the shipyard's containers hold | whole: no half block]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardWreck(
        string station,
        string items = "Ingot/Gold=100,Component/Motor=10",
        string where = ""
    )
    {
        var yard = Yard(station);
        var position =
            yard.PositionComp.WorldAABB.Center
            + (
                where == "in"
                    ? Vector3D.Zero
                    : yard.WorldMatrix.Up * (yard.PositionComp.LocalAABB.HalfExtents.Y + 30)
            );
        MyObjectBuilder_CubeBlock Armor(int z, float percent) =>
            new()
            {
                SubtypeName = "LargeBlockArmorBlock",
                Min = new Vector3I(0, 0, z),
                BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Forward,
                    Base6Directions.Direction.Up
                ),
                IntegrityPercent = percent,
                BuildPercent = percent,
            };
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = "Wreck",
            PositionAndOrientation = new MyPositionAndOrientation(
                position,
                (Vector3)yard.WorldMatrix.Forward,
                (Vector3)yard.WorldMatrix.Up
            ),
            CubeBlocks =
            [
                Block<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer", Vector3I.Zero, 0),
                Armor(1, 1),
                Armor(2, 1),
                Armor(3, where == "whole" ? 1 : 0.5f),
            ],
        };
        // Just more stone than the shipyard has room for: a sale past 1,000,000 SC gives the player
        // an achievement, and a client with no Steam crashes on that
        var room = yard.GetFatBlocks<MyCargoContainer>()
            .Sum(c => (double)(c.GetInventory().MaxVolume - c.GetInventory().CurrentVolume));
        if (where == "over")
            for (
                var i = 0;
                i < room / (421.875 * MySession.Static.BlocksInventorySizeMultiplier) + 1;
                i++
            )
                grid.CubeBlocks.Add(
                    Block<MyObjectBuilder_CargoContainer>(
                        "LargeBlockLargeContainer",
                        new Vector3I(3, 0, i * 3),
                        0
                    )
                );
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        var containers = entity.GetFatBlocks<MyCargoContainer>().ToList();
        var stone = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Stone");
        var left = (MyFixedPoint)(
            (room + 5) / MyDefinitionManager.Static.GetPhysicalItemDefinition(stone).Volume
        );
        foreach (
            var large in containers.Where(c =>
                c.BlockDefinition.Id.SubtypeName == "LargeBlockLargeContainer"
            )
        )
        {
            var amount = MyFixedPoint.Min(left, large.GetInventory().ComputeAmountThatFits(stone));
            large.GetInventory().AddItems(amount, MyObjectBuilderSerializer.CreateNewObject(stone));
            left -= amount;
        }
        var cargo = containers
            .First(c => c.BlockDefinition.Id.SubtypeName == "LargeBlockSmallContainer")
            .GetInventory();
        foreach (var pair in items.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = pair.Split('=');
            if (!MyDefinitionId.TryParse("MyObjectBuilder_" + f[0], out var id))
                return $"BADITEM {f[0]}";
            cargo.AddItems(
                (MyFixedPoint)decimal.Parse(f[1], CultureInfo.InvariantCulture),
                MyObjectBuilderSerializer.CreateNewObject(id)
            );
        }
        return $"WRECK {entity.EntityId}";
    }

    [Command(
        "yardtow",
        "A wreck of an NPC faction by the shipyard, held by a tug of the caller's as by a connector, and the PCU limit a tow is judged by",
        "!kprobe yardtow <station> <pcu limit>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardTow(string station, int limit)
    {
        var answer = YardWreck(station);
        if (!answer.StartsWith("WRECK ", StringComparison.Ordinal))
            return answer;
        var grid = (MyCubeGrid)MyEntities.GetEntityById(long.Parse(answer.Substring(6)));
        var npc = MySession
            .Static.Factions.Select(f => f.Value)
            .Where(f => f.Tag != "OCT" && f.Tag != "WRECKS" && f.IsEveryoneNpc())
            .SelectMany(f => f.Members.Keys)
            .First();
        grid.ChangeGridOwnership(npc, MyOwnershipShareModeEnum.None);
        var tug = (MyCubeGrid)
            MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(
                new MyObjectBuilder_CubeGrid
                {
                    EntityId = 0,
                    GridSizeEnum = MyCubeSize.Large,
                    IsStatic = false,
                    PersistentFlags = MyPersistentEntityFlags2.InScene,
                    DisplayName = "Tug",
                    PositionAndOrientation = new MyPositionAndOrientation(
                        grid.PositionComp.GetPosition() + grid.WorldMatrix.Left * 30,
                        (Vector3)grid.WorldMatrix.Forward,
                        (Vector3)grid.WorldMatrix.Up
                    ),
                    CubeBlocks =
                    [
                        Block<MyObjectBuilder_CargoContainer>(
                            "LargeBlockSmallContainer",
                            Vector3I.Zero,
                            0
                        ),
                    ],
                }
            );
        tug.ChangeGridOwnership(Context.Caller.IdentityId, MyOwnershipShareModeEnum.Faction);
        MyCubeGrid.CreateGridGroupLink(GridLinkTypeEnum.Physical, tug.EntityId, tug, grid);
        AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ServerPlugin.Contracts"))
            .First(t => t != null)
            .GetField("PcuLimitForTests", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, limit);
        return answer;
    }

    [Command(
        "yardtarget",
        "Builds the caller a ship to repair in the middle of the station's shipyard, or shifted along it by the given meters: a projector, a battery, an armor block at 30% and two more, or the given number, only in the projection: TARGET grid-id",
        "!kprobe yardtarget <station> [projected blocks] [shift m]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardTarget(string station, int projected = 2, double shift = 0)
    {
        var yard = Yard(station);
        var beams = Beams(yard).ToList();
        var center = Vector3D.Zero;
        foreach (var beam in beams)
            center += beam.PositionComp.GetPosition();
        center /= beams.Count;
        // Along the shipyard: the axis its welders spread the farthest on
        var along = new[] { yard.WorldMatrix.Forward, yard.WorldMatrix.Right, yard.WorldMatrix.Up }
            .OrderByDescending(axis =>
                beams.Max(b => Math.Abs(Vector3D.Dot(b.PositionComp.GetPosition() - center, axis)))
            )
            .First();
        center += along * shift;
        long me = Context.Caller.IdentityId;
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            new Vector3I(0, 0, 1),
            me
        );
        battery.CurrentStoredPower = 3;
        MyObjectBuilder_CubeBlock Armor(int z, float percent) =>
            new()
            {
                SubtypeName = "LargeBlockArmorBlock",
                Min = new Vector3I(0, 0, z),
                BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Forward,
                    Base6Directions.Direction.Up
                ),
                BuiltBy = me,
                IntegrityPercent = percent,
                BuildPercent = percent,
            };
        var projector = Block<MyObjectBuilder_Projector>("LargeProjector", Vector3I.Zero, me);
        projector.KeepProjection = true;
        projector.ShareMode = MyOwnershipShareModeEnum.Faction;
        var plan = new MyObjectBuilder_CubeGrid
        {
            GridSizeEnum = MyCubeSize.Large,
            DisplayName = "Repair Target",
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            PositionAndOrientation = new MyPositionAndOrientation(
                Vector3D.Zero,
                Vector3.Forward,
                Vector3.Up
            ),
            CubeBlocks =
            [
                (MyObjectBuilder_CubeBlock)projector.Clone(),
                (MyObjectBuilder_CubeBlock)battery.Clone(),
                Armor(2, 1),
            ],
        };
        for (var i = 0; i < projected; i++)
        {
            var armor = Armor(3 + i / 8, 1);
            armor.Min = new Vector3I(i % 8, 0, 3 + i / 8);
            plan.CubeBlocks.Add(armor);
        }
        foreach (var block in plan.CubeBlocks)
            block.EntityId = MyEntityIdentifier.AllocateId();
        projector.ProjectedGrids = [plan];
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = true,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = "Repair Target",
            PositionAndOrientation = new MyPositionAndOrientation(
                center,
                (Vector3)yard.WorldMatrix.Forward,
                (Vector3)yard.WorldMatrix.Up
            ),
            CubeBlocks = [projector, battery, Armor(2, 0.3f)],
        };
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        return $"TARGET {entity.EntityId}";
    }

    [Command(
        "yardprinter",
        "Builds the caller a printer near the station's shipyard, at the given meters from the shipyard grid along its right, up and back: a projector, a battery and an armor block, with batteries to build in the projection (SE-1122): TARGET grid-id",
        "!kprobe yardprinter <station> <right m> <up m> <back m> [projected batteries]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardPrinter(
        string station,
        double right,
        double up,
        double back,
        int batteries = 2
    )
    {
        var yard = Yard(station);
        var m = yard.WorldMatrix;
        var at = m.Translation + m.Right * right + m.Up * up + m.Backward * back;
        long me = Context.Caller.IdentityId;
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            new Vector3I(0, 0, 1),
            me
        );
        battery.CurrentStoredPower = 3;
        var armor = new MyObjectBuilder_CubeBlock
        {
            SubtypeName = "LargeBlockArmorBlock",
            Min = new Vector3I(0, 0, 2),
            BlockOrientation = new SerializableBlockOrientation(
                Base6Directions.Direction.Forward,
                Base6Directions.Direction.Up
            ),
            BuiltBy = me,
        };
        var projector = Block<MyObjectBuilder_Projector>("LargeProjector", Vector3I.Zero, me);
        projector.KeepProjection = true;
        var plan = new MyObjectBuilder_CubeGrid
        {
            GridSizeEnum = MyCubeSize.Large,
            DisplayName = "Printer",
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            PositionAndOrientation = new MyPositionAndOrientation(
                Vector3D.Zero,
                Vector3.Forward,
                Vector3.Up
            ),
            CubeBlocks =
            [
                (MyObjectBuilder_CubeBlock)projector.Clone(),
                (MyObjectBuilder_CubeBlock)battery.Clone(),
                (MyObjectBuilder_CubeBlock)armor.Clone(),
            ],
        };
        for (var i = 0; i < batteries; i++)
        {
            var printed = Block<MyObjectBuilder_BatteryBlock>(
                "LargeBlockBatteryBlock",
                new Vector3I(0, 0, 3 + i),
                me
            );
            plan.CubeBlocks.Add(printed);
        }
        foreach (var block in plan.CubeBlocks)
            block.EntityId = MyEntityIdentifier.AllocateId();
        projector.ProjectedGrids = [plan];
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = true,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = "Printer",
            PositionAndOrientation = new MyPositionAndOrientation(
                at,
                (Vector3)m.Forward,
                (Vector3)m.Up
            ),
            CubeBlocks = [projector, battery, armor],
        };
        var entity = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(grid);
        return $"TARGET {entity.EntityId}";
    }

    private static string Who(MyCubeBlock block) =>
        (MySession.Static.Players.TryGetIdentity(block.OwnerId)?.DisplayName ?? "nobody").Replace(
            ' ',
            '_'
        )
        + "/"
        + block.IDModule?.ShareMode;

    private static string Summary(IEnumerable<MyCubeBlock> blocks) =>
        string.Join(";", blocks.Select(Who).Distinct());

    private static double Count(IEnumerable<MyCubeBlock> blocks, Func<MyInventory, double> value) =>
        blocks.Sum(b => value(b.GetInventory()));

    [Command(
        "yard",
        "Who has the shipyard's blocks, a player's access, what its inventories hold",
        "!kprobe yard <station> [player name]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardState(string station, string player = null)
    {
        var yard = Yard(station);
        var cargo = yard.GetFatBlocks<MyCargoContainer>().Cast<MyCubeBlock>().ToList();
        var connectors = yard.GetFatBlocks<MyShipConnector>().ToList();
        var beams = Beams(yard).ToList();
        var destros = Deconstructors(yard).ToList();
        var reactors = yard.GetFatBlocks<MyReactor>().Cast<MyCubeBlock>().ToList();
        var batteries = yard.GetFatBlocks<MyBatteryBlock>().Cast<MyCubeBlock>().ToList();
        var refills = yard.GetFatBlocks<MyCubeBlock>()
            .Where(b => b.BlockDefinition.Id.SubtypeName == "InsetRefillStation")
            .ToList();
        var uranium = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
        var text =
            $"YARD {yard.EntityId} cargo={Summary(cargo)} welders={Summary(beams)} connectors={Summary(connectors)} "
            + $"reactors={Summary(reactors)} batteries={Summary(batteries)} "
            + $"bigowner={string.Join(";", yard.BigOwners.Select(o => MySession.Static.Players.TryGetIdentity(o)?.DisplayName?.Replace(' ', '_')))} "
            + $"uranium={Count(reactors, i => (double)i.GetItemAmount(uranium))} "
            + $"elsewhere={Count(cargo.Concat(beams), i => (double)i.GetItemAmount(uranium))} "
            + $"items={Count(cargo.Concat(beams).Concat(connectors), i => (double)i.GetItems().Sum(x => (double)x.Amount))} "
            + $"connected={connectors.Count(c => c.Connected)} trading={connectors.Count(c => c.TradingEnabled)} "
            + $"disabled={connectors.Count(c => !c.Enabled)} "
            + $"connectorson={connectors.Count(c => c.Enabled)} "
            + $"welderson={beams.Count(b => ((MyFunctionalBlock)b).Enabled)} "
            + $"destros={Summary(destros)} "
            + $"destroson={destros.Count(b => ((MyFunctionalBlock)b).Enabled)}/{destros.Count} "
            + $"destroshoot={destros.Count(Shooting)} "
            + $"reactorson={reactors.Count(b => b.IsWorking)}/{reactors.Count} "
            + $"batterieson={batteries.Count(b => b.IsWorking)}/{batteries.Count} "
            + $"refill={refills.Count(b => b.IsWorking)}/{refills.Count} "
            + $"cell={cargo[0].Position.X},{cargo[0].Position.Y},{cargo[0].Position.Z} "
            + $"reactorcell={reactors[0].Position.X},{reactors[0].Position.Y},{reactors[0].Position.Z}";
        if (player != null)
        {
            var who = MySession
                .Static.Players.GetAllIdentities()
                .First(i => i.DisplayName == player)
                .IdentityId;
            bool All(IEnumerable<MyCubeBlock> blocks) =>
                blocks.All(b => ((MyTerminalBlock)b).HasPlayerAccess(who));
            text +=
                $" access cargo={All(cargo)} welders={All(beams)} connectors={All(connectors)} reactors={All(reactors)} "
                + $"destros={All(destros)} "
                + $"refill={refills.All(b => b.GetUserRelationToOwner(who).IsFriendly())}";
        }
        return text;
    }

    [Command(
        "yardstash",
        "Puts steel plates into the shipyard's first container",
        "!kprobe yardstash <station> <amount>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardStash(string station, int amount)
    {
        var plate = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");
        Yard(station)
            .GetFatBlocks<MyCargoContainer>()
            .First()
            .GetInventory()
            .AddItems(amount, MyObjectBuilderSerializer.CreateNewObject(plate));
        return $"STASHED {amount}";
    }

    [Command(
        "info",
        "A grid's blocks, complete blocks, steel plates in its cargo, integrity and owners: INFO id blocks full plates integrity owners",
        "!kprobe info <grid id|grid name>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Info(string grid)
    {
        var found = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g => g.EntityId.ToString() == grid || g.DisplayName == grid);
        if (found == null)
            return $"INFO {grid} gone";
        var plate = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");
        var plates = found
            .GetFatBlocks<MyCargoContainer>()
            .Sum(c => (double)c.GetInventory().GetItemAmount(plate));
        return $"INFO {found.EntityId} blocks={found.BlocksCount} "
            + $"full={found.GetBlocks().Count(b => b.IsFullIntegrity)} plates={plates} "
            + $"integrity={found.GetBlocks().Sum(b => b.Integrity):0} "
            + $"owners={string.Join(";", found.BigOwners)}";
    }

    [Command(
        "balance",
        "Adds to the caller's account and shows it: BALANCE sc",
        "!kprobe balance [SC to add]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Balance(long add = 0)
    {
        if (add != 0)
            MyBankingSystem.ChangeBalance(Context.Caller.IdentityId, add);
        MyBankingSystem.Static.TryGetAccountInfo(Context.Caller.IdentityId, out var account);
        return $"BALANCE {account.Balance}";
    }

    [Command(
        "air",
        "Fills the suit oxygen and battery of everyone online, for a test longer than they last: AIR count",
        "!sprobe air"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Air()
    {
        var count = 0;
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            var character = player.Character;
            if (character?.OxygenComponent == null)
                continue;
            character.OxygenComponent.SuitOxygenAmount = character.OxygenComponent.OxygenCapacity;
            character.SuitBattery.ResourceSource.SetRemainingCapacityByType(
                MyResourceDistributorComponent.ElectricityId,
                MyEnergyConstants.BATTERY_MAX_CAPACITY
            );
            count++;
        }
        return $"AIR {count}";
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
        "connectors",
        "The station's connectors: CONNECTOR grid-id x,y,z trading=yes|no share=mode connected=yes|no unlock=s release=ship:docked:left:told|none, one per connector",
        "!sprobe connectors <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Connectors(string station) =>
        $"FRAME {MySession.Static.GameplayFrameCounter} "
        + string.Join(
            " ",
            StationGrids(station)
                .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
                .Select(c =>
                    $"CONNECTOR {c.CubeGrid.EntityId} {c.Min.X},{c.Min.Y},{c.Min.Z} "
                    + $"trading={((bool)c.TradingEnabled ? "yes" : "no")} share={c.IDModule?.ShareMode} "
                    + $"connected={(c.Connected ? "yes" : "no")} unlock={(float)c.AutoUnlockTime:0} "
                    + $"release={(c.Storage?.TryGetValue(ReleaseKey, out var r) == true ? r.Replace(' ', ':') : "none")}"
                )
        );

    // The Octahedron plugin's StationRelease.StorageKey
    private static readonly Guid ReleaseKey = new("3E7B5D21-9C4A-4F86-B1D2-6A0C8E5F7B13");

    [Command(
        "connectorpower",
        "Turns the station's free connectors on or off: CONNECTORPOWER station on|off count",
        "!sprobe connectorpower <station> <on|off>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ConnectorPower(string station, string state)
    {
        var free = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .Where(c => !c.Connected)
            .ToList();
        foreach (var c in free)
            c.Enabled = state == "on";
        return $"CONNECTORPOWER {station} {state} {free.Count}";
    }

    [Command(
        "letgo",
        "Disconnects the ship from the station: LETGO grid-id",
        "!sprobe letgo <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string LetGo(string gridId)
    {
        if (
            !long.TryParse(gridId, out long id)
            || !MyEntities.TryGetEntityById(id, out MyCubeGrid grid)
        )
            return $"LETGO {gridId} gone";
        grid.GetFatBlocks<MyShipConnector>().First().TryDisconnect();
        return $"LETGO {gridId}";
    }

    [Command(
        "ice",
        "Puts ice into the station's gas generators: ICE station count",
        "!sprobe ice <station> <kg>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Ice(string station, int kg)
    {
        var generators = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyGasGenerator>())
            .ToList();
        foreach (var generator in generators)
            generator
                .GetInventory()
                .AddItems(
                    kg,
                    MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>("Ice")
                );
        return $"ICE {station} {generators.Count}";
    }

    // Whether conveyors lead from a gas generator of the station to a tank of the ship
    private static bool Reach(MyCubeGrid ship, MyCubeGrid station) =>
        station
            .GetFatBlocks<MyGasGenerator>()
            .Any(g =>
                ship.GetFatBlocks<MyGasTank>()
                    .Any(t =>
                        Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(
                            g.ConveyorEndpoint,
                            t.ConveyorEndpoint
                        )
                    )
            );

    [Command(
        "trading",
        "Turns trading on or off at the station's connectors that are free: TRADING station on|off count",
        "!sprobe trading <station> <on|off>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Trading(string station, string state)
    {
        var free = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .Where(c => !c.Connected && c.Other == null)
            .ToList();
        foreach (var c in free)
            c.TradingEnabled.Value = state == "on";
        return $"TRADING {station} {state} {free.Count}";
    }

    [Command(
        "link",
        "What a docked ship's connection carries: LINK grid-id trading=ship,station power=yes|no logical=yes|no electrical=yes|no stored=MWh",
        "!sprobe link <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Link(string gridId)
    {
        if (
            !long.TryParse(gridId, out long id)
            || !MyEntities.TryGetEntityById(id, out MyCubeGrid grid)
        )
            return $"LINK {gridId} gone";
        var c = grid.GetFatBlocks<MyShipConnector>().First();
        var other = c.Other;
        if (other == null)
            return $"LINK {gridId} none";
        string YesNo(bool b) => b ? "yes" : "no";
        bool Same(GridLinkTypeEnum type) =>
            MyCubeGridGroups.Static.GetGroups(type).HasSameGroup(grid, other.CubeGrid);
        return string.Format(
            CultureInfo.InvariantCulture,
            "LINK {0} trading={1},{2} power={3} logical={4} electrical={5} stored={6:0.000} reach={7}",
            gridId,
            YesNo(c.TradingEnabled),
            YesNo(other.TradingEnabled),
            YesNo(c.IsTransferingElectricityCurrently || other.IsTransferingElectricityCurrently),
            YesNo(Same(GridLinkTypeEnum.Logical)),
            YesNo(Same(GridLinkTypeEnum.Electrical)),
            grid.GetFatBlocks<MyBatteryBlock>().Sum(b => b.CurrentStoredPower),
            YesNo(Reach(grid, other.CubeGrid))
        );
    }

    [Command(
        "gas",
        "The gas in the H2 tanks, or the O2 tanks with Oxygen, of a grid and its subgrids: GAS grid-id liters tanks",
        "!sprobe gas <grid id|grid name> [Hydrogen|Oxygen]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Gas(string grid, string gas = "Hydrogen")
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
            .Where(t =>
                t.BlockDefinition is MyGasTankDefinition d && d.StoredGasId.SubtypeName == gas
            )
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
        "griditem",
        "How much of an item a grid's cargo containers hold: ITEM grid-id amount",
        "!sprobe griditem <grid id> <Type/Subtype>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string GridItem(string grid, string item)
    {
        if (!MyDefinitionId.TryParse("MyObjectBuilder_" + item, out var id))
            return $"BADITEM {item}";
        var found = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g => g.EntityId.ToString() == grid);
        if (found == null)
            return $"NOGRID {grid}";
        var amount = found
            .GetFatBlocks<MyCargoContainer>()
            .Sum(c => (double)c.GetInventory().GetItemAmount(id));
        return string.Format(CultureInfo.InvariantCulture, "ITEM {0} {1:0}", grid, amount);
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

    [Command(
        "bottleswap",
        "In one of the station's Free Tools boxes, swaps a full oxygen and a full hydrogen bottle for a half full and an empty one, as a player would, then counts the bottles as bottles does",
        "!sprobe bottleswap <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string BottleSwap(string station)
    {
        var box = FreeTools(station).FirstOrDefault();
        if (box == null)
            return "NOBOX";
        var inventory = box.GetInventory();
        foreach (var (subtype, level) in new[] { ("OxygenBottle", 0.5f), ("HydrogenBottle", 0f) })
        {
            var items = inventory.GetItems();
            var full = items.FindIndex(i =>
                i.Content is MyObjectBuilder_GasContainerObject b
                && b.SubtypeName == subtype
                && b.GasLevel >= 1f
            );
            if (full < 0)
                return $"NOFULL {subtype}";
            var used = (MyObjectBuilder_GasContainerObject)items[full].Content.Clone();
            // One bottle of the stack, as a player takes one
            inventory.RemoveItems(items[full].ItemId, 1);
            used.GasLevel = level;
            inventory.AddItems(1, used);
        }
        return Bottles(station);
    }

    [Command(
        "bottles",
        "The bottles in the station's Free Tools boxes: BOTTLES boxes=count subtype full=count used=count ...",
        "!sprobe bottles <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Bottles(string station)
    {
        var boxes = FreeTools(station).ToList();
        // A stack of bottles is one item with an amount
        var bottles = boxes
            .SelectMany(b => b.GetInventory().GetItems())
            .Where(i => i.Content is MyObjectBuilder_GasContainerObject)
            .Select(i => ((MyObjectBuilder_GasContainerObject)i.Content, (int)i.Amount))
            .ToList();
        int Count(string subtype, bool full) =>
            bottles
                .Where(b => b.Item1.SubtypeName == subtype && b.Item1.GasLevel >= 0.999f == full)
                .Sum(b => b.Item2);
        return $"BOTTLES boxes={boxes.Count} "
            + string.Join(
                " ",
                new[] { "OxygenBottle", "HydrogenBottle" }.Select(subtype =>
                    $"{subtype} full={Count(subtype, true)} used={Count(subtype, false)}"
                )
            );
    }

    private static IEnumerable<MyCargoContainer> FreeTools(string station) =>
        StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<MyCargoContainer>())
            .Where(b => b.CustomName?.ToString().Trim() == "Free Tools");

    [Command(
        "power",
        "The station's spare power and its reactors' uranium, after turning its reactors on or off if asked, and with off, all but the given number of its batteries: POWER station spare=MW uranium=kg reactors=n",
        "!sprobe power <station> [on|off [batteries]]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Power(string station, string state = "", int batteries = int.MaxValue)
    {
        var grid = StationGrids(station).First();
        var reactors = grid.GetFatBlocks<MyReactor>().ToList();
        if (state != "")
        {
            foreach (var reactor in reactors)
                reactor.Enabled = state == "on";
            int left = state == "on" ? int.MaxValue : batteries;
            foreach (var battery in grid.GetFatBlocks<MyBatteryBlock>())
                battery.Enabled = left-- > 0;
        }
        var distributor = grid.GridSystems.ResourceDistributor;
        var electricity = MyResourceDistributorComponent.ElectricityId;
        var spare =
            distributor.MaxAvailableResourceByType(electricity, grid)
            - distributor.TotalRequiredInputByType(electricity, grid);
        var uranium = reactors.Sum(r =>
            (double)r.GetInventory().GetItemAmount(r.BlockDefinition.FuelInfos[0].FuelId)
        );
        return string.Format(
            CultureInfo.InvariantCulture,
            "POWER {0} spare={1:0.###} uranium={2:0.####} reactors={3}",
            station,
            spare,
            uranium,
            reactors.Count
        );
    }

    [Command(
        "redock",
        "Disconnects the ship from the station and connects it again once the connector lets it: REDOCK grid-id",
        "!sprobe redock <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Redock(string gridId)
    {
        if (
            !long.TryParse(gridId, out long id)
            || !MyEntities.TryGetEntityById(id, out MyCubeGrid grid)
        )
            return $"REDOCK {gridId} gone";
        var c = grid.GetFatBlocks<MyShipConnector>().First();
        var station = c.Other;
        c.TryDisconnect();
        // A trading connector won't lock again for a while after letting go
        connecting.Add((c, station, -1800));
        return $"REDOCK {gridId}";
    }

    [Command("undock", "Disconnects and deletes the probe's ships", "!sprobe undock")]
    [Permission(MyPromoteLevel.Admin)]
    public string Undock()
    {
        int count = 0;
        foreach (
            var grid in MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(g => g.DisplayName is "Dock Hauler" or "Stranger Hauler" or "Repair Target")
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
    [Command(
        "gear",
        "A ship of one landing gear or magnetic plate with its lock face on a static armor block, 60% of the radius into the station's safe zone or 100 m out of it: GEAR grid-id inzone=yes|no gravity=m/s2",
        "!sprobe gear <station> <in|out> <LargeBlockLandingGear|LargeBlockMagneticPlate> [auto]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Gear(string station, string where, string subtype, string auto = "")
    {
        var zone = StationZone(station);
        if (zone == null)
            return "NOZONE";
        var center = zone.PositionComp.GetPosition();
        var distance = where == "in" ? zone.Radius * 0.6 : zone.Radius + 100;
        var spot = FreeSpot(center, distance, 25);
        if (spot == null)
            return "NOSPOT";
        var owner = Context.Caller.IdentityId;
        var ship = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = $"Gear {subtype}",
            PositionAndOrientation = new MyPositionAndOrientation(
                spot.Value,
                Vector3.Forward,
                Vector3.Up
            ),
        };
        var block = Block<MyObjectBuilder_LandingGear>(subtype, Vector3I.Zero, owner);
        block.AutoLock = auto == "auto";
        ship.CubeBlocks.Add(block);
        var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(ship);
        var gear = grid.GetFatBlocks<MyLandingGear>().First();
        // The lock box: its Y axis is the face's normal, pointing away from the block
        gear.GetBoxFromMatrix(gear.LockPositions[0], out var half, out var pivot, out var turn);
        var normal = Vector3D.Transform(Vector3D.Up, turn);
        if (Vector3D.Dot(normal, pivot - gear.PositionComp.WorldAABB.Center) < 0)
            normal = -normal;
        var pad = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = true,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = $"Gear Pad {grid.EntityId}",
            PositionAndOrientation = new MyPositionAndOrientation(
                pivot + normal * (MyDefinitionManager.Static.GetCubeSize(MyCubeSize.Large) / 2),
                (Vector3)normal,
                (Vector3)Vector3D.CalculatePerpendicularVector(normal)
            ),
        };
        pad.CubeBlocks.Add(
            new MyObjectBuilder_CubeBlock
            {
                SubtypeName = "LargeBlockArmorBlock",
                Min = Vector3I.Zero,
                BlockOrientation = new SerializableBlockOrientation(
                    Base6Directions.Direction.Forward,
                    Base6Directions.Direction.Up
                ),
            }
        );
        MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(pad);
        return $"GEAR {grid.EntityId} inzone={(zone.Contains(grid.PositionComp.WorldAABB.Center) ? "yes" : "no")} "
            + $"gravity={MyGravityProviderSystem.CalculateNaturalGravityInPoint(spot.Value).Length():0.###} "
            + $"half={V(half)}";
    }

    [Command(
        "gearstate",
        "The ship's landing gear: GEARSTATE grid-id mode=Unlocked|ReadyToLock|Locked auto=yes|no to=entity-id|none",
        "!sprobe gearstate <grid-id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string GearState(long id)
    {
        var gear = ShipGear(id);
        if (gear == null)
            return $"NOGEAR {id}";
        var to = ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).GetAttachedEntity();
        return $"GEARSTATE {id} mode={gear.LockMode} auto={(gear.AutoLock ? "yes" : "no")} "
            + $"to={(to == null ? "none" : to.EntityId.ToString())}";
    }

    [Command(
        "gearlock",
        "Locks or unlocks the ship's landing gear, as its Lock and Unlock actions do",
        "!sprobe gearlock <grid-id> <on|off>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string GearLock(long id, string state)
    {
        var gear = ShipGear(id);
        if (gear == null)
            return $"NOGEAR {id}";
        var api = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)gear;
        if (state == "on")
            api.Lock();
        else
            api.Unlock();
        return $"GEARLOCK {id} {state}";
    }

    [Command(
        "zone",
        "Shows or sets the station's safe zone allowed actions, as the admin menu does: ZONE actions",
        "!sprobe zone <station> [Drilling+LandingGearLock+...|None]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Zone(string station, string actions = "")
    {
        var zone = StationZone(station);
        if (zone == null)
            return "NOZONE";
        if (actions != "")
            zone.AllowedActions =
                actions == "None" ? 0 : Enum.Parse<MySafeZoneAction>(actions.Replace('+', ','));
        return zone.AllowedActions == 0
            ? "ZONE None"
            : $"ZONE {zone.AllowedActions.ToString().Replace(", ", "+")}";
    }

    // The first of 200 directions spread over a sphere around the center where no block of any
    // grid is within the clearance
    private static Vector3D? FreeSpot(Vector3D center, double distance, double clearance)
    {
        var blocks = new HashSet<MySlimBlock>();
        for (int i = 0; i < 200; i++)
        {
            var y = 1 - (i + 0.5) / 100;
            var r = Math.Sqrt(1 - y * y);
            var a = i * Math.PI * (3 - Math.Sqrt(5));
            var sphere = new BoundingSphereD(
                center + new Vector3D(r * Math.Cos(a), y, r * Math.Sin(a)) * distance,
                clearance
            );
            var clear = true;
            foreach (
                var g in MyEntities.GetTopMostEntitiesInSphere(ref sphere).OfType<MyCubeGrid>()
            )
            {
                blocks.Clear();
                g.GetBlocksInsideSphere(ref sphere, blocks);
                clear &= blocks.Count == 0;
            }
            if (clear)
                return sphere.Center;
        }
        return null;
    }

    [Command(
        "drill",
        "A static grid of a switched on ship drill, named so when a name is given, and a battery inside a small asteroid, 60% of the radius into the station's safe zone or 100 m out of it: DRILL grid-id inzone=yes|no at=x,y,z",
        "!sprobe drill <station> <in|out> [drill name]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Drill(string station, string where, string name = "")
    {
        var zone = StationZone(station);
        if (zone == null)
            return "NOZONE";
        var center = zone.PositionComp.GetPosition();
        var spot = FreeSpot(center, where == "in" ? zone.Radius * 0.6 : zone.Radius + 100, 45);
        if (spot == null)
            return "NOSPOT";
        MyWorldGenerator.AddAsteroidPrefabCentered(
            "DeformedSphere1_64x64x64",
            spot.Value,
            $"Drill Rock {where}"
        );
        var owner = Context.Caller.IdentityId;
        var ship = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = true,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = $"Drill {where}",
            PositionAndOrientation = new MyPositionAndOrientation(
                spot.Value,
                Vector3.Forward,
                Vector3.Up
            ),
        };
        var drill = Block<MyObjectBuilder_Drill>("LargeBlockDrill", Vector3I.Zero, owner);
        drill.Enabled = true;
        if (name != "")
            drill.CustomName = name;
        ship.CubeBlocks.Add(drill);
        var size = MyDefinitionManager
            .Static.GetCubeBlockDefinition(
                new MyDefinitionId(typeof(MyObjectBuilder_Drill), "LargeBlockDrill")
            )
            .Size;
        var battery = Block<MyObjectBuilder_BatteryBlock>(
            "LargeBlockBatteryBlock",
            new Vector3I(0, size.Y, 0),
            owner
        );
        battery.CurrentStoredPower = 1;
        ship.CubeBlocks.Add(battery);
        var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(ship);
        return string.Format(
            CultureInfo.InvariantCulture,
            "DRILL {0} inzone={1} at={2:0.#},{3:0.#},{4:0.#}",
            grid.EntityId,
            zone.Contains(spot.Value) ? "yes" : "no",
            spot.Value.X,
            spot.Value.Y,
            spot.Value.Z
        );
    }

    [Command(
        "drillstate",
        "What the drill has dug: DRILLSTATE grid-id ore=kg on=yes|no ores=subtype:kg,...",
        "!sprobe drillstate <grid-id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string DrillState(long id)
    {
        if (!MyEntities.TryGetEntityById(id, out MyCubeGrid grid))
            return $"NODRILL {id}";
        var drill = grid.GetFatBlocks<MyShipDrill>().FirstOrDefault();
        if (drill == null)
            return $"NODRILL {id}";
        var items = drill.GetInventory().GetItems();
        var ore = items.Sum(i => (double)i.Amount);
        var ores = items
            .GroupBy(i => i.Content.SubtypeName)
            .Select(g =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:0.###}",
                    g.Key,
                    g.Sum(i => (double)i.Amount)
                )
            );
        return string.Format(
            CultureInfo.InvariantCulture,
            "DRILLSTATE {0} ore={1:0.###} on={2} ores={3}",
            id,
            ore,
            drill.Enabled && drill.IsWorking ? "yes" : "no",
            string.Join(",", ores)
        );
    }

    // The admin safe zone around the station
    private static MySafeZone StationZone(string station)
    {
        var grid = StationGrids(station).FirstOrDefault();
        return grid == null
            ? null
            : MySessionComponentSafeZones.SafeZones.FirstOrDefault(z =>
                z.SafeZoneBlockId == 0 && z.Contains(grid.PositionComp.GetPosition())
            );
    }

    private static MyLandingGear ShipGear(long id) =>
        MyEntities.TryGetEntityById(id, out MyCubeGrid grid)
            ? grid.GetFatBlocks<MyLandingGear>().FirstOrDefault()
            : null;

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

    // The NPC's blocks of the station and its shipyard
    private static IEnumerable<MyTerminalBlock> NpcBlocks(string station) =>
        StationGrids(station)
            .Append(Yard(station))
            .Where(g => g != null)
            .SelectMany(g => g.GetFatBlocks<MyTerminalBlock>())
            .Where(b => MySession.Static.Players.IdentityIsNpc(b.OwnerId));

    private static string Kind(MyCubeBlock block) =>
        $"{block.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")}/{block.BlockDefinition.Id.SubtypeName}";

    // The game's "Anyone can use": every player uses the block without access to it
    private static bool Anyone(MyTerminalBlock block) =>
        block switch
        {
            MyStoreBlock b => b.AnyoneCanUse,
            MyContractBlock b => b.AnyoneCanUse,
            MyDoorBase b => b.AnyoneCanUse,
            SpaceEngineers.Game.Entities.Blocks.MyButtonPanel b => b.AnyoneCanUse,
            _ => false,
        };

    [Command(
        "uses",
        "The NPC's blocks of the station that anyone can use: one USE line each, then USES count",
        "!sprobe uses <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Uses(string station)
    {
        long me = Context.Caller.IdentityId;
        var lines = NpcBlocks(station)
            .Where(Anyone)
            .Select(b =>
                $"USE {b.EntityId} {b.CubeGrid.EntityId} {b.Min.X},{b.Min.Y},{b.Min.Z} {Kind(b)} "
                + $"share={b.IDModule.ShareMode} access={(b.HasPlayerAccess(me) ? "yes" : "no")} name={b.CustomName}"
            )
            .ToList();
        lines.Add($"USES {lines.Count}");
        return string.Join("\n", lines);
    }

    [Command(
        "doors",
        "The station's doors: DOOR id grid x,y,z kind enabled=yes|no open=yes|no working=yes|no powered=yes|no anyone=yes|no share=mode access=yes|no name=name",
        "!sprobe doors <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Doors(string station)
    {
        long me = Context.Caller.IdentityId;
        string Yes(bool b) => b ? "yes" : "no";
        return string.Join(
            "\n",
            StationGrids(station)
                .SelectMany(g => g.GetFatBlocks<MyDoorBase>())
                .Select(d =>
                    $"DOOR {d.EntityId} {d.CubeGrid.EntityId} {d.Min.X},{d.Min.Y},{d.Min.Z} {Kind(d)} enabled={Yes(d.Enabled)} "
                    + $"open={Yes(d.Open)} working={Yes(d.IsWorking)} powered={Yes(d.ResourceSink?.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) == true)} anyone={Yes(d.AnyoneCanUse)} share={d.IDModule.ShareMode} "
                    + $"access={Yes(d.HasPlayerAccess(me))} name={d.CustomName}"
                )
        );
    }

    [Command("open", "Whether a door is open: OPEN id yes|no", "!sprobe open <entity id>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Open(long entityId) =>
        MyEntities.TryGetEntityById(entityId, out MyDoorBase door)
            ? $"OPEN {entityId} {(door.Open ? "yes" : "no")}"
            : $"NODOOR {entityId}";

    [Command(
        "access",
        "The NPC's blocks of the station and its shipyard that the caller's terminal can work: one BLOCK line each, then ACCESS count NOACCESS count",
        "!sprobe access <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Access(string station)
    {
        long me = Context.Caller.IdentityId;
        var lines = new List<string>();
        var none = 0;
        foreach (var block in NpcBlocks(station))
        {
            if (!block.HasPlayerAccess(me))
            {
                none++;
                continue;
            }
            var inventory = block.HasInventory ? (block.ShowInInventory ? "yes" : "hidden") : "-";
            lines.Add(
                $"BLOCK {block.EntityId} {block.CubeGrid.EntityId} {block.Min.X},{block.Min.Y},{block.Min.Z} {Kind(block)} "
                    + $"share={block.IDModule.ShareMode} terminal={(block.ShowInTerminal ? "yes" : "hidden")} inventory={inventory} name={block.CustomName}"
            );
        }
        lines.Add($"ACCESS {lines.Count} NOACCESS {none}");
        return string.Join("\n", lines);
    }

    private static readonly Dictionary<long, Dictionary<string, string>> saved = [];

    // Everything the block saves but its inventories, as path=value
    private static Dictionary<string, string> Settings(MyCubeBlock block)
    {
        var builder = block.GetObjectBuilderCubeBlock(copy: true);
        builder.ComponentContainer = null;
        using var stream = new System.IO.MemoryStream();
        VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(stream, builder);
        stream.Position = 0;
        var values = new Dictionary<string, string>();
        void Walk(System.Xml.Linq.XElement element, string path)
        {
            if (!element.HasElements)
            {
                values[path] = element.Value;
                return;
            }
            var seen = new Dictionary<string, int>();
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                seen[name] = seen.TryGetValue(name, out var n) ? n + 1 : 0;
                Walk(child, $"{path}/{name}[{seen[name]}]");
            }
        }
        Walk(System.Xml.Linq.XDocument.Load(stream).Root, "");
        return values;
    }

    [Command(
        "settings",
        "save keeps the settings of the NPC's blocks the caller has access to or anyone can use; diff lists what changed since: CHANGED id kind path old -> new, then SAME count CHANGED count",
        "!sprobe settings <station> <save|diff>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string SettingsCommand(string station, string what)
    {
        long me = Context.Caller.IdentityId;
        var blocks = NpcBlocks(station).Where(b => b.HasPlayerAccess(me) || Anyone(b)).ToList();
        if (what == "save")
        {
            saved.Clear();
            foreach (var block in blocks)
                saved[block.EntityId] = Settings(block);
            return $"SAVED {saved.Count}";
        }
        var lines = new List<string>();
        int same = 0,
            changed = 0;
        foreach (var block in blocks)
        {
            if (!saved.TryGetValue(block.EntityId, out var before))
                continue;
            var after = Settings(block);
            var diffs = before
                .Keys.Union(after.Keys)
                .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
                .ToList();
            if (diffs.Count == 0)
            {
                same++;
                continue;
            }
            changed++;
            foreach (var k in diffs.Take(5))
                lines.Add(
                    $"CHANGED {block.EntityId} {Kind(block)} {k} {before.GetValueOrDefault(k)} -> {after.GetValueOrDefault(k)}"
                );
        }
        lines.Add($"SAME {same} CHANGED {changed}");
        return string.Join("\n", lines);
    }

    [Command(
        "groups",
        "The block groups of the station's grids: GROUPS count names",
        "!sprobe groups <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Groups(string station)
    {
        var names = StationGrids(station)
            .SelectMany(g => g.GridSystems.TerminalSystem.BlockGroups)
            .Select(g => g.Name.ToString())
            .Distinct()
            .ToList();
        return $"GROUPS {names.Count} {string.Join("|", names)}";
    }

    [Command(
        "suit",
        "The caller's health, suit energy, oxygen and hydrogen: SUIT health energy o2 h2; drain sets them to 50, 0.1, 0.1 and 0",
        "!sprobe suit [drain]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Suit(string what = "")
    {
        var character = MySession
            .Static.Players.TryGetIdentity(Context.Caller.IdentityId)
            ?.Character;
        if (character?.OxygenComponent == null || character.StatComp?.Health == null)
            return "NOCHARACTER";
        var oxygen = character.OxygenComponent;
        var hydrogen = MyCharacterOxygenComponent.HydrogenId;
        if (what == "drain")
        {
            character.StatComp.Health.Value = 50;
            character.SuitBattery.ResourceSource.SetRemainingCapacityByType(
                MyResourceDistributorComponent.ElectricityId,
                MyEnergyConstants.BATTERY_MAX_CAPACITY * 0.1f
            );
            oxygen.SuitOxygenAmount = oxygen.OxygenCapacity * 0.1f;
            oxygen.UpdateStoredGasLevel(ref hydrogen, 0);
        }
        return string.Format(
            CultureInfo.InvariantCulture,
            "SUIT {0:0.###} {1:0.###} {2:0.###} {3:0.###}",
            character.StatComp.Health.Value,
            character.SuitEnergyLevel,
            oxygen.SuitOxygenLevel,
            oxygen.GetGasFillLevel(hydrogen)
        );
    }

    [Command(
        "medical",
        "The station's medical room and where its use detectors are: MEDICAL id x y z FORWARD x y z UP x y z, then DETECTOR name x y z",
        "!sprobe medical <station>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Medical(string station)
    {
        var room = StationGrids(station)
            .SelectMany(g => g.GetFatBlocks<SpaceEngineers.Game.Entities.Blocks.MyMedicalRoom>())
            .FirstOrDefault();
        if (room == null)
            return "NOMEDICAL";
        var m = room.WorldMatrix;
        var lines = new List<string>
        {
            $"MEDICAL {room.EntityId} {V(m.Translation)} FORWARD {V(m.Forward)} UP {V(m.Up)}",
        };
        foreach (var dummy in room.Model.Dummies)
            if (dummy.Key.StartsWith("detector", StringComparison.OrdinalIgnoreCase))
                lines.Add(
                    $"DETECTOR {dummy.Key} {V(Vector3D.Transform(dummy.Value.Matrix.Translation, room.WorldMatrix))}"
                );
        return string.Join("\n", lines);
    }

    [Command(
        "enabled",
        "Whether a block is switched on: ENABLED id yes|no",
        "!sprobe enabled <entity id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Enabled(long entityId) =>
        MyEntities.TryGetEntityById(entityId, out MyFunctionalBlock block)
            ? $"ENABLED {entityId} {(block.Enabled ? "yes" : "no")}"
            : $"NOBLOCK {entityId}";

    [Command(
        "owner",
        "Who owns a block: OWNER id npc|player|other share",
        "!sprobe owner <entity id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Owner(long entityId)
    {
        if (!MyEntities.TryGetEntityById(entityId, out MyCubeBlock block))
            return $"NOBLOCK {entityId}";
        var who =
            block.OwnerId == Context.Caller.IdentityId ? "player"
            : MySession.Static.Players.IdentityIsNpc(block.OwnerId) ? "npc"
            : "other";
        return $"OWNER {entityId} {who} {block.IDModule?.ShareMode}";
    }

    [Command(
        "where",
        "Where a block is and its use detectors: WHERE id x y z FORWARD x y z UP x y z, then DETECTOR name x y z",
        "!sprobe where <entity id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Where(long entityId)
    {
        if (!MyEntities.TryGetEntityById(entityId, out MyCubeBlock block))
            return $"NOBLOCK {entityId}";
        var m = block.WorldMatrix;
        var lines = new List<string>
        {
            $"WHERE {entityId} {V(m.Translation)} FORWARD {V(m.Forward)} UP {V(m.Up)}",
        };
        foreach (var dummy in block.Model.Dummies)
            if (dummy.Key.StartsWith("detector", StringComparison.OrdinalIgnoreCase))
                lines.Add(
                    $"DETECTOR {dummy.Key} {V(Vector3D.Transform(dummy.Value.Matrix.Translation, m))}"
                );
        return string.Join("\n", lines);
    }

    [Command(
        "item",
        "How many of an item a block holds: ITEM entity-id amount",
        "!sprobe item <block entity id> <Type/Subtype>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Item(long entityId, string item)
    {
        if (!MyEntities.TryGetEntityById(entityId, out MyCubeBlock block) || !block.HasInventory)
            return $"NOBLOCK {entityId}";
        var id = MyDefinitionId.Parse("MyObjectBuilder_" + item);
        return $"ITEM {entityId} {(double)block.GetInventory().GetItemAmount(id)}";
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
