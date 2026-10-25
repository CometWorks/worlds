using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>What a player asks the kiosk for.</summary>
public enum KioskVerb
{
    /// <summary>Only the kiosk's state.</summary>
    State,

    /// <summary>Items from the source (ship or character) into the ledger the kiosk works on.</summary>
    Deposit,

    /// <summary>Items from the ledger the kiosk works on into the source.</summary>
    Withdraw,

    /// <summary>Items from the operator's ledger to their faction's.</summary>
    Give,

    /// <summary>Items from the faction's ledger to the operator's; needs the trading rank.</summary>
    Take,

    /// <summary>A leader or the founder lets every member trade (amount 1) or only the leaders (0).</summary>
    Members,
}

/// <summary>
/// One request of the kiosk client mod (octahedron/kiosk-mod), a line of tab-separated fields:
/// verb, sequence number, kiosk block id, faction switch (1/0), source ("hand", a ship's grid
/// id, or empty for the first docked ship), item ("Ingot/Iron"), amount in units ("1.5", "all").
/// The fields after the kiosk id may be left out. The mod keeps its own copy of this format.
/// </summary>
public sealed class KioskRequest
{
    public const string Hand = "hand";

    public KioskVerb Verb;
    public long Sequence;
    public long Kiosk;
    public bool Faction;
    public string Source = "";
    public string Item = "";

    // Raw amount, millionths of a unit; 0: all there is
    public long Amount;

    public bool FromHand => Source == Hand;

    public static KioskRequest Parse(string text)
    {
        var f = text.TrimEnd('\n', '\r').Split('\t');
        if (f.Length < 3)
            throw new FormatException("Malformed kiosk request");
        string Field(int i) => i < f.Length ? f[i] : "";
        if (
            !Enum.TryParse(f[0], true, out KioskVerb verb)
            || !Enum.IsDefined(typeof(KioskVerb), verb)
            || f[0].All(char.IsDigit)
        )
            throw new FormatException("Unknown kiosk request " + f[0]);
        var request = new KioskRequest
        {
            Verb = verb,
            Sequence = long.Parse(f[1], CultureInfo.InvariantCulture),
            Kiosk = long.Parse(f[2], CultureInfo.InvariantCulture),
            Faction = Field(3) == "1",
            Source = Field(4),
            Item = Field(5),
        };
        string amount = Field(6);
        if (amount.Length > 0 && !amount.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (
                !decimal.TryParse(
                    amount,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal units
                )
                || units <= 0
            )
                throw new FormatException("Not an amount: " + amount);
            request.Amount = checked((long)(units * LedgerBook.Unit));
        }
        return request;
    }
}

/// <summary>One item row of the kiosk: raw amounts in the operator's and the faction's ledger and in the source.</summary>
public struct KioskItem
{
    public string Item;
    public string Name;
    public long Mine;
    public long Faction;
    public long Source;
}

/// <summary>
/// The kiosk's answer to every request: its outcome, then the state the screens show, one
/// record per line, tab-separated:
///   reply     sequence, ok (1/0), message
///   kiosk     kiosk id, station, reach in meters
///   faction   tag, may trade with its ledger (1/0), members trade (1/0), may change that (1/0); only in a faction
///   ship      grid id, name; every docked ship the operator has inventory access to
///   source    "hand", the selected ship's grid id, or empty when there is none
///   item      item, display name, raw amounts: mine, the faction's, in the source
/// </summary>
public sealed class KioskReply
{
    public long Sequence;
    public bool Ok;
    public string Message = "";
    public long Kiosk;
    public string Station = "";
    public double Reach;
    public string FactionTag;
    public bool MayTrade;
    public bool MembersTrade;
    public bool MaySetMembers;
    public List<KeyValuePair<long, string>> Ships = new();
    public string Source = "";
    public List<KioskItem> Items = new();

    public string Encode()
    {
        var text = new StringBuilder();
        void Line(params object[] fields) =>
            text.Append(string.Join("\t", fields.Select(Field))).Append('\n');
        Line("reply", Sequence, Ok, Message);
        Line("kiosk", Kiosk, Station, Reach);
        if (FactionTag != null)
            Line("faction", FactionTag, MayTrade, MembersTrade, MaySetMembers);
        foreach (var ship in Ships)
            Line("ship", ship.Key, ship.Value);
        Line("source", Source);
        foreach (var item in Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            Line("item", item.Item, item.Name, item.Mine, item.Faction, item.Source);
        return text.ToString();
    }

    // Names come from players: keep the separators out
    private static string Field(object value) =>
        (
            value switch
            {
                bool b => b ? "1" : "0",
                IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? "",
            }
        )
            .Replace('\t', ' ')
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}

/// <summary>Who may do what at a kiosk, without game types.</summary>
public static class KioskRules
{
    /// <summary>Trading with the faction's ledger: its leaders and founder, everyone when the faction lets its members.</summary>
    public static bool MayTrade(bool leaderOrFounder, bool membersTrade) =>
        leaderOrFounder || membersTrade;

    /// <summary>Why the operator may not make this request, or null when they may.</summary>
    public static string Refusal(
        KioskRequest request,
        bool inFaction,
        bool mayTrade,
        bool maySetMembers
    )
    {
        bool moves = request.Verb is KioskVerb.Deposit or KioskVerb.Withdraw;
        bool factionLedger =
            (request.Faction && moves)
            || request.Verb is KioskVerb.Give or KioskVerb.Take or KioskVerb.Members;
        if (factionLedger && !inFaction)
            return "You are not in a faction.";
        if (request.Verb == KioskVerb.Members && !maySetMembers)
            return "Only the faction's leaders and founder decide who trades.";
        if ((request.Verb == KioskVerb.Take || (request.Faction && moves)) && !mayTrade)
            return "Your faction rank may not trade with the faction's ledger.";
        if (
            request.Verb is not (KioskVerb.State or KioskVerb.Members)
            && !LedgerBook.ValidName(request.Item)
        )
            return "No item chosen.";
        return null;
    }
}
