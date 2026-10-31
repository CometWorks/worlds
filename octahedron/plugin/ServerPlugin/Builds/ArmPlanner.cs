using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerPlugin;

/// <summary>A cell of a grid, or a direction between cells.</summary>
public readonly struct Cell(int x, int y, int z) : IEquatable<Cell>
{
    public readonly int X = x;
    public readonly int Y = y;
    public readonly int Z = z;

    public static readonly Cell[] Directions =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    ];

    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Cell operator -(Cell a) => new(-a.X, -a.Y, -a.Z);

    public int Dot(Cell b) => X * b.X + Y * b.Y + Z * b.Z;

    public static bool operator ==(Cell a, Cell b) => a.Equals(b);

    public static bool operator !=(Cell a, Cell b) => !a.Equals(b);

    public bool Equals(Cell b) => X == b.X && Y == b.Y && Z == b.Z;

    public override bool Equals(object obj) => obj is Cell c && Equals(c);

    public override int GetHashCode() => (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);

    public override string ToString() => $"{X},{Y},{Z}";
}

public enum ArmPiece
{
    /// <summary>The first pipe, on the rotor head.</summary>
    End,
    Straight,
    Corner,

    /// <summary>The last pipe: straight through, and a branch.</summary>
    Tee,
    Cap,
    Connector,

    /// <summary>A block the projector hangs on next to the connector.</summary>
    Support,
    Projector,
}

/// <summary>
/// A block of the arm. Faces are the directions it connects to its neighbors in the arm; a
/// connector's Front faces the ship's connector, a projector's the block it builds first.
/// </summary>
public sealed class ArmBlock(Cell cell, ArmPiece piece, Cell[] faces, Cell front = default)
{
    public readonly Cell Cell = cell;
    public readonly ArmPiece Piece = piece;
    public readonly Cell[] Faces = faces;
    public readonly Cell Front = front;
}

/// <summary>
/// The arm to plan, in the cells of the rotor head's grid. The ship's volume holds the cells of
/// every block of the blueprint where it will be built; Blocked the shipyard and everything else
/// the arm must not go through.
/// </summary>
public sealed class ArmProblem
{
    public Cell Min;
    public Cell Max;

    /// <summary>What the arm must not go through. Occupied takes it faster when it is set.</summary>
    public Func<Cell, bool> Blocked = _ => false;
    public ArmGrid Occupied;
    public HashSet<Cell> Volume = [];

    /// <summary>The first pipe's cell on the rotor head, and the way out of the head.</summary>
    public Cell Start;
    public Cell Up;

    /// <summary>
    /// Blocks of the ship's main grid a projector can start the build from, by cell: the
    /// directions from the block to a projector next to it whose faces mount together.
    /// </summary>
    public Dictionary<Cell, Cell[]> Touches = [];

    /// <summary>The ship's connectors the arm may dock to, best first: cell and front.</summary>
    public List<(Cell Cell, Cell Front)> Connectors = [];

    /// <summary>Armor blocks the projector may take the place of, lightest first.</summary>
    public List<(Cell Cell, double Mass)> GiveUps = [];

    /// <summary>The cost of a turn, in cells of pipe.</summary>
    public int TurnCost = 3;

    /// <summary>Search at most this many states, so a hopeless plan ends.</summary>
    public int MaxStates = 5_000_000;

    /// <summary>Plan no arm in a box of more cells than this, for the memory.</summary>
    public long MaxCells = 40_000_000;

    /// <summary>Asked now and then during the search; true ends it without a plan.</summary>
    public Func<bool> Stop = () => false;

    /// <summary>Set when Stop ended the search.</summary>
    public bool Stopped;
}

/// <summary>A bit per cell of a box: the cells an arm can't go through.</summary>
public sealed class ArmGrid
{
    private readonly Cell min;
    private readonly int sizeX;
    private readonly int sizeY;
    private readonly int sizeZ;
    private readonly ulong[] bits;

