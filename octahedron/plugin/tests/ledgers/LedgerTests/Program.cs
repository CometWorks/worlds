using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks of the station ledger bookkeeping (ServerPlugin/Ledgers/LedgerBook.cs) against fake
// world rules, and of the kiosk requests, replies and rules (ServerPlugin/Kiosks/KioskProtocol.cs). Prints one line per failed check and a summary; exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/ledgers/LedgerTests

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

const long U = LedgerBook.Unit;
const string A = "Alpha Station";
const string B = "Beta Station";
const string Iron = "Ingot/Iron"; // 1 kg, fractional
const string Plate = "Component/SteelPlate"; // 20 kg, whole units
var p1 = LedgerOwner.Player(1);
var p2 = LedgerOwner.Player(2);
var f10 = LedgerOwner.OfFaction(10);

LedgerOp Op(LedgerAction action, LedgerOwner owner, params (string Item, long Raw)[] items)
{
    var op = new LedgerOp
    {
        Action = action,
        Station = A,
        Owner = owner,
    };
    foreach (var (item, raw) in items)
        op.Add(item, raw);
    return op;
}

LedgerOp Res(
    LedgerAction action,
    LedgerOwner owner,
    string id,
    params (string Item, long Raw)[] items
)
{
    var op = Op(action, owner, items);
    op.Reservation = id;
    return op;
}

long Free(LedgerBook book, LedgerOwner owner, string item, string station = A) =>
    book.FreeOf(station, owner).TryGetValue(item, out long raw) ? raw : 0;

long Reserved(LedgerBook book, string id, string item) =>
    book.Get(A)?.Reservations.TryGetValue(id, out var r) == true
    && r.Items.TryGetValue(item, out long raw)
        ? raw
        : 0;

// Every failed apply must leave the book exactly as it was
string Refused(LedgerBook book, string what, params LedgerOp[] ops)
{
    string before = book.Serialize();
    string error = book.Apply(ops);
    Check(error != null, what + ": refused");
    Check(book.Serialize() == before, what + ": nothing changed");
    return error ?? "";
}

void Applied(LedgerBook book, string what, params LedgerOp[] ops)
{
    string error = book.Apply(ops);
    Check(error == null, what + ": " + error);
}

var rules = new FakeRules();
LedgerBook New() => new(rules);

// Deposit and withdraw
{
    var book = New();
    Applied(book, "deposit", Op(LedgerAction.Deposit, p1, (Iron, 1500 * U), (Plate, 10 * U)));
    Check(Free(book, p1, Iron) == 1500 * U, "deposit: iron");
    Check(Free(book, p1, Plate) == 10 * U, "deposit: plates");
    Check(Math.Abs(book.MassKg(A, p1) - 1700) < 1e-6, "deposit: mass 1500 + 200 kg");
    Check(Free(book, p1, Iron, B) == 0, "deposit: other station untouched");
    Applied(book, "withdraw part", Op(LedgerAction.Withdraw, p1, (Iron, 500 * U)));
    Check(Free(book, p1, Iron) == 1000 * U, "withdraw: rest");
    Refused(book, "withdraw too much", Op(LedgerAction.Withdraw, p1, (Iron, 1001 * U)));
    Refused(book, "withdraw another's", Op(LedgerAction.Withdraw, p2, (Iron, 1 * U)));
    Applied(book, "withdraw all", Op(LedgerAction.Withdraw, p1, (Iron, 1000 * U), (Plate, 10 * U)));
    Check(book.Get(A) == null, "withdraw all: the empty station is dropped");
    Check(book.Serialize() == New().Serialize(), "withdraw all: empty book");
}

