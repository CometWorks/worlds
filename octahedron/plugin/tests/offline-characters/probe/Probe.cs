using System;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;

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

    // Into the nearest seat within 500 m, as the player's own use of it
    [Command("seat", "Seats the character in the nearest seat", "!cprobe seat")]
    [Permission(MyPromoteLevel.Admin)]
    public string Seat()
    {
        var body = Body;
        var at = body.PositionComp.GetPosition();
        var seat = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .SelectMany(g => g.GetFatBlocks<MyCockpit>())
            .Where(c => c.Pilot == null)
            .OrderBy(c => VRageMath.Vector3D.DistanceSquared(c.PositionComp.GetPosition(), at))
            .FirstOrDefault();
        if (seat == null || VRageMath.Vector3D.Distance(seat.PositionComp.GetPosition(), at) > 500)
            return "SEAT none";
        seat.AttachPilot(body, 0);
        MySession.Static.Players.SetControlledEntity(Context.Caller.SteamId, seat);
        return $"SEAT {seat.EntityId} {seat.DisplayNameText}";
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
