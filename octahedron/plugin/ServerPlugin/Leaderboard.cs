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
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace ServerPlugin;

/// <summary>
/// The faction leaderboard on the station panels named <see cref="OctahedronConfig.LeaderboardBlockName"/>:
/// the factions ranked by their bank account, with the money held for their market orders, plus
/// their grids valued at the stations' prices, and their PCU. A grid is its biggest owner's
/// faction's; its value is its blocks' mounted components and its inventories, each item at the
/// middle of OCT's bid and ask as the stations' markets show them (a specialty's bid while the day's
/// quota lasts), averaged over the stations. NPC factions and OCT are left out.
///
/// Every <see cref="OctahedronConfig.LeaderboardIntervalSeconds"/> the server counts its grids,
/// a few per tick, and writes the board on the panels. On a cluster each node counts the grids it
/// simulates: it gets the prices from the World Authority, which keeps the market and the bank
/// accounts, sends it the totals per faction and gets the ranked factions back, summed over the
/// latest totals of every node.
/// </summary>
internal static class Leaderboard
{
    private const string PricesTopic = "leaderboard/prices";
    private const string ReportTopic = "leaderboard/report";
    private const string LocalSource = "local";
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

    // On the authority: the latest totals of every server, by its name
    private static readonly Dictionary<
        string,
        (DateTime At, Dictionary<long, FactionGrids> Grids)
    > reports = new();

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
            reports.Clear();
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
        Stations.Ask(
            ReportTopic,
            LeaderboardMath.EncodeGrids(grids),
            (text, error) =>
            {
                if (text == null)
                {
                    Fail("Leaderboard: the World Authority did not rank the factions: " + error);
                    return;
                }
                Show(LeaderboardMath.DecodeRows(text), panels);
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
        string text = LeaderboardMath.Format(ranked, Config.LeaderboardRows, DateTime.UtcNow);
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

    /// <summary>Keeps the server's totals and ranks the factions by them and every other server's latest.</summary>
    private static List<LeaderboardRow> Board(string source, Dictionary<long, FactionGrids> grids)
    {
        var now = DateTime.UtcNow;
        reports[source] = (now, grids);
        var oldest = now - TimeSpan.FromSeconds(3 * Config.LeaderboardIntervalSeconds);
        foreach (var gone in reports.Where(r => r.Value.At < oldest).Select(r => r.Key).ToList())
            reports.Remove(gone);
        var sum = LeaderboardMath.Sum(reports.Values.Select(r => r.Grids));

        long octId = Market.OctId;
        var rows = new List<LeaderboardRow>();
        foreach (var faction in MySession.Static.Factions.Select(f => f.Value))
        {
            if (faction.FactionId == octId || faction.IsEveryoneNpc())
                continue;
            sum.TryGetValue(faction.FactionId, out var g);
            long balance =
                MyBankingSystem.Static?.TryGetAccountInfo(faction.FactionId, out var account)
                == true
                    ? account.Balance
                    : 0;
            rows.Add(
                new LeaderboardRow
                {
                    Tag = faction.Tag ?? "",
                    Name = faction.Name ?? "",
                    Cash = balance,
                    Grids = g.Value,
                    Pcu = g.Pcu,
                }
            );
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
        public readonly Dictionary<long, FactionGrids> Grids = new();
        public readonly List<MyTextPanel> Panels = new();
        private readonly Dictionary<MyDefinitionId, long> prices = new();
        private readonly Dictionary<MyCubeBlockDefinition, decimal> built = new();
        private readonly Dictionary<long, decimal> milli = new();
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
            if (next < todo.Count)
                return false;
            foreach (var (faction, value) in milli)
            {
                var g = Grids[faction];
                g.Value = LeaderboardMath.Sc(value);
                Grids[faction] = g;
            }
            return true;
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

            var faction =
                grid.BigOwners.Count > 0
                    ? MySession.Static.Factions.GetPlayerFaction(grid.BigOwners[0])
                    : null;
            if (faction == null)
                return;
            Grids.TryGetValue(faction.FactionId, out var g);
            g.Pcu += grid.BlocksPCU;
            Grids[faction.FactionId] = g;
            milli.TryGetValue(faction.FactionId, out var had);
            milli[faction.FactionId] = had + value;
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
