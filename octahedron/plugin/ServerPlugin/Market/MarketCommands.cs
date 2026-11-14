using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;

namespace ServerPlugin;

// Admin commands on the station markets, for tests and support; players trade at the Store blocks.
// A station is its name or the start of it, amounts are in units.
[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class MarketCommands : CommandModule
{
    [Command(
        "market show",
        "Shows a station's market: OCT's prices, stock and what it still buys",
        "!oct market show <station> [all|item]: all lists every catalog item, not only those in stock; an item lists only that one."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Show(string station, string filter = null)
    {
        if (!Stations.Find(ref station, out string error))
        {
            Context.Respond(CommandReply.Error(error));
            return;
        }
        var context = Context;
        string name = station;
        Market.Read(
            station,
            (text, failure) =>
            {
                if (text == null)
                {
                    context.Respond(CommandReply.Error(failure));
                    return;
                }
                foreach (var line in Describe(name, text, filter))
                    context.Respond(line);
            }
        );
    }

    private static IEnumerable<string> Describe(string station, string view, string filter)
    {
        if (view.Length == 0)
        {
            yield return "The market is closed.";
            yield break;
        }
        yield return $"Market of {station}:";
        foreach (var row in StoreRow.DecodeAll(view))
            if (filter == null ? row.Stock > 0 : filter == "all" || filter == row.Item)
                yield return $"  {row.Item}: OCT buys {MarketBook.Price(row.Bid)} up to {Amounts.Units(row.Wanted)}, "
                    + $"sells {MarketBook.Price(row.Ask)}, stock {Amounts.Units(row.Stock)}"
                    + (
                        row.Lot > 1
                            ? $"; lots of {row.Lot:N0}: buys {MarketBook.Price(row.LotBid * row.Lot)}, "
                                + $"sells {MarketBook.Price(row.LotAsk * row.Lot)}"
                            : ""
                    );
    }

    [Command("market stations", "The stations that have a market", "!oct market stations")]
    [Permission(MyPromoteLevel.Admin)]
    public string StationList() => string.Join(", ", Stations.StationNames().OrderBy(s => s));

    [Command(
        "market set",
        "Sets OCT's stock of an item at a station, on the World Authority of a cluster",
        "!oct market set <station> <item> <units>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Set(string station, string item, double units)
    {
        if (!Market.On)
            return "The market is closed.";
        if (!Stations.IsAuthority)
            return "The market runs on the World Authority.";
        if (!Stations.Find(ref station, out string error))
            return error;
        Market.Book.SetStock(station, item, (long)(units * Amounts.Unit));
        Market.MarkDirty();
        return $"OCT holds {Amounts.Units(Market.Book.Stock(station, item))} {item} at {station}.";
    }

    [Command(
        "market tick",
        "Restocks what is out and tops up zone chips; with force as on a new day",
        "!oct market tick [force]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Tick(string force = null) =>
        Market.On ? Market.Tick(force != null) : "The market is closed.";

    [Command(
        "market stores",
        "Counts the store items of OCT's stations; with tick runs the game's economy tick first; "
            + "with an item, what the Store blocks of the stations on this process ask and bid for it",
        "!oct market stores [tick|item]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Stores(string tick = null)
    {
        var oct = MySession.Static.Factions.TryGetFactionByTag(
            Plugin.Config.MoneyAuthorityFactionTag
        );
        if (oct == null)
            return $"No faction tagged {Plugin.Config.MoneyAuthorityFactionTag}.";
        if (tick != null && tick.Contains("/"))
            return StorePrices(oct, tick);
        if (tick != null)
            MySession.Static.GetComponent<MySessionComponentEconomy>()?.ForceEconomyTick();
        return string.Join(
            "\n",
            oct.Stations.OfType<MyFactionStation>()
                .OrderBy(s => s.Name)
                .Select(s => $"STORE {s.Name.Split(' ')[0]} {s.StoreItems.Count}")
        );
    }

    // One line per station this process simulates, with the node's name on a cluster
    private static string StorePrices(MyFaction oct, string item)
    {
        if (!Stations.TryItemId(item, out var id))
            return $"No item {item}.";
        string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? "server";
        var lines = oct
            .Stations.OfType<MyFactionStation>()
            .Where(s => MyEntities.EntityExists(s.StationEntityId))
            .OrderBy(s => s.Name)
            .Select(s =>
            {
                int Price(StoreItemTypes type) =>
                    s.StoreItems.FirstOrDefault(i =>
                        i.Item.HasValue
                        && (MyDefinitionId)i.Item.Value == id
                        && i.StoreItemType == type
                    )?.PricePerUnit
                    ?? 0;
                return $"STORE {node} {s.Name.Split(' ')[0]} {item} ask {Price(StoreItemTypes.Offer)} bid {Price(StoreItemTypes.Order)}";
            })
            .ToList();
        return lines.Count == 0 ? $"STORE {node} none" : string.Join("\n", lines);
    }
}
