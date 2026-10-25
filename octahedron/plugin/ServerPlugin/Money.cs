using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace ServerPlugin;

// OCT's account is real: the economy no longer tops it back up toward 1e9 every economy tick.
// New money comes only from the subsidy per active player and from topping OCT up to its floor.
// Once a UTC day every rich account pays the wealth tax into a held pool. Once a UTC hour a part
// of that pool, the hour's subsidy and OCT's balance above its cap are split equally between the
// players online at any time in the last day.
//
// It runs where the bank accounts are authoritative: a plain server, or the World Authority of a
// cluster, whose banking the nodes forward to. Its state (the tax day, the payout hour, the held
// tax, when each player was last online) is a world variable there, so it is saved and restored
// with the accounts.
internal static class Money
{
    private const string VariableName = "Octahedron-Money";
    private const string WorldAuthorityRole = "WorldAuthority";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static DateTime nextUpdate;
    private static bool missingOctLogged;

    private static OctahedronConfig Config => Plugin.Config;
    public static bool On => Config.Enabled && Config.MoneyEnabled;

    private static bool Authority =>
        !PluginCluster.IsClusterProcess || Plugin.Cluster?.Context.Role == WorldAuthorityRole;

    public static void Update()
    {
        if (!On || DateTime.UtcNow < nextUpdate)
            return;
        if (MySession.Static?.Ready != true || MyBankingSystem.Static == null || !Authority)
            return;
        nextUpdate = DateTime.UtcNow + Interval;
        Run(tax: false, pay: false);
    }

    // !oct taxday and !oct payday: collect the tax or pay the hour out now
    public static string Force(bool tax)
    {
        if (!On)
            return "The money rules are off.";
        if (!Authority)
            return "The money rules run on the World Authority.";
        return Run(tax, pay: !tax) ?? "Done, see the server log.";
    }

    // Returns why nothing ran, or null
    private static string Run(bool tax, bool pay)
    {
        var session = MySession.Static;
        var oct = session.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag);
        if (oct == null)
        {
            if (!missingOctLogged)
                Plugin.Log.Error(
                    $"Money: no faction tagged {Config.MoneyAuthorityFactionTag}, nothing settled"
                );
            missingOctLogged = true;
            return $"No faction tagged {Config.MoneyAuthorityFactionTag}.";
        }
        missingOctLogged = false;

        var state = State.Load();
        var now = DateTime.UtcNow;
        var hour = now.Date.AddHours(now.Hour);
        var nowSeconds = new DateTimeOffset(now).ToUnixTimeSeconds();
        foreach (var identity in OnlinePlayers())
            state.Seen[identity] = nowSeconds;
        var oldest = nowSeconds - (long)Config.MoneyActiveHours * 3_600;
        foreach (var gone in state.Seen.Where(p => p.Value < oldest).Select(p => p.Key).ToList())
            state.Seen.Remove(gone);

        if (state.Day == null)
        {
            // The world's OCT starts with the game's 1e9; giving that away would end the economy
            var balance = Balance(oct.FactionId);
            if (balance > Config.MoneyOctCap)
            {
                Change(oct.FactionId, Config.MoneyOctCap - balance);
                Plugin.Log.Info(
                    $"Money rules start: OCT's balance cut from {Sc(balance)} to its cap {Sc(Config.MoneyOctCap)}"
                );
            }
            state.Day = now.Date;
            state.Hour = hour;
        }
        else
        {
            if (tax || state.Day < now.Date)
            {
                CloseDay(oct.FactionId, state);
                CollectTax(session, oct.FactionId, state);
                state.Day = now.Date;
            }
            if (pay || state.Hour < hour)
            {
                Pay(session, oct.FactionId, state, 24 - now.Hour);
                state.Hour = hour;
            }
        }

