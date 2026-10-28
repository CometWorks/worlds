using System;

namespace ServerPlugin;

// Where a jump from p to p + d stops at the world limit, without the game, so tests/jump can
// check it alone. Positions are relative to the world center and given by their dot products.
internal static class JumpLimitMath
{
    // The fraction of the jump to take, 0..1. A jump ending inside the limit radius is taken in
    // full. Otherwise it stops where its line leaves the sphere of the limit radius, or of the
    // start's own distance when the start is already beyond the limit, so a jump can't take a
    // ship farther out than it is. A jump straight outward from there stops at 0.
    public static double Fraction(double pp, double pd, double dd, double limitRadius)
    {
        var rr = Math.Max(limitRadius * limitRadius, pp);
        if (dd <= 0 || pp + 2 * pd + dd <= rr)
            return 1;
        // |p + t d|² = rr; pp <= rr, so the larger root is >= 0
        var t = (-pd + Math.Sqrt(Math.Max(0, pd * pd - dd * (pp - rr)))) / dd;
        return Math.Clamp(t, 0, 1);
    }
}
