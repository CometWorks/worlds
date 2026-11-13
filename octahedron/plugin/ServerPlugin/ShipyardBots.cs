using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.World;

namespace ServerPlugin;

// Human players' orders go before the bots' (SE-1207). The bots are the Steam IDs
// ShipyardBotSteamIds names. The queue (BuildBook.Order) puts a human player's order ahead of the
// bots' waiting ones and marks the bot orders it takes the place of as bumped: a bot's build in
// progress or turn, and the last bot order waiting when the queue would be over its size. The
// node that simulates the shipyard ends them at its next check, as the bot's own cancel would, and
// tells the bot, which orders again. A free build's ship is removed and the build doesn't count
// toward the cool-down; a paid build's labor fee comes back and its components are sold for it.
internal static partial class ShipyardOrders
{
    private const string Bumped = "a player's order took its place.";

    private static readonly SteamIdRanges Bots = new(
        nameof(OctahedronConfig.ShipyardBotSteamIds),
        () => Config.ShipyardBotSteamIds
    );

    // The bumped orders that are leaving the queue, waiting for the authority's answer
    private static readonly HashSet<string> bumping = [];

    internal static bool IsBot(long player) =>
        Bots.Contains(MySession.Static.Players.TryGetSteamId(player));

    // The police and janitor bots repair for free (SE-1468)
    private static readonly SteamIdRanges FreeRepairBots = new(
        nameof(OctahedronConfig.ShipyardFreeRepairSteamIds),
        () => Config.ShipyardFreeRepairSteamIds
    );

    // A repair slot this player books is free and unlimited: an admin who ignores PCU limits
    // (SE-1467) or a bot of ShipyardFreeRepairSteamIds (SE-1468)
    internal static bool FreeRepair(long player) =>
        FreePrints.Exempt(player)
        || FreeRepairBots.Contains(MySession.Static.Players.TryGetSteamId(player));

    // Why the player's repair is free, for the chat: "Free repair: <name> <why>"
    internal static string FreeRepairWhy(long player) =>
        FreeRepairBots.Contains(MySession.Static.Players.TryGetSteamId(player))
            ? "is a station's bot"
            : "ignores PCU limits";

    private static void Bump(MyCubeGrid shipyard, BuildOrder entry)
    {
        var told =
            $"Your order '{entry.Ship}' at {shipyard.DisplayName} was canceled: {Bumped} Order again later.";
        if (entry.Phase is BuildPhase.Loading or BuildPhase.After)
        {
            // Before the start nothing was paid; after a paid build's, its components are in the
            // containers and sold for the bot
            EndTurn(
                shipyard,
                entry,
                entry.Phase == BuildPhase.Loading ? told : "Your turn is over."
            );
            return;
        }
        if (bumping.Contains(entry.Id))
            return;
        if (!entry.Active)
        {
            // Nothing was paid for a waiting order
            bumping.Add(entry.Id);
            Finish(entry, told);
        }
        else if (orders.TryGetValue(shipyard.EntityId, out var order) && order.Id == entry.Id)
        {
            bumping.Add(entry.Id);
            order.Canceled = order.Bumped = true;
            var done = BlocksDone(shipyard, order);
            if (!order.Free)
            {
                order.Refund = true;
                KeepParts(shipyard, order);
            }
            Scrap(shipyard, order);
            End(shipyard, order, done);
        }
        else if (!starting.Contains(shipyard.EntityId) && !docks.ContainsKey(shipyard.EntityId))
            // Building on no node, as after a crash: it ends as a lost one does
            Lost(shipyard, entry);
        // Otherwise starting, or docking a finished ship: the next check sees it
    }
}
