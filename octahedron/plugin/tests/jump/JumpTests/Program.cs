using System;
using ServerPlugin;

// Checks where a jump stops at the world limit (ServerPlugin/JumpLimitMath.cs). Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/jump/JumpTests

int checks = 0,
    failures = 0;

const double Limit = 450_000;

// The end of a jump from p by d, in km from the center
double EndKm((double X, double Y, double Z) p, (double X, double Y, double Z) d)
{
    double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    var t = JumpLimitMath.Fraction(Dot(p, p), Dot(p, d), Dot(d, d), Limit);
    var e = (X: p.X + t * d.X, Y: p.Y + t * d.Y, Z: p.Z + t * d.Z);
    return Math.Sqrt(Dot(e, e)) / 1000;
}

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

// Inside stays inside: unchanged
Check(
    JumpLimitMath.Fraction(1e10, 0, 1e10, Limit) == 1,
    "a jump within the limit is taken in full"
);
Check(Near(EndKm((400_000, 0, 0), (40_000, 0, 0)), 440), "400 -> 440 km unchanged");

// Straight out from 400 km to 600 km stops at 450 km, a quarter of the way
Check(
    Near(
        JumpLimitMath.Fraction(
            400_000.0 * 400_000,
            400_000.0 * 200_000,
            200_000.0 * 200_000,
            Limit
        ),
        0.25
    ),
    "400 -> 600 km stops at a quarter"
);
Check(Near(EndKm((400_000, 0, 0), (200_000, 0, 0)), 450), "400 -> 600 km lands at 450 km");

// Slanted: from 300 km on x, 1000 km along y, lands on the sphere along that line
Check(Near(EndKm((300_000, 0, 0), (0, 1_000_000, 0)), 450), "sideways jump lands on the sphere");
var tSide = JumpLimitMath.Fraction(300_000.0 * 300_000, 0, 1e12, Limit);
Check(
    Near(tSide * 1_000_000, Math.Sqrt(450_000.0 * 450_000 - 300_000.0 * 300_000)),
    "sideways: y on the sphere"
);

// Through the planet and out the far side: stops at the far crossing, not the near one
Check(
    Near(EndKm((300_000, 0, 0), (-900_000, 0, 0)), 450),
    "through the center to -600 km lands at -450 km"
);
Check(
    Near(
        JumpLimitMath.Fraction(
            300_000.0 * 300_000,
            -300_000.0 * 900_000,
            900_000.0 * 900_000,
            Limit
        ),
        750.0 / 900
    ),
    "through the center: 750 of 900 km"
);

// Starting beyond the limit: never farther out than the start
Check(
    JumpLimitMath.Fraction(480_000.0 * 480_000, 480_000.0 * 50_000, 50_000.0 * 50_000, Limit) == 0,
    "480 km straight out goes nowhere"
);
Check(
    JumpLimitMath.Fraction(480_000.0 * 480_000, -480_000.0 * 100_000, 100_000.0 * 100_000, Limit)
        == 1,
    "480 km inward is free"
);
Check(Near(EndKm((480_000, 0, 0), (0, 0, 2_000_000)), 480), "480 km sideways stays at 480 km");
Check(
    EndKm((480_000, 0, 0), (-10_000, 0, 300_000)) <= 480 + 1e-6,
    "480 km slanted out stays within 480 km"
);

// No jump at all
Check(JumpLimitMath.Fraction(1e10, 0, 0, Limit) == 1, "zero length jump");

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
