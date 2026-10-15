using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using PluginSdk.Commands;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

// A player orders the blueprint loaded into a station's hologram table with !oct order, and
// the station's shipyard builds it. The ship starts as a static grid of one of its blocks,
// centered in the shipyard's free space, with an NPC-owned projector and battery (the scaffold)
// next to that block projecting the whole blueprint. The shipyard's cargo containers get the
// components of the remaining blocks and its OmniArc welders (ToolCore) are switched on.
// Projected blocks welded onto the ship are the player's, owner and BuiltBy. The order ends
// when every block is complete or after ShipyardOrderMinutes: the welders go off, the scaffold
// and the leftover components are removed, and the ship, finished or not, becomes the
// player's dynamic ship.
//
// The order lives in the world, in the scaffold projector's CustomData, so it survives a
// restart and moves with the station's partition on a cluster: every node checks the
// shipyards it simulates every 10 s.
internal static class ShipyardOrders
{
    private const string ScaffoldName = "Shipyard Order";
    private const string ShootProperty = "ToolCore_Shoot";
    private const string TrackProperty = "ToolCore_TrackTargets";
    private const int UpdateTicks = 600;

    private sealed class Order
    {
        public MyProjectorBase Projector;
        public long Player;
        public DateTime Ends;
        public int Blocks;
        public Dictionary<MyDefinitionId, int> Components;
    }

    // By shipyard entity id
    private static readonly Dictionary<long, Order> orders = new();

    // The shipyard's free space around its welders, in its grid's cells
    private static readonly Dictionary<long, BoundingBoxI> freeBoxes = new();

    private static int ticks;