// Validation
{
    var book = New();
    Applied(book, "fractional ingot", Op(LedgerAction.Deposit, p1, (Iron, U / 2)));
    Refused(book, "fractional component", Op(LedgerAction.Deposit, p1, (Plate, U / 2)));
    Refused(book, "unknown item", Op(LedgerAction.Deposit, p1, ("Ingot/Unobtainium", U)));
    Refused(book, "zero amount", Op(LedgerAction.Deposit, p1, (Iron, 0)));
    Refused(book, "negative amount", Op(LedgerAction.Deposit, p1, (Iron, -U)));
    Refused(book, "no items", Op(LedgerAction.Deposit, p1));
    Refused(book, "unknown owner", Op(LedgerAction.Deposit, LedgerOwner.Player(99), (Iron, U)));
    var station = Op(LedgerAction.Deposit, p1, (Iron, U));
    station.Station = "Nowhere Station";
    Refused(book, "unknown station", station);
    var tab = Op(LedgerAction.Deposit, p1, (Iron, U));
    tab.Station = "Alpha\tStation";
    Refused(book, "tab in a station name", tab);
    Refused(book, "nothing to do", []);
    Refused(book, "transfer to self", TransferOp(p1, p1, (Iron, U / 4)));
    Refused(book, "transfer to nobody", TransferOp(p1, default, (Iron, U / 4)));
    Refused(book, "overflow", Op(LedgerAction.Deposit, p1, (Iron, long.MaxValue)));
}

LedgerOp TransferOp(LedgerOwner from, LedgerOwner to, params (string, long)[] items)
{
    var op = Op(LedgerAction.Transfer, from, items);
    op.Other = to;
    return op;
}

// The limit: 100 t a player, 1000 t a faction, none for player 2
{
    var book = New();
    Applied(book, "up to the limit", Op(LedgerAction.Deposit, p1, (Iron, 100_000 * U)));
    string error = Refused(book, "over the limit", Op(LedgerAction.Deposit, p1, (Iron, U)));
    Check(error.Contains("100 t"), "over the limit: names the limit: " + error);
    var beta = Op(LedgerAction.Deposit, p1, (Iron, 100_000 * U));
    beta.Station = B;
    Applied(book, "the limit is per station", beta);
    Applied(book, "no limit for player 2", Op(LedgerAction.Deposit, p2, (Iron, 5_000_000 * U)));
    Refused(book, "transfer over the receiver's limit", TransferOp(p2, p1, (Iron, U)));
    Applied(book, "transfer to a faction", TransferOp(p2, f10, (Iron, 1_000_000 * U)));
    Refused(book, "over the faction's limit", TransferOp(p2, f10, (Iron, U)));
    Applied(book, "withdraw under the limit", Op(LedgerAction.Withdraw, p1, (Iron, 10 * U)));
    Applied(book, "deposit up to it again", Op(LedgerAction.Deposit, p1, (Iron, 10 * U)));
    // Taking out first makes room within one batch
    Applied(
        book,
        "swap within the limit",
        Op(LedgerAction.Withdraw, p1, (Iron, 200 * U)),
        Op(LedgerAction.Deposit, p1, (Plate, 10 * U))
    );
    Check(Math.Abs(book.MassKg(A, p1) - 100_000) < 1e-6, "swap: at the limit");
}

