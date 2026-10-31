using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>An entry of a shipyard's queue: a free build, or a paid one with its build ledger.</summary>
public sealed class BuildOrder
{
    public string Id = "";
    public string Station = "";

    // The player who ordered; only they cancel it
    public long Player;

    // Who pays and gets the leftovers back: the player or their faction. Unset: a free build.
    public LedgerOwner Payer;

    // The labor fee paid to OCT, SC
    public long Fee;
    public int Pcu;
    public string Ship = "";

    // Unix seconds
    public long Created;
    public bool Started;

    // An admin who ignores PCU limits ordered it (FreePrints.Exempt): always a free build, without
    // the per-ship PCU cap. Kept here because the node that builds it may not know their admin
    // settings.
    public bool Exempt;

    // The blueprint, opaque here: base64, no tabs or line breaks
    public string Blueprint = "";

    // What the build ledger got: every block's components and the ice for the hydrogen, raw
    public Dictionary<string, long> Items = new();

    public bool Free => !Payer.IsSet;

    /// <summary>The ledger reservation of the payer that is the build ledger.</summary>
    public string Reservation => "build " + Id;
}

/// <summary>Where one item of a paid build comes from, raw amounts, costs in SC.</summary>
public sealed class QuoteLine
{
    public string Item = "";
    public long Needed;
    public long Ledger;
    public long Listings;
    public long ListingsCost;
    public long Oct;
    public long OctCost;

    // Nobody has it at the station
    public long Missing;

    public long Bought => Listings + Oct;
    public long Cost => ListingsCost + OctCost;
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