    public static CommandReply Request(long identityId, string tableName)
    {
        var config = Plugin.Config;
        if (config.ShipyardOrderMinutes <= 0)
            return CommandReply.Error("Shipyard orders are off on this server.");

        var identity = MySession.Static.Players.TryGetIdentity(identityId);
        var character = identity?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return CommandReply.Error("Spawn first, then order at a hologram table.");

        // A ship is built for free only while the player has less than FreeBuildPcuLimit
        var pcu = StarterShip.PlayerPcu(identity);
        if (pcu >= config.FreeBuildPcuLimit)
            return CommandReply.Error(
                $"You have {pcu:N0} PCU. The shipyard builds for free only for players under {config.FreeBuildPcuLimit:N0} PCU."
            );

        var table = StarterShip.FindTable(character, tableName, config);
        if (table == null)
            return CommandReply.Error(
                $"Stand within {config.StarterShipTableReach:0} m of a station's hologram table."
            );
        var blueprint = ((MyObjectBuilder_ProjectorBase)table.GetObjectBuilderCubeBlock(true))
            .ProjectedGrids?.Select(g => (MyObjectBuilder_CubeGrid)g.Clone())
            .ToList();
        if (blueprint == null || blueprint.Count == 0)
            return CommandReply.Error(
                "Switch the hologram table on and load a blueprint into it first."
            );

        // The ordered ship is the player's, so GroupPcuLimit would stop welding it there
        var shipPcu = blueprint.Sum(g => g.CalculatePCUs());
        if (config.GroupPcuLimit > 0 && shipPcu > config.GroupPcuLimit)
            return CommandReply.Error(
                $"The blueprint has {shipPcu:N0} PCU, a ship at most {config.GroupPcuLimit:N0}."
            );

        var shipyard = FindShipyard(table);
        if (shipyard == null)
            return CommandReply.Error("This station has no shipyard.");
        if (!Plugin.Simulated(table.CubeGrid) || !Plugin.Simulated(shipyard))
            return CommandReply.Error("The station is busy, try again in a few seconds.");
        if (orders.TryGetValue(shipyard.EntityId, out var current))
        {
            var left = Math.Max(1, Math.Ceiling((current.Ends - DateTime.UtcNow).TotalMinutes));
            return CommandReply.Error(
                $"{shipyard.DisplayName} is building '{current.Projector.CubeGrid.DisplayName}' "
                    + $"for {Name(current.Player)}, it is free in {left} min at the latest."
            );
        }
        var space = FreeSpace(shipyard);
        var obstacle = GridsIn(space).FirstOrDefault(g => g != shipyard);
        if (obstacle != null)
            return CommandReply.Error(
                $"'{obstacle.DisplayName}' is in {shipyard.DisplayName}, it has to leave first."
            );

        var main = blueprint[0];
        if (!FindStart(main, out var start, out var outward))
            return CommandReply.Error(
                "The blueprint's main grid needs a 1x1x1 block on its outside to start from."
            );
        // The projector anchors the projection's first block, so the start block goes first
        main.CubeBlocks.Remove(start);
        main.CubeBlocks.Insert(0, start);
        Vector3I startCell = start.Min;
        var scaffold = new[] { startCell + outward, startCell + 2 * outward };

        var cube = MyDefinitionManager.Static.GetCubeSize(main.GridSizeEnum);
        var bounds = TableHologram.Bounds(blueprint);
        foreach (var cell in scaffold)
            bounds.Include(
                new BoundingBoxD(((Vector3D)cell - 0.5) * cube, ((Vector3D)cell + 0.5) * cube)
            );
        var (forward, up) = StarterShip.CockpitFrame(main);
        var turn = MatrixD.Transpose(MatrixD.CreateWorld(Vector3D.Zero, forward, up));
        var size = Vector3D.Abs(Vector3D.TransformNormal(bounds.Size, turn));
        var room = space.HalfExtent * 2;
        if (size.X > room.X || size.Y > room.Y || size.Z > room.Z)
            return CommandReply.Error(
                $"The ship is {Meters(size)} (width, height, length), {shipyard.DisplayName} builds up to {Meters(room)}."
            );

        var components = Components(blueprint);
        var containers = Containers(shipyard).ToList();
        var needed = components.Sum(c =>
            MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value
        );
        var free = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        if (needed > free)
            return CommandReply.Error(
                $"The ship's components take {needed:N0} m³, {shipyard.DisplayName}'s containers have room for {free:N0} m³."
            );

        var npc = shipyard.BigOwners.First(MySession.Static.Players.IdentityIsNpc);
        var ends = DateTime.UtcNow.AddMinutes(config.ShipyardOrderMinutes);
        var world = turn * shipyard.WorldMatrix.GetOrientation();
        var ship = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_CubeGrid>();
        ship.GridSizeEnum = main.GridSizeEnum;
        ship.IsStatic = true;
        ship.DisplayName = main.DisplayName ?? "Ship";
        ship.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        ship.PositionAndOrientation = new MyPositionAndOrientation(
            space.Center - Vector3D.TransformNormal(bounds.Center, world),
            (Vector3)world.Forward,
            (Vector3)world.Up
        );
        var first = (MyObjectBuilder_CubeBlock)start.Clone();
        first.SetupForProjector();
        first.IntegrityPercent = first.BuildPercent = 1;
        first.ConstructionStockpile = null;
        first.Owner = first.BuiltBy = identityId;
        first.ShareMode = MyOwnershipShareModeEnum.Faction;
        ship.CubeBlocks =
        [
            first,
            Projector(main.GridSizeEnum, scaffold[0], outward, blueprint, npc),
            Battery(main.GridSizeEnum, scaffold[1], npc),
        ];
        MyEntities.RemapObjectBuilder(ship);
        if (MyEntities.CreateFromObjectBuilderAndAdd(ship, true) is not MyCubeGrid grid)
            return CommandReply.Error("The ship could not be started.");

        var projector = grid.GetFatBlocks<MyProjectorBase>().First();
        projector.CustomData =
            $"Player={identityId}\nEnds={ends.ToString("o", CultureInfo.InvariantCulture)}";
        Load(containers, components);
        orders[shipyard.EntityId] = new Order
        {
            Projector = projector,
            Player = identityId,
            Ends = ends,
            Blocks = blueprint.Sum(g => g.CubeBlocks.Count),
            Components = components,
        };
        Welders(shipyard, true);
        Plugin.Log.Info(
            $"Shipyard order '{grid.DisplayName}' ({grid.EntityId}, {shipPcu} PCU) "
                + $"for {identity.DisplayName} at {shipyard.DisplayName}, until {ends:u}"
        );
        return CommandReply.Ok(
            $"{shipyard.DisplayName} builds '{grid.DisplayName}' for you, in {config.ShipyardOrderMinutes} min at most."
        );
    }