// Reservations that count toward the limit, as listings
{
    var book = New();
    Applied(book, "stock", Op(LedgerAction.Deposit, p1, (Iron, 1000 * U)));
    var reserve = Res(LedgerAction.Reserve, p1, "listing-1", (Iron, 400 * U));
    reserve.Counted = true;
    Applied(book, "reserve", reserve);
    Check(
        Free(book, p1, Iron) == 600 * U && Reserved(book, "listing-1", Iron) == 400 * U,
        "reserve: moved"
    );
    Check(Math.Abs(book.MassKg(A, p1) - 1000) < 1e-6, "reserve: counted mass unchanged");
    Refused(book, "withdraw reserved", Op(LedgerAction.Withdraw, p1, (Iron, 601 * U)));
    Refused(
        book,
        "reserve more than free",
        Res(LedgerAction.Reserve, p1, "listing-2", (Iron, 601 * U))
    );
    Refused(
        book,
        "another owner's reservation id",
        Res(LedgerAction.Reserve, p2, "listing-1", (Iron, U))
    );
    Refused(book, "release another's", Res(LedgerAction.Release, p2, "listing-1"));
    Refused(book, "release a missing one", Res(LedgerAction.Release, p1, "listing-9"));
    Refused(
        book,
        "release more than reserved",
        Res(LedgerAction.Release, p1, "listing-1", (Iron, 401 * U))
    );
    Applied(book, "release part", Res(LedgerAction.Release, p1, "listing-1", (Iron, 100 * U)));
    Check(
        Free(book, p1, Iron) == 700 * U && Reserved(book, "listing-1", Iron) == 300 * U,
        "release part: moved"
    );
    Check(book.ReservationsOf(A, p1).Single().Value.Counted, "release part: still counted");
    // A sale: the reservation goes to the buyer
    Applied(book, "give part", GiveOp(p1, "listing-1", p2, (Iron, 100 * U)));
    Check(
        Free(book, p2, Iron) == 100 * U && Reserved(book, "listing-1", Iron) == 200 * U,
        "give part: moved"
    );
    Refused(book, "give to the owner", GiveOp(p1, "listing-1", p1));
    Applied(book, "give the rest", GiveOp(p1, "listing-1", p2));
    Check(Free(book, p2, Iron) == 300 * U, "give the rest: buyer has it");
    Check(!book.ReservationsOf(A, p1).Any(), "give the rest: reservation gone");
    Check(Math.Abs(book.MassKg(A, p1) - 700) < 1e-6, "give the rest: seller's mass");
}

LedgerOp GiveOp(LedgerOwner owner, string id, LedgerOwner to, params (string, long)[] items)
{
    var op = Res(LedgerAction.Give, owner, id, items);
    op.Other = to;
    return op;
}

// A counted reservation still counts: a listing can't make room for more stock
{
    var book = New();
    Applied(book, "full", Op(LedgerAction.Deposit, p1, (Iron, 100_000 * U)));
    var reserve = Res(LedgerAction.Reserve, p1, "listing-1", (Iron, 50_000 * U));
    reserve.Counted = true;
    Applied(book, "list half", reserve);
    Refused(book, "deposit while listed", Op(LedgerAction.Deposit, p1, (Iron, U)));
    // A sale to a full buyer is refused
    Applied(book, "buyer stock", Op(LedgerAction.Deposit, p2, (Iron, U)));
    var full = LedgerOwner.Player(3);
    Applied(book, "full buyer", Op(LedgerAction.Deposit, full, (Iron, 100_000 * U)));
    Refused(book, "give to a full buyer", GiveOp(p1, "listing-1", full, (Iron, U)));
}

// Reservations outside the limit, as build ledgers
{
    var book = New();
    Applied(book, "own stock", Op(LedgerAction.Deposit, p1, (Plate, 100 * U)));
    Applied(book, "reserve own plates", Res(LedgerAction.Reserve, p1, "build-1", (Plate, 100 * U)));
    Check(book.MassKg(A, p1) == 0, "build: not counted");
    Applied(
        book,
        "fill bought plates past the limit",
        Res(LedgerAction.Fill, p1, "build-1", (Plate, 10_000 * U))
    );
    Check(Reserved(book, "build-1", Plate) == 10_100 * U, "fill: 10,100 plates");
    Applied(book, "deposit while building", Op(LedgerAction.Deposit, p1, (Iron, 100_000 * U)));
    Applied(
        book,
        "build start takes most",
        Res(LedgerAction.Take, p1, "build-1", (Plate, 10_000 * U))
    );
    Refused(
        book,
        "take more than is left",
        Res(LedgerAction.Take, p1, "build-1", (Plate, 101 * U))
    );
    // Leftovers go back even past the limit, and then only taking out works
    Applied(book, "leftovers back", Res(LedgerAction.Release, p1, "build-1"));
    Check(
        Free(book, p1, Plate) == 100 * U && !book.ReservationsOf(A, p1).Any(),
        "leftovers back: all of it"
    );
    Check(book.MassKg(A, p1) > 100_000, "leftovers back: over the limit");
    Refused(book, "deposit over the limit", Op(LedgerAction.Deposit, p1, (Iron, U)));
    Refused(
        book,
        "a batch that still raises the mass",
        Op(LedgerAction.Withdraw, p1, (Iron, U)),
        Op(LedgerAction.Deposit, p1, (Plate, 1 * U))
    );
    Applied(
        book,
        "a batch that lowers the mass over the limit",
        Op(LedgerAction.Withdraw, p1, (Iron, 100 * U)),
        Op(LedgerAction.Deposit, p1, (Plate, 2 * U))
    );
    Check(Math.Abs(book.MassKg(A, p1) - 101_940) < 1e-6, "lowered: 101.94 t");
    Applied(book, "withdraw over the limit", Op(LedgerAction.Withdraw, p1, (Iron, 3000 * U)));
    Applied(book, "a counted fill", Fill(p1, "listing-x", true, (Iron, U)));
    Refused(book, "a counted fill past the limit", Fill(p1, "listing-x", true, (Iron, 2000 * U)));
    Applied(book, "take it all", Res(LedgerAction.Take, p1, "listing-x"));
}

