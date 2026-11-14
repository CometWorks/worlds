using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace ServerPlugin;

// A bot removes a ship of its own that it gave up (SE-1147): one that is stuck, out of power or
// spent. Without this the ship stays in the world for good, and a fleet of bots leaves hundreds.
// Only bots (ShipyardBotSteamIds) may do it, on the node that simulates the ship. A ship with
// someone else seated in it, or joined by a rotor, piston or hinge to a grid of somebody else (a
// build still on a shipyard's arm), stays. The bot itself is put out of its seat first.
// A bot may also remove a ship another bot gave up (SE-1526): that bot can do it only from the
// ship's node, and one that went elsewhere left it standing for good, some in a shipyard's
// corridor where finished ships come out. The janitors clear those. Every owner of such a ship
// must be a bot with no character within OwnerNear of it.
internal static class BotShips
{
    // m from its ship within which a bot's character keeps another bot from scrapping it
    private const double OwnerNear = 1000;

    internal static string Scrap(long bot, long shipId)
    {
        if (!ShipyardOrders.IsBot(bot))
            return "Bots only.";
        if (MyEntities.GetEntityById(shipId) is not MyCubeGrid ship || ship.MarkedForClose)
            return $"No ship {shipId} here.";
        var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
        var owners = grids.SelectMany(g => g.BigOwners).Distinct().ToList();
        if (
            grids.Any(g => g.IsStatic)
            || owners.Any(o => o != bot && !(ShipyardOrders.IsBot(o) && !OwnerNearby(o, ship)))
        )
            return $"Ship {shipId} is not yours alone.";
        if (owners.Count == 0)
            return $"Ship {shipId} has no owner.";
        var seats = grids.SelectMany(g => g.GetFatBlocks<MyCockpit>()).ToList();
        if (seats.Any(s => s.Pilot != null && s.Pilot.GetPlayerIdentityId() != bot))
            return $"Someone sits in ship {shipId}.";
        foreach (var seat in seats.Where(s => s.Pilot != null))
        {
            seat.RequestRemovePilot();
            seat.RemovePilot();
        }
        var name = ship.DisplayName;
        foreach (var grid in grids)
            grid.Close();
        var whose = owners.Contains(bot)
            ? "its ship"
            : $"the ship of {string.Join(", ", owners.Select(o => MySession.Static.Players.TryGetIdentity(o)?.DisplayName))}";
        Plugin.Log.Info(
            $"Bot {MySession.Static.Players.TryGetIdentity(bot)?.DisplayName} scrapped {whose} '{name}' ({shipId}), {grids.Count} grid(s)"
        );
        return $"Ship {shipId} scrapped.";
    }

    private static bool OwnerNearby(long owner, MyCubeGrid ship) =>
        MySession.Static.Players.TryGetIdentity(owner)?.Character
            is { MarkedForClose: false, IsDead: false } character
        && Vector3D.Distance(character.PositionComp.GetPosition(), ship.PositionComp.GetPosition())
            < OwnerNear;
}
