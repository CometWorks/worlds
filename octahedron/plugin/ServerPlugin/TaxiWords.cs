using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerPlugin;

/// <summary>
/// What a player's private message to a taxi driver asks for (SE-1279): "call", or a station's name with or without
/// the word "station". Case and punctuation don't count, and a typo or two is forgiven: one in a word of up to four
/// letters, two in a longer one. Two answers equally close are ambiguous. No game types, so tests run it offline.
/// </summary>
public static class TaxiWords
{
    public enum Kind
    {
        None,
        Call,
        Station,
        Ambiguous,
    }

    public const string CallWord = "call";

    /// <summary>The answer: Station with the station's own name, Ambiguous with the close names.</summary>
    public static (Kind Kind, string Station, IReadOnlyList<string> Close) Match(
        string text,
        IEnumerable<string> stations
    )
    {
        var said = Key(text);
        if (said.Length == 0)
            return (Kind.None, null, []);
        var options = new List<(string Key, Kind Kind, string Station)>
        {
            (CallWord, Kind.Call, null),
        };
        options.AddRange(
            stations.Select(s => (Key(s), Kind.Station, s)).Where(o => o.Item1.Length > 0)
        );

        var exact = options.Where(o => o.Key == said).ToList();
        if (exact.Count == 0 && said.Length >= 3)
            exact = options.Where(o => o.Key.StartsWith(said, StringComparison.Ordinal)).ToList();
        if (exact.Count == 0)
        {
            var scored = options
                .Select(o => (o, d: Distance(said, o.Key)))
                .Where(x => x.d <= Allowed(x.o.Key))
                .ToList();
            if (scored.Count > 0)
            {
                var best = scored.Min(x => x.d);
                exact = scored.Where(x => x.d == best).Select(x => x.o).ToList();
            }
        }
        return exact.Count switch
        {
            0 => (Kind.None, null, []),
            1 => (exact[0].Kind, exact[0].Station, []),
            _ => (Kind.Ambiguous, null, exact.Select(o => o.Station ?? CallWord).ToList()),
        };
    }

    // Lower case letters and digits in words, without the word "station" (or a close typo of it) when more is said
    internal static string Key(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text ?? "")
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        var words = plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1)
            words.RemoveAll(w => Distance(w, "station") <= 2);
        return string.Join(" ", words);
    }

    private static int Allowed(string key) => key.Length <= 4 ? 1 : 2;

    // Optimal string alignment distance: insertions, deletions, substitutions and swaps of neighbors cost 1
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }
}
