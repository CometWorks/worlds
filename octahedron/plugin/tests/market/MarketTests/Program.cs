using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks of the station market (ServerPlugin/Market/MarketBook.cs) against a fake world: OCT's
// prices with the tax and specialties, what the Store blocks show, trades reported from them, a
// build's purchases, the hourly restock from a station's profile, price drift, the zone chips OCT
// sells at a fixed price, the saved format, and that no money is made or lost. Prints one line per
// failed check and a summary; exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/market/MarketTests

int checks = 0;
int failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

const long U = Amounts.Unit;
const string A = "Alpha Station";
const string B = "Beta Station";
const string Iron = "Ingot/Iron"; // 1 kg, 10 SC
const string Plate = "Component/SteelPlate"; // 20 kg, whole, 50 SC
const string Motor = "Component/Motor"; // 24 kg, whole, 100 SC
const string Platinum = "Ingot/Platinum"; // 1 kg, 35 SC, Alpha's specialty
const string Gold = "Ingot/Gold"; // 1 kg, not in OCT's catalog, base price 3 SC
const string Grit = "Ore/Grit"; // 1 kg, 0.4 SC
const string Chip = "Component/ZoneChip"; // whole, not in OCT's catalog, sold at a fixed price
const string Moon = "Moon Station";
const long P1 = 1;

MarketSettings Exact() => new() { DriftKg = 0 };

StoreRow Row(MarketBook m, FakeWorld w, MarketSettings s, string station, string item) =>
    m.Rows(w, s, station).FirstOrDefault(r => r.Item == item);

// OCT's prices: the reference and the tax, in whole SC
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Check(m.OctAsk(w, s, B, Motor) == 102_000, "sells at 102 %: " + m.OctAsk(w, s, B, Motor));
    Check(m.OctBid(w, s, B, Motor) == 98_000, "buys at 98 %: " + m.OctBid(w, s, B, Motor));
    Check(
        m.OctAsk(w, s, B, Plate) == 51_000 && m.OctBid(w, s, B, Plate) == 49_000,
        "50 SC: 51 and 49"
    );
    Check(
        m.OctAsk(w, s, B, Iron) == 10_000 && m.OctBid(w, s, B, Iron) == 9_000,
        "10 SC: rounded prices stay 1 SC apart: "
            + m.OctAsk(w, s, B, Iron)
            + " "
            + m.OctBid(w, s, B, Iron)
    );
    Check(
        m.OctAsk(w, s, B, Grit) == 1_000 && m.OctBid(w, s, B, Grit) == 0,
        "under 1 SC: sold at 1 SC, not bought"
    );
    Check(m.OctAsk(w, s, A, Gold) == 0 && m.OctBid(w, s, A, Gold) == 0, "gold: not in the catalog");

    var tax = new MarketSettings { DriftKg = 0, TaxPercent = 6 };
    Check(m.OctAsk(w, tax, B, Motor) == 106_000, "the tax is a setting");
    var old = new MarketSettings
    {
        DriftKg = 0,
        OctBuyPercent = 60,
        OctSellPercent = 140,
    };
    Check(
        m.OctAsk(w, old, B, Motor) == 143_000 && m.OctBid(w, old, B, Motor) == 59_000,
        "the shares are settings"
    );

    Check(
        m.OctBid(w, s, A, Platinum, true) == 41_000,
        "specialty 120 % less the tax: " + m.OctBid(w, s, A, Platinum, true)
    );
    Check(m.OctBid(w, s, A, Platinum) == 34_000, "past the quota: the normal price");
    Check(
        m.OctAsk(w, s, A, Platinum) == 43_000,
        "a specialty sells above what the station pays for it, so the Store makes no money"
    );
    Check(m.OctBid(w, s, B, Platinum, true) == 34_000, "no specialty at Beta");
}

