using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace CharacterProbe;

// Test-only server plugin of the offline characters tests: admin commands on the caller's character
public class Probe : IPlugin
{
    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update() { }

    public void Dispose() { }
}

[CommandRoot("cprobe", "Character probe", "Offline characters test commands")]
public sealed class ProbeCommands : CommandModule
{
    private MyCharacter Body =>
        MySession.Static.Players.TryGetIdentity(Context.Caller.IdentityId)?.Character;

    private static MyInventory Inventory(MyCharacter body) => (MyInventory)body.GetInventory();

    private static string Name(MyObjectBuilder_PhysicalObject content) =>
        content.GetObjectId().TypeId.ToString().Replace("MyObjectBuilder_", "")
        + "/"
        + content.SubtypeName;

    [Command("give", "Adds items to the character", "!cprobe give <Type/Subtype> <amount>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Give(string item, long amount)
    {
        var id = MyDefinitionId.Parse("MyObjectBuilder_" + item);
        var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(id);
        return $"GIVE {Inventory(Body).AddItems((MyFixedPoint)(double)amount, content)}";
    }

    // Stone up to what fits, so the next body's own gear leaves no room for all of this
    [Command("fill", "Fills the character's inventory with stone", "!cprobe fill")]
    [Permission(MyPromoteLevel.Admin)]
    public string Fill()
    {
        var content = (MyObjectBuilder_PhysicalObject)
            MyObjectBuilderSerializer.CreateNewObject(
                MyDefinitionId.Parse("MyObjectBuilder_Ore/Stone")
            );
        var inventory = Inventory(Body);
        var room = inventory.ComputeAmountThatFits(content.GetObjectId());
        inventory.AddItems(room, content);
        return $"FILL {(double)room:0.###}";
    }

    [Command("clear", "Empties the character's inventory", "!cprobe clear")]
    [Permission(MyPromoteLevel.Admin)]
    public string Clear()
    {
        Inventory(Body).Clear();
        return "CLEAR";
    }

    [Command("inv", "The character's inventory, one line", "!cprobe inv")]
    [Permission(MyPromoteLevel.Admin)]
    public string Inv()
    {
        var body = Body;
        if (body == null)
            return "INV none";
        var items = Inventory(body)
            .GetItems()
            .GroupBy(i => Name(i.Content))
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Sum(i => (double)i.Amount):0.###}");
        return $"INV {body.EntityId} {string.Join(",", items)}";
    }

    [Command("bodies", "The player characters on this server", "!cprobe bodies")]
    [Permission(MyPromoteLevel.Admin)]
    public string Bodies()
    {
        var entities = MyEntities.GetEntities();
        var bodies = entities
            .OfType<MyCharacter>()
            .Concat(
                entities
                    .OfType<MyCubeGrid>()
                    .SelectMany(g => g.GetFatBlocks<MyCockpit>())
                    .Select(c => c.Pilot)
                    .Where(p => p != null)
            )
            .Where(b => b.GetPlayerIdentityId() != 0 && !b.IsDead && !b.MarkedForClose);
        return "BODIES "
            + string.Join(
                ",",
                bodies.Select(b =>
                    $"{b.EntityId}:{b.GetPlayerIdentityId()}:{(b.Parent is MyCockpit ? "seated" : "standing")}"
                )
            );
    }

    // Seating the client in a grid it is still receiving crashes its HUD, so this only names the
    // nearest seat and its grid: SeatIn after the client has the grid
    [Command("seat", "The nearest free seat within 500 m", "!cprobe seat")]
    [Permission(MyPromoteLevel.Admin)]
    public string Seat()
    {
        var at = Body.PositionComp.GetPosition();
        var seat = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .SelectMany(g => g.GetFatBlocks<MyCockpit>())
            .Where(c => c.Pilot == null)
            .OrderBy(c => Vector3D.DistanceSquared(c.PositionComp.GetPosition(), at))
            .FirstOrDefault();
        if (seat == null || Vector3D.Distance(seat.PositionComp.GetPosition(), at) > 500)
            return "SEAT none";
        return $"SEAT {seat.EntityId} {seat.CubeGrid.EntityId}";
    }

    // The nearest station's bounding sphere, as the plugin takes it
    private BoundingSphereD NearestSphere()
    {
        var at = Body.PositionComp.GetPosition();
        return MySession
            .Static.Factions.SelectMany(f => f.Value.Stations)
            .Select(s => MyEntities.GetEntityById(s.StationEntityId))
            .OfType<MyCubeGrid>()
            .Select(g => g.PositionComp.WorldVolume)
            .OrderBy(s => Vector3D.DistanceSquared(s.Center, at))
            .First();
    }

    // A direction from the sphere's center with nothing around the point on its edge: no block and
    // no asteroid within the reach of a ship across the edge. The shipyard stands next to the station.
    private static Vector3D ClearDirection(BoundingSphereD sphere)
    {
        const double reach = 20;
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
        {
            if (x == 0 && y == 0 && z == 0)
                continue;
            var direction = Vector3D.Normalize(new Vector3D(x, y, z));
            var around = new BoundingSphereD(sphere.Center + direction * sphere.Radius, reach);
            var entities = new List<MyEntity>();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref around, entities);
            var clear = !entities.Any(e =>
                e is MyVoxelMap || (e is MyCubeGrid grid && BlocksIn(grid, around) > 0)
            );
            if (clear)
                return direction;
        }
        throw new InvalidOperationException("no clear direction");
    }

    private static int BlocksIn(MyCubeGrid grid, BoundingSphereD around)
    {
        var blocks = new HashSet<MySlimBlock>();
        grid.GetBlocksInsideSphere(ref around, blocks);
        return blocks.Count;
    }

    [Command("sphere", "The nearest station's bounding sphere", "!cprobe sphere")]
    [Permission(MyPromoteLevel.Admin)]
    public string Sphere()
    {
        var sphere = NearestSphere();
        var distance = Vector3D.Distance(sphere.Center, Body.PositionComp.GetPosition());
        var clear = ClearDirection(sphere);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"SPHERE {sphere.Center.X} {sphere.Center.Y} {sphere.Center.Z} {sphere.Radius} {distance} {clear.X} {clear.Y} {clear.Z}"
        );
    }

