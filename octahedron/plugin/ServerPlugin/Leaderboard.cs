using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace ServerPlugin;

/// <summary>
/// The leaderboard on the station panels named <see cref="OctahedronConfig.LeaderboardBlockName"/>:
/// the factions ranked by their bank account, with the money held for their market orders, plus
/// their grids valued at the stations' prices, and their PCU. A grid is its biggest owner's
/// faction's; its value is its blocks' mounted components and its inventories, each item at the
/// middle of OCT's bid and ask as the stations' markets show them (a specialty's bid while the day's
/// quota lasts), averaged over the stations. Only factions players made are on it: OCT, the NPC
/// factions and those of mods like MES are left out. With
/// <see cref="OctahedronConfig.LeaderboardOctPlayers"/> OCT's players, which every player starts as
/// (OctFaction), are ranked one by one with their own bank account and grids.
///
/// Every <see cref="OctahedronConfig.LeaderboardIntervalSeconds"/> the server counts its grids,
/// a few per tick, and writes the board on the panels. On a cluster each node counts the grids it
/// simulates: it gets the prices from the World Authority, which keeps the market and the bank
/// accounts, sends it every grid's value and PCU and gets the ranked factions back. The World
/// Authority keeps each grid's latest count in its world (<see cref="LeaderboardBook"/>), so the grids
/// in offline partitions count as they were last counted before they went offline (SE-1270).
/// </summary>
internal static class Leaderboard
{
    private const string PricesTopic = "leaderboard/prices";
    private const string ReportTopic = "leaderboard/report";
    private const string LocalSource = "local";
    private const string VariableName = "Octahedron-Leaderboard-Grids";

    // A message holds 64 KiB, a grid's line about 60 bytes
    private const int GridsPerReport = 800;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static OctahedronConfig Config => Plugin.Config;

    public static bool On =>
        Config.Enabled && !string.IsNullOrWhiteSpace(Config.LeaderboardBlockName);

    private static bool IsWorldAuthority => PluginCluster.IsClusterProcess && Stations.IsAuthority;

    private static DateTime nextCount;
    private static bool busy;
    private static Count count;
    private static MySession session;
    private static readonly List<Action<IReadOnlyList<LeaderboardRow>, string>> waiting = new();

    // On the authority, or a plain server: every grid's latest count, loaded from the world on first use
    private static LeaderboardBook book;

