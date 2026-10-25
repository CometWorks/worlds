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
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.ModAPI;
using VRage.Game;

namespace ServerPlugin;

/// <summary>
/// The station markets in the game: one <see cref="MarketBook"/> kept with the ledgers, by the
/// server itself or the World Authority of a cluster, in the world's sandbox variables. The
/// kiosks send their market requests here (<see cref="Execute"/>, <see cref="Read"/>); on the
/// authority the book moves the items through the ledgers and the money through the bank
/// accounts in one go. Every minute it closes expired listings and, once a UTC day, adds OCT's
/// daily stock. OCT's stores replace the game's generated station store items.
/// </summary>
internal static class Market
{
    public const string Variable = "Octahedron.Market";
    private const string Command = "market";
    private const string ReadTopic = "market/read";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static MarketBook book;
    private static MySession bookSession;
    private static bool dirty;
    private static DateTime nextTick;
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
            FeePercent = Config.MarketFeePercent,
            ListingDays = Config.MarketListingDays,
            MaxListings = Config.MarketMaxListings,
            OctBuyPercent = Config.MarketOctBuyPercent,
            OctSellPercent = Config.MarketOctSellPercent,
            SpecialtyPercent = Config.MarketSpecialtyPercent,
            SpecialtyDailyKg = Config.MarketSpecialtyDailyTonnes * 1000,
            OctMaxStockKg = Config.MarketOctMaxStockTonnes * 1000,
            DriftKg = Config.MarketPriceDriftTonnes * 1000,
            DriftHalfLifeHours = Config.MarketPriceDriftHalfLifeHours,
            DriftMaxFactor = Config.MarketPriceDriftMax,
        };

    /// <summary>The market, on the authority only, loaded from the world on first use.</summary>
    public static MarketBook Book
    {
        get
        {
            if (!Ledgers.IsAuthority)
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
            var loaded = MarketBook.Deserialize(text);
            Plugin.Log.Info($"Market loaded: {loaded.Listings.Count()} listings");
            return loaded;
        }
        return new MarketBook();
    }

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

    // --- requests ---

    /// <summary>
    /// Does a market request wherever the market is kept; <paramref name="done"/> gets whether it
    /// worked and what to tell the player, on the game thread.
    /// </summary>
    public static void Execute(Guid operationId, MarketRequest request, Action<bool, string> done)
    {
        if (!On)
        {
            done(false, "The market is closed.");
            return;
        }
        if (Ledgers.IsAuthority)
        {
            var r = Run(request);
            done(r.Ok, r.Message);
            return;
        }
        Ledgers.Call(
            Command,
            operationId,
            request.Encode(),
            (outcome, text) =>
            {
                if (outcome != LedgerOutcome.Applied)
                {
                    done(
                        false,
                        outcome == LedgerOutcome.Unknown
                            ? $"The station did not answer. Ask an admin about market request {operationId}."
                            : text
                    );
                    return;
                }
                done(text.StartsWith("1"), text.Length > 2 ? text.Substring(2) : "");
            }
        );
    }

    /// <summary>On the authority: does the request and logs what it did.</summary>
    public static MarketResult Run(MarketRequest request)
    {
        if (!On)
            return MarketResult.No("The market is closed.");
        if (OctId == 0)
            return MarketResult.No($"No faction tagged {Config.MoneyAuthorityFactionTag}.");
        if (!Ledgers.StationNames().Contains(request.Station) && request.Verb != KioskVerb.Cancel)
            return MarketResult.No($"Unknown station '{request.Station}'.");
        MarketResult result;
        try
        {
            result = Book.Run(World, Settings, request);
        }
        catch (OverflowException)
        {
            result = MarketResult.No("The amount is too large.");
        }
        if (result.Ok)
        {
            dirty = true;
            Plugin.Log.Info(
                $"Market {request.Verb} at {request.Station} by {request.Owner}: {request.Item} {LedgerBook.Units(request.Amount)}: {result.Message}"
            );
        }
        return result;
    }

    /// <summary>The station's market as kiosk records for the operator and their faction, to <paramref name="done"/> on the game thread; null with the reason on failure.</summary>
    public static void Read(
        string station,
        LedgerOwner me,
        LedgerOwner? faction,
        Action<string, string> done
    )
    {
        if (!On)
        {
            done("", null);
            return;
        }
        if (Ledgers.IsAuthority)
        {
            done(View(station, me, faction), null);
            return;
        }
        Ledgers.Ask(ReadTopic, $"{station}\t{me}\t{faction}", done);
    }

    private static string View(string station, LedgerOwner me, LedgerOwner? faction) =>
        Book.View(World, Settings, station, me, faction, ItemTitle);

    public static string ItemTitle(string item) =>
        Ledgers.TryItemId(item, out var id)
        && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
            ? definition.DisplayNameText
            : item;

    // --- upkeep ---

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true || DateTime.UtcNow < nextTick)
            return;
        nextTick = DateTime.UtcNow + Interval;
        ClearStoreItems();
        if (!Ledgers.IsAuthority)
            return;
        Register();
        if (OctId == 0)
            return;
        Tick(false);
    }

    /// <summary>On the authority: closes expired listings and restocks OCT on a new day, or now with force.</summary>
    public static string Tick(bool force)
    {
        if (!Ledgers.IsAuthority)
            return "The market runs on the World Authority.";
        if (OctId == 0)
            return $"No faction tagged {Config.MoneyAuthorityFactionTag}.";
        var lines = Book.Expire(World, Settings);
        var restock = Book.Restock(World, Settings, Ledgers.StationNames(), Daily(), force);
        if (restock != null)
            lines.Add(restock);
        if (lines.Count > 0)
            dirty = true;
        foreach (var line in lines)
            Plugin.Log.Info("Market: " + line);
        return lines.Count == 0 ? "Nothing to do." : string.Join("\n", lines);
    }

    // On a cluster the World Authority serves the nodes' requests
    private static void Register()
    {
        if (
            commandRegistration != null
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
        var r = Run(MarketRequest.Decode(Encoding.UTF8.GetString(payload)));
        return Encoding.UTF8.GetBytes((r.Ok ? "1\t" : "0\t") + r.Message);
    }

    private static Task<byte[]> ServeRead(PluginMessage message)
    {
        var f = Encoding.UTF8.GetString(message.Payload ?? []).Split('\t');
        var me = f.Length > 1 && LedgerOwner.TryParse(f[1], out var m) ? m : default;
        LedgerOwner? faction = f.Length > 2 && LedgerOwner.TryParse(f[2], out var o) ? o : null;
        return Task.FromResult(Encoding.UTF8.GetBytes(View(f[0], me, faction)));
    }

    // --- the game's station stores ---

    // OCT's stations sell nothing through the game's store blocks; the kiosks are the market
    private static void ClearStoreItems()
    {
        var oct = MySession.Static.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag);
        if (oct == null)
            return;
        foreach (var station in oct.Stations.OfType<MyFactionStation>())
            if (station.StoreItems.Count > 0)
            {
                Plugin.Log.Info(
                    $"Market: {station.StoreItems.Count} generated store items of {station.Name} removed"
                );
                station.StoreItems.Clear();
            }
    }

    [HarmonyPatch(typeof(MyStoreItemsGenerator), nameof(MyStoreItemsGenerator.Update))]
    private static class NoGeneratedStoreItems
    {
        // ReSharper disable once UnusedMember.Local
        private static bool Prefix(MyFaction faction) =>
            !(On && faction.Tag == Config.MoneyAuthorityFactionTag);
    }

    // --- the catalog ---

    private static string catalogKey;
    private static List<string> catalog = new();
    private static string specialtiesKey;
    private static Dictionary<string, HashSet<string>> specialties = new();
    private static string dailyKey;
    private static List<KeyValuePair<string, long>> daily = new();
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
            .Select(d => Ledgers.ItemName(d.Id))
            .Where(i => include?.IsMatch(i) == true && exclude?.IsMatch(i) != true)
            .Distinct()
            .OrderBy(i => i, StringComparer.Ordinal)
            .ToList();
        return catalog;
    }

    /// <summary>What OCT adds to its stock at every station each day, raw amounts; also its acquisition targets.</summary>
    public static IReadOnlyList<KeyValuePair<string, long>> Daily() =>
        Parsed(
            Config.MarketDailyStock,
            ref dailyKey,
            ref daily,
            MarketBook.ParseAmounts,
            "MarketDailyStock"
        );

    /// <summary>
    /// For OCT's acquisition contracts, on the authority: the station's daily stock items OCT's
    /// ledger there holds less of, with the amount missing (raw).
    /// </summary>
    public static List<KeyValuePair<string, long>> Missing(string station) =>
        MarketBook.Missing(World, station, Daily());

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
                    .ToDictionary(p => p.Key, p => p.Value / (LedgerBook.Unit / 1000)),
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
        if (!Ledgers.TryItemId(item, out var id))
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
        public LedgerOwner Oct => LedgerOwner.OfFaction(OctId);
        public LedgerBook Ledger => Ledgers.Book;

        public bool Item(string item, out double massKg, out bool integral)
        {
            massKg = 0;
            integral = true;
            if (
                !Ledgers.TryItemId(item, out var id)
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

        public IEnumerable<string> Catalog(string station) =>
            Market.Catalog().Where(i => ReferencePrice(i) > 0);

        public bool Specialty(string station, string item) =>
            Specialties().TryGetValue(station, out var items) && items.Contains(item);

        public double RoomKg(string station, LedgerOwner owner)
        {
            double limit =
                1000
                * (
                    owner.Faction ? Config.LedgerFactionLimitTonnes : Config.LedgerPlayerLimitTonnes
                );
            if (limit <= 0 || (owner.Faction && owner.Id == OctId))
                return double.PositiveInfinity;
            return limit - Ledgers.Book.MassKg(station, owner);
        }

        public long Balance(LedgerOwner owner) =>
            MyBankingSystem.Static?.TryGetAccountInfo(owner.Id, out var account) == true
                ? account.Balance
                : -1;

        public bool ChangeBalance(LedgerOwner owner, long amount) =>
            amount == 0 || MyBankingSystem.ChangeBalance(owner.Id, amount);

        public string ApplyLedger(IReadOnlyList<LedgerOp> ops) => Ledgers.Apply(ops);
    }
}
