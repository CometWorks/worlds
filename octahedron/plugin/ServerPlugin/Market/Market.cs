using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;

namespace ServerPlugin;

/// <summary>
/// The station markets in the game: one <see cref="MarketBook"/> kept by the server itself or the
/// World Authority of a cluster, in the world's sandbox variables. Players trade at the stations'
/// Store blocks with the game's own code, which already settles the money across a cluster. The
/// plugin fills the stations' store items from the book for the stations this process simulates,
/// and reports each trade back to the book. Every minute the authority restocks what is
/// completely out, on a new hour, and tops up the zone chips OCT sells at fixed price.
/// </summary>
internal static class Market
{
    public const string Variable = "Octahedron.Market";
    public const string ZoneChip = "Component/ZoneChip";
    private const string Command = "market";
    private const string ReadTopic = "market/read";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    // How old a node's view of the stock and prices may get
    private static readonly TimeSpan StoreInterval = TimeSpan.FromSeconds(15);

    private static MarketBook book;
    private static MySession bookSession;
    private static bool dirty;
    private static DateTime nextTick;
    private static DateTime nextStores;
    private static IDisposable commandRegistration;
    private static IDisposable readRegistration;
    private static bool registrationFailed;

    private static OctahedronConfig Config => Plugin.Config;
    public static bool On => Config.Enabled && Config.MarketEnabled;