    /// <summary>The first item nobody has enough of, the hydrogen left out: a ship gets less of that.</summary>
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
                        N(l.Ledger),
                        N(l.Listings),
                        N(l.ListingsCost),
                        N(l.Oct),
                        N(l.OctCost),
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
            else if (f[0] == "L" && f.Length == 9)
                quote.Lines.Add(
                    new QuoteLine
                    {
                        Item = f[1],
                        Needed = L(f[2]),
                        Ledger = L(f[3]),
                        Listings = L(f[4]),
                        ListingsCost = L(f[5]),
                        Oct = L(f[6]),
                        OctCost = L(f[7]),
                        Missing = L(f[8]),
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

/// <summary>The market as a build needs it: plan a purchase and buy into a reservation.</summary>
public interface IBuildMarket
{
    List<MarketFill> PlanBuy(string station, LedgerOwner buyer, string item, long raw);

    MarketResult Buy(string station, LedgerOwner buyer, string item, long raw, string reservation);
}

public sealed class BuildSettings
{
    // Queue entries per shipyard, the one being built included
    public int QueueMax = 6;

    // The item the build's hydrogen is bought as; it is not loaded into the shipyard
    public string HydrogenItem = "Ore/Ice";
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
/// The shipyards' queues and their build ledgers. A paid build's components are reserved
/// in the payer's ledger at the station (an uncounted reservation, the build ledger): first
/// what the payer has there, then bought from the cheapest listings and OCT, and the labor fee
/// goes to OCT. Canceling gives it all back. At build start the components leave the ledger
/// for the shipyard's containers; at the end the leftovers come back. Pure bookkeeping without
/// game types, so it is tested on its own (tests/builds). Runs on the authority's game thread,
/// the World Authority's on a cluster.
/// </summary>
public sealed class BuildBook
{
    private const string Header = "octahedron-builds 1";
    private readonly Dictionary<string, BuildOrder> orders = new();
    private long nextId;

    public IEnumerable<BuildOrder> Orders => orders.Values;

    public BuildOrder Get(string id) => id != null && orders.TryGetValue(id, out var o) ? o : null;

    /// <summary>The station's queue in order: the build running first, then by when they were ordered.</summary>
    public List<BuildOrder> Queue(string station) =>
        orders
            .Values.Where(o => o.Station == station)
            .OrderByDescending(o => o.Started)
            .ThenBy(o => o.Created)
            .ThenBy(o => o.Id.Length)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The next order to build at the station; null when none waits or one is running.</summary>
    public BuildOrder Next(string station)
    {
        var queue = Queue(station);
        return queue.Count == 0 || queue[0].Started ? null : queue[0];
    }

    /// <summary>The labor fee: per10Pcu SC per 10 PCU, rounded up.</summary>
    public static long Fee(int pcu, long per10Pcu) =>
        pcu <= 0 || per10Pcu <= 0 ? 0 : checked((pcu * per10Pcu + 9) / 10);

    // --- quote and order ---

    /// <summary>
    /// Where the items would come from: the payer's free stock at the station, then the market
    /// (the cheapest listings, OCT among them at its price), what is left missing. No market: the
    /// ledger only.
    /// </summary>
    public static BuildQuote Quote(
        IMarketWorld w,
        IBuildMarket market,
        string station,
        LedgerOwner payer,
        IReadOnlyDictionary<string, long> needed,
        long fee
    )
    {
        var quote = new BuildQuote { Fee = fee, Balance = w.Balance(payer) };
        var free = w.Ledger.FreeOf(station, payer);
        foreach (
            var item in needed.Where(n => n.Value > 0).OrderBy(n => n.Key, StringComparer.Ordinal)
        )
        {
            var line = new QuoteLine { Item = item.Key, Needed = item.Value };
            line.Ledger = Math.Min(
                item.Value,
                free.TryGetValue(item.Key, out long have) ? have : 0
            );
            long rest = item.Value - line.Ledger;
            if (rest > 0 && market != null)
                foreach (var fill in market.PlanBuy(station, payer, item.Key, rest))
                    if (fill.Oct)
                    {
                        line.Oct += fill.Amount;
                        line.OctCost += fill.Cost;
                    }
                    else
                    {
                        line.Listings += fill.Amount;
                        line.ListingsCost += fill.Cost;
                    }
            line.Missing = rest - line.Bought;
            quote.Lines.Add(line);
        }
        return quote;
    }

    /// <summary>
    /// Queues an order. A paid one (Payer set) gets its build ledger: <paramref name="provided"/>
    /// comes from outside (the hydrogen of a ship at the station, as ice), the rest of
    /// <paramref name="needed"/> from the payer's ledger and the market as quoted, and the fee
    /// is paid, all as long as the quote's total is at most <paramref name="maxTotal"/>.
    /// </summary>
    public BuildResult Order(
        IMarketWorld w,
        IBuildMarket market,
        BuildSettings s,
        BuildOrder order,
        IReadOnlyDictionary<string, long> needed,
        IReadOnlyDictionary<string, long> provided,
        long maxTotal
    )
    {
        if (!LedgerBook.ValidName(order.Station))
            return BuildResult.No("Unknown station.");
        var mine = orders.Values.FirstOrDefault(o =>
            o.Station == order.Station && o.Player == order.Player
        );
        if (mine != null)
            return BuildResult.No(
                $"'{mine.Ship}' is in this shipyard's queue for you already, one ship at a time."
            );
        if (order.Free && orders.Values.Any(o => o.Free && o.Player == order.Player))
            return BuildResult.No("The shipyards build one free ship at a time for you.");
        if (orders.Values.Count(o => o.Station == order.Station) >= s.QueueMax)
            return BuildResult.No($"The shipyard's queue is full, {s.QueueMax} ships.");

        order.Id = "b" + (nextId + 1).ToString(CultureInfo.InvariantCulture);
        order.Created = w.Now;
        order.Started = false;
        order.Ship = Clean(order.Ship);
        order.Items = new Dictionary<string, long>();
        if (order.Free)
        {
            nextId++;
            orders[order.Id] = order;
            return new BuildResult(true, $"'{order.Ship}' is in the queue, a free build.", order);
        }

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
                $"{LedgerBook.Units(missing.Missing)} {missing.Item} more than this station has for sale."
            );
        if (quote.Total > maxTotal)
            return BuildResult.No(
                $"The price is {MarketBook.Sc(quote.Total)} now, see the new quote and press again."
            );
        if (quote.Balance < 0)
            return BuildResult.No("No bank account.");
        if (quote.Balance < quote.Total)
            return BuildResult.No(
                $"That costs {MarketBook.Sc(quote.Total)}, the account has {MarketBook.Sc(quote.Balance)}."
            );

        var ops = new List<LedgerOp>();
        var fromLedger = quote.Lines.Where(l => l.Ledger > 0).ToList();
        if (fromLedger.Count > 0)
        {
            var reserve = new LedgerOp
            {
                Action = LedgerAction.Reserve,
                Station = order.Station,
                Owner = order.Payer,
                Reservation = order.Reservation,
            };
            foreach (var l in fromLedger)
                reserve.Add(l.Item, l.Ledger);
            ops.Add(reserve);
        }
        if (ops.Count > 0)
        {
            string error = w.ApplyLedger(ops);
            if (error != null)
                return BuildResult.No(error);
        }

        foreach (var l in quote.Lines.Where(l => l.Bought > 0))
        {
            var bought = market.Buy(
                order.Station,
                order.Payer,
                l.Item,
                l.Bought,
                order.Reservation
            );
            if (!bought.Ok)
                return Undo(w, order, $"Buying {l.Item} failed: {bought.Message}");
        }
        if (order.Fee > 0)
        {
            if (!w.ChangeBalance(order.Payer, -order.Fee))
                return Undo(w, order, "The labor fee could not be paid.");
            w.ChangeBalance(w.Oct, order.Fee);
        }
        // Last, so that a refusal before leaves what came from outside with the caller
        var fill = new LedgerOp
        {
            Action = LedgerAction.Fill,
            Station = order.Station,
            Owner = order.Payer,
            Reservation = order.Reservation,
        };
        foreach (var p in provided.Where(p => p.Value > 0 && needed.ContainsKey(p.Key)))
            fill.Add(p.Key, Math.Min(p.Value, needed[p.Key]));
        if (fill.Items.Count > 0)
            w.ApplyLedger([fill]);

        // The hydrogen the station doesn't have is left out: the ship's tanks get less
        foreach (var n in needed.Where(n => n.Value > 0))
            order.Items[n.Key] =
                n.Value - (quote.Lines.FirstOrDefault(l => l.Item == n.Key)?.Missing ?? 0);
        if (order.Items.TryGetValue(s.HydrogenItem, out long ice) && ice <= 0)
            order.Items.Remove(s.HydrogenItem);
        nextId++;
        orders[order.Id] = order;
        return new BuildResult(
            true,
            $"'{order.Ship}' is in the queue: {MarketBook.Sc(quote.Cost)} for the components and "
                + $"{MarketBook.Sc(order.Fee)} labor paid.",
            order
        );
    }

    // A purchase failed half way: what is in the build ledger goes back to the payer
    private static BuildResult Undo(IMarketWorld w, BuildOrder order, string why)
    {
        Release(w, order);
        return BuildResult.No(why + " What was bought for it is in the ledger.");
    }

    private static bool HasReservation(IMarketWorld w, BuildOrder order) =>
        w.Ledger.Get(order.Station)?.Reservations.ContainsKey(order.Reservation) == true;

    private static string Release(IMarketWorld w, BuildOrder order) =>
        HasReservation(w, order)
            ? w.ApplyLedger([
                new LedgerOp
                {
                    Action = LedgerAction.Release,
                    Station = order.Station,
                    Owner = order.Payer,
                    Reservation = order.Reservation,
                },
            ])
            : null;

    /// <summary>The player's order at the station leaves the queue, if it hasn't started: the build ledger goes back to the payer, and the fee.</summary>
    public BuildResult Cancel(IMarketWorld w, string station, long player)
    {
        var order = orders.Values.FirstOrDefault(o => o.Station == station && o.Player == player);
        if (order == null)
            return BuildResult.No("You have no ship in this shipyard's queue.");
        if (order.Started)
            return BuildResult.No($"'{order.Ship}' is being built, it can't be canceled now.");
        if (order.Free)
        {
            orders.Remove(order.Id);
            return new BuildResult(true, $"'{order.Ship}' left the queue.", order);
        }
        string error = Release(w, order);
        if (error != null)
            return BuildResult.No(error);
        Refund(w, order);
        orders.Remove(order.Id);
        return new BuildResult(
            true,
            $"'{order.Ship}' left the queue. Its components are in the ledger at {station} and "
                + $"the labor fee of {MarketBook.Sc(order.Fee)} is paid back.",
            order
        );
    }

    private static void Refund(IMarketWorld w, BuildOrder order)
    {
        if (order.Fee <= 0)
            return;
        // OCT keeps a floor, so it can pay; should it not, the payer still gets the fee back
        w.ChangeBalance(w.Oct, -order.Fee);
        w.ChangeBalance(order.Payer, order.Fee);
    }

    /// <summary>
    /// The build starts: a paid build's components leave the build ledger for the shipyard. The
    /// hydrogen stays in it until the end. Returns the items to load (none for a free build).
    /// </summary>
    public BuildResult Start(IMarketWorld w, BuildSettings s, string id)
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        if (order.Started)
            return BuildResult.No($"'{order.Ship}' is being built already.");
        var components = order.Items.Where(i => i.Key != s.HydrogenItem).ToList();
        if (!order.Free && components.Count > 0)
        {
            var take = new LedgerOp
            {
                Action = LedgerAction.Take,
                Station = order.Station,
                Owner = order.Payer,
                Reservation = order.Reservation,
            };
            foreach (var c in components)
                take.Add(c.Key, c.Value);
            string error = w.ApplyLedger([take]);
            if (error != null)
                return BuildResult.No(error);
        }
        order.Started = true;
        return new BuildResult(true, $"'{order.Ship}' is being built.", order);
    }

    /// <summary>
    /// The build ended: the order leaves the queue. A paid build gets the leftover components
    /// back into the payer's ledger, uses up the ice of the hydrogen that went into the ship and
    /// returns the rest. With <paramref name="refund"/> (the build couldn't start, or its ship
    /// was removed) every component comes back, whatever the leftovers, and the fee too. An order
    /// that never started still has its components in the build ledger, so only that comes back.
    /// </summary>
    public BuildResult Finish(
        IMarketWorld w,
        BuildSettings s,
        string id,
        IReadOnlyDictionary<string, long> leftovers,
        long hydrogenUsed,
        bool refund
    )
    {
        var order = Get(id);
        if (order == null)
            return BuildResult.No($"No order {id}.");
        if (order.Free)
        {
            orders.Remove(id);
            return new BuildResult(true, "", order);
        }
        if (refund)
            leftovers = order.Started
                ? order
                    .Items.Where(i => i.Key != s.HydrogenItem)
                    .ToDictionary(i => i.Key, i => i.Value)
                : new Dictionary<string, long>();
        var ops = new List<LedgerOp>();
        var back = new LedgerOp
        {
            Action = LedgerAction.Fill,
            Station = order.Station,
            Owner = order.Payer,
            Reservation = order.Reservation,
        };
        foreach (var l in leftovers.Where(l => l.Value > 0 && l.Key != s.HydrogenItem))
            back.Add(l.Key, l.Value);
        if (back.Items.Count > 0)
            ops.Add(back);
        long reserved = 0;
        if (
            w.Ledger.Get(order.Station)?.Reservations.TryGetValue(order.Reservation, out var r)
            == true
        )
            r.Items.TryGetValue(s.HydrogenItem, out reserved);
        long used = Math.Min(Math.Max(0, hydrogenUsed), reserved);
        if (used > 0)
            ops.Add(
                new LedgerOp
                {
                    Action = LedgerAction.Take,
                    Station = order.Station,
                    Owner = order.Payer,
                    Reservation = order.Reservation,
                }.Add(s.HydrogenItem, used)
            );
        string error = ops.Count > 0 ? w.ApplyLedger(ops) : null;
        error ??= Release(w, order);
        if (error != null)
            return BuildResult.No(error);
        if (refund)
            Refund(w, order);
        orders.Remove(id);
        return new BuildResult(true, "", order);
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
                o.Payer.IsSet ? o.Payer.ToString() : "",
                BuildQuote.N(o.Fee),
                o.Pcu.ToString(CultureInfo.InvariantCulture),
                Clean(o.Ship),
                BuildQuote.N(o.Created),
                // Flags: 1 started, 2 exempt; orders from before Exempt have 0 or 1
                ((o.Started ? 1 : 0) | (o.Exempt ? 2 : 0)).ToString(CultureInfo.InvariantCulture),
                blueprint ? o.Blueprint ?? "" : ""
            )
        );
        foreach (var i in o.Items.OrderBy(i => i.Key, StringComparer.Ordinal))
            text.Append('\t').Append(i.Key).Append('\t').Append(BuildQuote.N(i.Value));
        return text.ToString();
    }