    public ArmGrid(Cell min, Cell max)
    {
        this.min = min;
        sizeX = max.X - min.X + 1;
        sizeY = max.Y - min.Y + 1;
        sizeZ = max.Z - min.Z + 1;
        bits = new ulong[((long)sizeX * sizeY * sizeZ + 63) / 64];
    }

    private long Index(Cell c)
    {
        int x = c.X - min.X,
            y = c.Y - min.Y,
            z = c.Z - min.Z;
        return x < 0 || y < 0 || z < 0 || x >= sizeX || y >= sizeY || z >= sizeZ
            ? -1
            : ((long)x * sizeY + y) * sizeZ + z;
    }

    /// <summary>Marks a cell; one outside the box is ignored.</summary>
    public void Mark(Cell c)
    {
        var i = Index(c);
        if (i >= 0)
            bits[i >> 6] |= 1UL << (int)(i & 63);
    }

    public bool this[Cell c]
    {
        get
        {
            var i = Index(c);
            return i >= 0 && (bits[i >> 6] & 1UL << (int)(i & 63)) != 0;
        }
    }
}

public sealed class ArmPlan
{
    public List<ArmBlock> Blocks = [];

    /// <summary>The block of the ship's main grid the projection is anchored on.</summary>
    public Cell Touch;

    /// <summary>The ship's connector the arm docks to, or null with a conveyor cap.</summary>
    public Cell? Connector;

    /// <summary>The ship's block the projector took the place of, or null.</summary>
    public Cell? GivenUp;

    public double Cost;

    public ArmBlock Projector => Blocks.First(b => b.Piece == ArmPiece.Projector);
}

/// <summary>
/// Plans the support arm from the rotor head to the ship: a pipe end on the head, pipes around
/// the ship's volume, then a tee with the projector next to the ship. With a connector the tee's
/// branch carries a connector facing the ship's, and the projector hangs on a support block next
/// to it; without one, the branch ends in a cap. Costs one per cell and TurnCost per turn.
/// Prefers, in this order: docking at a connector, a free spot next to the ship, then giving up
/// the lightest armor block for the projector.
/// </summary>
public static class ArmPlanner
{
    // An end of the line: the tee's cell, the direction the line enters it, and the rest
    private sealed class Terminal(Cell tee, Cell into, double extra, Func<List<ArmBlock>> blocks)
    {
        public readonly Cell Tee = tee;
        public readonly Cell In = into;
        public readonly double Extra = extra;
        public readonly Func<List<ArmBlock>> Blocks = blocks;
        public Cell Touch;
        public Cell? Connector;
        public Cell? GivenUp;
        public HashSet<Cell> Cells = [];
    }

    public static ArmPlan Plan(ArmProblem p)
    {
        if (Cells(p) > p.MaxCells)
            return null;
        Occupy(p);
        if (!Free(p, p.Start))
            return null;
        foreach (var connector in p.Connectors)
        {
            var plan = Search(p, DockTerminals(p, connector).ToList());
            if (plan != null || p.Stopped)
                return plan;
        }
        var open = Search(p, CapTerminals(p, null).ToList());
        if (open != null || p.Stopped)
            return open;
        foreach (var tier in p.GiveUps.GroupBy(g => g.Mass).OrderBy(g => g.Key))
        {
            var plan = Search(p, tier.SelectMany(g => CapTerminals(p, g.Cell)).ToList());
            if (plan != null || p.Stopped)
                return plan;
        }
        return null;
    }

    private static void Occupy(ArmProblem p)
    {
        if (p.Occupied == null)
        {
            p.Occupied = new ArmGrid(p.Min, p.Max);
            for (var x = p.Min.X; x <= p.Max.X; x++)
            for (var y = p.Min.Y; y <= p.Max.Y; y++)
            for (var z = p.Min.Z; z <= p.Max.Z; z++)
                if (p.Blocked(new Cell(x, y, z)))
                    p.Occupied.Mark(new Cell(x, y, z));
        }
        foreach (var c in p.Volume)
            p.Occupied.Mark(c);
    }

