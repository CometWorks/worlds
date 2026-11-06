using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ServerPlugin;

/// <summary>Text laid out in the Monospace font for an LCD surface.</summary>
public static class PanelText
{
    public static string Wrap(string text, int columns)
    {
        var lines = new List<string>();
        foreach (var line in text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r', ' ')))
        {
            var rest = line;
            while (rest.Length > columns)
            {
                var cut = rest.LastIndexOf(' ', columns);
                if (cut <= 0)
                    cut = columns;
                lines.Add(rest.Substring(0, cut).TrimEnd());
                rest = rest.Substring(cut).TrimStart();
            }
            lines.Add(rest);
        }
        return string.Join("\n", lines);
    }

    // Wrapped, and cut after maxLines with "..." at the end
    public static string Clip(string text, int columns, int maxLines)
    {
        var lines = Wrap(text, columns).Split('\n');
        if (lines.Length <= maxLines)
            return string.Join("\n", lines);
        var last = lines[maxLines - 1];
        if (last.Length > columns - 3)
            last = last.Substring(0, columns - 3).TrimEnd();
        lines[maxLines - 1] = last + "...";
        return string.Join("\n", lines.Take(maxLines));
    }

    // The text with the name in it cut short, like "Long na...", as far as it takes to fit in
    // maxLines, so that what follows the name, like a deadline, is not cut
    public static string ClipName(Func<string, string> text, string name, int columns, int maxLines)
    {
        for (var n = name.Length; n > 0; n--)
        {
            var lines = Wrap(
                text(n == name.Length ? name : name.Substring(0, n).TrimEnd() + "..."),
                columns
            );
            if (lines.Split('\n').Length <= maxLines)
                return lines;
        }
        return Clip(text("..."), columns, maxLines);
    }

    // A deadline as the minutes left and its UTC time, like "59 min (by 03:38 UTC)". A bare UTC
    // time reads like a local one, so a player one hour ahead of UTC took it for the start (SE-962)
    public static string Left(DateTime ends, DateTime now) =>
        $"{Math.Max(0, (int)Math.Ceiling((ends - now).TotalMinutes))} min "
        + $"(by {ends.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC)";

    // The minutes left, rounded up, like "46 mins" or "1 min"
    public static string Mins(DateTime ends, DateTime now)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling((ends - now).TotalMinutes));
        return minutes == 1 ? "1 min" : $"{minutes} mins";
    }

    // A shipyard queue entry: "1. 53% (46 mins) Viktor 'Ship' (44,181 PCU)", the state empty
    // after the first entry
    public static string QueueEntry(
        int place,
        string state,
        string player,
        string ship,
        int columns,
        int maxLines
    ) =>
        Item(
            $"{place}. ",
            (state.Length > 0 ? state + " " : "") + $"{player} {ship}",
            columns,
            maxLines
        );

    // A list item: the prefix, like "1. ", then the text clipped and indented under its first line
    public static string Item(string prefix, string text, int columns, int maxLines) =>
        prefix
        + Clip(text, columns - prefix.Length, maxLines)
            .Replace("\n", "\n" + new string(' ', prefix.Length));

    // The largest font up to maxSize that fits the text. At size 1 on a 512 pixel texture a Monospace
    // character takes 24 x 37 pixels in its font file, times the game's text scale of 0.778.
    public static float FontSize(
        string text,
        float width,
        float height,
        float textureSize,
        float maxSize
    )
    {
        var lines = text.TrimEnd().Split('\n');
        var scale = textureSize / 512f;
        var size = Math.Min(
            width * 0.96f / (Math.Max(1, lines.Max(l => l.Length)) * 18.7f * scale),
            height * 0.96f / (lines.Length * 28.8f * scale)
        );
        return Math.Max(0.2f, Math.Min(maxSize, size));
    }
}