        var octBalance = Balance(oct.FactionId);
        if (octBalance < Config.MoneyOctFloor)
        {
            var amount = Config.MoneyOctFloor - octBalance;
            Change(oct.FactionId, amount);
            state.TopUps = checked(state.TopUps + amount);
            Plugin.Log.Info($"Money: OCT topped up by {Sc(amount)} to its floor");
        }
        state.Save();
        return null;
    }

    // The day's totals, logged once when the next day starts
    private static void CloseDay(long octId, State state)
    {
        Plugin.Log.Info(
            $"Money day {state.Day:yyyy-MM-dd} closed: paid out {Sc(state.Paid)} in {state.Payouts} hourly payouts "
                + $"(tax {Sc(state.PaidTax)}, subsidy {Sc(state.Subsidy)}, OCT above its cap {Sc(state.OctExcess)}); "
                + $"OCT topped up {Sc(state.TopUps)}; tax held {Sc(state.Held)}; OCT now {Sc(Balance(octId))}, "
                + $"all accounts {Sc(MyBankingSystem.Static.OverallBalance)}"
        );
        state.Paid = state.PaidTax = state.Subsidy = state.OctExcess = state.TopUps = 0;
        state.Payouts = 0;
    }

    private static void CollectTax(MySession session, long octId, State state)
    {
        var factions = session
            .Factions.Select(p => p.Value)
            .Where(f => f.FactionId != octId && !f.IsEveryoneNpc())
            .Select(f => f.FactionId)
            .ToHashSet();
        var taxed = session
            .Players.GetAllIdentities()
            .Select(i => i.IdentityId)
            .Where(id => !session.Players.IdentityIsNpc(id))
            .Select(id => (Id: id, Threshold: Config.MoneyPlayerTaxThreshold))
            .Concat(factions.Select(id => (Id: id, Threshold: Config.MoneyFactionTaxThreshold)))
            .Where(a => HasAccount(a.Id))
            .Select(a => (a.Id, Balance(a.Id), a.Threshold));
        var (taxes, total) = MoneyMath.Taxes(taxed, Config.MoneyWealthTaxBasisPoints);
        var held = checked(state.Held + total);

        var failed = 0;
        long collected = 0;
        foreach (var (id, amount) in taxes)
            if (Change(id, -amount))
                collected += amount;
            else
                failed++;
        state.Held = held - (total - collected);

        var factionTax = taxes.Where(t => factions.Contains(t.Id)).ToList();
        var factionTotal = factionTax.Sum(t => t.Amount);
        Plugin.Log.Info(
            $"Money tax {DateTime.UtcNow:yyyy-MM-dd}: {Sc(total - factionTotal)} from {taxes.Count - factionTax.Count} players "
                + $"and {Sc(factionTotal)} from {factionTax.Count} factions; tax held {Sc(state.Held)}"
                + (failed > 0 ? $"; {failed} balance changes FAILED" : "")
        );
    }

    private static void Pay(MySession session, long octId, State state, int hoursLeft)
    {
        var recipients = state
            .Seen.Keys.Where(id => HasAccount(id) && !session.Players.IdentityIsNpc(id))
            .OrderBy(id => id)
            .ToList();
        var p = MoneyMath.Pay(
            state.Held,
            hoursLeft,
            Balance(octId),
            Config.MoneyOctCap,
            Config.MoneySubsidyPerActivePlayer,
            recipients
        );

        var failed = 0;
        failed += Change(octId, -p.OctExcess) ? 0 : 1;
        if (p.Share > 0)
            foreach (var id in recipients)
                failed += Change(id, p.Share) ? 0 : 1;
        checked
        {
            state.Held = state.Held - p.Tax + p.Rest;
            state.Paid += p.Share * recipients.Count;
            state.PaidTax += p.Tax;
            state.Subsidy += p.Subsidy;
            state.OctExcess += p.OctExcess;
        }
        state.Payouts++;

        Plugin.Log.Info(
            $"Money hour {DateTime.UtcNow:yyyy-MM-dd HH}:00: tax {Sc(p.Tax)}, subsidy {Sc(p.Subsidy)}, "
                + $"OCT above its cap {Sc(p.OctExcess)}, pool {Sc(p.Pool)} to {recipients.Count} active players "
                + $"at {Sc(p.Share)} each; tax held {Sc(state.Held)}"
                + (failed > 0 ? $"; {failed} balance changes FAILED" : "")
        );
    }

    // Identity ids of the players online anywhere: the World Authority's merged list on a
    // cluster, this server's players otherwise
    private static IEnumerable<long> OnlinePlayers()
    {
        if (Plugin.Cluster?.OnlinePlayers() is { } cluster)
            return cluster.Select(p => p.IdentityId);
        var players = new List<MyPlayer>();
        MySession.Static.Players.GetOnlineHumanPlayers(players);
        return players.Select(p => p.Identity.IdentityId);
    }

    public static IEnumerable<string> Describe(long caller)
    {
        if (!On)
            yield return "The money rules are off.";
        var oct = MySession.Static.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag);
        if (oct != null)
            yield return $"OCT: {Sc(Balance(oct.FactionId))} (floor {Sc(Config.MoneyOctFloor)}, cap {Sc(Config.MoneyOctCap)})";
        yield return $"You: {Sc(Balance(caller))}";
        yield return $"All accounts: {Sc(MyBankingSystem.Static.OverallBalance)}";
        if (!Authority)
        {
            yield return "Run on the World Authority.";
            yield break;
        }
        var state = State.Load();
        yield return $"Tax day: {state.Day?.ToString("yyyy-MM-dd") ?? "none"}, tax held: {Sc(state.Held)}";
        yield return $"Last payout hour: {state.Hour?.ToString("yyyy-MM-dd HH") ?? "none"}, paid out today: {Sc(state.Paid)}";
        yield return $"Active players (last {Config.MoneyActiveHours} hours): {state.Seen.Count}";
        yield return $"OCT topped up today: {Sc(state.TopUps)}";
    }

    private static bool HasAccount(long id) => MyBankingSystem.Static.TryGetAccountInfo(id, out _);

    private static long Balance(long id) =>
        MyBankingSystem.Static.TryGetAccountInfo(id, out var account) ? account.Balance : 0;

    private static bool Change(long id, long amount) =>
        amount == 0 || MyBankingSystem.ChangeBalance(id, amount);

    private static string Sc(long amount) =>
        amount.ToString("N0", CultureInfo.InvariantCulture) + " SC";

    // day=2026-10-25;hour=2026-10-25T13;held=0;paid=0;...;seen=<identity>:<unix seconds>,...
    private sealed class State
    {
        private const string DayFormat = "yyyy-MM-dd";
        private const string HourFormat = "yyyy-MM-dd'T'HH";

        public DateTime? Day;
        public DateTime? Hour;

        // Tax collected and not paid out yet
        public long Held;

        // The day's totals, for its log line
        public long Paid;
        public long PaidTax;
        public long Subsidy;
        public long OctExcess;
        public long TopUps;
        public long Payouts;
        public readonly Dictionary<long, long> Seen = new();

        private static long Number(string text) => long.Parse(text, CultureInfo.InvariantCulture);

        private static DateTime Time(string text, string format) =>
            DateTime.ParseExact(text, format, CultureInfo.InvariantCulture);

        public static State Load()
        {
            var state = new State();
            if (!MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var text))
                return state;
            try
            {
                foreach (var part in text.Split(';'))
                {
                    var split = part.IndexOf('=');
                    var (key, value) = (part.Substring(0, split), part.Substring(split + 1));
                    if (value.Length == 0)
                        continue;
                    switch (key)
                    {
                        case "day":
                            state.Day = Time(value, DayFormat);
                            break;
                        case "hour":
                            state.Hour = Time(value, HourFormat);
                            break;
                        case "held":
                            state.Held = Number(value);
                            break;
                        case "paid":
                            state.Paid = Number(value);
                            break;
                        case "paidtax":
                            state.PaidTax = Number(value);
                            break;
                        case "subsidy":
                            state.Subsidy = Number(value);
                            break;
                        case "octexcess":
                            state.OctExcess = Number(value);
                            break;
                        case "topups":
                            state.TopUps = Number(value);
                            break;
                        case "payouts":
                            state.Payouts = Number(value);
                            break;
                        case "seen":
                            foreach (
                                var seen in value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            )
                            {
                                var pair = seen.Split(':');
                                state.Seen[Number(pair[0])] = Number(pair[1]);
                            }
                            break;
                    }
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                // A fresh state pays nobody seen before; the log keeps the held tax it loses
                Plugin.Log.Error($"Money: unreadable state, starting over: {text}", e);
                return new State();
            }
            return state;
        }

        public void Save()
        {
            string N(long value) => value.ToString(CultureInfo.InvariantCulture);
            MyAPIGateway.Utilities.SetVariable(
                VariableName,
                $"day={Day?.ToString(DayFormat, CultureInfo.InvariantCulture)};"
                    + $"hour={Hour?.ToString(HourFormat, CultureInfo.InvariantCulture)};"
                    + $"held={N(Held)};paid={N(Paid)};paidtax={N(PaidTax)};subsidy={N(Subsidy)};"
                    + $"octexcess={N(OctExcess)};topups={N(TopUps)};payouts={N(Payouts)};seen="
                    + string.Join(",", Seen.Select(p => $"{N(p.Key)}:{N(p.Value)}"))
            );
        }
    }

    // The economy moves every station owner's balance 40 % toward 1e9 each economy tick
    [HarmonyPatch(typeof(MySessionComponentEconomy), "UpdateUpdateFactionBalance")]
    private static class NoBalanceRecovery
    {
        private static bool Prefix(MyFaction faction) =>
            !(On && faction.Tag == Config.MoneyAuthorityFactionTag);
    }

    // New player accounts; a cluster node forwards the call with its amount to the World Authority
    [HarmonyPatch(typeof(MyBankingSystem), nameof(MyBankingSystem.CreateAccount), typeof(long))]
    private static class StartingBalance
    {
        private static bool Prefix(MyBankingSystem __instance, long ownerIdentifier)
        {
            if (!On)
                return true;
            __instance.CreateAccount(ownerIdentifier, Config.MoneyStartingBalance);
            return false;
        }
    }
}