    public static void Update()
    {
        if (++ticks % UpdateTicks != 0)
            return;
        var shipyards = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(ShipyardZoneWelding.IsShipyard)
            .ToList();
        foreach (var shipyard in shipyards)
        {
            if (!Plugin.Simulated(shipyard))
            {
                orders.Remove(shipyard.EntityId);
                continue;
            }
            orders.TryGetValue(shipyard.EntityId, out var order);
            var projector = GridsIn(FreeSpace(shipyard))
                .SelectMany(g => g.GetFatBlocks<MyProjectorBase>())
                .FirstOrDefault(IsScaffold);
            if (projector == null)
            {
                if (order != null)
                    End(
                        shipyard,
                        order,
                        $"Your order at {shipyard.DisplayName} ended: the ship is gone."
                    );
                continue;
            }
            if (order?.Projector != projector)
            {
                order = Restore(projector);
                if (order == null)
                    continue;
                orders[shipyard.EntityId] = order;
                Plugin.Log.Info(
                    $"Shipyard order '{projector.CubeGrid.DisplayName}' ({projector.CubeGrid.EntityId}) "
                        + $"of {Name(order.Player)} picked up at {shipyard.DisplayName}, until {order.Ends:u}"
                );
            }

            var ship = projector.CubeGrid.DisplayName;
            var built = Built(projector.CubeGrid);
            if (built >= order.Blocks)
                End(
                    shipyard,
                    order,
                    $"Your ship '{ship}' is ready in {shipyard.DisplayName}. Fly it out to make room for the next order."
                );
            else if (DateTime.UtcNow >= order.Ends)
                End(
                    shipyard,
                    order,
                    $"{shipyard.DisplayName} stopped after {Plugin.Config.ShipyardOrderMinutes} min with {built} of "
                        + $"{order.Blocks} blocks of '{ship}' complete. The projection and the leftover components are gone, "
                        + "the unfinished ship is yours."
                );
            else
                Welders(shipyard, true);
        }
    }

