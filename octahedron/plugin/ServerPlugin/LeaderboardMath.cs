using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>A faction's grids on one server: their value in SC and their PCU.</summary>
public struct FactionGrids
{
    public long Value;
    public long Pcu;
}

/// <summary>A faction on the leaderboard.</summary>
public sealed class LeaderboardRow
{
    public string Tag = "";
    public string Name = "";

    // SC: the bank account with the money held for its market orders, and its grids
    public long Cash;
    public long Grids;
    public long Pcu;

    public long Total => Cash + Grids;
}

/// <summary>
/// The leaderboard's arithmetic, text and wire formats, without the game, so tests/leaderboard
/// checks them alone. Prices are milli-SC per unit and amounts raw ledger units, as in the market.
/// </summary>
public static class LeaderboardMath
{
    public const int Width = 45;

    /// <summary>milli-SC of a raw amount at a price.</summary>
    public static decimal Value(long raw, long price) => (decimal)raw / LedgerBook.Unit * price;

    /// <summary>milli-SC as whole SC, rounded down, at most long.MaxValue.</summary>
    public static long Sc(decimal milli) =>
        milli >= (decimal)long.MaxValue * 1000 ? long.MaxValue : (long)(milli / 1000);

    /// <summary>The middle of OCT's bid and ask, averaged over the stations that trade the item; 0: none does.</summary>
    public static long MidPrice(IEnumerable<(long Bid, long Ask)> stations)
    {
        var mids = stations
            .Where(s => s.Bid > 0 && s.Ask > 0)
            .Select(s => (s.Bid + s.Ask) / 2m)
            .ToList();
        return mids.Count == 0 ? 0 : (long)Math.Round(mids.Average());
    }

    /// <summary>The grids of every faction, summed over the servers' reports.</summary>
    public static Dictionary<long, FactionGrids> Sum(
        IEnumerable<IReadOnlyDictionary<long, FactionGrids>> reports
    )
    {
        var sum = new Dictionary<long, FactionGrids>();
        foreach (var report in reports)
        foreach (var (faction, grids) in report)
        {
            sum.TryGetValue(faction, out var had);
            sum[faction] = new FactionGrids
            {
                Value = checked(had.Value + grids.Value),
                Pcu = checked(had.Pcu + grids.Pcu),
            };
        }
        return sum;
    }

    /// <summary>Richest first; the same total by PCU, then by tag.</summary>
    public static List<LeaderboardRow> Rank(IEnumerable<LeaderboardRow> rows) =>
        rows.OrderByDescending(r => r.Total)
            .ThenByDescending(r => r.Pcu)
            .ThenBy(r => r.Tag, StringComparer.Ordinal)
            .ToList();

    /// <summary>The board's text from ranked rows, for a Monospace panel <see cref="Width"/> characters wide.</summary>
    public static string Format(IReadOnlyList<LeaderboardRow> ranked, int rows, DateTime updated)
    {
        var text = new StringBuilder();
        text.Append(Center("FACTION LEADERBOARD")).Append("\n\n");
        text.Append(" #  ")
            .Append("Tag".PadRight(5))
            .Append(' ')
            .Append("Faction".PadRight(13))
            .Append(' ')
            .Append("Value SC".PadLeft(13))
            .Append(' ')
            .Append("PCU".PadLeft(7))
            .Append('\n');
        if (ranked.Count == 0)
            text.Append(Center("No factions yet")).Append('\n');
        for (int i = 0; i < Math.Min(rows, ranked.Count); i++)
        {
            var r = ranked[i];
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2))
                .Append("  ")
                .Append(Cut(r.Tag, 5).PadRight(5))
                .Append(' ')
                .Append(Cut(r.Name, 13).PadRight(13))
                .Append(' ')
                .Append(N(r.Total).PadLeft(13))
                .Append(' ')
                .Append(N(r.Pcu).PadLeft(7))
                .Append('\n');
        }
        text.Append("\nValue: bank account and grids at station\n");
        text.Append("prices. Updated ")
            .Append(updated.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC.\n");
        return text.ToString();
    }

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Center(string text) => text.PadLeft((Width + text.Length) / 2);

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text.Substring(0, length);

    // --- wire formats: a record per line, tab-separated ---

    private static string Clean(string text) =>
        (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static long L(string text) => long.Parse(text, CultureInfo.InvariantCulture);

    private static string S(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string[]> Records(string text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t'));

    /// <summary>item, price</summary>
    public static string EncodePrices(IReadOnlyDictionary<string, long> prices) =>
        string.Concat(prices.Select(p => $"{Clean(p.Key)}\t{S(p.Value)}\n"));

    public static Dictionary<string, long> DecodePrices(string text) =>
        Records(text).ToDictionary(f => f[0], f => L(f[1]));

    /// <summary>faction id, value, PCU</summary>
    public static string EncodeGrids(IReadOnlyDictionary<long, FactionGrids> grids) =>
        string.Concat(grids.Select(g => $"{S(g.Key)}\t{S(g.Value.Value)}\t{S(g.Value.Pcu)}\n"));

    public static Dictionary<long, FactionGrids> DecodeGrids(string text) =>
        Records(text)
            .ToDictionary(f => L(f[0]), f => new FactionGrids { Value = L(f[1]), Pcu = L(f[2]) });

    /// <summary>tag, name, cash, grids, PCU</summary>
    public static string EncodeRows(IEnumerable<LeaderboardRow> rows) =>
        string.Concat(
            rows.Select(r =>
                $"{Clean(r.Tag)}\t{Clean(r.Name)}\t{S(r.Cash)}\t{S(r.Grids)}\t{S(r.Pcu)}\n"
            )
        );

    public static List<LeaderboardRow> DecodeRows(string text) =>
        Records(text)
            .Select(f => new LeaderboardRow
            {
                Tag = f[0],
                Name = f[1],
                Cash = L(f[2]),
                Grids = L(f[3]),
                Pcu = L(f[4]),
            })
            .ToList();
}
