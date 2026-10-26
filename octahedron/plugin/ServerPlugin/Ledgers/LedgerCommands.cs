using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// Admin commands on the station ledgers, for tests and support; players get kiosks. An owner is
// "me", a player's name, "faction:TAG", or P<identity id> / F<faction id>. A station is its name
// or the start of it ("Alpha"). Amounts are in units, for example 1.5 of an ingot.
[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class LedgerCommands : CommandModule
{
    [Command("ledger stations", "Lists the stations that keep ledgers")]
    [Permission(MyPromoteLevel.Admin)]
    public string Stations() => string.Join(", ", Ledgers.StationNames().OrderBy(s => s));

    [Command(
        "ledger show",
        "Shows a station's ledger, everyone's or one owner's",
        "!oct ledger show <station> [owner]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Show(string station, string owner = null)
    {
        if (!Station(ref station, out string error) || !Owner(owner, true, out var only, out error))
        {
            Context.Respond(CommandReply.Error(error));
            return;
        }
        var context = Context;
        Ledgers.Read(
            station,
            only,
            (copy, failure) =>
            {
                if (copy == null)
                {
                    context.Respond(CommandReply.Error(failure));
                    return;
                }
                foreach (var line in Describe(copy, station))
                    context.Respond(line);
            }
        );
    }

    [Command(
        "ledger deposit",
        "Adds items to an owner's ledger",
        "!oct ledger deposit <station> <owner> <item> <amount>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Deposit(string station, string owner, string item, string amount) =>
        Run(LedgerAction.Deposit, station, owner, null, null, false, item, amount);

    [Command(
        "ledger withdraw",
        "Takes items out of an owner's ledger",
        "!oct ledger withdraw <station> <owner> <item> <amount>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Withdraw(string station, string owner, string item, string amount) =>
        Run(LedgerAction.Withdraw, station, owner, null, null, false, item, amount);

    [Command(
        "ledger transfer",
        "Moves items from one owner's ledger to another's",
        "!oct ledger transfer <station> <from> <to> <item> <amount>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Transfer(string station, string from, string to, string item, string amount) =>
        Run(LedgerAction.Transfer, station, from, to, null, false, item, amount);

    [Command(
        "ledger reserve",
        "Reserves an owner's items under a reservation id, as a listing does",
        "!oct ledger reserve <station> <owner> <id> <item> <amount> [counted]: counted (default) keeps the items in the owner's limit."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Reserve(
        string station,
        string owner,
        string id,
        string item,
        string amount,
        bool counted = true
    ) => Run(LedgerAction.Reserve, station, owner, null, id, counted, item, amount);

    [Command(
        "ledger fill",
        "Adds items straight to a reservation, as buying into a build ledger does",
        "!oct ledger fill <station> <owner> <id> <item> <amount> [counted]: not counted by default."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Fill(
        string station,
        string owner,
        string id,
        string item,
        string amount,
        bool counted = false
    ) => Run(LedgerAction.Fill, station, owner, null, id, counted, item, amount);

    [Command(
        "ledger release",
        "Returns a reservation, all of it or some items, to its owner",
        "!oct ledger release <station> <owner> <id> [item amount]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Release(
        string station,
        string owner,
        string id,
        string item = null,
        string amount = null
    ) => Run(LedgerAction.Release, station, owner, null, id, false, item, amount);

    [Command(
        "ledger give",
        "Moves a reservation, all of it or some items, to another owner, as a sale does",
        "!oct ledger give <station> <owner> <id> <to> [item amount]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Give(
        string station,
        string owner,
        string id,
        string to,
        string item = null,
        string amount = null
    ) => Run(LedgerAction.Give, station, owner, to, id, false, item, amount);

    [Command(
        "ledger take",
        "Takes a reservation, all of it or some items, out of the ledger, as a build does",
        "!oct ledger take <station> <owner> <id> [item amount]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void Take(
        string station,
        string owner,
        string id,
        string item = null,
        string amount = null
    ) => Run(LedgerAction.Take, station, owner, null, id, false, item, amount);

    private void Run(
        LedgerAction action,
        string station,
        string owner,
        string other,
        string reservation,
        bool counted,
        string item,
        string amount
    )
    {
        var op = new LedgerOp
        {
            Action = action,
            Reservation = reservation,
            Counted = counted,
        };
        LedgerOwner? to = null;
        if (
            !Station(ref station, out string error)
            || !Owner(owner, false, out var from, out error)
            || (other != null && !Owner(other, false, out to, out error))
            || !Item(item, amount, op, out error)
        )
        {
            Context.Respond(CommandReply.Error(error));
            return;
        }
        op.Station = station;
        op.Owner = from.Value;
        op.Other = to ?? default;
        var context = Context;
        Ledgers.Execute(
            Guid.NewGuid(),
            [op],
            result =>
                context.Respond(
                    result.Applied
                        ? CommandReply.Ok($"Ledger {action} done.")
                        : CommandReply.Error($"Ledger {action} {result.Outcome}: {result.Message}")
                )
        );
    }

    private static bool Item(string item, string amount, LedgerOp op, out string error)
    {
        error = null;
        if (item == null)
            return true;
        if (
            amount == null
            || !decimal.TryParse(
                amount,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal units
            )
        )
        {
            error = $"Not an amount: {amount}";
            return false;
        }
        try
        {
            op.Add(item, checked((long)(units * LedgerBook.Unit)));
            return true;
        }
        catch (OverflowException)
        {
            error = "The amount is too large.";
            return false;
        }
    }

    internal static bool Station(ref string station, out string error)
    {
        error = null;
        string given = station;
        var names = Ledgers.StationNames().ToList();
        var starting = names
            .Where(n => n.StartsWith(given, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var match =
            names.FirstOrDefault(n => n == given) ?? (starting.Count == 1 ? starting[0] : null);
        if (match == null)
        {
            error = $"No station '{given}'. Stations: {string.Join(", ", names)}";
            return false;
        }
        station = match;
        return true;
    }

    private bool Owner(string text, bool optional, out LedgerOwner? owner, out string error) =>
        Owner(Context.Caller.IdentityId, text, optional, out owner, out error);

    internal static bool Owner(
        long caller,
        string text,
        bool optional,
        out LedgerOwner? owner,
        out string error
    )
    {
        owner = null;
        error = null;
        if (text == null)
        {
            if (optional)
                return true;
            error = "No owner given.";
            return false;
        }
        var session = MySession.Static;
        if (text.Equals("me", StringComparison.OrdinalIgnoreCase))
            owner = LedgerOwner.Player(caller);
        else if (text.StartsWith("faction:", StringComparison.OrdinalIgnoreCase))
        {
            var faction = session.Factions.TryGetFactionByTag(text.Substring(8));
            if (faction != null)
                owner = LedgerOwner.OfFaction(faction.FactionId);
        }
        else if (LedgerOwner.TryParse(text, out var parsed))
            owner = parsed;
        else
        {
            var identity = session
                .Players.GetAllIdentities()
                .FirstOrDefault(i =>
                    string.Equals(i.DisplayName, text, StringComparison.OrdinalIgnoreCase)
                );
            if (identity != null)
                owner = LedgerOwner.Player(identity.IdentityId);
        }
        if (owner == null)
            error = $"No player or faction '{text}'.";
        return owner != null;
    }

    public static IEnumerable<string> Describe(LedgerBook copy, string station)
    {
        var owners = copy.OwnersAt(station).ToList();
        if (owners.Count == 0)
        {
            yield return $"{station}: empty.";
            yield break;
        }
        foreach (var owner in owners.OrderBy(o => o.ToString()))
        {
            yield return $"{station}, {OwnerName(owner)} ({owner}): {LedgerBook.Tonnes(copy.MassKg(station, owner))}";
            foreach (var item in copy.FreeOf(station, owner).OrderBy(i => i.Key))
                yield return $"  {item.Key} {LedgerBook.Units(item.Value)}";
            foreach (var r in copy.ReservationsOf(station, owner).OrderBy(r => r.Key))
                yield return $"  reserved {r.Key}{(r.Value.Counted ? " (counted)" : "")}: "
                    + string.Join(
                        ", ",
                        r.Value.Items.OrderBy(i => i.Key)
                            .Select(i => $"{i.Key} {LedgerBook.Units(i.Value)}")
                    );
        }
    }

    private static string OwnerName(LedgerOwner owner) =>
        owner.Faction
            ? MySession.Static.Factions.TryGetFactionById(owner.Id)?.Tag ?? "unknown faction"
            : MySession.Static.Players.TryGetIdentity(owner.Id)?.DisplayName ?? "unknown player";
}
