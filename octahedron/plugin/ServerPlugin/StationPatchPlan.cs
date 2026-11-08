using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ServerPlugin;

// The game-free half of station patches (StationPatches.cs): the patch format, and the checks of a
// whole patch before any of it is applied. tests/patches/PatchTests runs it without the game.
// Cells are ArmPlanner's (Builds/ArmPlanner.cs).

public enum PatchOpKind
{
    Set,
    Del,
    Add,
    Swap,
}

public sealed class PatchOp
{
    public PatchOpKind Kind;
    public Cell At;

    // set, del, swap: the block the cell must hold, Type/Subtype or Subtype
    public string Expected;

    // set: enabled, name or data
    public string Key;
    public string Value;

    // add, swap: the new block, Type/Subtype
    public string Block;

    // add: orientation and the optional name
    public string Forward;
    public string Up;
    public string Name;

    public string Text;

    public override string ToString() => Text;
}

public sealed class StationPatch
{
    public string Id;
    public string Grid;
    public List<PatchOp> Ops;
}

public static class PatchFormat
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9._-]+$");
    private static readonly string[] Directions =
    [
        "Forward",
        "Backward",
        "Left",
        "Right",
        "Up",
        "Down",
    ];

    /// <summary>
    /// A patch from its id, grid name and ops: "op;op;...", each op's fields separated by ':'
    /// <code>
    /// set:X,Y,Z:Expected:enabled=0|1    set:X,Y,Z:Expected:name=Text    set:X,Y,Z:Expected:data=Text
    /// del:X,Y,Z:Expected
    /// add:X,Y,Z:Type/Subtype:Forward:Up[:Name]
    /// swap:X,Y,Z:Expected:Type/Subtype
    /// </code>
    /// Null with the reason in error when it doesn't parse.
    /// </summary>
    public static StationPatch Parse(string id, string grid, string ops, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(id) || !IdPattern.IsMatch(id))
        {
            error = $"bad patch id '{id}': letters, digits, '.', '_' and '-' only";
            return null;
        }
        if (string.IsNullOrWhiteSpace(grid))
        {
            error = "no grid name";
            return null;
        }
        var list = new List<PatchOp>();
        foreach (
            var text in (ops ?? "").Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            var op = ParseOp(text, out var why);
            if (op == null)
            {
                error = $"op {list.Count + 1} '{text}': {why}";
                return null;
            }
            list.Add(op);
        }
        if (list.Count == 0)
        {
            error = "no ops";
            return null;
        }
        return new StationPatch
        {
            Id = id,
            Grid = grid.Trim(),
            Ops = list,
        };
    }

    private static bool TryCell(string text, out Cell cell)
    {
        cell = default;
        var p = text.Split(',');
        var n = new int[3];
        for (var i = 0; i < 3; i++)
            if (
                p.Length != 3
                || !int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])
            )
                return false;
        cell = new Cell(n[0], n[1], n[2]);
        return true;
    }

    private static PatchOp ParseOp(string text, out string error)
    {
        error = null;
        var a = text.Split(':');
        var op = new PatchOp { Text = text };
        if (a.Length < 2 || !TryCell(a[1], out op.At))
        {
            error = "no cell X,Y,Z";
            return null;
        }
        switch (a[0])
        {
            case "set":
            {
                // The value keeps any ':' it has
                a = text.Split(':', 4);
                var eq = a.Length == 4 ? a[3].IndexOf('=') : -1;
                if (eq < 0 || a[2].Length == 0)
                {
                    error = "set:X,Y,Z:Expected:key=value";
                    return null;
                }
                op.Kind = PatchOpKind.Set;
                op.Expected = a[2];
                op.Key = a[3][..eq];
                op.Value = a[3][(eq + 1)..];
                if (op.Key is not ("enabled" or "name" or "data"))
                    error = $"can't set '{op.Key}': enabled, name or data";
                else if (op.Key == "enabled" && op.Value is not ("0" or "1"))
                    error = "enabled=0 or enabled=1";
                break;
            }
            case "del":
                op.Kind = PatchOpKind.Del;
                if (a.Length != 3 || a[2].Length == 0)
                    error = "del:X,Y,Z:Expected";
                else
                    op.Expected = a[2];
                break;
            case "add":
                op.Kind = PatchOpKind.Add;
                if (a.Length is < 5 or > 6 || !a[2].Contains('/'))
                    error = "add:X,Y,Z:Type/Subtype:Forward:Up[:Name]";
                else if (!Directions.Contains(a[3]) || !Directions.Contains(a[4]))
                    error = $"directions are {string.Join(", ", Directions)}";
                else if (Array.IndexOf(Directions, a[3]) / 2 == Array.IndexOf(Directions, a[4]) / 2)
                    error = $"{a[3]} and {a[4]} are on one axis";
                else
                {
                    op.Block = a[2];
                    op.Forward = a[3];
                    op.Up = a[4];
                    op.Name = a.Length == 6 ? a[5] : null;
                }
                break;
            case "swap":
                op.Kind = PatchOpKind.Swap;
                if (a.Length != 4 || a[2].Length == 0 || !a[3].Contains('/'))
                    error = "swap:X,Y,Z:Expected:Type/Subtype";
                else
                {
                    op.Expected = a[2];
                    op.Block = a[3];
                }
                break;
            default:
                error = "ops are set, del, add and swap";
                break;
        }
        return error == null ? op : null;
    }
}

