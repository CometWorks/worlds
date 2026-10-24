using System;

namespace ServerPlugin;

/// <summary>
/// The spatial pattern NPC spawns must follow: space is cut into Voronoi cells around jittered lattice
/// sites, and only the CORE of a cell is spawnable. A core is the cell shrunk by half the gap from every
/// face, so two cores are at least <c>gap</c> apart and the band between them stays empty. Cores are
/// convex (an intersection of half-spaces), so a straight path whose ends lie in one core lies in it
/// entirely.
///
/// Pure arithmetic on doubles, deterministic for a given (cellSize, gap, jitter, seed): every node, the
/// cluster's sweep and the tests see the same cells.
/// </summary>
public sealed class CellPattern
{
    // How many lattice cubes around the point's own cube are searched. Sites move at most jitter/2 cells
    // off their lattice point, so every site stays in its own cube, and the own cube's site is closer than
    // sqrt(3) cells. A site matters to the core test only if it is less than a gap farther than the nearest
    // site (the distance to a bisector is at least half that difference), so every site that matters is
    // within sqrt(3) + gap cells, and a cube R + 1 away holds no site closer than R cells.
    private readonly int reach;

    public double CellSize { get; }
    public double Gap { get; }
    public double Jitter { get; }
    public long Seed { get; }

    public CellPattern(double cellSize, double gap, double jitter, long seed)
    {
        if (!(cellSize > 0))
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (!(gap >= 0) || gap >= cellSize)
            throw new ArgumentOutOfRangeException(
                nameof(gap),
                "the gap must be below the cell size"
            );
        if (!(jitter >= 0 && jitter <= 1))
            throw new ArgumentOutOfRangeException(nameof(jitter));

        CellSize = cellSize;
        Gap = gap;
        Jitter = jitter;
        Seed = seed;
        reach = (int)Math.Ceiling(Math.Sqrt(3) + gap / cellSize);
    }

    /// <summary>
    /// The pattern of the config values: <paramref name="gap"/> in metres, or when it is 0,
    /// <paramref name="gapSyncDistances"/> times the world's <paramref name="syncDistance"/>.
    /// </summary>
    public static CellPattern FromSettings(
        double cellSize,
        double gap,
        double gapSyncDistances,
        double syncDistance,
        double jitter,
        long seed
    ) => new(cellSize, gap > 0 ? gap : gapSyncDistances * syncDistance, jitter, seed);

    /// <summary>
    /// A short hash of the four parameters. Every node logs it at start: equal fingerprints mean the
    /// nodes see the same cells, cores and gap bands.
    /// </summary>
    public string Fingerprint =>
        Mix(
                Mix(
                    Mix(
                        Mix((ulong)BitConverter.DoubleToInt64Bits(CellSize))
                            ^ (ulong)BitConverter.DoubleToInt64Bits(Gap)
                    ) ^ (ulong)BitConverter.DoubleToInt64Bits(Jitter)
                ) ^ (ulong)Seed
            )
            .ToString("x16");

    /// <summary>
    /// The cell whose core contains the point, as a lattice key, or null when the point lies in a gap band.
    /// </summary>
    public (long I, long J, long K)? CoreOf(double x, double y, double z)
    {
        long ci = (long)Math.Floor(x / CellSize);
        long cj = (long)Math.Floor(y / CellSize);
        long ck = (long)Math.Floor(z / CellSize);

        // Nearest site
        double bestD2 = double.MaxValue;
        long bi = 0,
            bj = 0,
            bk = 0;
        double bx = 0,
            by = 0,
            bz = 0;
        for (long i = ci - reach; i <= ci + reach; i++)
        for (long j = cj - reach; j <= cj + reach; j++)
        for (long k = ck - reach; k <= ck + reach; k++)
        {
            Site(i, j, k, out var sx, out var sy, out var sz);
            double d2 = Sq(x - sx) + Sq(y - sy) + Sq(z - sz);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bi = i;
                bj = j;
                bk = k;
                bx = sx;
                by = sy;
                bz = sz;
            }
        }

        // Distance from the point to the bisector plane between its site and every other site; the point is
        // in the core when all of them are at least half the gap.
        double halfGap = Gap / 2;
        for (long i = ci - reach; i <= ci + reach; i++)
        for (long j = cj - reach; j <= cj + reach; j++)
        for (long k = ck - reach; k <= ck + reach; k++)
        {
            if (i == bi && j == bj && k == bk)
                continue;

            Site(i, j, k, out var sx, out var sy, out var sz);
            double d2 = Sq(x - sx) + Sq(y - sy) + Sq(z - sz);
            double separation = Math.Sqrt(Sq(sx - bx) + Sq(sy - by) + Sq(sz - bz));
            if ((d2 - bestD2) / (2 * separation) < halfGap)
                return null;
        }

        return (bi, bj, bk);
    }

    public bool InGap(double x, double y, double z) => CoreOf(x, y, z) == null;

    /// <summary>The site (generator point) of lattice cell (i, j, k).</summary>
    public void Site(long i, long j, long k, out double x, out double y, out double z)
    {
        ulong h = Mix((ulong)Seed ^ Mix((ulong)i ^ Mix((ulong)j ^ Mix((ulong)k))));
        x = (i + 0.5 + Jitter * (Unit(h) - 0.5)) * CellSize;
        h = Mix(h);
        y = (j + 0.5 + Jitter * (Unit(h) - 0.5)) * CellSize;
        h = Mix(h);
        z = (k + 0.5 + Jitter * (Unit(h) - 0.5)) * CellSize;
    }

    private static double Sq(double v) => v * v;

    // SplitMix64 finalizer
    private static ulong Mix(ulong v)
    {
        v += 0x9E3779B97F4A7C15UL;
        v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL;
        v = (v ^ (v >> 27)) * 0x94D049BB133111EBUL;
        return v ^ (v >> 31);
    }

    // Top 53 bits as a double in [0, 1)
    private static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));
}