    // A ship across the sphere's edge in the clear direction: the seat 4 m on the given side of the
    // edge, the hull through it to the other side
    [Command("ship", "A ship across the sphere's edge", "!cprobe ship <inside|outside>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Ship(string side)
    {
        var sphere = NearestSphere();
        var clear = ClearDirection(sphere);
        var outside = side == "outside";
        var owner = Context.Caller.IdentityId;
        var grid = new MyObjectBuilder_CubeGrid
        {
            EntityId = 0,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            DisplayName = "Probe ship",
            PositionAndOrientation = new MyPositionAndOrientation(
                sphere.Center + clear * (sphere.Radius + (outside ? 4 : -4)),
                (Vector3)(outside ? -clear : clear),
                (Vector3)Vector3D.CalculatePerpendicularVector(clear)
            ),
        };
        grid.CubeBlocks.Add(
            new MyObjectBuilder_Cockpit
            {
                SubtypeName = "LargeBlockCockpit",
                Min = Vector3I.Zero,
                Owner = owner,
                BuiltBy = owner,
            }
        );
        for (var z = 1; z <= 6; z++)
            grid.CubeBlocks.Add(
                new MyObjectBuilder_CubeBlock
                {
                    SubtypeName = "LargeBlockArmorBlock",
                    Min = new Vector3I(0, 0, -z),
                    BuiltBy = owner,
                }
            );
        var ship = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(grid, true);
        return $"SHIP {ship.GetFatBlocks<MyCockpit>().First().EntityId} {ship.EntityId}";
    }

    [Command("seatin", "Seats the character in a given seat", "!cprobe seatin <entity id>")]
    [Permission(MyPromoteLevel.Admin)]
    public string SeatIn(long seat)
    {
        var cockpit = (MyCockpit)MyEntities.GetEntityById(seat);
        cockpit.AttachPilot(Body, 0);
        MySession.Static.Players.SetControlledEntity(Context.Caller.SteamId, cockpit);
        return $"SEAT {seat}";
    }

    [Command("pilot", "Who sits in a seat", "!cprobe pilot <entity id>")]
    [Permission(MyPromoteLevel.Admin)]
    public string Pilot(long seat) =>
        MyEntities.GetEntityById(seat) is MyCockpit cockpit
            ? $"PILOT {cockpit.Pilot?.EntityId ?? 0}"
            : "PILOT absent";

    [Command("kill", "Kills the character", "!cprobe kill")]
    [Permission(MyPromoteLevel.Admin)]
    public string Kill()
    {
        ((VRage.Game.ModAPI.IMyCharacter)Body).Kill();
        return "KILLED";
    }
}