/// <summary>A block of a patched grid, as the checks see it: an existing one or one a patch adds.</summary>
public sealed class PatchBlock
{
    public Cell Min;
    public List<Cell> Cells;

    // Type/Subtype, "CubeBlock/LargeBlockArmorBlock"
    public string Type;
    public bool Terminal;
    public bool Functional;

    // The game's block, or what the game needs to build a new one
    public object Game;

    public bool Is(string expected) =>
        Type == expected || Type.EndsWith("/" + expected, StringComparison.Ordinal);

    public override string ToString() => $"{Type}@{Min}";
}

/// <summary>The grid a patch changes, as its checks need it.</summary>
public interface IPatchGrid
{
    /// <summary>The block covering the cell now, or null.</summary>
    PatchBlock BlockAt(Cell cell);

    IEnumerable<PatchBlock> Blocks();

    /// <summary>The blocks joined to this one by mount points now.</summary>
    IEnumerable<PatchBlock> Neighbours(PatchBlock block);

    /// <summary>The block an add places, or a swap in place of replaced; null with why.</summary>
    PatchBlock NewBlock(PatchOp op, PatchBlock replaced, out string error);

    /// <summary>A new block and a block next to it join by mount points.</summary>
    bool Mounts(PatchBlock added, PatchBlock other);

    /// <summary>Why the op may not touch the block now (a docked ship, a running order), or null.</summary>
    string Veto(PatchOp op, PatchBlock block);
}

public sealed class PatchStep
{
    public PatchOp Op;

    // The block set, removed or replaced
    public PatchBlock Old;

    // The block added
    public PatchBlock New;
}

