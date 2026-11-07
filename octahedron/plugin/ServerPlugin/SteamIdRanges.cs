using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerPlugin;

/// <summary>The Steam IDs a config option names, comma separated; first-last is a range. Parsed again when the option changes.</summary>
internal sealed class SteamIdRanges(string option, Func<string> text)
{
    private string parsed;
    private List<(ulong First, ulong Last)> ranges = [];

    public bool Contains(ulong steamId) =>
        steamId != 0 && Ranges.Any(r => steamId >= r.First && steamId <= r.Last);

    private List<(ulong First, ulong Last)> Ranges
    {
        get
        {
            var current = text() ?? "";
            if (current == parsed)
                return ranges;
            ranges = [];
            foreach (var part in current.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var ends = part.Split('-');
                if (
                    ends.Length <= 2
                    && ulong.TryParse(ends[0].Trim(), out var first)
                    && ulong.TryParse(ends[ends.Length - 1].Trim(), out var last)
                )
                    ranges.Add((first, last));
                else
                    Plugin.Log.Error($"{option}: '{part}' is no Steam ID or range");
            }
            parsed = current;
            return ranges;
        }
    }
}