    public static BuildOrder DecodeOrder(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 11 || f[0] != "O" || (f.Length - 11) % 2 != 0)
            throw new FormatException("Malformed build order");
        var order = new BuildOrder
        {
            Id = f[1],
            Station = f[2],
            Player = BuildQuote.L(f[3]),
            Fee = BuildQuote.L(f[5]),
            Pcu = int.Parse(f[6], CultureInfo.InvariantCulture),
            Ship = f[7],
            Created = BuildQuote.L(f[8]),
            Blueprint = f[10],
        };
        var flags = int.Parse(f[9], CultureInfo.InvariantCulture);
        order.Started = (flags & 1) != 0;
        order.Exempt = (flags & 2) != 0;
        if (f[4].Length > 0 && !LedgerOwner.TryParse(f[4], out order.Payer))
            throw new FormatException("Malformed build payer");
        for (int i = 11; i < f.Length; i += 2)
            order.Items[f[i]] = BuildQuote.L(f[i + 1]);
        return order;
    }

    public string Serialize()
    {
        var text = new StringBuilder(Header).Append('\n');
        text.Append("N\t").Append(BuildQuote.N(nextId)).Append('\n');
        foreach (var o in orders.Values.OrderBy(o => o.Created).ThenBy(o => o.Id))
            text.Append(EncodeOrder(o, blueprint: true)).Append('\n');
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
        text.AppendLine($"From listings: {quote.Lines.Sum(l => l.ListingsCost):N0} SC");
        text.AppendLine($"From OCT: {quote.Lines.Sum(l => l.OctCost):N0} SC");
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

    /// <summary>Read: the station's queue, without blueprints.</summary>
    Queue,

    /// <summary>Read: the next order to build at the station, with its blueprint.</summary>
    Next,
    Order,
    Cancel,
    Start,

    /// <summary>Needed: the leftovers.</summary>
    Finish,
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
    public LedgerOwner Payer;
    public string Id = "";
    public long Fee;
    public int Pcu;
    public string Ship = "";
    public string Blueprint = "";
    public long MaxTotal;
    public long HydrogenUsed;
    public bool Refund;
    public bool Exempt;
    public Dictionary<string, long> Needed = new();
    public Dictionary<string, long> Provided = new();

    public bool Reads => Verb is BuildVerb.Quote or BuildVerb.Queue or BuildVerb.Next;

    public string Encode() =>
        string.Join(
            "\t",
            Verb.ToString(),
            Station,
            BuildQuote.N(Player),
            Payer.IsSet ? Payer.ToString() : "",
            Id ?? "",
            BuildQuote.N(Fee),
            Pcu.ToString(CultureInfo.InvariantCulture),
            (Ship ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '),
            Blueprint ?? "",
            BuildQuote.N(MaxTotal),
            BuildQuote.N(HydrogenUsed),
            Refund ? "1" : "0",
            Exempt ? "1" : "0"
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
        // 12 fields: from a node from before Exempt
        if (lines.Length != 3 || f.Length is not (12 or 13))
            throw new FormatException("Malformed build request");
        var r = new BuildRequest
        {
            Verb = (BuildVerb)Enum.Parse(typeof(BuildVerb), f[0]),
            Station = f[1],
            Player = BuildQuote.L(f[2]),
            Id = f[4],
            Fee = BuildQuote.L(f[5]),
            Pcu = int.Parse(f[6], CultureInfo.InvariantCulture),
            Ship = f[7],
            Blueprint = f[8],
            MaxTotal = BuildQuote.L(f[9]),
            HydrogenUsed = BuildQuote.L(f[10]),
            Refund = f[11] == "1",
            Exempt = f.Length > 12 && f[12] == "1",
            Needed = Items(lines[1]),
            Provided = Items(lines[2]),
        };
        if (f[3].Length > 0 && !LedgerOwner.TryParse(f[3], out r.Payer))
            throw new FormatException("Malformed build payer");
        return r;
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
                    book.Queue(Station).Select(o => BuildBook.EncodeOrder(o, blueprint: false))
                );
            case BuildVerb.Next:
                var next = book.Next(Station);
                return next == null ? "" : BuildBook.EncodeOrder(next, blueprint: true);
        }
        BuildResult r = Verb switch
        {
            BuildVerb.Order => book.Order(
                w,
                market,
                s,
                new BuildOrder
                {
                    Station = Station,
                    Player = Player,
                    Payer = Payer,
                    Fee = Payer.IsSet ? Fee : 0,
                    Pcu = Pcu,
                    Ship = Ship,
                    Blueprint = Blueprint,
                    Exempt = Exempt,
                },
                Needed,
                Provided,
                MaxTotal
            ),
            BuildVerb.Cancel => book.Cancel(w, Station, Player),
            BuildVerb.Start => book.Start(w, s, Id),
            BuildVerb.Finish => book.Finish(w, s, Id, Needed, HydrogenUsed, Refund),
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