// The Store: real stock, what OCT still buys, and trades reported back
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    var empty = Row(m, w, s, A, Plate);
    Check(empty.Stock == 0 && empty.Ask == 51_000, "no stock yet, a price all the same");
    Check(empty.Wanted == 10_000 * U, "wanted: up to 200 t at 20 kg: " + empty.Wanted);
    m.SetStock(A, Plate, 100 * U);
    Check(Row(m, w, s, A, Plate).Stock == 100 * U, "the stock shows");
    Check(Row(m, w, s, A, Plate).Wanted == 9_900 * U, "the stock counts toward the limit");
    Check(Row(m, w, s, B, Plate).Stock == 0, "stock is per station");
    Check(m.Rows(w, s, A).All(r => r.Item != Gold), "only the catalog");
    Check(Row(m, w, s, B, Grit).Wanted == 0, "not bought: wanted 0");

    m.Bought(w, s, A, Plate, 30 * U);
    Check(m.Stock(A, Plate) == 70 * U, "a purchase at the Store takes stock");
    m.Bought(w, s, A, Plate, 500 * U);
    Check(m.Stock(A, Plate) == 0, "never below none");
    m.Sold(w, s, A, Plate, 10 * U);
    Check(m.Stock(A, Plate) == 10 * U, "a sale at the Store adds stock");

    // The day's specialty quota caps what is wanted at that price, then the normal price is back
    var special = Row(m, w, s, A, Platinum);
    Check(special.Bid == 41_000 && special.Wanted == 50_000 * U, "specialty up to 50 t a day");
    m.Sold(w, s, A, Platinum, 20_000 * U);
    Check(Row(m, w, s, A, Platinum).Wanted == 30_000 * U, "the quota counts down");
    m.Sold(w, s, A, Platinum, 30_000 * U);
    var normal = Row(m, w, s, A, Platinum);
    Check(
        normal.Bid == 34_000 && normal.Wanted == 150_000 * U,
        "quota gone: normal price and limit"
    );
    m.SetStock(A, Platinum, 200_000 * U);
    Check(Row(m, w, s, A, Platinum).Wanted == 0, "OCT stops buying at 200 t");

    var encoded = string.Join("\n", m.Rows(w, s, A).Select(r => r.Encode()));
    Check(StoreRow.DecodeAll(encoded).SequenceEqual(m.Rows(w, s, A)), "rows: wire format");
    Check(StoreRow.DecodeAll("").Count == 0, "rows: none");
}

// A build's purchase: all or nothing, money moves, stock goes
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    m.SetStock(A, Plate, 100 * U);
    var fill = m.PlanBuy(w, s, A, Plate, 150 * U);
    Check(fill.Amount == 100 * U && fill.Cost == 5_100, "plan: what there is");
    Check(m.PlanBuy(w, s, A, Gold, U).Amount == 0, "plan: not sold");

    string before = m.Serialize() + w.Snapshot();
    Check(!m.Buy(w, s, A, P1, Plate, 150 * U).Ok, "too much");
    Check(!m.Buy(w, s, A, P1, Plate, U / 2).Ok, "half a plate");
    Check(!m.Buy(w, s, A, P1, Plate, 0).Ok, "nothing");
    Check(!m.Buy(w, s, A, 99, Plate, U).Ok, "no account");
    Check(!m.Buy(w, s, A, P1, "No\tSuch", U).Ok, "unknown item");
    var poor = new FakeWorld();
    poor.Balances[P1] = 10;
    Check(!m.Buy(poor, s, A, P1, Plate, U).Ok, "too poor");
    Check(m.Serialize() + w.Snapshot() == before, "refusals change nothing");

    var bought = m.Buy(w, s, A, P1, Plate, 10 * U);
    Check(bought.Ok && w.Balances[P1] == 25_000 - 510, "bought: " + bought.Message);
    Check(w.Balances[FakeWorld.Oct_] == 20_000_510, "OCT got the money");
    Check(m.Stock(A, Plate) == 90 * U && w.Total() == w.Start, "stock down, no money made or lost");
    m.Return(w, s, A, Plate, 10 * U);
    Check(m.Stock(A, Plate) == 100 * U, "back in stock");
}

// What a shipyard leaves behind: sold at OCT's price or the base price, into the stock
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    Check(m.SalePrice(w, s, A, Plate) == 49_000, "sale price: what OCT pays");
    Check(m.SalePrice(w, s, A, Gold) == 2_940, "no OCT price: base price less the tax");
    long net = m.SellOff(
        w,
        s,
        A,
        P1,
        new Dictionary<string, long>
        {
            [Plate] = 10 * U,
            [Gold] = 100 * U,
            ["Gas/Bottle"] = 2 * U,
        }
    );
    Check(net == 490 + 294 + 0, "sold for " + net);
    Check(w.Balances[P1] == 25_000 + net && w.Total() == w.Start, "paid by OCT");
    Check(m.Stock(A, Plate) == 10 * U && m.Stock(A, Gold) == 100 * U, "into the stock");
    Check(m.Stock(A, "Gas/Bottle") == 0, "what can't be stocked is gone");
    m.SetStock(A, Plate, 10_000 * U);
    m.SellOff(w, s, A, P1, new Dictionary<string, long> { [Plate] = U });
    Check(m.Stock(A, Plate) == 10_001 * U, "past OCT's stock limit");
}

