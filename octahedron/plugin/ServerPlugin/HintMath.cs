using System;

namespace ServerPlugin;

// When Good.bot's next hint is due and which one it is (SE-1538). Hints.cs sends it; this
// decides, with no game types, so tests/hints can run it.
internal static class HintMath
{
    // Minutes until the next hint: between min and max, whichever order they come in. roll is 0 to 1
    public static double Wait(double min, double max, double roll)
    {
        var low = Math.Max(0, Math.Min(min, max));
        var high = Math.Max(0, Math.Max(min, max));
        return low + (high - low) * roll;
    }

    // The next hint's index: any but the last one sent. next(n) gives 0 to n - 1
    public static int Pick(int count, int last, Func<int, int> next)
    {
        if (count <= 1 || last < 0 || last >= count)
            return next(count);
        var i = next(count - 1);
        return i < last ? i : i + 1;
    }
}
