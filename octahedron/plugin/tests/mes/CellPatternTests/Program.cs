using System;
using System.Linq;
using ServerPlugin;

// Property checks of the spawn cell pattern. Exit code 0 = all pass.
// Usage: dotnet run [-- samples]

int samples = args.Length > 0 ? int.Parse(args[0]) : 20000;
int failures = 0;
var rng = new Random(1321);

void Check(bool ok, string what)
{
    if (ok)
        return;
    failures++;
    if (failures <= 20)
        Console.WriteLine("FAIL " + what);
}

// Octahedron defaults (40 km cells, 12 km gap) plus edge cases: a wide gap and full jitter, no jitter
var patterns = new[]
{
    new CellPattern(40000, 12000, 0.5, 1321),
    new CellPattern(40000, 30000, 1.0, 7),
    new CellPattern(10000, 0, 0.0, 0),
    new CellPattern(25000, 9000, 1.0, -5),
};

foreach (var pattern in patterns)
{
    string name =
        $"cell={pattern.CellSize} gap={pattern.Gap} jitter={pattern.Jitter} seed={pattern.Seed}";
    double span = 3e6; // 3000 km around the origin, both signs
    int inCore = 0;

    for (int n = 0; n < samples; n++)
    {
        double x = (rng.NextDouble() * 2 - 1) * span;
        double y = (rng.NextDouble() * 2 - 1) * span;
        double z = (rng.NextDouble() * 2 - 1) * span;
        var core = pattern.CoreOf(x, y, z);

        // 1. Same answer as a brute force over a much wider neighbourhood
        Check(
            Equals(core, BruteCore(pattern, x, y, z)),
            $"{name}: CoreOf differs from brute force at ({x},{y},{z})"
        );

        // 2. Deterministic
        Check(Equals(core, pattern.CoreOf(x, y, z)), $"{name}: not deterministic at ({x},{y},{z})");

        if (core == null)
            continue;
        inCore++;

        // 3. Gap: any point closer than the gap is in the same core or in no core
        var dir = RandomUnit(rng);
        double r = rng.NextDouble() * pattern.Gap * 0.999;
        var other = pattern.CoreOf(x + dir.X * r, y + dir.Y * r, z + dir.Z * r);
        Check(
            other == null || other.Equals(core),
            $"{name}: cores {core} and {other} only {r:F0} m apart"
        );

        // 4. Convex: the midpoint of two points of one core is in that core
        double far = rng.NextDouble() * pattern.CellSize;
        var dir2 = RandomUnit(rng);
        double qx = x + dir2.X * far,
            qy = y + dir2.Y * far,
            qz = z + dir2.Z * far;
        if (core.Equals(pattern.CoreOf(qx, qy, qz)))
            Check(
                core.Equals(pattern.CoreOf((x + qx) / 2, (y + qy) / 2, (z + qz) / 2)),
                $"{name}: core {core} not convex"
            );
    }

    Console.WriteLine($"{name}: core fraction {(double)inCore / samples:P1}");
    if (pattern.Gap > 0)
        Check(inCore > 0, $"{name}: no point in any core");
    else
        Check(inCore == samples, $"{name}: zero gap must leave no gap band");
}

// Config: every pattern parameter comes from the plugin's settings, the gap absolute or in sync distances
Check(
    CellPattern.FromSettings(40000, 0, 4, 3000, 0.5, 1321).Gap == 12000,
    "default gap is 4 x the 3000 m sync distance"
);
Check(
    CellPattern.FromSettings(40000, 0, 2.5, 3000, 0.5, 1321).Gap == 7500,
    "gap of 2.5 sync distances"
);
Check(
    CellPattern.FromSettings(40000, 9000, 4, 3000, 0.5, 1321).Gap == 9000,
    "an absolute gap overrides the multiple"
);
var configured = CellPattern.FromSettings(30000, 0, 3, 2000, 0.25, 77);
Check(
    configured.CellSize == 30000
        && configured.Gap == 6000
        && configured.Jitter == 0.25
        && configured.Seed == 77,
    "cell size, gap, jitter and seed reach the pattern"
);
Check(
    CellPattern.FromSettings(40000, 0, 4, 3000, 0.5, 1321).Fingerprint
        == new CellPattern(40000, 12000, 0.5, 1321).Fingerprint,
    "equal settings, equal fingerprint"
);