    private static IDisposable pricesRegistration;
    private static IDisposable reportRegistration;
    private static bool registrationFailed;

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true)
            return;
        if (session != MySession.Static)
        {
            session = MySession.Static;
            book = null;
            count = null;
            busy = false;
            nextCount = DateTime.MinValue;
        }
        if (IsWorldAuthority)
        {
            Register();
            return;
        }
        if (count != null)
        {
            if (count.Step())
                Report();
            return;
        }
        if (!busy && DateTime.UtcNow >= nextCount)
            Start();
    }

    /// <summary>
    /// !oct leaderboard: counts now, or waits for the count under way; <paramref name="done"/>
    /// gets the ranked factions, or null with why not.
    /// </summary>
    public static void CountNow(Action<IReadOnlyList<LeaderboardRow>, string> done)
    {
        if (!On)
            done(null, "The leaderboard is off.");
        else if (IsWorldAuthority)
            done(null, "The nodes count the leaderboard, ask one of them.");
        else if (MySession.Static?.Ready != true)
            done(null, "The world is not loaded.");
        else
        {
            waiting.Add(done);
            if (!busy)
                Start();
        }
    }

    private static void Start()
    {
        busy = true;
        nextCount = DateTime.UtcNow + TimeSpan.FromSeconds(Config.LeaderboardIntervalSeconds);
        if (!PluginCluster.IsClusterProcess)
        {
            count = new Count(Prices());
            return;
        }
        Stations.Ask(
            PricesTopic,
            "",
            (text, error) =>
            {
                if (text == null)
                {
                    Fail("Leaderboard: no prices from the World Authority: " + error);
                    return;
                }
                count = new Count(LeaderboardMath.DecodePrices(text));
            }
        );
    }

    private static void Report()
    {
        var grids = count.Grids;
        var panels = count.Panels;
        Plugin.Log.Info($"Leaderboard: counted {count}");
        count = null;
        if (!PluginCluster.IsClusterProcess)
        {
            Show(Board(LocalSource, grids), panels);
            return;
        }
        // Every part gets the ranked factions back; the board shows the last answer
        int parts = Math.Max(1, (grids.Count + GridsPerReport - 1) / GridsPerReport);
        int waiting = parts;
        List<LeaderboardRow> ranked = null;
        string failed = null;
        for (int part = 0; part < parts; part++)
            Stations.Ask(
                ReportTopic,
                LeaderboardMath.EncodeGrids(grids.Skip(part * GridsPerReport).Take(GridsPerReport)),
                (text, error) =>
                {
                    if (text == null)
                        failed = error;
                    else
                        ranked = LeaderboardMath.DecodeRows(text);
                    if (--waiting > 0)
                        return;
                    if (ranked == null)
                        Fail(
                            "Leaderboard: the World Authority did not rank the factions: " + failed
                        );
                    else
                        Show(ranked, panels);
                }
            );
    }

    private static void Fail(string why)
    {
        Plugin.Log.Warning(why);
        busy = false;
        nextCount = DateTime.UtcNow + RetryDelay;
        Answer(null, why);
    }

    private static void Show(IReadOnlyList<LeaderboardRow> ranked, List<MyTextPanel> panels)
    {
        busy = false;
        string text = LeaderboardMath.Format(
            ranked,
            Config.LeaderboardRows,
            DateTime.UtcNow,
            Config.LeaderboardOctPlayers
        );
        int written = 0;
        foreach (var panel in panels.Where(p => !p.Closed))
            if (Write(panel, text))
                written++;
        if (written > 0)
            Plugin.Log.Info(
                $"Leaderboard: {ranked.Count} factions on {written} of {panels.Count} panels"
            );
        Answer(ranked, null);
    }

    private static void Answer(IReadOnlyList<LeaderboardRow> ranked, string error)
    {
        var answers = waiting.ToList();
        waiting.Clear();
        foreach (var done in answers)
            done(ranked, error);
    }

    // True when the panel's text changed. Through the panel's surface, as a script writes it: the
    // block's own IMyTextSurface writes its public description, which the surface's text doesn't show.
    private static bool Write(MyTextPanel panel, string text)
    {
        if (panel.PanelComponent is not IMyTextSurface surface)
            return false;
        if (surface.ContentType != ContentType.TEXT_AND_IMAGE)
            surface.ContentType = ContentType.TEXT_AND_IMAGE;
        if (surface.Font != "Monospace")
            surface.Font = "Monospace";
        float size = (float)Config.LeaderboardFontSize;
        if (Math.Abs(surface.FontSize - size) > 0.001f)
            surface.FontSize = size;
        if (surface.Alignment != TextAlignment.LEFT)
            surface.Alignment = TextAlignment.LEFT;
        if (surface.GetText() == text)
            return false;
        surface.WriteText(text);
        return true;
    }

    // --- on the authority ---

    /// <summary>The price of every catalog item, milli-SC per unit, from OCT's prices as the stations' markets show them.</summary>
    private static Dictionary<string, long> Prices()
    {
        var book = Market.Book;
        var world = Market.World;
        var settings = Market.Settings;
        var stations = Stations.StationNames().ToList();
        var prices = new Dictionary<string, long>();
        foreach (var item in Market.Catalog())
        {
            long price = LeaderboardMath.MidPrice(
                stations.Select(s =>
                    (
                        book.OctBid(
                            world,
                            settings,
                            s,
                            item,
                            book.SpecialtyBoughtKg(s) < settings.SpecialtyDailyKg
                        ),
                        book.OctAsk(world, settings, s, item)
                    )
                )
            );
            if (price > 0)
                prices[item] = price;
        }
        return prices;
    }

    // On a cluster, kept in the World Authority's world: the grids in offline partitions are in no report
    private static LeaderboardBook Book
    {
        get
        {
            if (book != null)
                return book;
            book = new LeaderboardBook();
            if (
                PluginCluster.IsClusterProcess
                && MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var saved)
            )
            {
                try
                {
                    book.Load(saved, DateTime.UtcNow);
                    Plugin.Log.Info($"Leaderboard: {book.Count} grids loaded from the world");
                }
                catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
                {
                    Plugin.Log.Error("Leaderboard: unreadable grids, starting over", e);
                    book = new LeaderboardBook();
                }
            }
            return book;
        }
    }

    // The faction a grid counts for, by its biggest owner, or the player for OCT's players; null: none
    private static long? Key(long owner)
    {
        var faction = MySession.Static.Factions.GetPlayerFaction(owner);
        if (faction == null)
            return null;
        return Config.LeaderboardOctPlayers && OctFaction.Member(owner) ? owner : faction.FactionId;
    }

    /// <summary>Keeps the server's grids and ranks the factions by them and every other server's latest.</summary>
    private static List<LeaderboardRow> Board(string source, List<LeaderboardGrid> grids)
    {
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(Config.LeaderboardIntervalSeconds);
        var state = Book;
        bool cluster = PluginCluster.IsClusterProcess;
        // A plain server's count is the whole world
        state.GoneAfter = cluster ? 2 * interval : TimeSpan.Zero;
        state.SilentAfter = 3 * interval;
        state.Offline = cluster ? ContractOffline.Grids(now) : new Dictionary<long, DateTime>();
        state.Report(source, grids, now);
        var current = state.Current(now, out int offline);
        var sum = LeaderboardMath.Sum(current, Key);
        if (cluster)
        {
            MyAPIGateway.Utilities.SetVariable(VariableName, state.Serialize());
            Plugin.Log.Info(
                $"Leaderboard: {grids.Count} grids from {source}, ranking {current.Count} grids, "
                    + $"{offline} in offline partitions"
                    + (state.Offline == null ? " (not known yet)" : "")
            );
        }

        long octId = Market.OctId;
        var rows = new List<LeaderboardRow>();
        LeaderboardRow Row(long key, string tag, string name)
        {
            sum.TryGetValue(key, out var g);
            long balance =
                MyBankingSystem.Static?.TryGetAccountInfo(key, out var account) == true
                    ? account.Balance
                    : 0;
            return new LeaderboardRow
            {
                Tag = tag ?? "",
                Name = name ?? "",
                Cash = balance,
                Grids = g.Value,
                Pcu = g.Pcu,
            };
        }
        var players = MySession.Static.Players;
        foreach (var faction in MySession.Static.Factions.Select(f => f.Value))
        {
            if (faction.FactionId == octId)
            {
                if (Config.LeaderboardOctPlayers)
                    rows.AddRange(
                        faction
                            .Members.Keys.Where(m => !players.IdentityIsNpc(m))
                            .Select(m => Row(m, "", players.TryGetIdentity(m)?.DisplayName))
                    );
            }
            // Only factions players made: economy and mod (MES) factions come from definitions
            // and have a type of their own
            else if (
                faction.FactionType is MyFactionTypes.PlayerMade or MyFactionTypes.None
                && !faction.IsEveryoneNpc()
            )
                rows.Add(Row(faction.FactionId, faction.Tag, faction.Name));
        }
        return LeaderboardMath.Rank(rows);
    }

    private static void Register()
    {
        if (pricesRegistration != null || registrationFailed || PluginCluster.Current == null)
            return;
        pricesRegistration = Plugin.Cluster.RegisterHandler(
            PricesTopic,
            _ => Task.FromResult(Encoding.UTF8.GetBytes(LeaderboardMath.EncodePrices(Prices())))
        );
        reportRegistration = Plugin.Cluster.RegisterHandler(ReportTopic, ServeReport);
        if (pricesRegistration == null || reportRegistration == null)
        {
            registrationFailed = true;
            Plugin.Log.Error("Leaderboard: this cluster has no plugin handlers, no leaderboard");
            return;
        }
        Plugin.Log.Info("Ranking the leaderboard on the World Authority");
    }

    private static Task<byte[]> ServeReport(PluginMessage message)
    {
        var grids = LeaderboardMath.DecodeGrids(Encoding.UTF8.GetString(message.Payload ?? []));
        var rows = Board(message.Source ?? "", grids).Take(Config.LeaderboardRows);
        return Task.FromResult(Encoding.UTF8.GetBytes(LeaderboardMath.EncodeRows(rows)));
    }

    // --- counting the grids ---

    /// <summary>One count of this server's grids, a few per tick.</summary>
    private sealed class Count
    {
        // The owned grids; the World Authority sums them by faction
        public readonly List<LeaderboardGrid> Grids = new();
        public readonly List<MyTextPanel> Panels = new();
        private readonly Dictionary<MyDefinitionId, long> prices = new();
        private readonly Dictionary<MyCubeBlockDefinition, decimal> built = new();
        private readonly List<MyCubeGrid> todo;
        private readonly Stopwatch work = new();
        private int next;
        private int steps;

        public override string ToString() =>
            $"{todo.Count} grids in {steps} ticks, {work.Elapsed.TotalMilliseconds:0} ms of work";

        public Count(Dictionary<string, long> byName)
        {
            foreach (var (item, price) in byName)
                if (Stations.TryItemId(item, out var id))
                    prices[id] = price;
            todo = MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(g => !g.IsPreview && Plugin.Simulated(g))
                .ToList();
        }

        /// <summary>Counts grids for a moment; true when all are counted.</summary>
        public bool Step()
        {
            var watch = Stopwatch.StartNew();
            work.Start();
            while (next < todo.Count && watch.Elapsed < Budget)
                Add(todo[next++]);
            work.Stop();
            steps++;
            return next >= todo.Count;
        }

        private void Add(MyCubeGrid grid)
        {
            if (grid.MarkedForClose || grid.Closed)
                return;
            string name = Config.LeaderboardBlockName;
            decimal value = 0;
            foreach (var block in grid.GetBlocks())
            {
                value += Value(block);
                if (block.FatBlock is not { } fat)
                    continue;
                if (
                    fat is MyTextPanel panel
                    && string.Equals(
                        panel.CustomName?.ToString(),
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    Panels.Add(panel);
                for (int i = 0; i < fat.InventoryCount; i++)
                    foreach (var item in fat.GetInventory(i).GetItems())
                        if (prices.TryGetValue(item.Content.GetId(), out long price))
                            value += LeaderboardMath.Value(item.Amount.RawValue, price);
            }

            if (grid.BigOwners.Count == 0)
                return;
            Grids.Add(
                new LeaderboardGrid
                {
                    Id = grid.EntityId,
                    Owner = grid.BigOwners[0],
                    Milli = LeaderboardMath.Milli(value),
                    Pcu = grid.BlocksPCU,
                }
            );
        }

        // The block's mounted components, milli-SC
        private decimal Value(MySlimBlock block)
        {
            var definition = block.BlockDefinition;
            bool full = block.ComponentStack.IsFullIntegrity;
            if (full && built.TryGetValue(definition, out decimal known))
                return known;
            decimal value = 0;
            var stack = block.ComponentStack;
            for (int i = 0; i < stack.GroupCount; i++)
            {
                var group = stack.GetGroupInfo(i);
                if (prices.TryGetValue(group.Component.Id, out long price))
                    value += (decimal)group.MountedCount * price;
            }
            if (full)
                built[definition] = value;
            return value;
        }
    }
}
