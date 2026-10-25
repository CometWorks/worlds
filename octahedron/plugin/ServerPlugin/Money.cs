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
// New money comes only from the daily subsidy per active player and from topping OCT up to its
// floor. Once a UTC day the wealth tax, the subsidy and OCT's balance above its cap make a pool
// split equally between the players active in the last days.
//
// It runs where the bank accounts are authoritative: a plain server, or the World Authority of a
// cluster, whose banking the nodes forward to. Its state (the last day settled, when each player
// was last online) is a world variable there, so it is saved and restored with the accounts.
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
        Run(force: false);
    }

    // !oct payday: settles now, as if a new day had started
    public static string Payday()
    {
        if (!On)
            return "The money rules are off.";
        if (!Authority)
            return "The money rules run on the World Authority, at 00:00 UTC.";
        return Run(force: true) ?? "Settled, see the server log.";
    }

    // Returns why nothing ran, or null
    private static string Run(bool force)
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
        var nowSeconds = new DateTimeOffset(now).ToUnixTimeSeconds();
        foreach (var identity in OnlinePlayers())
            state.Seen[identity] = nowSeconds;
        var oldest = nowSeconds - (long)Config.MoneyActiveDays * 86_400;
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
        }
        else if (force || state.Day < now.Date)
        {
            Settle(session, oct.FactionId, state);
            state.Day = now.Date;
            state.TopUps = 0;
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

    private static void Settle(MySession session, long octId, State state)
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
        var recipients = state
            .Seen.Keys.Where(id => HasAccount(id) && !session.Players.IdentityIsNpc(id))
            .OrderBy(id => id)
            .ToList();
        var s = MoneyMath.Settle(
            taxed,
            Config.MoneyWealthTaxBasisPoints,
            Balance(octId),
            Config.MoneyOctCap,
            Config.MoneySubsidyPerActivePlayer,
            recipients
        );

        var failed = 0;
        foreach (var (id, amount) in s.Taxes)
            failed += Change(id, -amount) ? 0 : 1;
        failed += Change(octId, s.ToOct - s.OctExcess) ? 0 : 1;
        if (s.Share > 0)
            foreach (var id in s.Recipients)
                failed += Change(id, s.Share) ? 0 : 1;

        var factionTax = s.Taxes.Where(t => factions.Contains(t.Id)).ToList();
        var factionTotal = factionTax.Sum(t => t.Amount);
        Plugin.Log.Info(
            $"Money day {state.Day:yyyy-MM-dd} settled: "
                + $"tax {Sc(s.TaxTotal - factionTotal)} from {s.Taxes.Count - factionTax.Count} players "
                + $"and {Sc(factionTotal)} from {factionTax.Count} factions, "
                + $"OCT above its cap {Sc(s.OctExcess)}, subsidy {Sc(s.Subsidy)}, "
                + $"pool {Sc(s.Pool)} to {s.Recipients.Count} active players at {Sc(s.Share)} each, "
                + $"{Sc(s.ToOct)} left to OCT; OCT topped up {Sc(state.TopUps)} during the day; "
                + $"OCT now {Sc(Balance(octId))}, all accounts {Sc(MyBankingSystem.Static.OverallBalance)}"
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
            yield return "Settled on the World Authority at 00:00 UTC.";
            yield break;
        }
        var state = State.Load();
        yield return $"Last day settled: {state.Day?.ToString("yyyy-MM-dd") ?? "none"}, the next at 00:00 UTC";
        yield return $"Active players (last {Config.MoneyActiveDays} days): {state.Seen.Count}";
        yield return $"OCT topped up since: {Sc(state.TopUps)}";
    }

    private static bool HasAccount(long id) => MyBankingSystem.Static.TryGetAccountInfo(id, out _);

    private static long Balance(long id) =>
        MyBankingSystem.Static.TryGetAccountInfo(id, out var account) ? account.Balance : 0;

    private static bool Change(long id, long amount) =>
        amount == 0 || MyBankingSystem.ChangeBalance(id, amount);

    private static string Sc(long amount) =>
        amount.ToString("N0", CultureInfo.InvariantCulture) + " SC";

    // day=2026-10-25;topups=0;seen=<identity>:<unix seconds>,...
    private sealed class State
    {
        public DateTime? Day;
        public long TopUps;
        public readonly Dictionary<long, long> Seen = new();

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
                    if (key == "day")
                        state.Day = DateTime.ParseExact(
                            value,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture
                        );
                    else if (key == "topups")
                        state.TopUps = long.Parse(value, CultureInfo.InvariantCulture);
                    else if (key == "seen")
                        foreach (
                            var seen in value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        )
                        {
                            var pair = seen.Split(':');
                            state.Seen[long.Parse(pair[0], CultureInfo.InvariantCulture)] =
                                long.Parse(pair[1], CultureInfo.InvariantCulture);
                        }
                }
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                // A fresh state settles nothing for a day and pays nobody seen before
                Plugin.Log.Error($"Money: unreadable state, starting over: {text}", e);
                return new State();
            }
            return state;
        }

        public void Save() =>
            MyAPIGateway.Utilities.SetVariable(
                VariableName,
                $"day={Day:yyyy-MM-dd};topups={TopUps.ToString(CultureInfo.InvariantCulture)};seen="
                    + string.Join(",", Seen.Select(p => $"{p.Key}:{p.Value}"))
            );
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
