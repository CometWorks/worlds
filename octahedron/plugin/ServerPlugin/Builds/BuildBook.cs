using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>Where an order is. A free build goes from waiting to building and leaves the queue; a paid build and a towed grid's breakdown are the player's turn at the shipyard.</summary>
public enum BuildPhase
{
    Waiting,

    /// <summary>A paid build's turn: the player loads the components and presses the button.</summary>
    Loading,
    Building,

    /// <summary>The build or breakdown is over: the player takes what is in the containers.</summary>
    After,
}

/// <summary>An entry of a shipyard's queue: a free build, a paid one, or a towed grid to break down.</summary>
public sealed class BuildOrder
{
    public string Id = "";
    public string Station = "";

    // The player who ordered; only they cancel it
    public long Player;

    // The account that pays and gets the sales: the player's. 0: a free build or a breakdown.
    public long Payer;

    // The labor fee paid to OCT when the build starts, SC
    public long Fee;

    // What the build's start took from the payer, labor fee included, SC
    public long Paid;

    public int Pcu;
    public string Ship = "";

    // Unix seconds
    public long Created;
    public BuildPhase Phase;

    // When the turn's current window ends, Unix seconds: the build start or the taking of what is left
    public long TurnEnds;

    // A towed grid's entity id: the order breaks it down
    public long Grid;

    // An admin who ignores PCU limits ordered it (FreePrints.Exempt): always a free build, without
    // the per-ship PCU cap. Kept here because the node that builds it may not know their admin
    // settings.
    public bool Exempt;

    // A slot for the player to repair a ship with the shipyard's welders: no blueprint or payer
    public bool Repair;

    // The blueprint, opaque here: base64, no tabs or line breaks
    public string Blueprint = "";

    // A paid build's bill, every block's components and the ice for the hydrogen, raw. From the
    // build's start: what came, and the ice of the hydrogen the ship gets.
    public Dictionary<string, long> Items = new();

    public bool Free => Payer == 0 && Grid == 0;
    public bool Started => Phase == BuildPhase.Building;

    /// <summary>The player's turn at the shipyard is on: the order is at the front of the queue.</summary>
    public bool Active => Phase != BuildPhase.Waiting;
}

/// <summary>A ship an order built, as long as it stays in the shipyard's build space.</summary>
public sealed class BuiltShip
{
    public long Grid;
    public string Station = "";
    public long Player;
    public string Name = "";

    // What comes back if it has to be removed, SC
    public long Refund;

    public string Encode() =>
        string.Join(
            "\t",
            "S",
            BuildQuote.N(Grid),
            Station,
            BuildQuote.N(Player),
            BuildQuote.N(Refund),
            Name
        );

    public static BuiltShip Decode(string line)
    {
        var f = line.Split('\t');
        if (f.Length != 6 || f[0] != "S")
            throw new FormatException("Malformed built ship");
        return new BuiltShip
        {
            Grid = BuildQuote.L(f[1]),
            Station = f[2],
            Player = BuildQuote.L(f[3]),
            Refund = BuildQuote.L(f[4]),
            Name = f[5],
        };
    }
}

/// <summary>What one item of a paid build costs at the station's Store, raw amounts, costs in SC.</summary>
public sealed class QuoteLine
{
    public string Item = "";
    public long Needed;
    public long Bought;
    public long Cost;

    // The Store doesn't have it
    public long Missing;
}

/// <summary>The price of a paid build at a station for a payer.</summary>
public sealed class BuildQuote
{
    public List<QuoteLine> Lines = new();
    public long Fee;

    // The payer's balance, SC; -1 without an account
    public long Balance;

    public long Cost => Lines.Sum(l => l.Cost);
    public long Total => Cost + Fee;
    public bool Complete => Lines.All(l => l.Missing == 0);

    /// <summary>The first item the Store has too little of, the hydrogen left out: a ship gets less of that.</summary>
    public QuoteLine Short(string hydrogenItem) =>
        Lines.FirstOrDefault(l => l.Missing > 0 && l.Item != hydrogenItem);

    // One line per item, then the totals, tab-separated
    public string Encode()
    {
        var text = new StringBuilder();
        text.Append("T\t").Append(N(Fee)).Append('\t').Append(N(Balance)).Append('\n');
        foreach (var l in Lines)
            text.Append(
                    string.Join(
                        "\t",
                        "L",
                        l.Item,
                        N(l.Needed),
                        N(l.Bought),
                        N(l.Cost),
                        N(l.Missing)
                    )
                )
                .Append('\n');
        return text.ToString();
    }

    public static BuildQuote Decode(string text)
    {
        var quote = new BuildQuote();
        foreach (var line in text.Split('\n').Where(l => l.Length > 0))
        {
            var f = line.Split('\t');
            if (f[0] == "T" && f.Length == 3)
            {
                quote.Fee = L(f[1]);
                quote.Balance = L(f[2]);
            }
            else if (f[0] == "L" && f.Length == 6)
                quote.Lines.Add(
                    new QuoteLine
                    {
                        Item = f[1],
                        Needed = L(f[2]),
                        Bought = L(f[3]),
                        Cost = L(f[4]),
                        Missing = L(f[5]),
                    }
                );
            else
                throw new FormatException("Malformed quote line: " + line);
        }
        return quote;
    }

    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static long L(string text) => long.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>The market as a build needs it: the Store's terms, purchases and what is left over.</summary>
public interface IBuildMarket
{
    MarketFill PlanBuy(string station, string item, long raw);