// Changing the seed moves the cores; changing the gap shrinks or grows them around the same sites
{
    var baseline = new CellPattern(40000, 12000, 0.5, 1321);
    var reseeded = new CellPattern(40000, 12000, 0.5, 1322);
    var wider = new CellPattern(40000, 20000, 0.5, 1321);
    var fingerprints = new[]
    {
        baseline,
        reseeded,
        wider,
        new CellPattern(41000, 12000, 0.5, 1321),
        new CellPattern(40000, 12000, 0.6, 1321),
    }
        .Select(p => p.Fingerprint)
        .Distinct()
        .Count();
    Check(fingerprints == 5, "every parameter changes the fingerprint");

    int movedSites = 0,
        sites = 0;
    for (long i = -5; i <= 5; i++)
    for (long j = -5; j <= 5; j++)
    for (long k = -5; k <= 5; k++)
    {
        baseline.Site(i, j, k, out var ax, out var ay, out var az);
        reseeded.Site(i, j, k, out var bx, out var by, out var bz);
        sites++;
        if (Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz) > 1)
            movedSites++;
    }
    Check(movedSites == sites, $"a new seed moves every site ({movedSites}/{sites})");

    int coreChanged = 0,
        inAnyCore = 0,
        baseCore = 0,
        widerCore = 0,
        wideButNotBase = 0,
        gapGrew = 0;
    for (int n = 0; n < samples; n++)
    {
        double x = (rng.NextDouble() * 2 - 1) * 1e6,
            y = (rng.NextDouble() * 2 - 1) * 1e6,
            z = (rng.NextDouble() * 2 - 1) * 1e6;
        var a = baseline.CoreOf(x, y, z);
        var b = reseeded.CoreOf(x, y, z);
        var w = wider.CoreOf(x, y, z);
        if (a != null || b != null)
        {
            inAnyCore++;
            if (!Equals(a, b))
                coreChanged++;
        }
        if (a != null)
            baseCore++;
        if (w != null)
        {
            widerCore++;
            if (!Equals(w, a))
                wideButNotBase++;
        }
        if (a != null && w == null)
            gapGrew++;
    }
    Console.WriteLine(
        $"seed 1321 -> 1322: {(double)coreChanged / inAnyCore:P1} of the points in a core under either seed change core or fall into a gap; "
            + $"gap 12 km -> 20 km: core fraction {(double)baseCore / samples:P1} -> {(double)widerCore / samples:P1}"
    );
    Check(
        coreChanged > inAnyCore / 2,
        $"a new seed moves the cores ({coreChanged}/{inAnyCore} core points changed)"
    );
    Check(
        wideButNotBase == 0,
        $"a wider gap only shrinks each core around the same site ({wideButNotBase} points broke that)"
    );
    Check(gapGrew > 0 && widerCore < baseCore, "a wider gap turns core points into gap points");
}

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures} failures");
return failures == 0 ? 0 : 1;

static (double X, double Y, double Z) RandomUnit(Random rng)
{
    while (true)
    {
        double x = rng.NextDouble() * 2 - 1,
            y = rng.NextDouble() * 2 - 1,
            z = rng.NextDouble() * 2 - 1;
        double l = Math.Sqrt(x * x + y * y + z * z);
        if (l > 1e-3 && l <= 1)
            return (x / l, y / l, z / l);
    }
}

// The definition, evaluated over 13 x 13 x 13 sites
static (long, long, long)? BruteCore(CellPattern p, double x, double y, double z)
{
    const int reach = 6;
    long ci = (long)Math.Floor(x / p.CellSize),
        cj = (long)Math.Floor(y / p.CellSize),
        ck = (long)Math.Floor(z / p.CellSize);
    double best = double.MaxValue;
    (long, long, long) key = default;
    double bx = 0,
        by = 0,
        bz = 0;
    for (long i = ci - reach; i <= ci + reach; i++)
    for (long j = cj - reach; j <= cj + reach; j++)
    for (long k = ck - reach; k <= ck + reach; k++)
    {
        p.Site(i, j, k, out var sx, out var sy, out var sz);
        double d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy) + (z - sz) * (z - sz));
        if (d < best)
        {
            best = d;
            key = (i, j, k);
            bx = sx;
            by = sy;
            bz = sz;
        }
    }
    for (long i = ci - reach; i <= ci + reach; i++)
    for (long j = cj - reach; j <= cj + reach; j++)
    for (long k = ck - reach; k <= ck + reach; k++)
    {
        if ((i, j, k) == key)
            continue;
        p.Site(i, j, k, out var sx, out var sy, out var sz);
        // Signed distance of the point from the bisector plane, measured along the site-to-site axis
        double nx = sx - bx,
            ny = sy - by,
            nz = sz - bz;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        double mx = (sx + bx) / 2,
            my = (sy + by) / 2,
            mz = (sz + bz) / 2;
        double dist = -((x - mx) * nx + (y - my) * ny + (z - mz) * nz) / len;
        if (dist < p.Gap / 2)
            return null;
    }
    return key;
}
