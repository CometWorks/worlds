using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
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
            .Where(b => b.BlockDefinition.Id.SubtypeName == "OmniArcBeam");

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
        "Builds a ship to repair in the middle of the station's shipyard: a projector, a battery, an armor block at 30% and two more, or the given number, only in the projection: TARGET grid-id",
        "!kprobe yardtarget <station> [projected blocks]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string YardTarget(string station, int projected = 2)
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
        "A grid's blocks, complete blocks, steel plates in its cargo and owners: INFO id blocks full plates owners",
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