    private static long Cells(ArmProblem p)
    {
        var size = p.Max - p.Min + new Cell(1, 1, 1);
        return (long)size.X * size.Y * size.Z;
    }

    /// <summary>Why Plan found no arm, for the log: what blocks the start, or how far it got.</summary>
    public static string Explain(ArmProblem p)
    {
        var box = $"box {p.Min}..{p.Max}, ship volume {p.Volume.Count} cells";
        if (!Inside(p, p.Start))
            return $"the pipe end {p.Start} is outside the {box}";
        if (p.Volume.Contains(p.Start))
            return $"the ship's volume covers the pipe end {p.Start}";
        if (Cells(p) > p.MaxCells)
            return $"the {box} is too large to search";
        if (p.Stopped)
            return $"the search ran out of time in the {box}";
        Occupy(p);
        if (p.Occupied[p.Start])
            return $"the pipe end {p.Start} is taken by the shipyard";
        var spots = new HashSet<Cell>(
            p.Touches.SelectMany(t => t.Value.Select(d => t.Key + d)).Where(c => Free(p, c))
        );
        // The free cells the pipe end reaches, any way round
        var reached = new HashSet<Cell> { p.Start };
        var todo = new Queue<Cell>(reached);
        while (todo.Count > 0)
        {
            var c = todo.Dequeue();
            foreach (var d in Cell.Directions)
                if (Free(p, c + d) && reached.Add(c + d))
                    todo.Enqueue(c + d);
        }
        return $"the pipe end {p.Start} going {p.Up} reaches {reached.Count} free cells, "
            + $"{spots.Count(reached.Contains)} of {spots.Count} free projector spots next to "
            + $"{p.Touches.Count} touchable blocks; {box}";
    }

    private static bool Inside(ArmProblem p, Cell c) =>
        c.X >= p.Min.X
        && c.Y >= p.Min.Y
        && c.Z >= p.Min.Z
        && c.X <= p.Max.X
        && c.Y <= p.Max.Y
        && c.Z <= p.Max.Z;

    private static bool Free(ArmProblem p, Cell c) => Inside(p, c) && !p.Occupied[c];

    private static IEnumerable<Cell> Perpendicular(Cell d) =>
        Cell.Directions.Where(e => e.Dot(d) == 0);

    // The ship's block the projector at cell builds first: a touching block other than skip
    private static Cell? TouchFrom(ArmProblem p, Cell projector, Cell? skip)
    {
        foreach (var d in Cell.Directions)
        {
            var block = projector + d;
            if (block != skip && p.Touches.TryGetValue(block, out var faces) && faces.Contains(-d))
                return block;
        }
        return null;
    }

    // Tee, projector straight on, cap on a free side of the tee. With givenUp the projector
    // stands in that block's cell.
    private static IEnumerable<Terminal> CapTerminals(ArmProblem p, Cell? givenUp)
    {
        IEnumerable<Cell> spots =
            givenUp != null
                ? [givenUp.Value]
                : p.Touches.SelectMany(t => t.Value.Select(d => t.Key + d).Where(c => Free(p, c)));
        foreach (var projector in spots.Distinct())
        {
            var touch = TouchFrom(p, projector, givenUp);
            if (touch == null)
                continue;
            foreach (var m in Cell.Directions)
            {
                var tee = projector - m;
                if (!Free(p, tee) || tee == p.Start)
                    continue;
                Cell? capSide = Perpendicular(m)
                    .Select(c => (Cell?)c)
                    .FirstOrDefault(c => Free(p, tee + c.Value));
                // An open tee if no side is free
                var branch = capSide ?? Perpendicular(m).First();
                var t = new Terminal(
                    tee,
                    m,
                    capSide == null ? 1 : 2,
                    () =>
                    {
                        var blocks = new List<ArmBlock>
                        {
                            new(projector, ArmPiece.Projector, [-m], touch.Value - projector),
                        };
                        if (capSide != null)
                            blocks.Add(new(tee + branch, ArmPiece.Cap, [-branch]));
                        return blocks;
                    }
                )
                {
                    Touch = touch.Value,
                    GivenUp = givenUp,
                };
                t.Cells.Add(projector);
                if (capSide != null)
                    t.Cells.Add(tee + branch);
                yield return t;
            }
        }
    }