    MarketResult Buy(string station, long buyer, string item, long raw);

    /// <summary>Items a failed purchase gives back to the stock; the caller pays the money back.</summary>
    void Return(string station, string item, long raw);

    /// <summary>What the station pays for a unit of what a turn leaves behind, SC, tax taken.</summary>
    long SalePrice(string station, string item);

    /// <summary>Sells the items for the seller on Store terms; returns the SC they got.</summary>
    long SellOff(string station, long seller, IReadOnlyDictionary<string, long> items);
}

/// <summary>An item of a towed grid at its breakdown: raw amount, the liters of all of it, the price per unit.</summary>
public readonly record struct BreakdownItem(
    string Item,
    long Raw,
    double Liters,
    long Price,
    bool Integral
);

public sealed class BuildSettings
{
    // Queue entries per shipyard, the one being built included
    public int QueueMax = 6;

    // The item the build's hydrogen is bought as; it is not loaded into the shipyard
    public string HydrogenItem = "Ore/Ice";

    // How long each window of a player's turn lasts
    public int TurnMinutes = 10;
}

public readonly struct BuildResult
{
    public readonly bool Ok;
    public readonly string Message;
    public readonly BuildOrder Order;

    public BuildResult(bool ok, string message, BuildOrder order = null)
    {
        Ok = ok;
        Message = message;
        Order = order;
    }

    public static BuildResult No(string message) => new(false, message);
}

/// <summary>
/// The shipyards' queues and the player's turn at the front of one. An order costs nothing while
/// it waits. At its turn the player loads what they have into the shipyard (<see cref="Turn"/>);
/// the build's start (<see cref="Begin"/>) buys what is still missing at the station's Store and
/// pays the labor fee. After the build or a towed grid's breakdown (<see cref="After"/>) the player
/// has a while to take what is left, then the rest is sold to the Store for them
/// (<see cref="EndTurn"/>). Pure bookkeeping without game types, so it is tested on its own
/// (tests/builds). Runs on the authority's game thread, the World Authority's on a cluster.
/// </summary>
public sealed class BuildBook
{
    private const string Header = "octahedron-builds 3";

    /// <summary>
    /// A cluster's plugin messages carry 64 KiB at most, a blueprint of over about 700 blocks
    /// doesn't fit: it goes between a node and the authority in parts of this many characters.
    /// </summary>
    public const int BlueprintPartSize = 60_000;
    private const long UploadSeconds = 600;

    private readonly Dictionary<string, BuildOrder> orders = new();

    // The ships that ended a build and haven't left the build space, by grid
    private readonly Dictionary<long, BuiltShip> ships = new();

    // The blueprints arriving for orders, not saved: an order after a restart asks for a new press
    private readonly Dictionary<string, (long At, List<string> Parts)> uploads = new();
    private long nextId;

    public IEnumerable<BuildOrder> Orders => orders.Values;

    public IEnumerable<BuiltShip> Ships => ships.Values;

    public BuildOrder Get(string id) => id != null && orders.TryGetValue(id, out var o) ? o : null;

    /// <summary>The station's queue in order: the build running first, then by when they were ordered.</summary>
    public List<BuildOrder> Queue(string station) =>
        orders
            .Values.Where(o => o.Station == station)
            .OrderByDescending(o => o.Active)
            .ThenBy(o => o.Created)
            .ThenBy(o => o.Id.Length)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The order at the front of the station's queue, unless it is building or past its build.</summary>
    public BuildOrder Next(string station)
    {
        var queue = Queue(station);
        return queue.Count == 0 || queue[0].Phase is BuildPhase.Building or BuildPhase.After
            ? null
            : queue[0];
    }

    /// <summary>The parts a blueprint goes in, one at least.</summary>
    public static List<string> BlueprintParts(string blueprint)
    {
        var parts = new List<string>();
        for (int at = 0; at == 0 || at < blueprint.Length; at += BlueprintPartSize)
            parts.Add(blueprint.Substring(at, Math.Min(BlueprintPartSize, blueprint.Length - at)));
        return parts;
    }

    /// <summary>Keeps a part of an upload; the parts come in order, a repeated one replaces itself.</summary>
    public bool Upload(long now, string id, int part, string text)
    {
        foreach (var old in uploads.Where(u => now - u.Value.At > UploadSeconds).ToList())
            uploads.Remove(old.Key);
        var parts = uploads.TryGetValue(id, out var upload) ? upload.Parts : new List<string>();
        if (part < 0 || part > parts.Count)
            return false;
        if (part == parts.Count)
            parts.Add(text);
        else
            parts[part] = text;
        uploads[id] = (now, parts);
        return true;
    }