// The hourly restock: only what is completely out, to the station's minimum
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = Exact();
    var common = new List<KeyValuePair<string, long>> { new(Iron, 2000 * U), new(Plate, 300 * U) };
    var alpha = new List<KeyValuePair<string, long>> { new(Motor, 5 * U) };
    IReadOnlyList<KeyValuePair<string, long>> Profile(string station) =>
        station == A ? alpha : common;

    Check(m.Restock(w, s, [A, B], Profile) != null, "first restock");
    Check(m.Stock(B, Iron) == 2000 * U && m.Stock(B, Plate) == 300 * U, "the common profile");
    Check(m.Stock(A, Motor) == 5 * U && m.Stock(A, Iron) == 0, "Alpha's own profile");
    Check(m.Restock(w, s, [A, B], Profile) == null, "once an hour");

    m.SetStock(B, Iron, 1);
    m.Bought(w, s, B, Plate, 300 * U);
    w.Now += 3600;
    Check(m.Restock(w, s, [A, B], Profile) != null, "the next hour");
    Check(m.Stock(B, Plate) == 300 * U, "out: back to the minimum");
    Check(m.Stock(B, Iron) == 1, "not completely out: left alone");
    w.Now += 3600;
    Check(m.Restock(w, s, [A, B], Profile) == null, "nothing out: nothing to say");
    var lots = new List<KeyValuePair<string, long>> { new(Iron, 500_000 * U) };
    m.SetStock(A, Iron, 0);
    m.Restock(w, s, [A], _ => lots, force: true);
    Check(m.Stock(A, Iron) == 200_000 * U, "up to OCT's 200 t: " + m.Stock(A, Iron));

    m.SetStock(B, Iron, 500 * U);
    var missing = m.Missing(B, common);
    Check(
        missing.Count == 1 && missing[0].Key == Iron && missing[0].Value == 1500 * U,
        "missing: what OCT lacks of the profile"
    );

    // A new UTC day starts the specialty purchases again, the hour's check or not
    var d = new MarketBook();
    d.Restock(w, s, [A], _ => []);
    d.Sold(w, s, A, Platinum, 50_000 * U);
    Check(d.SpecialtyBoughtKg(A) == 50_000, "the day's specialty count");
    w.Now += 3600;
    d.Restock(w, s, [A], _ => []);
    w.Now += 86_400;
    d.Restock(w, s, [A], _ => []);
    Check(d.SpecialtyBoughtKg(A) == 0, "the next day starts again");
}

// Prices drift with the volume at the station, and wear off
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = new MarketSettings();
    m.Bought(w, s, A, Iron, 100_000 * U);
    Check(Math.Abs(m.Factor(s, A, Iron, w.Now) - 2) < 1e-9, "100 t bought doubles");
    Check(m.OctAsk(w, s, A, Iron) == 20_000, "the ask doubled: " + m.OctAsk(w, s, A, Iron));
    Check(m.OctAsk(w, s, B, Iron) == 10_000, "other stations stay");
    Check(m.OctAsk(w, s, A, Motor) == 102_000, "other items stay");
    w.Now += 24 * 3600;
    Check(Math.Abs(m.Factor(s, A, Iron, w.Now) - Math.Sqrt(2)) < 1e-9, "half gone in a day");
    m.Sold(w, s, B, Iron, 1_000_000 * U);
    Check(Math.Abs(m.Factor(s, B, Iron, w.Now) - 0.5) < 1e-9, "at most halved");
    // A canceled purchase takes its price effect back
    var c = new MarketBook();
    c.SetStock(A, Iron, 50_000 * U);
    c.Buy(w, s, A, P1, Iron, 100 * U);
    Check(c.Factor(s, A, Iron, w.Now) > 1, "a purchase raises the price");
    c.Return(w, s, A, Iron, 100 * U);
    Check(Math.Abs(c.Factor(s, A, Iron, w.Now) - 1) < 1e-9, "its return takes that back");
}

// Zone chips: a fixed price from a stock kept topped up, not bought back
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = new MarketSettings { FixedAsks = { [(Moon, Chip)] = 1_000_000 }, FixedStock = 100 * U };
    Check(m.TopUp(s, [A, B]) == null, "not at these stations");
    Check(m.TopUp(s, [Moon]) != null && m.Stock(Moon, Chip) == 100 * U, "topped up");
    Check(m.TopUp(s, [Moon]) == null, "full");
    var row = Row(m, w, s, Moon, Chip);
    Check(
        row.Ask == 1_000_000 && row.Stock == 100 * U && row.Bid == 0 && row.Wanted == 0,
        "the row"
    );
    Check(Row(m, w, s, A, Chip).Item == null, "not at Alpha");
    m.Bought(w, s, Moon, Chip, 30 * U);
    Check(m.OctAsk(w, s, Moon, Chip) == 1_000_000, "the price doesn't drift");
    Check(m.TopUp(s, [Moon]) != null && m.Stock(Moon, Chip) == 100 * U, "topped up again");
}

