using System.Globalization;

namespace ServerPlugin;

/// <summary>Amounts of items as the market keeps them: raw, in millionths of a unit.</summary>
public static class Amounts
{
    public const long Unit = 1_000_000;

    public static string Units(long raw) =>
        ((decimal)raw / Unit).ToString("#,0.######", CultureInfo.InvariantCulture);

    // Station names and items go into tab-separated lines
    public static bool ValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['\t', '\n', '\r']) < 0;
}