    /// <summary>The blueprint of a finished upload, null unless exactly that many parts arrived.</summary>
    public string TakeUpload(string id, int parts)
    {
        if (!uploads.TryGetValue(id, out var upload) || upload.Parts.Count != parts)
            return null;
        uploads.Remove(id);
        return string.Concat(upload.Parts);
    }

    /// <summary>A part of the order's blueprint, shorter than a whole part at the end; null past that or without the order.</summary>
    public string BlueprintPart(string id, int part)
    {
        var blueprint = Get(id)?.Blueprint;
        long at = (long)part * BlueprintPartSize;
        if (blueprint == null || part < 0 || at > blueprint.Length)
            return null;
        return blueprint.Substring(
            (int)at,
            Math.Min(BlueprintPartSize, blueprint.Length - (int)at)
        );
    }

    /// <summary>The labor fee: per100Pcu SC per 100 PCU, rounded up.</summary>
    public static long Fee(int pcu, long per100Pcu) =>
        pcu <= 0 || per100Pcu <= 0 ? 0 : checked((pcu * per100Pcu + 99) / 100);

    // --- quote and order ---

    /// <summary>What the items would cost at the station's Store, what it doesn't have left missing. No market: all missing.</summary>
    public static BuildQuote Quote(
        IMarketWorld w,
        IBuildMarket market,
        string station,
        long payer,
        IReadOnlyDictionary<string, long> needed,
        long fee
    )
    {
        var quote = new BuildQuote { Fee = fee, Balance = w.Balance(payer) };
        foreach (
            var item in needed.Where(n => n.Value > 0).OrderBy(n => n.Key, StringComparer.Ordinal)
        )
        {
            var fill = market?.PlanBuy(station, item.Key, item.Value) ?? default;
            quote.Lines.Add(
                new QuoteLine
                {
                    Item = item.Key,
                    Needed = item.Value,
                    Bought = fill.Amount,
                    Cost = fill.Cost,
                    Missing = item.Value - fill.Amount,
                }
            );
        }
        return quote;
    }

    /// <summary>
    /// Queues an order. A paid one (Payer set) keeps <paramref name="needed"/> as its bill. A player
    /// in the window of their turn goes on with it instead, for a paid order, in the Loading phase:
    /// what they have in the containers can go into this ship.
    /// </summary>
    public BuildResult Order(
        IMarketWorld w,
        BuildSettings s,
        BuildOrder order,
        IReadOnlyDictionary<string, long> needed
    )
    {
        if (!Amounts.ValidName(order.Station))
            return BuildResult.No("Unknown station.");
        var mine = orders.Values.FirstOrDefault(o => o.Player == order.Player);
        if (mine?.Station == order.Station && mine.Phase is BuildPhase.Loading or BuildPhase.After)
        {
            if (order.Free)
                return BuildResult.No(
                    "A free ship can't use your turn at this shipyard: end it first with !oct shipyard done."
                );
            mine.Grid = 0;
            mine.Repair = false;
            mine.Payer = order.Payer;
            mine.Fee = order.Fee;
            mine.Paid = 0;
            mine.Pcu = order.Pcu;
            mine.Ship = Clean(order.Ship);
            mine.Exempt = order.Exempt;
            mine.Blueprint = order.Blueprint;
            mine.Items = Bill(needed);
            mine.Phase = BuildPhase.Loading;
            mine.TurnEnds = w.Now + s.TurnMinutes * 60L;
            return new BuildResult(true, $"'{mine.Ship}' goes on with your turn.", mine);
        }
        if (mine != null)
            return BuildResult.No(
                $"'{mine.Ship}' is in a shipyard's queue for you already, one entry at a time."
            );
        if (orders.Values.Count(o => o.Station == order.Station) >= s.QueueMax)
            return BuildResult.No($"The shipyard's queue is full, {s.QueueMax} ships.");

        Add(w, order);
        order.Items = order.Free ? new Dictionary<string, long>() : Bill(needed);
        return new BuildResult(
            true,
            order.Repair ? $"'{order.Ship}' is in the queue."
                : order.Free ? $"'{order.Ship}' is in the queue, a free build."
                : $"'{order.Ship}' is in the queue. At its turn you load the components and press the button.",
            order
        );
    }

    /// <summary>
    /// A towed grid's breakdown takes the free shipyard: the order comes back, already building.
    /// With anything in the shipyard's queue no order comes back, and the grid is to be sold.
    /// </summary>
    public BuildResult Tow(IMarketWorld w, BuildOrder order)
    {
        if (!Amounts.ValidName(order.Station))
            return BuildResult.No("Unknown station.");
        var known = orders.Values.FirstOrDefault(o => o.Grid == order.Grid);
        if (known != null)
            return new BuildResult(true, "", known);
        if (orders.Values.Any(o => o.Station == order.Station))
            return new BuildResult(true, "", null);
        Add(w, order);
        order.Phase = BuildPhase.Building;
        return new BuildResult(true, "", order);
    }

