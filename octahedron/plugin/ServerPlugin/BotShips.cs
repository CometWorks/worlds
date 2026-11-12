using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace ServerPlugin;

// A bot removes a ship of its own that it gave up (SE-1147): one that is stuck, out of power or
// spent. Without this the ship stays in the world for good, and a fleet of bots leaves hundreds.
// Only bots (ShipyardBotSteamIds) may do it, only to a ship they own, on the node that simulates
// it. A ship with someone else seated in it, or joined by a rotor, piston or hinge to a grid the
// bot doesn't own (a build still on a shipyard's arm), stays. The bot itself is put out of its
// seat first.
internal static class BotShips
{
    internal static string Scrap(long bot, long shipId)
    {
        if (!ShipyardOrders.IsBot(bot))
            return "Bots only.";
        if (MyEntities.GetEntityById(shipId) is not MyCubeGrid ship || ship.MarkedForClose)
            return $"No ship {shipId} here.";
        var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ship);
        if (grids.Any(g => g.IsStatic || (g.BigOwners.Count > 0 && !g.BigOwners.Contains(bot))))
            return $"Ship {shipId} is not yours alone.";
        if (grids.All(g => g.BigOwners.Count == 0))
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
        Plugin.Log.Info(
            $"Bot {MySession.Static.Players.TryGetIdentity(bot)?.DisplayName} scrapped its ship '{name}' ({shipId}), {grids.Count} grid(s)"
        );
        return $"Ship {shipId} scrapped.";
    }
}