LedgerOp Fill(LedgerOwner owner, string id, bool counted, params (string, long)[] items)
{
    var op = Res(LedgerAction.Fill, owner, id, items);
    op.Counted = counted;
    return op;
}

// Batches are all or none
{
    var book = New();
    Applied(book, "stock", Op(LedgerAction.Deposit, p1, (Iron, 10 * U)));
    var other = Op(LedgerAction.Deposit, p2, (Iron, 5 * U));
    other.Station = B;
    Refused(
        book,
        "batch with a bad last op",
        other,
        Op(LedgerAction.Withdraw, p1, (Iron, 10 * U)),
        Op(LedgerAction.Withdraw, p1, (Iron, U))
    );
    Check(book.Get(B) == null, "batch: the new station is gone again");
    Check(Free(book, p1, Iron) == 10 * U, "batch: the first withdraw is undone");
    Applied(
        book,
        "a trade in one batch",
        TransferOp(p1, f10, (Iron, 4 * U)),
        Res(LedgerAction.Reserve, f10, "listing-f", (Iron, 4 * U))
    );
    Check(Reserved(book, "listing-f", Iron) == 4 * U, "trade: the faction lists it");
}

// Persistence and the wire format
{
    var book = New();
    Applied(book, "stock", Op(LedgerAction.Deposit, p1, (Iron, 1234567), (Plate, 3 * U)));
    Applied(book, "faction stock", Op(LedgerAction.Deposit, f10, (Plate, 7 * U)));
    var listing = Res(LedgerAction.Reserve, p1, "listing 1", (Plate, 2 * U));
    listing.Counted = true;
    Applied(book, "listing", listing);
    Applied(book, "build", Res(LedgerAction.Fill, f10, "build-9", (Iron, 5 * U)));
    var beta = Op(LedgerAction.Deposit, p2, (Iron, U));
    beta.Station = B;
    Applied(book, "beta", beta);

    string saved = book.Serialize();
    var loaded = LedgerBook.Deserialize(saved, rules);
    Check(loaded.Serialize() == saved, "round trip");
    Check(Free(loaded, p1, Iron) == 1234567, "round trip: fractional iron");
    Check(loaded.ReservationsOf(A, p1).Single().Value.Counted, "round trip: counted");
    Check(!loaded.ReservationsOf(A, f10).Single().Value.Counted, "round trip: not counted");
    Check(Math.Abs(loaded.MassKg(A, p1) - book.MassKg(A, p1)) < 1e-9, "round trip: mass");

    var mine = book.Extract(A, p1);
    Check(mine.OwnersAt(A).SequenceEqual([p1]), "extract: one owner");
    Check(mine.Get(B) == null, "extract: one station");
    Check(
        LedgerBook.Deserialize(mine.Serialize(), rules).Serialize() == mine.Serialize(),
        "extract: round trip"
    );
    Check(book.Extract(A, null).OwnersAt(A).Count() == 2, "extract: everyone");
    Check(book.Extract("Nowhere", null).Stations.Count == 0, "extract: unknown station");

    try
    {
        LedgerBook.Deserialize("something else\n", rules);
        Check(false, "a foreign text is refused");
    }
    catch (FormatException) { }

    var ops = new[]
    {
        GiveOp(p1, "listing 1", p2, (Plate, U)),
        Op(LedgerAction.Deposit, f10, (Iron, 42)),
    };
    string wire = LedgerOp.EncodeBatch(ops);
    var decoded = LedgerOp.DecodeBatch(wire);
    Check(LedgerOp.EncodeBatch(decoded) == wire, "wire round trip");
    Check(
        decoded[0].Other.Equals(p2)
            && decoded[0].Reservation == "listing 1"
            && decoded[1].Owner.Equals(f10),
        "wire: fields"
    );
    Applied(book, "decoded ops apply", decoded.ToArray());
    Check(Free(book, p2, Plate) == U, "decoded ops: sale applied");

    Check(
        LedgerOwner.TryParse("F-5", out var negative) && negative.Faction && negative.Id == -5,
        "owner parse"
    );
    Check(
        !LedgerOwner.TryParse("X5", out _) && !LedgerOwner.TryParse("P", out _),
        "owner parse refuses"
    );
}