    /// <summary>What a towed grid is made of is sold at once, for a busy shipyard. The answer says for how much.</summary>
    public static BuildResult Sale(
        IBuildMarket market,
        string station,
        long player,
        IReadOnlyDictionary<string, long> items
    ) => new(true, MarketBook.Sc(market?.SellOff(station, player, items) ?? 0));

    private void Add(IMarketWorld w, BuildOrder order)
    {
        order.Id = "b" + (++nextId).ToString(CultureInfo.InvariantCulture);
        order.Created = w.Now;
        order.Phase = BuildPhase.Waiting;
        order.Ship = Clean(order.Ship);
        order.Items = new Dictionary<string, long>();
        orders[order.Id] = order;
    }

    private static Dictionary<string, long> Bill(IReadOnlyDictionary<string, long> needed) =>
        needed.Where(n => n.Value > 0).ToDictionary(n => n.Key, n => n.Value);

    /// <summary>The player's turn starts: they have a while to load the components and press the button.</summary>
    public BuildResult Turn(IMarketWorld w, BuildSettings s, string id)
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        if (order.Phase != BuildPhase.Waiting)
            return BuildResult.No($"'{order.Ship}' has its turn already.");
        order.Phase = BuildPhase.Loading;
        order.TurnEnds = w.Now + s.TurnMinutes * 60L;
        return new BuildResult(true, $"It is the turn of '{order.Ship}'.", order);
    }

    /// <summary>
    /// The player presses start: <paramref name="needed"/>, what the containers don't have, is
    /// bought at the Store. <paramref name="provided"/> comes from outside (the hydrogen of a ship
    /// at the station and the ice in the containers). All or nothing, but for the hydrogen, of
    /// which the ship gets less if the station lacks it. The labor fee is paid. The order's Items
    /// then say what came, for the shipyard to load.
    /// </summary>
    public BuildResult Begin(
        IMarketWorld w,
        IBuildMarket market,
        BuildSettings s,
        string id,
        long player,
        IReadOnlyDictionary<string, long> needed,
        IReadOnlyDictionary<string, long> provided
    )
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        if (order.Player != player)
            return BuildResult.No("Only the player who ordered it can start it.");
        if (order.Phase != BuildPhase.Loading)
            return BuildResult.No($"'{order.Ship}' can't start now.");

        var rest = new Dictionary<string, long>();
        foreach (var n in needed.Where(n => n.Value > 0))
        {
            long given = provided.TryGetValue(n.Key, out long p) ? Math.Min(p, n.Value) : 0;
            if (n.Value > given)
                rest[n.Key] = n.Value - given;
        }
        var quote = Quote(w, market, order.Station, order.Payer, rest, order.Fee);
        var missing = quote.Short(s.HydrogenItem);
        if (missing != null)
            return BuildResult.No(
                $"{Amounts.Units(missing.Missing)} {missing.Item} more than this station has for sale: load them into the containers."
            );
        if (quote.Balance < 0)
            return BuildResult.No("No bank account.");
        if (quote.Balance < quote.Total)
            return BuildResult.No(
                $"What is missing costs {MarketBook.Sc(quote.Total)}, the account has {MarketBook.Sc(quote.Balance)}: load more into the containers."
            );

        var bought = new List<QuoteLine>();
        foreach (var l in quote.Lines.Where(l => l.Bought > 0))
        {
            var bill = market.Buy(order.Station, order.Payer, l.Item, l.Bought);
            if (!bill.Ok)
            {
                foreach (var b in bought)
                    market.Return(order.Station, b.Item, b.Bought);
                PayBack(w, order.Payer, bought.Sum(b => b.Cost));
                return BuildResult.No($"Buying {l.Item} failed: {bill.Message}");
            }
            bought.Add(l);
        }
        if (order.Fee > 0)
        {
            if (!w.ChangeBalance(order.Payer, -order.Fee))
            {
                foreach (var b in bought)
                    market.Return(order.Station, b.Item, b.Bought);
                PayBack(w, order.Payer, bought.Sum(b => b.Cost));
                return BuildResult.No("The labor fee could not be paid.");
            }
            w.ChangeBalance(w.Oct, order.Fee);
        }