    // The arm's connector faces the ship's; the tee behind it, the line through the tee to a
    // support, the projector on the support next to the connector
    private static IEnumerable<Terminal> DockTerminals(ArmProblem p, (Cell Cell, Cell Front) ship)
    {
        var f = ship.Front;
        var connector = ship.Cell + f;
        var tee = connector + f;
        if (!Free(p, connector) || !Free(p, tee) || tee == p.Start)
            yield break;
        foreach (var m in Perpendicular(f))
        {
            var support = tee + m;
            var projector = connector + m;
            if (!Free(p, support) || !Free(p, projector))
                continue;
            var touch = TouchFrom(p, projector, null);
            if (touch == null)
                continue;
            var t = new Terminal(
                tee,
                m,
                3,
                () =>
                    [
                        new(connector, ArmPiece.Connector, [f], -f),
                        new(support, ArmPiece.Support, [-m, -f]),
                        new(projector, ArmPiece.Projector, [f], touch.Value - projector),
                    ]
            )
            {
                Touch = touch.Value,
                Connector = ship.Cell,
            };
            t.Cells.UnionWith([connector, support, projector]);
            yield return t;
        }
    }

    // A* over (cell, direction of travel) from the pipe end to the cheapest terminal. A path
    // through a terminal's other cells is refused afterwards and that terminal dropped.
    private static ArmPlan Search(ArmProblem p, List<Terminal> terminals)
    {
        while (terminals.Count > 0)
        {
            var (path, terminal, cost) = Route(p, terminals);
            if (path == null || p.Stopped)
                return null;
            if (path.Any(terminal.Cells.Contains))
            {
                terminals.Remove(terminal);
                continue;
            }
            return Build(p, path, terminal, cost);
        }
        return null;
    }