// Whether a faction's members all trade: set, saved, copied and undone with a failed batch
{
    var book = New();
    LedgerOp Members(LedgerOwner faction, bool on) =>
        new()
        {
            Action = LedgerAction.MembersTrade,
            Owner = faction,
            Counted = on,
        };
    Check(!book.MembersTrade(10), "members trade: off by default");
    Applied(book, "members trade on", Members(f10, true));
    Check(book.MembersTrade(10), "members trade: on");
    Refused(book, "members trade of a player", Members(p1, true));
    Refused(book, "members trade of an unknown faction", Members(LedgerOwner.OfFaction(11), true));
    Refused(
        book,
        "members trade off in a failed batch",
        Members(f10, false),
        Op(LedgerAction.Withdraw, p1, (Iron, U))
    );
    Check(book.MembersTrade(10), "members trade: a failed batch leaves it on");
    Applied(book, "a deposit", Op(LedgerAction.Deposit, p1, (Iron, U)));
    var saved = LedgerBook.Deserialize(book.Serialize(), rules);
    Check(saved.MembersTrade(10) && saved.Serialize() == book.Serialize(), "members trade: saved");
    Check(book.Extract(A, p1).MembersTrade(10), "members trade: in an owner's copy");
    var wire = LedgerOp.DecodeBatch(LedgerOp.EncodeBatch([Members(f10, false)]));
    Applied(book, "members trade off over the wire", wire.ToArray());
    Check(!book.MembersTrade(10), "members trade: off");
    Check(!book.Serialize().Contains("\nT\t"), "members trade: off is not saved");
}

// Kiosk requests
{
    var r = KioskRequest.Parse("deposit\t7\t123\t1\thand\tIngot/Iron\t1.5\n");
    Check(
        r.Verb == KioskVerb.Deposit
            && r.Sequence == 7
            && r.Kiosk == 123
            && r.Faction
            && r.FromHand
            && r.Item == Iron
            && r.Amount == 3 * U / 2,
        "kiosk request: fields"
    );
    var state = KioskRequest.Parse("State\t1\t5");
    Check(
        state.Verb == KioskVerb.State && !state.Faction && state.Source == "" && state.Amount == 0,
        "kiosk request: short"
    );
    Check(
        KioskRequest.Parse("withdraw\t1\t5\t0\t99\tIngot/Iron\tall").Amount == 0,
        "kiosk request: all"
    );
    foreach (
        var bad in new[]
        {
            "",
            "state\t1",
            "steal\t1\t5",
            "1\t1\t5",
            "state\tx\t5",
            "deposit\t1\t5\t0\t\tIngot/Iron\t-1",
            "deposit\t1\t5\t0\t\tIngot/Iron\t0",
            "deposit\t1\t5\t0\t\tIngot/Iron\tlots",
        }
    )
    {
        try
        {
            KioskRequest.Parse(bad);
            Check(false, "kiosk request refused: " + bad.Replace("\t", "|"));
        }
        catch (FormatException) { }
    }
}