public static class PatchPlan
{
    /// <summary>
    /// Checks every op of the patch against the grid before any is applied, in order, each seeing
    /// the grid as the ops before it leave it. The steps to apply, or null with why in error.
    /// A patch that removes a block must leave the grid in one piece: at a removal the game splits
    /// off every group of blocks not joined to the largest by mount points. Every block it adds
    /// needs a mount neighbor, or it splits off at the next removal.
    /// </summary>
    public static List<PatchStep> Check(StationPatch patch, IPatchGrid grid, out string error)
    {
        error = null;
        var steps = new List<PatchStep>();
        // Cells the patch has changed so far: the new block there, or null where it freed one
        var changed = new Dictionary<Cell, PatchBlock>();
        var removed = new HashSet<PatchBlock>();
        var added = new List<PatchBlock>();
        PatchBlock At(Cell c) => changed.TryGetValue(c, out var b) ? b : grid.BlockAt(c);

        string Step(PatchOp op)
        {
            PatchBlock old = null;
            if (op.Kind != PatchOpKind.Add)
            {
                old = At(op.At);
                if (old == null)
                    return "no block there";
                if (added.Contains(old))
                    return $"{old} is a block this patch adds";
                if (!old.Is(op.Expected))
                    return $"the cell holds {old.Type}, not {op.Expected}";
                if (
                    op.Kind == PatchOpKind.Set
                    && (op.Key == "enabled" ? !old.Functional : !old.Terminal)
                )
                    return $"{old.Type} has no {op.Key}";
                var veto = grid.Veto(op, old);
                if (veto != null)
                    return veto;
            }
            if (op.Kind == PatchOpKind.Set)
            {
                steps.Add(new PatchStep { Op = op, Old = old });
                return null;
            }
            if (old != null)
            {
                foreach (var c in old.Cells)
                    changed[c] = null;
                removed.Add(old);
            }
            if (op.Kind == PatchOpKind.Del)
            {
                steps.Add(new PatchStep { Op = op, Old = old });
                return null;
            }
            var made = grid.NewBlock(op, old, out var why);
            if (made == null)
                return why;
            foreach (var c in made.Cells)
                if (At(c) is { } there)
                    return $"cell {c} is taken by {there}";
            var no = grid.Veto(op, made);
            if (no != null)
                return no;
            foreach (var c in made.Cells)
                changed[c] = made;
            added.Add(made);
            steps.Add(
                new PatchStep
                {
                    Op = op,
                    Old = old,
                    New = made,
                }
            );
            return null;
        }

        for (var i = 0; i < patch.Ops.Count; i++)
        {
            var why = Step(patch.Ops[i]);
            if (why != null)
            {
                error = $"op {i + 1} '{patch.Ops[i].Text}': {why}";
                return null;
            }
        }

        // The mount points of the new blocks with what is next to them once the patch is done
        var joins = new Dictionary<PatchBlock, HashSet<PatchBlock>>();
        void Join(PatchBlock a, PatchBlock b)
        {
            if (!joins.TryGetValue(a, out var set))
                joins[a] = set = [];
            set.Add(b);
        }
        foreach (var made in added)
        foreach (var c in made.Cells)
        foreach (var face in Cell.Directions)
            if (At(c + face) is { } next && next != made && grid.Mounts(made, next))
            {
                Join(made, next);
                Join(next, made);
            }
        foreach (var made in added)
            if (!joins.ContainsKey(made))
            {
                error = $"the new {made} has no mount neighbor";
                return null;
            }
        if (removed.Count == 0)
            return steps;

        var after = grid.Blocks().Where(b => !removed.Contains(b)).Concat(added);
        var groups = Groups(
            after,
            b =>
                (
                    added.Contains(b) ? [] : grid.Neighbours(b).Where(n => !removed.Contains(n))
                ).Concat(joins.TryGetValue(b, out var set) ? set : [])
        );
        if (groups.Count > 1)
        {
            error =
                $"the grid would split: {groups.Count - 1} groups not joined to its largest by mount points, "
                + Describe(groups);
            return null;
        }
        return steps;
    }

    /// <summary>The blocks in groups joined by mount points, the largest first.</summary>
    public static List<List<PatchBlock>> Groups(
        IEnumerable<PatchBlock> blocks,
        Func<PatchBlock, IEnumerable<PatchBlock>> neighbours
    )
    {
        var left = new HashSet<PatchBlock>(blocks);
        var groups = new List<List<PatchBlock>>();
        while (left.Count > 0)
        {
            var first = left.First();
            left.Remove(first);
            var group = new List<PatchBlock>();
            var todo = new Stack<PatchBlock>();
            todo.Push(first);
            while (todo.Count > 0)
            {
                var block = todo.Pop();
                group.Add(block);
                foreach (var next in neighbours(block))
                    if (left.Remove(next))
                        todo.Push(next);
            }
            groups.Add(group);
        }
        return groups.OrderByDescending(g => g.Count).ToList();
    }

    /// <summary>The groups after the largest: "6 from FloorPlanSign@-1,7,-3, ...".</summary>
    public static string Describe(List<List<PatchBlock>> groups) =>
        string.Join(
            ", ",
            groups
                .Skip(1)
                .Take(10)
                .Select(g =>
                    $"{g.Count} from {g.OrderBy(b => b.Min.ToString(), StringComparer.Ordinal).First()}"
                )
        );
}