    private static (List<Cell> Path, Terminal Terminal, double Cost) Route(
        ArmProblem p,
        List<Terminal> terminals
    )
    {
        var size = p.Max - p.Min + new Cell(1, 1, 1);
        int Index(Cell c, int d) =>
            ((((c.X - p.Min.X) * size.Y + (c.Y - p.Min.Y)) * size.Z) + (c.Z - p.Min.Z)) * 6 + d;
        Cell CellOf(int i)
        {
            i /= 6;
            var z = i % size.Z;
            i /= size.Z;
            return new Cell(i / size.Y + p.Min.X, i % size.Y + p.Min.Y, z + p.Min.Z);
        }

        var goals = new Dictionary<int, Terminal>();
        foreach (var t in terminals)
        {
            var key = Index(t.Tee, Array.IndexOf(Cell.Directions, t.In));
            if (!goals.TryGetValue(key, out var had) || had.Extra > t.Extra)
                goals[key] = t;
        }
        var field = Distances(p, size, terminals);
        if (field == null)
            return (null, null, 0);
        int Field(Cell c) =>
            field[((c.X - p.Min.X) * size.Y + (c.Y - p.Min.Y)) * size.Z + (c.Z - p.Min.Z)];
        if (Field(p.Start) == Unreached)
            return (null, null, 0);
        var least = terminals.Min(t => t.Extra);

        var lo = new Cell(
            terminals.Min(t => t.Tee.X),
            terminals.Min(t => t.Tee.Y),
            terminals.Min(t => t.Tee.Z)
        );
        var hi = new Cell(
            terminals.Max(t => t.Tee.X),
            terminals.Max(t => t.Tee.Y),
            terminals.Max(t => t.Tee.Z)
        );

        // The turns any way there takes: one for each axis left to the goals' box, but the one
        // going along it
        int Turns(Cell c, Cell dir)
        {
            var dx =
                c.X < lo.X ? 1
                : c.X > hi.X ? -1
                : 0;
            var dy =
                c.Y < lo.Y ? 1
                : c.Y > hi.Y ? -1
                : 0;
            var dz =
                c.Z < lo.Z ? 1
                : c.Z > hi.Z ? -1
                : 0;
            var axes = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
            var along = dir.X * dx + dir.Y * dy + dir.Z * dz > 0 ? 1 : 0;
            return axes - along;
        }

        // The cells left to the nearest goal, around what is in the way, and the turns: those
        // above, or one if going on straight doesn't get closer. It never overestimates, so the
        // plan stays the cheapest
        double Heuristic(Cell c, int d)
        {
            var left = Field(c);
            if (left == 0)
                return least;
            var dir = Cell.Directions[d];
            var ahead = c + dir;
            var turns = Math.Max(Turns(c, dir), Free(p, ahead) && Field(ahead) < left ? 0 : 1);
            return left + least + turns * p.TurnCost;
        }

        // Of the states as cheap, the one closest to the goal: without this a wide open box
        // has every way there tied, and the search would go through them all
        double Priority(double g, double h) => g + h + h * 1e-6;

        var cost = new Dictionary<int, double>();
        var from = new Dictionary<int, int>();
        var queue = new Heap();
        var up = Array.IndexOf(Cell.Directions, p.Up);
        var start = Index(p.Start, up);
        cost[start] = 1;
        from[start] = -1;
        queue.Enqueue(start, Priority(1, Heuristic(p.Start, up)));
        // A finished line: the goal's state and its total, kept until nothing cheaper is left
        var best = (State: -1, Total: double.MaxValue);
        for (var pops = 0; queue.Count > 0; pops++)
        {
            if ((pops & 4095) == 0 && p.Stop())
            {
                p.Stopped = true;
                return (null, null, 0);
            }
            var (state, priority) = queue.Dequeue();
            if (priority >= best.Total)
                break;
            var c = CellOf(state);
            var d = state % 6;
            var g = cost[state];
            if (priority > Priority(g, Heuristic(c, d)) + 1e-9)
                continue;
            if (goals.TryGetValue(state, out var goal) && g + goal.Extra < best.Total)
                best = (state, g + goal.Extra);
            for (var e = 0; e < 6; e++)
            {
                var dir = Cell.Directions[e];
                if (dir == -Cell.Directions[d] || (state == start && e != d))
                    continue;
                var next = c + dir;
                if (!Free(p, next) || Field(next) == Unreached)
                    continue;
                var n = Index(next, e);
                var ng = g + 1 + (e == d ? 0 : p.TurnCost);
                if (cost.TryGetValue(n, out var had) && had <= ng)
                    continue;
                if (cost.Count >= p.MaxStates)
                    return (null, null, 0);
                cost[n] = ng;
                from[n] = state;
                queue.Enqueue(n, Priority(ng, Heuristic(next, e)));
            }
        }
        if (best.State < 0)
            return (null, null, 0);
        var path = new List<Cell>();
        for (var s = best.State; s >= 0; s = from[s])
            path.Add(CellOf(s));
        path.Reverse();
        return (path, goals[best.State], best.Total);
    }

    private const ushort Unreached = ushort.MaxValue;