// Kiosk replies
{
    var reply = new KioskReply
    {
        Sequence = 3,
        Ok = true,
        Message = "Stored\t2\nplates",
        Kiosk = 9,
        Station = A,
        Reach = 4.5,
        FactionTag = "OCT",
        MayTrade = true,
        Ships = { new(77, "Hauler") },
        Source = "77",
        Items =
        {
            new KioskItem
            {
                Item = Plate,
                Name = "Steel Plate",
                Mine = 2 * U,
                Source = 5 * U,
            },
            new KioskItem
            {
                Item = Iron,
                Name = "Iron Ingot",
                Faction = U / 2,
            },
        },
    };
    var lines = reply.Encode().Split('\n');
    Check(
        lines[0] == "reply\t3\t1\tStored 2 plates",
        "kiosk reply: outcome, separators cleaned: " + lines[0]
    );
    Check(lines[1] == "kiosk\t9\tAlpha Station\t4.5", "kiosk reply: kiosk " + lines[1]);
    Check(lines[2] == "faction\tOCT\t1\t0\t0", "kiosk reply: faction " + lines[2]);
    Check(
        lines[3] == "ship\t77\tHauler" && lines[4] == "source\t77",
        "kiosk reply: ship and source"
    );
    Check(
        lines[5] == "item\tIngot/Iron\tIron Ingot\t0\t500000\t0",
        "kiosk reply: items by name " + lines[5]
    );
    Check(
        lines[6] == $"item\t{Plate}\tSteel Plate\t{2 * U}\t0\t{5 * U}",
        "kiosk reply: item " + lines[6]
    );
    Check(
        !new KioskReply().Encode().Contains("faction"),
        "kiosk reply: no faction line without one"
    );
}

// Kiosk rules
{
    KioskRequest Req(KioskVerb verb, bool faction = false, string item = Iron) =>
        new()
        {
            Verb = verb,
            Faction = faction,
            Item = item,
        };
    Check(
        KioskRules.MayTrade(true, false)
            && KioskRules.MayTrade(false, true)
            && !KioskRules.MayTrade(false, false),
        "kiosk: who trades"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.State), false, false, false) == null,
        "kiosk: anyone sees the state"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Deposit), false, false, false) == null,
        "kiosk: anyone deposits for themselves"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Withdraw), false, false, false) == null,
        "kiosk: anyone withdraws for themselves"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Deposit, item: ""), false, false, false) != null,
        "kiosk: an item is needed"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Deposit, true), false, false, false) != null,
        "kiosk: faction switch without a faction"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Deposit, true), true, false, false) != null,
        "kiosk: faction switch needs the rank"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Withdraw, true), true, true, false) == null,
        "kiosk: faction switch with the rank"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Give), true, false, false) == null,
        "kiosk: any member gives"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Give), false, false, false) != null,
        "kiosk: giving needs a faction"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Take), true, false, false) != null,
        "kiosk: taking needs the rank"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Take), true, true, false) == null,
        "kiosk: taking with the rank"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Members, item: ""), true, true, false) != null,
        "kiosk: members setting needs a leader"
    );
    Check(
        KioskRules.Refusal(Req(KioskVerb.Members, item: ""), true, true, true) == null,
        "kiosk: a leader sets it"
    );
}

Console.WriteLine($"{checks - failures}/{checks} ledger checks passed");
return failures == 0 ? 0 : 1;

sealed class FakeRules : ILedgerRules
{
    private static readonly Dictionary<string, (double Kg, bool Integral)> Items = new()
    {
        ["Ingot/Iron"] = (1, false),
        ["Component/SteelPlate"] = (20, true),
    };

    public bool Item(string item, out double massKg, out bool integral)
    {
        bool known = Items.TryGetValue(item, out var info);
        massKg = info.Kg;
        integral = info.Integral;
        return known;
    }

    public double LimitKg(LedgerOwner owner) =>
        owner.Faction ? 1_000_000
        : owner.Id == 2 ? 0
        : 100_000;

    public bool KnownStation(string station) => station is "Alpha Station" or "Beta Station";

    public bool KnownOwner(LedgerOwner owner) =>
        owner.Faction ? owner.Id == 10 : owner.Id is 1 or 2 or 3;
}