    /// <summary>OCT's faction id, 0 when there is none.</summary>
    public static long OctId =>
        MySession.Static?.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag)?.FactionId
        ?? 0;

    public static MarketSettings Settings =>
        new()
        {
            TaxPercent = Config.MarketTaxPercent,
            OctBuyPercent = Config.MarketOctBuyPercent,
            OctSellPercent = Config.MarketOctSellPercent,
            SpecialtyPercent = Config.MarketSpecialtyPercent,
            SpecialtyDailyKg = Config.MarketSpecialtyDailyTonnes * 1000,
            OctMaxStockKg = Config.MarketOctMaxStockTonnes * 1000,
            DriftKg = Config.MarketPriceDriftTonnes * 1000,
            DriftHalfLifeHours = Config.MarketPriceDriftHalfLifeHours,
            DriftMaxFactor = Config.MarketPriceDriftMax,
            FixedAsks = (Config.ZoneChipStations ?? "")
                .Split(',')
                .Select(station => station.Trim())
                .Where(station => station.Length > 0)
                .Distinct()
                .ToDictionary(
                    station => (station, ZoneChip),
                    _ => Config.ZoneChipPrice * MarketBook.MilliPerSc
                ),
            FixedStock = Config.ZoneChipStock * Amounts.Unit,
        };

    /// <summary>The market, on the authority only, loaded from the world on first use.</summary>
    public static MarketBook Book
    {
        get
        {
            if (!Stations.IsAuthority)
                throw new InvalidOperationException("Only the authority keeps the market");
            var session = MySession.Static;
            if (book == null || bookSession != session)
            {
                book = Load();
                bookSession = session;
                dirty = false;
            }
            return book;
        }
    }

    private static MarketBook Load()
    {
        if (
            MyAPIUtilities.Static.Variables.TryGetValue(Variable, out object value)
            && value is string text
            && text.Length > 0
        )
        {
            try
            {
                return MarketBook.Deserialize(text);
            }
            catch (FormatException e)
            {
                // A world from before the Store blocks; those need a wipe anyway
                Plugin.Log.Warning("Market: the saved one is left out: " + e.Message);
            }
        }
        return new MarketBook();
    }

    /// <summary>The book changed outside this class, by an admin's command.</summary>
    public static void MarkDirty() => dirty = true;

    [HarmonyPatch(typeof(MySession), nameof(MySession.GetCheckpoint))]
    private static class SavePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix()
        {
            try
            {
                if (dirty && book != null && bookSession == MySession.Static)
                {
                    MyAPIUtilities.Static.Variables[Variable] = book.Serialize();
                    dirty = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error("Saving the market failed", e);
            }
        }
    }

    /// <summary>The game world for the book, on the authority.</summary>
    public static readonly IMarketWorld World = new GameWorld();

    // --- trades and reads ---

    private enum Verb
    {
        Bought,
        Sold,
        Add,
    }

    // On the authority: applies a change, returns the station's store as text
    private static string Apply(Verb verb, string station, string item, long raw)
    {
        if (verb == Verb.Bought)
            Book.Bought(World, Settings, station, item, raw);
        else if (verb == Verb.Sold)
            Book.Sold(World, Settings, station, item, raw);
        else
            Book.SetStock(station, item, Book.Stock(station, item) + raw);
        dirty = true;
        Plugin.Log.Info($"Market {verb} at {station}: {item} {Amounts.Units(raw)}");
        return View(station);
    }

    private static void Change(Guid operationId, Verb verb, string station, string item, long raw)
    {
        if (Stations.IsAuthority)
        {
            Show(station, Apply(verb, station, item, raw));
            return;
        }
        Stations.Call(
            Command,
            operationId,
            string.Join(
                "\t",
                verb.ToString(),
                station,
                item,
                raw.ToString(CultureInfo.InvariantCulture)
            ),
            (outcome, text) =>
            {
                if (outcome == CallOutcome.Applied)
                    Show(station, text);
                else
                    Plugin.Log.Error(
                        $"Market {verb} at {station}: {item} {Amounts.Units(raw)}: {outcome} {text}"
                    );
            }
        );
    }

    /// <summary>On the authority: a build's purchase from OCT's stock. A purchase never finds the fixed-price goods sold out between two ticks.</summary>
    public static MarketResult Buy(string station, long buyer, string item, long raw)
    {
        Book.TopUp(Settings, Stations.StationNames());
        var result = Book.Buy(World, Settings, station, buyer, item, raw);
        if (result.Ok)
        {
            dirty = true;
            Plugin.Log.Info(
                $"Market bought at {station} by {buyer}: {item} {Amounts.Units(raw)}: {result.Message}"
            );
        }
        return result;
    }

    /// <summary>On the authority: <see cref="MarketBook.Return"/>.</summary>
    public static void Return(string station, string item, long raw)
    {
        Book.Return(World, Settings, station, item, raw);
        dirty = true;
        Plugin.Log.Info($"Market back at {station}: {item} {Amounts.Units(raw)}");
    }

    /// <summary>On the authority: <see cref="MarketBook.SellOff"/>.</summary>
    public static long SellOff(string station, long seller, IReadOnlyDictionary<string, long> items)
    {
        long net = Book.SellOff(World, Settings, station, seller, items);
        dirty = true;
        Plugin.Log.Info(
            $"Market: left in the shipyard at {station} by {seller}: {string.Join(", ", items.Select(i => $"{i.Key} {Amounts.Units(i.Value)}"))} sold for {MarketBook.Sc(net)}"
        );
        return net;
    }

    /// <summary>Items into OCT's stock, an acquisition contract's; the same id applies once.</summary>
    public static void AddStock(Guid operationId, string station, string item, long raw) =>
        Change(operationId, Verb.Add, station, item, raw);

    /// <summary>The station's store as text to <paramref name="done"/> on the game thread; null with the reason on failure.</summary>
    public static void Read(string station, Action<string, string> done)
    {
        if (!On)
            done("", null);
        else if (Stations.IsAuthority)
            done(View(station), null);
        else
            Stations.Ask(ReadTopic, station, done);
    }

    private static string View(string station) =>
        string.Join("\n", Book.Rows(World, Settings, station).Select(r => r.Encode()));

    public static string ItemTitle(string item) =>
        Stations.TryItemId(item, out var id)
        && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
            ? definition.DisplayNameText
            : item;

    // --- upkeep ---

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true)
            return;
        var now = DateTime.UtcNow;
        if (now >= nextStores && OctId != 0)
        {
            nextStores = now + StoreInterval;
            if (Stations.IsAuthority && now >= nextTick)
            {
                nextTick = now + Interval;
                Register();
                Tick(false);
            }
            else if (!Stations.IsAuthority)
                Register();
            RefreshStores();
        }
    }

    /// <summary>On the authority: restocks what is out on a new hour, or now with force, and tops up OCT's zone chips.</summary>
    public static string Tick(bool force)
    {
        if (!Stations.IsAuthority)
            return "The market runs on the World Authority.";
        if (OctId == 0)
            return $"No faction tagged {Config.MoneyAuthorityFactionTag}.";
        var lines = new List<string>();
        var restock = Book.Restock(World, Settings, Stations.StationNames(), Profile, force);
        if (restock != null)
            lines.Add(restock);
        var topUp = Book.TopUp(Settings, Stations.StationNames());
        if (topUp != null)
            lines.Add(topUp);
        if (lines.Count > 0 || force)
            dirty = true;
        foreach (var line in lines)
            Plugin.Log.Info("Market: " + line);
        if (force)
            RefreshStores();
        return lines.Count == 0 ? "Nothing to do." : string.Join("\n", lines);
    }

    // On a cluster the World Authority serves the nodes' requests
    private static void Register()
    {
        if (
            !Stations.IsAuthority
            || commandRegistration != null
            || registrationFailed
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
        )
            return;
        commandRegistration = Plugin.Cluster.RegisterGlobalCommand(Command, Serve);
        if (commandRegistration == null)
        {
            registrationFailed = true;
            Plugin.Log.Error("This cluster has no global command market: the market is closed");
            return;
        }
        readRegistration = Plugin.Cluster.RegisterHandler(ReadTopic, ServeRead);
        Plugin.Log.Info("Serving the station markets on the World Authority");
    }

    private static byte[] Serve(byte[] payload)
    {
        var f = Encoding.UTF8.GetString(payload).Split('\t');
        return Encoding.UTF8.GetBytes(
            Apply(
                (Verb)Enum.Parse(typeof(Verb), f[0]),
                f[1],
                f[2],
                long.Parse(f[3], CultureInfo.InvariantCulture)
            )
        );
    }

    private static Task<byte[]> ServeRead(PluginMessage message) =>
        Task.FromResult(Encoding.UTF8.GetBytes(View(Encoding.UTF8.GetString(message.Payload ?? []))));

    // --- the game's station stores ---

    private static IEnumerable<MyFactionStation> OctStations() =>
        MySession
            .Static.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag)
            ?.Stations.OfType<MyFactionStation>() ?? [];

    // The stations this process simulates: only their Store blocks can be used here
    private static void RefreshStores()
    {
        foreach (
            var station in OctStations()
                .Where(s => MyEntities.EntityExists(s.StationEntityId))
                .ToList()
        )
            Read(
                station.Name,
                (text, error) =>
                {
                    if (text != null)
                        Show(station.Name, text);
                    else
                        Plugin.Log.Warning($"Market: no store for {station.Name}: {error}");
                }
            );
    }

    // Writes the book's rows into the station's store items, the ones a Store block lists. An item
    // keeps its id, so a client with the Store open can still buy it.
    private static void Show(string stationName, string text)
    {
        var station = OctStations().FirstOrDefault(s => s.Name == stationName);
        if (station == null)
            return;
        var ids = new HashSet<long>();
        void Put(string item, StoreItemTypes type, long raw, long price)
        {
            int amount = (int)Math.Min(int.MaxValue, raw / Amounts.Unit);
            if (amount <= 0 || price <= 0 || !Stations.TryItemId(item, out var itemId))
                return;
            long id = StoreItemId(stationName, item, type);
            ids.Add(id);
            int sc = (int)Math.Min(int.MaxValue, price / MarketBook.MilliPerSc);
            var shown = station.StoreItems.FirstOrDefault(i => i.Id == id);
            if (shown == null)
                station.StoreItems.Add(
                    new MyStoreItem(id, itemId, amount, sc, ItemTypes.PhysicalItem, type)
                );
            else
            {
                shown.Amount = amount;
                shown.PricePerUnit = sc;
            }
        }
        foreach (var row in StoreRow.DecodeAll(text))
        {
            Put(row.Item, StoreItemTypes.Offer, row.Stock, row.Ask);
            Put(row.Item, StoreItemTypes.Order, row.Wanted, row.Bid);
        }
        station.StoreItems.RemoveAll(i => !ids.Contains(i.Id));
    }

    // The same on every node and after a restart: FNV-1a
    private static long StoreItemId(string station, string item, StoreItemTypes type)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in $"{station}|{item}|{type}")
            hash = (hash ^ c) * 1099511628211;
        return (long)(hash & long.MaxValue);
    }

    // The game's generator stays off for OCT's stations, the book fills their stores
    [HarmonyPatch(typeof(MyStoreItemsGenerator), nameof(MyStoreItemsGenerator.Update))]
    private static class NoGeneratedStoreItems
    {
        // ReSharper disable once UnusedMember.Local
        private static bool Prefix(MyFaction faction) =>
            !(On && faction.Tag == Config.MoneyAuthorityFactionTag);
    }

    // Reputation with OCT changes no prices: a discount on one side and a bonus on the other
    // would let a friend make money from the 2 % between them
    [HarmonyPatch(typeof(MySessionComponentEconomy), "GetOffersFriendlyBonus")]
    private static class NoOffersBonus
    {
        // ReSharper disable once UnusedMember.Local
        private static bool Prefix(ref float __result)
        {
            __result = 1f;
            return !On;
        }
    }

    [HarmonyPatch(typeof(MySessionComponentEconomy), "GetOrdersFriendlyBonus")]
    private static class NoOrdersBonus
    {
        // ReSharper disable once UnusedMember.Local
        private static bool Prefix(ref float __result)
        {
            __result = 1f;
            return !On;
        }
    }

    // A station's name by its id, if it is one of OCT's
    private static string OctStation(long stationId) =>
        OctStations().FirstOrDefault(s => s.Id == stationId)?.Name;

    // The game takes the amount off the store item only when a trade goes through. The cluster's
    // own patches of these methods run inside them, around the same lines.
    [HarmonyPatch(typeof(MyStoreBlock), "BuyPhysicalItemInternal")]
    private static class StoreBuy
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(MyStoreItem storeItem, out int __state) =>
            __state = storeItem.Amount;

        // ReSharper disable once UnusedMember.Local
        private static void Postfix(
            int amount,
            long stationId,
            MyStoreItem storeItem,
            int __state
        )
        {
            if (!On || stationId == 0 || storeItem.Amount != __state - amount)
                return;
            string station = OctStation(stationId);
            if (station != null && storeItem.Item is { } item)
                Change(
                    Guid.NewGuid(),
                    Verb.Bought,
                    station,
                    Stations.ItemName(item),
                    amount * Amounts.Unit
                );
        }
    }

    [HarmonyPatch(typeof(MyStoreBlock), "SellToStation")]
    private static class StoreSell
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(long id, IMyFactionStation station, out int __state) =>
            __state = (station as MyFactionStation)?.StoreItems.FirstOrDefault(i => i.Id == id)?.Amount ?? -1;

        // ReSharper disable once UnusedMember.Local
        private static void Postfix(long id, int amount, IMyFactionStation station, int __state)
        {
            if (!On || station == null || OctStation(station.Id) == null)
                return;
            var shown = (station as MyFactionStation)?.StoreItems.FirstOrDefault(i => i.Id == id);
            if (shown?.Item is { } item && shown.Amount == __state - amount)
                Change(
                    Guid.NewGuid(),
                    Verb.Sold,
                    station.Name,
                    Stations.ItemName(item),
                    amount * Amounts.Unit
                );
        }
    }

    // --- the catalog ---

    private static string catalogKey;
    private static List<string> catalog = new();
    private static string specialtiesKey;
    private static Dictionary<string, HashSet<string>> specialties = new();
    private static string minStockKey;
    private static List<KeyValuePair<string, long>> minStock = new();
    private static string profilesKey;
    private static Dictionary<string, List<KeyValuePair<string, long>>> profiles = new();
    private static string pricesKey;
    private static Dictionary<string, long> prices = new();

    /// <summary>The items OCT trades at every station.</summary>
    public static IReadOnlyList<string> Catalog()
    {
        string key = Config.MarketCatalog + "\n" + Config.MarketCatalogExcluded;
        if (catalogKey == key)
            return catalog;
        catalogKey = key;
        Regex include = MarketBook.Glob(Config.MarketCatalog),
            exclude = MarketBook.Glob(Config.MarketCatalogExcluded);
        catalog = MyDefinitionManager
            .Static.GetPhysicalItemDefinitions()
            .Where(d => d.Public)
            .Select(d => Stations.ItemName(d.Id))
            .Where(i => include?.IsMatch(i) == true && exclude?.IsMatch(i) != true)
            .Distinct()
            .OrderBy(i => i, StringComparer.Ordinal)
            .ToList();
        return catalog;
    }

    /// <summary>
    /// The station's profile, raw amounts: what it stocks at least, MarketMinStock and its part of
    /// MarketStationStock, which wins for an item in both. It tells what an hour's restock brings
    /// back and what OCT's acquisition contracts ask for.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, long>> Profile(string station)
    {
        var common = Parsed(
            Config.MarketMinStock,
            ref minStockKey,
            ref minStock,
            MarketBook.ParseAmounts,
            "MarketMinStock"
        );
        var own = Parsed(
            Config.MarketStationStock,
            ref profilesKey,
            ref profiles,
            MarketBook.ParseProfiles,
            "MarketStationStock"
        );
        if (!own.TryGetValue(station, out var extra))
            return common;
        return common.Where(c => extra.All(e => e.Key != c.Key)).Concat(extra).ToList();
    }

    /// <summary>
    /// For OCT's acquisition contracts, on the authority: the items of the station's profile OCT
    /// holds less of, with the amount missing (raw).
    /// </summary>
    public static List<KeyValuePair<string, long>> Missing(string station) =>
        Book.Missing(station, Profile(station));

    private static Dictionary<string, HashSet<string>> Specialties() =>
        Parsed(
            Config.MarketSpecialties,
            ref specialtiesKey,
            ref specialties,
            MarketBook.ParseStations,
            "MarketSpecialties"
        );

    private static Dictionary<string, long> Prices() =>
        Parsed(
            Config.MarketPrices,
            ref pricesKey,
            ref prices,
            text =>
                MarketBook
                    .ParseAmounts(text)
                    // units of the parser are SC here: raw / Unit * 1000 milli-SC
                    .ToDictionary(p => p.Key, p => p.Value / (Amounts.Unit / 1000)),
            "MarketPrices"
        );

    private static T Parsed<T>(
        string text,
        ref string key,
        ref T value,
        Func<string, T> parse,
        string option
    )
        where T : new()
    {
        if (key == text)
            return value;
        key = text;
        try
        {
            value = parse(text);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            Plugin.Log.Error($"{option}: {e.Message}, left empty");
            value = new T();
        }
        return value;
    }

    /// <summary>The reference price of an item, milli-SC per unit: MarketPrices, else the game's minimal price / 10; 0 if none.</summary>
    public static long ReferencePrice(string item)
    {
        if (Prices().TryGetValue(item, out long price))
            return price;
        if (!Stations.TryItemId(item, out var id))
            return 0;
        var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
        int minimal = economy?.GetMinimumItemPrice(id) ?? 0;
        if (
            minimal <= 0
            && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
        )
            minimal = definition.MinimalPricePerUnit;
        return Math.Max(0, minimal) * MarketBook.MilliPerSc / 10;
    }

    private sealed class GameWorld : IMarketWorld
    {
        public long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public long Oct => OctId;

        public bool Item(string item, out double massKg, out bool integral)
        {
            massKg = 0;
            integral = true;
            if (
                !Stations.TryItemId(item, out var id)
                || !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
                || definition is MyOxygenContainerDefinition
            )
                return false;
            massKg = definition.Mass;
            integral = definition.HasIntegralAmounts;
            return true;
        }

        public long Reference(string station, string item) =>
            Market.Catalog().Contains(item) ? ReferencePrice(item) : 0;

        public long BasePrice(string item) => ReferencePrice(item);

        public IEnumerable<string> Catalog(string station) =>
            Market.Catalog().Where(i => ReferencePrice(i) > 0);

        public bool Specialty(string station, string item) =>
            Specialties().TryGetValue(station, out var items) && items.Contains(item);

        public long Balance(long account) =>
            MyBankingSystem.Static?.TryGetAccountInfo(account, out var info) == true
                ? info.Balance
                : -1;

        public bool ChangeBalance(long account, long amount) =>
            amount == 0 || MyBankingSystem.ChangeBalance(account, amount);
    }
}