    // Every free cell's distance in cells from the nearest terminal's tee, a breadth first search
    // from them all; null if Stop ended it
    private static ushort[] Distances(ArmProblem p, Cell size, List<Terminal> terminals)
    {
        var field = new ushort[(long)size.X * size.Y * size.Z];
        for (var i = 0; i < field.Length; i++)
            field[i] = Unreached;
        var stepX = size.Y * size.Z;
        var steps = new[] { stepX, -stepX, size.Z, -size.Z, 1, -1 };
        var now = new List<int>();
        var next = new List<int>();
        foreach (var t in terminals)
        {
            var i =
                ((t.Tee.X - p.Min.X) * size.Y + (t.Tee.Y - p.Min.Y)) * size.Z + t.Tee.Z - p.Min.Z;
            if (field[i] != 0)
                now.Add(i);
            field[i] = 0;
        }
        for (ushort distance = 1; now.Count > 0; distance++)
        {
            if (p.Stop())
            {
                p.Stopped = true;
                return null;
            }
            next.Clear();
            foreach (var i in now)
            {
                var z = i % size.Z;
                var y = i / size.Z % size.Y;
                var x = i / stepX;
                for (var e = 0; e < 6; e++)
                {
                    var (cx, cy, cz) = e switch
                    {
                        0 => (x + 1, y, z),
                        1 => (x - 1, y, z),
                        2 => (x, y + 1, z),
                        3 => (x, y - 1, z),
                        4 => (x, y, z + 1),
                        _ => (x, y, z - 1),
                    };
                    if (cx < 0 || cy < 0 || cz < 0 || cx >= size.X || cy >= size.Y || cz >= size.Z)
                        continue;
                    var j = i + steps[e];
                    if (
                        field[j] == Unreached
                        && !p.Occupied[new Cell(cx + p.Min.X, cy + p.Min.Y, cz + p.Min.Z)]
                    )
                    {
                        field[j] = distance;
                        next.Add(j);
                    }
                }
            }
            (now, next) = (next, now);
        }
        return field;
    }

    private static ArmPlan Build(ArmProblem p, List<Cell> path, Terminal terminal, double cost)
    {
        var plan = new ArmPlan
        {
            Touch = terminal.Touch,
            Connector = terminal.Connector,
            GivenUp = terminal.GivenUp,
            Cost = cost,
        };
        for (var i = 0; i < path.Count; i++)
        {
            var into = i == 0 ? p.Up : path[i] - path[i - 1];
            if (i == path.Count - 1)
            {
                var extra = terminal.Blocks();
                var branch = extra.FirstOrDefault(b =>
                    b.Piece is ArmPiece.Cap or ArmPiece.Connector
                );
                var side = branch != null ? branch.Cell - path[i] : Perpendicular(into).First();
                plan.Blocks.Add(new(path[i], ArmPiece.Tee, [-into, into, side]));
                plan.Blocks.AddRange(extra);
            }
            else
            {
                var next = path[i + 1] - path[i];
                plan.Blocks.Add(
                    i == 0 ? new(path[i], ArmPiece.End, [-into, into])
                    : next == into ? new(path[i], ArmPiece.Straight, [-into, into])
                    : new(path[i], ArmPiece.Corner, [-into, next])
                );
            }
        }
        return plan;
    }

    // A binary min-heap of states by priority (no PriorityQueue on .NET Framework)
    private sealed class Heap
    {
        private readonly List<(int State, double Priority)> items = [];

        public int Count => items.Count;

        public void Enqueue(int state, double priority)
        {
            items.Add((state, priority));
            for (var i = items.Count - 1; i > 0; )
            {
                var parent = (i - 1) / 2;
                if (items[parent].Priority <= items[i].Priority)
                    break;
                (items[parent], items[i]) = (items[i], items[parent]);
                i = parent;
            }
        }

        public (int State, double Priority) Dequeue()
        {
            var top = items[0];
            items[0] = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            for (var i = 0; ; )
            {
                var smallest = i;
                for (var child = 2 * i + 1; child <= 2 * i + 2; child++)
                    if (child < items.Count && items[child].Priority < items[smallest].Priority)
                        smallest = child;
                if (smallest == i)
                    break;
                (items[smallest], items[i]) = (items[i], items[smallest]);
                i = smallest;
            }
            return top;
        }
    }
}