        // The hydrogen the station doesn't have is left out: the ship's tanks get less
        var came = new Dictionary<string, long>();
        foreach (var n in needed.Where(n => n.Value > 0))
        {
            long got = n.Value - (quote.Lines.FirstOrDefault(l => l.Item == n.Key)?.Missing ?? 0);
            if (got > 0)
                came[n.Key] = got;
        }
        order.Items = came;
        order.Paid = quote.Total;
        order.Phase = BuildPhase.Building;
        return new BuildResult(
            true,
            $"'{order.Ship}' starts: {MarketBook.Sc(quote.Cost)} for what was missing"
                + (order.Fee > 0 ? $" and {MarketBook.Sc(order.Fee)} labor" : "")
                + " paid.",
            order
        );
    }

    private static void PayBack(IMarketWorld w, long account, long sc)
    {
        if (sc <= 0)
            return;
        // OCT keeps a floor, so it can pay; should it not, the payer still gets it back
        w.ChangeBalance(w.Oct, -sc);
        w.ChangeBalance(account, sc);
    }

    /// <summary>The player's waiting order at the station leaves the queue; nothing was paid for it.</summary>
    public BuildResult Cancel(string station, long player)
    {
        var order = orders.Values.FirstOrDefault(o =>
            o.Station == station && o.Player == player && (o.Active || o.Grid == 0)
        );
        if (order == null)
            return BuildResult.No("You have no ship in this shipyard's queue.");
        if (order.Active)
            return BuildResult.No($"It is the turn of '{order.Ship}', it can't be canceled now.");
        orders.Remove(order.Id);
        return new BuildResult(true, $"'{order.Ship}' left the queue.", order);
    }

    /// <summary>A free build starts. Nothing is loaded for it from the book.</summary>
    public BuildResult Start(string id)
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        if (order.Phase == BuildPhase.Building)
            return BuildResult.No($"'{order.Ship}' is being built already.");
        order.Phase = BuildPhase.Building;
        return new BuildResult(true, $"'{order.Ship}' is being built.", order);
    }

    /// <summary>An order that won't get a turn, or a free build that ended, leaves the queue. <paramref name="ship"/>: the grid it left in the build space.</summary>
    public BuildResult Finish(string id, long ship = 0)
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        orders.Remove(id);
        Built(order, ship, 0);
        return new BuildResult(true, "", order);
    }

    private void Built(BuildOrder order, long grid, long refund)
    {
        if (grid != 0)
            ships[grid] = new BuiltShip
            {
                Grid = grid,
                Station = order.Station,
                Player = order.Player,
                Name = order.Ship,
                Refund = refund,
            };
    }

    /// <summary>
    /// The ship is out of the build space, and nothing is known of it any more. With
    /// <paramref name="refund"/> it was removed: what was paid for it comes back, once, as the
    /// entry goes with the payment.
    /// </summary>
    public BuildResult ShipOut(IMarketWorld w, long grid, bool refund)
    {
        if (!ships.TryGetValue(grid, out var ship))
            return BuildResult.No($"No ship {grid} from an order.");
        ships.Remove(grid);
        if (!refund || ship.Refund <= 0)
            return new BuildResult(true, "");
        PayBack(w, ship.Player, ship.Refund);
        return new BuildResult(true, $"The {MarketBook.Sc(ship.Refund)} you paid is paid back.");
    }

    /// <summary>
    /// The build or breakdown is over and the player has a while to take what is in the
    /// containers. The ice of the hydrogen that didn't go into the ship is sold back for the
    /// player; with <paramref name="refund"/> (it never got going, or its ship was removed) the fee
    /// comes back. <paramref name="sold"/> is what a breakdown's containers had no room for.
    /// <paramref name="ship"/> is the grid the build left in the build space and
    /// <paramref name="percent"/> how much of the blueprint it has: an unfinished one is worth that
    /// much of what the player paid, the rest of what they paid for being in their hands or sold.
    /// </summary>
    public BuildResult After(
        IMarketWorld w,
        IBuildMarket market,
        BuildSettings s,
        string id,
        long hydrogenUsed,
        bool refund,
        IReadOnlyDictionary<string, long> sold,
        long ship = 0,
        int percent = 100
    )
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        var sale = new Dictionary<string, long>();
        foreach (var item in sold)
            sale[item.Key] = item.Value;
        if (order.Payer != 0)
        {
            order.Items.TryGetValue(s.HydrogenItem, out long ice);
            long iceLeft = ice - Math.Min(Math.Max(0, hydrogenUsed), ice);
            if (iceLeft > 0)
                sale[s.HydrogenItem] = sale.GetValueOrDefault(s.HydrogenItem) + iceLeft;
            if (refund)
                PayBack(w, order.Payer, order.Fee);
        }
        long paid = SellOff(market, order, sale);
        Built(order, ship, Math.Max(0, order.Paid - paid) * percent / 100);
        order.Phase = BuildPhase.After;
        order.TurnEnds = w.Now + s.TurnMinutes * 60L;
        var message = new List<string>();
        if (refund && order.Payer != 0 && order.Fee > 0)
            message.Add($"The labor fee of {MarketBook.Sc(order.Fee)} is paid back.");
        if (sold.Count > 0)
            message.Add($"What did not fit the containers was sold for {MarketBook.Sc(paid)}.");
        else if (paid > 0)
            message.Add($"The ice the ship didn't take was sold for {MarketBook.Sc(paid)}.");
        return new BuildResult(true, string.Join(" ", message), order);
    }

    /// <summary>
    /// The player's turn ends: <paramref name="left"/>, what they left in the shipyard, is sold
    /// to the station for them, and the order leaves the queue.
    /// </summary>
    public BuildResult EndTurn(
        IMarketWorld w,
        IBuildMarket market,
        string id,
        IReadOnlyDictionary<string, long> left
    )
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        long paid = SellOff(market, order, left);
        orders.Remove(id);
        return new BuildResult(
            true,
            left.Count > 0
                ? $"What was left in the shipyard was sold for {MarketBook.Sc(paid)}."
                : "",
            order
        );
    }

    private static long SellOff(
        IBuildMarket market,
        BuildOrder order,
        IReadOnlyDictionary<string, long> items
    ) =>
        items.Count == 0 || market == null ? 0 : market.SellOff(order.Station, order.Player, items);

    /// <summary>
    /// What a breakdown sells because the containers lack the room: the cheapest units first,
    /// until the rest takes at most <paramref name="roomLiters"/>. Whole units of a whole item.
    /// </summary>
    public static Dictionary<string, long> Overflow(
        IEnumerable<BreakdownItem> items,
        double roomLiters
    )
    {
        var list = items.Where(i => i.Raw > 0).ToList();
        double over = list.Sum(i => i.Liters) - roomLiters;
        var sold = new Dictionary<string, long>();
        foreach (var item in list.OrderBy(i => i.Price).ThenBy(i => i.Item, StringComparer.Ordinal))
        {
            if (over <= 0)
                break;
            long raw = item.Raw;
            if (item.Liters > over)
            {
                raw = (long)Math.Ceiling(over / item.Liters * item.Raw);
                if (item.Integral && raw % Amounts.Unit != 0)
                    raw += Amounts.Unit - raw % Amounts.Unit;
                raw = Math.Min(raw, item.Raw);
            }
            sold[item.Item] = raw;
            over -= item.Liters * raw / item.Raw;
        }
        return sold;
    }

    // --- persistence: a line per order, tab-separated ---

    private static string Clean(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "Ship"
            : new string(text.Select(c => c is '\t' or '\n' or '\r' ? ' ' : c).ToArray()).Trim();

    public static string EncodeOrder(BuildOrder o, bool blueprint)
    {
        var text = new StringBuilder(
            string.Join(
                "\t",
                "O",
                o.Id,
                o.Station,
                BuildQuote.N(o.Player),
                BuildQuote.N(o.Payer),
                BuildQuote.N(o.Fee),
                o.Pcu.ToString(CultureInfo.InvariantCulture),
                Clean(o.Ship),
                BuildQuote.N(o.Created),
                // Flags: 2 exempt, 4 repair
                ((o.Exempt ? 2 : 0) | (o.Repair ? 4 : 0)).ToString(CultureInfo.InvariantCulture),
                blueprint ? o.Blueprint ?? "" : "",
                ((int)o.Phase).ToString(CultureInfo.InvariantCulture),
                BuildQuote.N(o.TurnEnds),
                BuildQuote.N(o.Grid),
                BuildQuote.N(o.Paid)
            )
        );
        foreach (var i in o.Items.OrderBy(i => i.Key, StringComparer.Ordinal))
            text.Append('\t').Append(i.Key).Append('\t').Append(BuildQuote.N(i.Value));
        return text.ToString();
    }

    public static BuildOrder DecodeOrder(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 14 || f[0] != "O")
            throw new FormatException("Malformed build order");
        // A world saved before Paid has an even number of fields
        var items = f.Length % 2 == 0 ? 14 : 15;
        var order = new BuildOrder
        {
            Id = f[1],
            Station = f[2],
            Player = BuildQuote.L(f[3]),
            Payer = BuildQuote.L(f[4]),
            Fee = BuildQuote.L(f[5]),
            Pcu = int.Parse(f[6], CultureInfo.InvariantCulture),
            Ship = f[7],
            Created = BuildQuote.L(f[8]),
            Blueprint = f[10],
            Phase = (BuildPhase)int.Parse(f[11], CultureInfo.InvariantCulture),
            TurnEnds = BuildQuote.L(f[12]),
            Grid = BuildQuote.L(f[13]),
            Paid = items == 15 ? BuildQuote.L(f[14]) : 0,
        };
        var flags = int.Parse(f[9], CultureInfo.InvariantCulture);
        order.Exempt = (flags & 2) != 0;
        order.Repair = (flags & 4) != 0;
        for (int i = items; i < f.Length; i += 2)
            order.Items[f[i]] = BuildQuote.L(f[i + 1]);
        return order;
    }

    public string Serialize()
    {
        var text = new StringBuilder(Header).Append('\n');
        text.Append("N\t").Append(BuildQuote.N(nextId)).Append('\n');
        foreach (var o in orders.Values.OrderBy(o => o.Created).ThenBy(o => o.Id))
            text.Append(EncodeOrder(o, blueprint: true)).Append('\n');
        foreach (var ship in ships.Values.OrderBy(ship => ship.Grid))
            text.Append(ship.Encode()).Append('\n');
        return text.ToString();
    }

    public static BuildBook Deserialize(string text)
    {
        var book = new BuildBook();
        var lines = text.Split('\n');
        if (lines[0] != Header)
            throw new FormatException("Not a build book: " + lines[0]);
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            if (line.StartsWith("N\t"))
                book.nextId = BuildQuote.L(line.Substring(2));
            else if (line.StartsWith("S\t"))
            {
                var ship = BuiltShip.Decode(line);
                book.ships[ship.Grid] = ship;
            }
            else
            {
                var order = DecodeOrder(line);
                book.orders[order.Id] = order;
            }
        }
        return book;
    }

    // --- texts for the station's screens ---

    /// <summary>The quote as text for the hologram table's screen.</summary>
    public static string QuoteText(
        BuildQuote quote,
        string hydrogenItem,
        long hydrogenFromShip,
        Func<string, string> title,
        Func<string, long, string> units
    )
    {
        var text = new StringBuilder();
        text.AppendLine($"{"Item", -16}{"Need", 7}{"Buy", 7}{"SC", 8}");
        foreach (var l in quote.Lines)
        {
            text.AppendLine(
                $"{Cut(title(l.Item), 16), -16}{units(l.Item, l.Needed), 7}"
                    + $"{units(l.Item, l.Bought), 7}{l.Cost, 8:N0}"
            );
            if (l.Missing > 0)
                text.AppendLine($"  {units(l.Item, l.Missing)} not for sale");
        }
        if (hydrogenFromShip > 0)
            text.AppendLine(
                $"Hydrogen: {units(hydrogenItem, hydrogenFromShip)} ice worth from your ship"
            );
        text.AppendLine();
        text.AppendLine($"From the Store: {quote.Cost:N0} SC");
        if (quote.Fee > 0)
            text.AppendLine($"Labor fee: {quote.Fee:N0} SC");
        text.AppendLine($"Total: {quote.Total:N0} SC");
        text.AppendLine($"The account has {Math.Max(0, quote.Balance):N0} SC");
        return text.ToString();
    }

    /// <summary>A free build's bill of materials: the items by title and how many of each.</summary>
    public static string PartsText(IDictionary<string, long> parts)
    {
        var text = new StringBuilder();
        text.AppendLine($"{"Item", -18}{"Need", 7}");
        foreach (var part in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.AppendLine($"{Cut(part.Key, 18), -18}{part.Value, 7:N0}");
        return text.ToString();
    }

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text.Substring(0, length - 1) + ".";
}