    // A projected block welded onto an order's ship is the player's. Runs before the
    // projection limits, so they count it toward the player.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlockRequestInternal))]
    private static class BuildRequest
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            MyCubeGrid __instance,
            ref MyCubeGrid.MyBlockLocation location,
            MyObjectBuilder_CubeBlock blockObjectBuilder,
            ref long ownerId,
            bool isProjection
        )
        {
            if (!isProjection || orders.Count == 0 || !Plugin.Config.Enabled)
                return;
            foreach (var order in orders.Values)
            {
                var ship = order.Projector.CubeGrid;
                if (
                    ship != __instance
                    && !MyCubeGridGroups.Static.Mechanical.HasSameGroup(ship, __instance)
                )
                    continue;
                ownerId = location.Owner = order.Player;
                if (blockObjectBuilder != null)
                    blockObjectBuilder.Owner = blockObjectBuilder.BuiltBy = order.Player;
                return;
            }
        }
    }

    private static void End(MyCubeGrid shipyard, Order order, string message)
    {
        orders.Remove(shipyard.EntityId);
        Welders(shipyard, false);
        var ship = order.Projector.CubeGrid;
        if (!ship.MarkedForClose)
        {
            var scaffold = ship.GetFatBlocks<MyTerminalBlock>()
                .Where(IsScaffold)
                .Select(b => b.Position)
                .ToList();
            // RazeBlocks would ask the safe zone, which forbids grinding
            ship.RazeGeneratedBlocks(scaffold);
            MyMultiplayer.RaiseEvent(ship, x => x.RazeBlocksClient, scaffold);
            HandOver(ship, order.Player);
            if (ship.IsStatic)
                MyMultiplayer.RaiseEvent(ship, x => x.OnConvertToDynamic);
        }
        RemoveLeftovers(shipyard, order.Components);
        MyVisualScriptLogicProvider.SendChatMessage(message, "Shipyard", order.Player);
        Plugin.Log.Info(
            $"Shipyard order '{ship.DisplayName}' of {Name(order.Player)} ended: {message}"
        );
    }

    // Blocks the projector built itself, like the tops of rotors and pistons, are BuiltBy its NPC
    private static void HandOver(MyCubeGrid ship, long player)
    {
        foreach (MyCubeGrid grid in Group(ship))
        {
            var authors = new HashSet<long>();
            foreach (var block in grid.GetBlocks())
            {
                if (block.BuiltBy == player)
                    continue;
                authors.Add(block.BuiltBy);
                block.TransferAuthorship(player);
            }
            foreach (var author in authors)
                MyMultiplayer.RaiseStaticEvent(
                    (IMyEventOwner _) => MyBlockLimits.TransferBlocksBuiltByIDClient,
                    grid.EntityId,
                    author,
                    player
                );
            grid.ChangeGridOwnership(player, MyOwnershipShareModeEnum.Faction);
        }
    }

    private static Order Restore(MyProjectorBase projector)
    {
        var grids = (
            (MyObjectBuilder_ProjectorBase)projector.GetObjectBuilderCubeBlock(true)
        ).ProjectedGrids;
        if (grids == null || grids.Count == 0)
            return null;
        // Written by Request: "Player=<identity>" and "Ends=<UTC time>" lines
        var values = projector
            .CustomData.Split('\n')
            .Select(l => l.Split('=').Last().Trim())
            .ToList();
        if (
            values.Count < 2
            || !long.TryParse(
                values[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var player
            )
            || !DateTime.TryParse(
                values[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var ends
            )
        )
            return null;
        return new Order
        {
            Projector = projector,
            Player = player,
            Ends = ends,
            Blocks = grids.Sum(g => g.CubeBlocks.Count),
            Components = Components(grids),
        };
    }

    // The nearest shipyard within 1 km of the table
    private static MyCubeGrid FindShipyard(MyProjectorBase table)
    {
        var position = table.PositionComp.GetPosition();
        var sphere = new BoundingSphereD(position, 1000);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var shipyard = entities
            .OfType<MyCubeGrid>()
            .Where(ShipyardZoneWelding.IsShipyard)
            .OrderBy(g => Vector3D.DistanceSquared(g.PositionComp.GetPosition(), position))
            .FirstOrDefault();
        entities.Clear();
        return shipyard;
    }

    // The largest box free of the shipyard's blocks, grown from the middle of its welders
    // one layer at a time and kept within its grid's bounds, in world coordinates
    private static MyOrientedBoundingBoxD FreeSpace(MyCubeGrid shipyard)
    {
        if (!freeBoxes.TryGetValue(shipyard.EntityId, out var box))
        {
            var beams = Beams(shipyard).ToList();
            var start = Vector3I.Zero;
            foreach (var beam in beams)
                start += beam.Position;
            start /= Math.Max(1, beams.Count);
            box = new BoundingBoxI(start, start);
            for (var grew = true; grew; )
            {
                grew = false;
                foreach (var d in Base6Directions.IntDirections)
                {
                    var grown = new BoundingBoxI(
                        box.Min + Vector3I.Min(d, Vector3I.Zero),
                        box.Max + Vector3I.Max(d, Vector3I.Zero)
                    );
                    // The new layer only
                    var layer = grown;
                    if (d.X + d.Y + d.Z > 0)
                        layer.Min = box.Min + (box.Max - box.Min + Vector3I.One) * d;
                    else
                        layer.Max = box.Max + (box.Max - box.Min + Vector3I.One) * d;
                    if (
                        Vector3I.Min(grown.Min, shipyard.Min) != shipyard.Min
                        || Vector3I.Max(grown.Max, shipyard.Max) != shipyard.Max
                        || !Free(shipyard, layer)
                    )
                        continue;
                    box = grown;
                    grew = true;
                }
            }
            if (beams.Count > 0)
                freeBoxes[shipyard.EntityId] = box;
        }
        var cube = shipyard.GridSize;
        return new MyOrientedBoundingBoxD(
            new BoundingBoxD(((Vector3D)box.Min - 0.5) * cube, ((Vector3D)box.Max + 0.5) * cube),
            shipyard.WorldMatrix
        );
    }

    private static bool Free(MyCubeGrid grid, BoundingBoxI box)
    {
        for (var x = box.Min.X; x <= box.Max.X; x++)
        for (var y = box.Min.Y; y <= box.Max.Y; y++)
        for (var z = box.Min.Z; z <= box.Max.Z; z++)
            if (grid.CubeExists(new Vector3I(x, y, z)))
                return false;
        return true;
    }

    private static List<MyCubeGrid> GridsIn(MyOrientedBoundingBoxD box)
    {
        var entities = MyEntities.GetEntitiesInOBB(ref box);
        var grids = entities.OfType<MyCubeGrid>().ToList();
        entities.Clear();
        return grids;
    }

    // A 1x1x1 block of the main grid on its outer face, preferably plain armor, and the way out
    // of the grid from it: the scaffold goes there, outside the main grid's bounds
    private static bool FindStart(
        MyObjectBuilder_CubeGrid grid,
        out MyObjectBuilder_CubeBlock start,
        out Vector3I outward
    )
    {
        var cube = MyDefinitionManager.Static.GetCubeSize(grid.GridSizeEnum);
        var bounds = grid.CalculateBoundingBox();
        var min = Vector3I.Round(bounds.Min / cube + 0.5f);
        var max = Vector3I.Round(bounds.Max / cube - 0.5f);
        start = null;
        outward = Vector3I.Zero;
        foreach (var block in grid.CubeBlocks)
        {
            if (
                !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                    block.GetId(),
                    out var definition
                )
                || definition.Size != Vector3I.One
            )
                continue;
            foreach (var d in Base6Directions.IntDirections)
            {
                Vector3I cell = block.Min;
                if (Vector3I.Dot(cell, d) != Math.Max(Vector3I.Dot(min, d), Vector3I.Dot(max, d)))
                    continue;
                if (start == null || block.GetType() == typeof(MyObjectBuilder_CubeBlock))
                {
                    start = block;
                    outward = d;
                }
                if (block.GetType() == typeof(MyObjectBuilder_CubeBlock))
                    return true;
                break;
            }
        }
        return start != null;
    }

    private static MyObjectBuilder_Projector Projector(
        MyCubeSize size,
        Vector3I cell,
        Vector3I outward,
        List<MyObjectBuilder_CubeGrid> blueprint,
        long npc
    )
    {
        var projector = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Projector>(
            size == MyCubeSize.Large ? "LargeProjector" : "SmallProjector"
        );
        projector.Min = cell;
        projector.BlockOrientation = new SerializableBlockOrientation(
            Base6Directions.Direction.Forward,
            Base6Directions.Direction.Up
        );
        projector.Owner = npc;
        projector.ShareMode = MyOwnershipShareModeEnum.None;
        projector.Enabled = true;
        projector.CustomName = ScaffoldName;
        projector.ProjectedGrids = blueprint;
        // The projection's first block lands on the projector's cell minus the offset
        projector.ProjectionOffset = outward;
        projector.KeepProjection = true;
        return projector;
    }

    private static MyObjectBuilder_BatteryBlock Battery(MyCubeSize size, Vector3I cell, long npc)
    {
        var subtype =
            size == MyCubeSize.Large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock";
        var battery = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_BatteryBlock>(
            subtype
        );
        battery.Min = cell;
        battery.Owner = npc;
        battery.ShareMode = MyOwnershipShareModeEnum.None;
        battery.Enabled = true;
        battery.CustomName = ScaffoldName;
        battery.CurrentStoredPower = (
            (MyBatteryBlockDefinition)MyDefinitionManager.Static.GetCubeBlockDefinition(battery)
        ).MaxStoredPower;
        return battery;
    }

    // The components of every block but the first one, which is built already
    private static Dictionary<MyDefinitionId, int> Components(List<MyObjectBuilder_CubeGrid> grids)
    {
        var components = new Dictionary<MyDefinitionId, int>();
        foreach (var block in grids.SelectMany(g => g.CubeBlocks).Skip(1))
        {
            if (
                !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                    block.GetId(),
                    out var definition
                )
            )
                continue;
            foreach (var component in definition.Components)
            {
                components.TryGetValue(component.Definition.Id, out var count);
                components[component.Definition.Id] = count + component.Count;
            }
        }
        return components;
    }

    private static void Load(
        List<MyInventory> containers,
        Dictionary<MyDefinitionId, int> components
    )
    {
        foreach (var (id, count) in components.Select(c => (c.Key, c.Value)))
        {
            MyFixedPoint left = count;
            foreach (var inventory in containers)
            {
                var amount = MyFixedPoint.Min(left, inventory.ComputeAmountThatFits(id));
                if (amount <= 0)
                    continue;
                inventory.AddItems(amount, MyObjectBuilderSerializer.CreateNewObject(id));
                left -= amount;
            }
        }
    }

    // Takes back what is left of the loaded components from the shipyard's containers and welders
    private static void RemoveLeftovers(
        MyCubeGrid shipyard,
        Dictionary<MyDefinitionId, int> components
    )
    {
        var left = components.ToDictionary(c => c.Key, c => (MyFixedPoint)c.Value);
        foreach (
            var inventory in Containers(shipyard)
                .Concat(Beams(shipyard).Select(b => b.GetInventory()))
        )
        {
            foreach (var id in left.Keys.ToList())
            {
                var amount = MyFixedPoint.Min(left[id], inventory.GetItemAmount(id));
                if (amount <= 0)
                    continue;
                inventory.RemoveItemsOfType(amount, id);
                left[id] -= amount;
            }
        }
    }

    private static IEnumerable<MyInventory> Containers(MyCubeGrid shipyard) =>
        shipyard.GetFatBlocks<MyCargoContainer>().Select(c => c.GetInventory());

    // The shipyard's ToolCore tools, its OmniArc welders
    private static IEnumerable<MyFunctionalBlock> Beams(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyFunctionalBlock>()
            .Where(b => ((IMyTerminalBlock)b).GetProperty(ShootProperty) != null);

    // Through the tools' terminal switches: on the server they also tell the clients, so
    // players see the beams
    private static void Welders(MyCubeGrid shipyard, bool on)
    {
        foreach (var beam in Beams(shipyard))
        {
            if (on)
                beam.Enabled = true;
            var block = (IMyTerminalBlock)beam;
            if (block.GetValueBool(TrackProperty) != on)
                block.SetValueBool(TrackProperty, on);
            if (block.GetValueBool(ShootProperty) != on)
                block.SetValueBool(ShootProperty, on);
        }
    }

    // Complete blocks of the ship and its subgrids, the scaffold left out
    private static int Built(MyCubeGrid ship) =>
        Group(ship)
            .SelectMany(g => ((MyCubeGrid)g).GetBlocks())
            .Count(b => b.IsFullIntegrity && !(b.FatBlock is MyTerminalBlock t && IsScaffold(t)));

    private static List<IMyCubeGrid> Group(MyCubeGrid ship)
    {
        var grids = new List<IMyCubeGrid>();
        MyAPIGateway.GridGroups.GetGroup(ship, GridLinkTypeEnum.Mechanical, grids);
        return grids;
    }

    private static bool IsScaffold(MyTerminalBlock block) =>
        block.CustomName.ToString() == ScaffoldName
        && MySession.Static.Players.IdentityIsNpc(block.OwnerId);

    private static string Name(long identityId) =>
        MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName ?? "someone";

    private static string Meters(Vector3D size) => $"{size.X:0} x {size.Y:0} x {size.Z:0} m";
}
