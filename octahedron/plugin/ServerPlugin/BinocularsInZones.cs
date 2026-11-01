using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.ObjectBuilders.Components;

namespace ServerPlugin;

// The Binoculars mod's tool is a rifle to the game, so a zone that forbids shooting refuses its
// zoom as well. That refusal is the client's own, in MyAutomaticRifleGun.CanShoot, and clients have
// no plugin: all it looks at is whether its copy of the zone holds the character. So while a player
// holds binoculars, the server tells the clients that the zones don't hold that character, as
// ShipyardZoneWelding.ShowBeams does for the shipyard. The server keeps its own list, so a rifle it
// is asked to fire stays refused, and so does anything else the zone forbids.
//
// A zone streamed to a client comes with the server's list, and a character entering a zone is sent
// as an insert, so the message repeats while the binoculars are held.
internal static class BinocularsInZones
{
    private const string Subtype = "BinocularsItem";
    private const int CheckFrames = 10;
    private const int RepeatFrames = 60;

    private static readonly HashSet<(MySafeZone Zone, MyCharacter Character)> told = [];
    private static readonly HashSet<(MySafeZone Zone, MyCharacter Character)> holding = [];
    private static int frame;

    internal static void Update()
    {
        if (++frame % CheckFrames != 0)
            return;

        holding.Clear();
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            var character = player.Character;
            if (character?.CurrentWeapon?.PhysicalItemDefinition?.Id.SubtypeName != Subtype)
                continue;
            foreach (var zone in MySessionComponentSafeZones.SafeZones)
                if (!zone.IsActionAllowed(character, MySafeZoneAction.Shooting))
                    holding.Add((zone, character));
        }

        var repeat = frame % RepeatFrames == 0;
        foreach (var pair in holding)
            if (told.Add(pair) || repeat)
                ShipyardZoneWelding.ShowOutside(pair.Zone, pair.Character.EntityId, true);

        told.RemoveWhere(pair =>
        {
            if (holding.Contains(pair))
                return false;
            // Still in the zone with something else in hand
            if (
                !pair.Zone.Closed
                && !pair.Character.MarkedForClose
                && !pair.Zone.IsActionAllowed(pair.Character, MySafeZoneAction.Shooting)
            )
                ShipyardZoneWelding.ShowOutside(pair.Zone, pair.Character.EntityId, false);
            return true;
        });
    }
}