public enum BuildVerb
{
    /// <summary>Read: the quote of Needed for the payer at the station.</summary>
    Quote,

    /// <summary>Read: the station's queue, without blueprints, then its built ships.</summary>
    Queue,

    /// <summary>Read: the next order to build at the station, without its blueprint.</summary>
    Next,

    /// <summary>Read: what the station pays for each item of Needed, milli-SC per unit, tax not taken yet.</summary>
    Prices,

    /// <summary>Id: the order; Part: of its blueprint.</summary>
    Download,

    /// <summary>Id: the upload; Part and Blueprint: the part. Kept in memory until its order.</summary>
    Upload,

    /// <summary>Part above 0: the blueprint is in that many parts of upload Id.</summary>
    Order,

    /// <summary>A towed grid takes the free shipyard, or is told it is busy.</summary>
    Tow,

    /// <summary>Needed is sold to the station for the player, at once.</summary>
    Sale,
    Cancel,
    Start,

    /// <summary>The order leaves the queue, with nothing to settle.</summary>
    Finish,

    /// <summary>A paid order's turn starts.</summary>
    Turn,

    /// <summary>Needed: what the containers miss; Provided: the hydrogen from outside.</summary>
    Begin,

    /// <summary>Needed: what is sold because it doesn't fit.</summary>
    After,

