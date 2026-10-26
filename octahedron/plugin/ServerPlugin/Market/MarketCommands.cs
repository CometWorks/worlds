using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// Admin commands on the station markets, for tests and support; players trade at the kiosks.
// Owners and stations as in the ledger commands; amounts in units, prices in SC per unit or as a
// percent of the reference price ("80%").
[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class MarketCommands : CommandModule
{
    [Command(
        "market show",
        "Shows a station's market: OCT's prices and stock, the best listings, an owner's listings",
        "!oct market show <station> [owner] [all]: all lists every catalog item, not only those in stock or listed."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Show(string station, string owner = "me", string all = null)
    {
        if (
            !LedgerCommands.Station(ref station, out string error)
            || !LedgerCommands.Owner(
                Context.Caller.IdentityId,
                owner,
                false,
                out var who,
                out error
            )
        )
        {
            Context.Respond(CommandReply.Error(error));
            return;
        }
        var context = Context;
        string name = station;
        Market.Read(
            station,
            who.Value,
            null,
            (text, failure) =>
            {
                if (text == null)
                {
                    context.Respond(CommandReply.Error(failure));
                    return;
                }
                foreach (var line in Describe(name, text, all != null))
                    context.Respond(line);
            }
        );
    }

    private static IEnumerable<string> Describe(string station, string view, bool all)
    {
        if (view.Length == 0)
        {
            yield return "The market is closed.";
            yield break;
        }
        yield return $"{station} market:";
        foreach (var line in view.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            long L(int i) => long.Parse(f[i], CultureInfo.InvariantCulture);
            switch (f[0])
            {
                case "market" when all || L(5) > 0 || L(7) > 0 || L(9) > 0:
                    yield return $"  {f[1]}: OCT buys {MarketBook.Price(L(3))}{(f[10] == "1" ? " (specialty)" : "")}, "
                        + $"sells {MarketBook.Price(L(4))}, stock {LedgerBook.Units(L(5))}"
                        + (
                            L(7) > 0
                                ? $"; offer {LedgerBook.Units(L(7))} at {MarketBook.Price(L(6))}"
                                : ""
                        )
                        + (
                            L(9) > 0
                                ? $"; order {LedgerBook.Units(L(9))} at {MarketBook.Price(L(8))}"
                                : ""
                        );
                    break;
                case "listing":
                    yield return $"  listing {f[1]} {f[2]} {f[3]} {LedgerBook.Units(L(5))} at {MarketBook.Price(L(6))}, "
                        + $"expires {DateTimeOffset.FromUnixTimeSeconds(L(8)):yyyy-MM-dd HH:mm} UTC";
                    break;
                case "balance":
                    yield return $"  balance {MarketBook.Sc(L(1))}";
                    break;
            }
        }
    }

    [Command(
        "market do",
        "Makes a market request for an owner, as a kiosk does",
        "!oct market do <buy|sell|offer|order|cancel> <station> <owner> <item|listing> [amount] [price]: amount 0 sells or offers all."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Do(
        string verb,
        string station,
        string owner,
        string what,
        string amount = "0",
        string price = null
    )
    {
        var request = new MarketRequest();
        if (
            !Enum.TryParse(verb, true, out request.Verb)
            || !KioskRules.IsMarket(request.Verb)
            || request.Verb == KioskVerb.Market
        )
        {
            Context.Respond(CommandReply.Error($"Not a market request: {verb}"));
            return;
        }
        if (
            !LedgerCommands.Station(ref station, out string error)
            || !LedgerCommands.Owner(
                Context.Caller.IdentityId,
                owner,
                false,
                out var who,
                out error
            )
        )
        {
            Context.Respond(CommandReply.Error(error));
            return;
        }
        request.Station = station;
        request.Owner = who.Value;
        if (request.Verb == KioskVerb.Cancel)
            request.Listing = what;
        else
            request.Item = what;
        try
        {
            // The kiosk's own parsing: amount and price fields as a kiosk sends them
            var parsed = KioskRequest.Parse(
                $"{verb}\t0\t0\t0\t\t{what}\t{(amount == "0" ? "" : amount)}\t{price}"
            );
            request.Amount = parsed.Amount;
            request.Price = parsed.Price;
            request.PricePercent = parsed.PricePercent;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            Context.Respond(CommandReply.Error(e.Message));
            return;
        }
        var context = Context;
        Market.Execute(
            Guid.NewGuid(),
            request,
            (ok, message) =>
                context.Respond(ok ? CommandReply.Ok(message) : CommandReply.Error(message))
        );
    }

    [Command(
        "market tick",
        "Closes expired listings now, and with force restocks OCT as on a new day",
        "!oct market tick [force]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Tick(string force = null) =>
        Market.On ? Market.Tick(force != null) : "The market is closed.";

    [Command(
        "market stores",
        "Counts the game's store items of OCT's stations; with tick runs the game's economy tick first",
        "!oct market stores [tick]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Stores(string tick = null)
    {
        if (tick != null)
            MySession.Static.GetComponent<MySessionComponentEconomy>()?.ForceEconomyTick();
        var oct = MySession.Static.Factions.TryGetFactionByTag(
            Plugin.Config.MoneyAuthorityFactionTag
        );
        if (oct == null)
            return $"No faction tagged {Plugin.Config.MoneyAuthorityFactionTag}.";
        return string.Join(
            "\n",
            oct.Stations.OfType<MyFactionStation>()
                .OrderBy(s => s.Name)
                .Select(s => $"STORE {s.Name.Split(' ')[0]} {s.StoreItems.Count}")
        );
    }

    [Command(
        "market age",
        "Makes a listing older by some days, to test its expiry",
        "!oct market age <listing> <days>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Age(string listing, double days)
    {
        if (!Ledgers.IsAuthority)
            return "The market runs on the World Authority.";
        var l = Market.Book.Get(listing);
        if (l == null)
            return $"No listing {listing}.";
        l.Created -= (long)(days * 86_400);
        return $"Listing {listing} is {days.ToString(CultureInfo.InvariantCulture)} days older.";
    }
}