// Saved and parsed
{
    var w = new FakeWorld();
    var m = new MarketBook();
    var s = new MarketSettings();
    m.Restock(w, s, [A], _ => [new(Iron, 5 * U)]);
    m.Sold(w, s, A, Platinum, 10 * U);
    var back = MarketBook.Deserialize(m.Serialize());
    Check(back.Serialize() == m.Serialize(), "saved: the same");
    Check(back.Stock(A, Iron) == 5 * U && back.Stock(A, Platinum) == 10 * U, "saved: the stock");
    Check(
        back.SpecialtyBoughtKg(A) == 10 && back.Hour == m.Hour && back.Day == m.Day,
        "saved: counts"
    );
    Check(
        back.Factor(s, A, Platinum, w.Now) == m.Factor(s, A, Platinum, w.Now),
        "saved: the drift"
    );
    bool old = false;
    try
    {
        MarketBook.Deserialize("octahedron-market 1\nN\t0\t0\n");
    }
    catch (FormatException)
    {
        old = true;
    }
    Check(old, "saved: the ledgers' format is refused");

    var profiles = MarketBook.ParseProfiles(
        "Moon Station=Ore/Uranium=100,Ingot/Uranium=50; Beta Station=Ore/Gold=2.5;"
    );
    Check(
        profiles["Moon Station"].Count == 2 && profiles["Beta Station"][0].Value == 5 * U / 2,
        "profiles"
    );
    Check(MarketBook.ParseProfiles("").Count == 0, "profiles: none");
    bool bad = false;
    try
    {
        MarketBook.ParseProfiles("Ore/Gold=2");
    }
    catch (FormatException)
    {
        bad = true;
    }
    Check(bad, "profiles: no station");
    Check(MarketBook.ParseAmounts("Ingot/Iron=2,Ore/Ice=0.5")[1].Value == U / 2, "amounts");
    Check(
        MarketBook.ParseStations("A=Ore/Iron,Ore/Gold; B=")["A"].Contains("Ore/Gold"),
        "stations"
    );
    Check(MarketBook.Glob("Ore/*, Ingot/Iron").IsMatch("ingot/iron"), "glob");
    Check(MarketBook.Glob("") == null, "glob: none");
    Check(MarketBook.Price(12_500) == "12.5 SC" && MarketBook.Sc(1234) == "1,234 SC", "formats");
}

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;

sealed class FakeWorld : IMarketWorld
{
    public const long Oct_ = 1000;
    private static readonly Dictionary<string, (double Kg, bool Integral, long Price)> Items = new()
    {
        ["Ingot/Iron"] = (1, false, 10_000),
        ["Component/SteelPlate"] = (20, true, 50_000),
        ["Component/Motor"] = (24, true, 100_000),
        ["Ingot/Platinum"] = (1, false, 35_000),
        ["Ingot/Gold"] = (1, false, 0),
        ["Ore/Grit"] = (1, false, 400),
        ["Component/ZoneChip"] = (0.25, true, 0),
    };

    public long Now { get; set; } = 1_800_000_000;
    public long Oct => Oct_;
    public readonly Dictionary<long, long> Balances = new();
    public long Start;

    public FakeWorld()
    {
        foreach (var id in new long[] { 1, 2, 3 })
            Balances[id] = 25_000;
        Balances[Oct_] = 20_000_000;
        Start = Total();
    }

    public long Total() => Balances.Values.Sum();

    public string Snapshot() =>
        string.Join(",", Balances.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value}"));

    public bool Item(string item, out double massKg, out bool integral)
    {
        bool known = Items.TryGetValue(item, out var info);
        massKg = info.Kg;
        integral = info.Integral;
        return known;
    }

    public long Reference(string station, string item) =>
        Items.TryGetValue(item, out var info) ? info.Price : 0;

    public long BasePrice(string item) => item == "Ingot/Gold" ? 3_000 : Reference("", item);

    public IEnumerable<string> Catalog(string station) =>
        Items.Where(i => i.Value.Price > 0).Select(i => i.Key);

    public bool Specialty(string station, string item) =>
        station == "Alpha Station" && item == "Ingot/Platinum";

    public long Balance(long account) => Balances.TryGetValue(account, out long b) ? b : -1;

    public bool ChangeBalance(long account, long amount)
    {
        if (!Balances.TryGetValue(account, out long b) || b + amount < 0)
            return false;
        Balances[account] = b + amount;
        return true;
    }
}