    /// <summary>Needed: what is left in the shipyard, which is sold.</summary>
    EndTurn,

    /// <summary>Grid: a built ship left the build space. Refund: it was removed.</summary>
    ShipOut,
}

/// <summary>
/// A request a node sends the authority: a line of tab-separated fields, then a line each with
/// the needed and the provided items, item and raw amount pairs.
/// </summary>
public sealed class BuildRequest
{
    public BuildVerb Verb;
    public string Station = "";
    public long Player;
    public long Payer;
    public string Id = "";
    public long Fee;
    public int Pcu;
    public string Ship = "";
    public string Blueprint = "";
    public long HydrogenUsed;
    public bool Refund;
    public bool Exempt;
    public bool Repair;
    public long Grid;
    public int Part;
    public int Percent = 100;
    public Dictionary<string, long> Needed = new();
    public Dictionary<string, long> Provided = new();

    // What doesn't change the saved book: goes without the global command ledger
    public bool Reads =>
        Verb
            is BuildVerb.Quote
                or BuildVerb.Queue
                or BuildVerb.Next
                or BuildVerb.Prices
                or BuildVerb.Download
                or BuildVerb.Upload;

    public string Encode() =>
        string.Join(
            "\t",
            Verb.ToString(),
            Station,
            BuildQuote.N(Player),
            BuildQuote.N(Payer),
            Id ?? "",
            BuildQuote.N(Fee),
            Pcu.ToString(CultureInfo.InvariantCulture),
            (Ship ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '),
            Blueprint ?? "",
            BuildQuote.N(HydrogenUsed),
            Refund ? "1" : "0",
            Exempt ? "1" : "0",
            Repair ? "1" : "0",
            BuildQuote.N(Grid),
            Part.ToString(CultureInfo.InvariantCulture),
            Percent.ToString(CultureInfo.InvariantCulture)
        )
        + "\n"
        + Items(Needed)
        + "\n"
        + Items(Provided);

    private static string Items(Dictionary<string, long> items) =>
        string.Join(
            "\t",
            items
                .OrderBy(i => i.Key, StringComparer.Ordinal)
                .Select(i => i.Key + "\t" + BuildQuote.N(i.Value))
        );

    private static Dictionary<string, long> Items(string line)
    {
        var f = line.Split('\t');
        var items = new Dictionary<string, long>();
        for (int i = 0; i + 1 < f.Length; i += 2)
            items[f[i]] = BuildQuote.L(f[i + 1]);
        return items;
    }

    public static BuildRequest Decode(string text)
    {
        var lines = text.Split('\n');
        var f = lines[0].Split('\t');
        if (lines.Length != 3 || f.Length != 16)
            throw new FormatException("Malformed build request");
        return new BuildRequest
        {
            Verb = (BuildVerb)Enum.Parse(typeof(BuildVerb), f[0]),
            Station = f[1],
            Player = BuildQuote.L(f[2]),
            Payer = BuildQuote.L(f[3]),
            Id = f[4],
            Fee = BuildQuote.L(f[5]),
            Pcu = int.Parse(f[6], CultureInfo.InvariantCulture),
            Ship = f[7],
            Blueprint = f[8],
            HydrogenUsed = BuildQuote.L(f[9]),
            Refund = f[10] == "1",
            Exempt = f[11] == "1",
            Repair = f[12] == "1",
            Grid = BuildQuote.L(f[13]),
            Part = int.Parse(f[14], CultureInfo.InvariantCulture),
            Percent = int.Parse(f[15], CultureInfo.InvariantCulture),
            Needed = Items(lines[1]),
            Provided = Items(lines[2]),
        };
    }

    /// <summary>
    /// Does the request on the authority's book. Changes answer "1" or "0", a tab and the
    /// message, Start with the order on the next line; reads answer their data.
    /// </summary>
    public string Run(BuildBook book, IMarketWorld w, IBuildMarket market, BuildSettings s)
    {
        switch (Verb)
        {
            case BuildVerb.Quote:
                return BuildBook.Quote(w, market, Station, Payer, Needed, Fee).Encode();
            case BuildVerb.Queue:
                return string.Join(
                    "\n",
                    book.Queue(Station)
                        .Select(o => BuildBook.EncodeOrder(o, blueprint: false))
                        .Concat(book.Ships.Where(b => b.Station == Station).Select(b => b.Encode()))
                );
            case BuildVerb.Next:
                var next = book.Next(Station);
                return next == null ? "" : BuildBook.EncodeOrder(next, blueprint: false);
            case BuildVerb.Prices:
                return string.Join(
                    "\n",
                    Needed.Keys.Select(i =>
                        i + "\t" + BuildQuote.N(market?.SalePrice(Station, i) ?? 0)
                    )
                );
            case BuildVerb.Download:
                var part = book.BlueprintPart(Id, Part);
                return part == null ? "0" : "1\t" + part;
            case BuildVerb.Upload:
                return book.Upload(w.Now, Id, Part, Blueprint) ? "1" : "0";
        }
        if (Verb == BuildVerb.Order && Part > 0)
        {
            Blueprint = book.TakeUpload(Id, Part);
            if (Blueprint == null)
                return "0\tThe blueprint did not reach the shipyard, press the button again.";
        }
        BuildResult r = Verb switch
        {
            BuildVerb.Order => book.Order(
                w,
                s,
                new BuildOrder
                {
                    Station = Station,
                    Player = Player,
                    Payer = Payer,
                    Fee = Payer != 0 ? Fee : 0,
                    Pcu = Pcu,
                    Ship = Ship,
                    Blueprint = Blueprint,
                    Exempt = Exempt,
                    Repair = Repair,
                },
                Needed
            ),
            BuildVerb.Tow => book.Tow(
                w,
                new BuildOrder
                {
                    Station = Station,
                    Player = Player,
                    Pcu = Pcu,
                    Ship = Ship,
                    Grid = Grid,
                }
            ),
            BuildVerb.Sale => BuildBook.Sale(market, Station, Player, Needed),
            BuildVerb.Cancel => book.Cancel(Station, Player),
            BuildVerb.Start => book.Start(Id),
            BuildVerb.Finish => book.Finish(Id, Grid),
            BuildVerb.Turn => book.Turn(w, s, Id),
            BuildVerb.Begin => book.Begin(w, market, s, Id, Player, Needed, Provided),
            BuildVerb.After => book.After(
                w,
                market,
                s,
                Id,
                HydrogenUsed,
                Refund,
                Needed,
                Grid,
                Percent
            ),
            BuildVerb.EndTurn => book.EndTurn(w, market, Id, Needed),
            BuildVerb.ShipOut => book.ShipOut(w, Grid, Refund),
            _ => BuildResult.No("Unknown request."),
        };
        return (r.Ok ? "1\t" : "0\t")
            + r.Message.Replace('\n', ' ')
            + (
                r.Ok && r.Order != null
                    ? "\n" + BuildBook.EncodeOrder(r.Order, blueprint: false)
                    : ""
            );
    }
}
