using System;
using System.Collections.Generic;
using System.Linq;
using ServerPlugin;

// Checks station patches without the game (ServerPlugin/StationPatchPlan.cs): the format, the checks
// of a whole patch before any op applies, and the island check. Exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/patches/PatchTests

int checks = 0,
    failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

StationPatch P(string ops) => PatchFormat.Parse("p1", "Alpha Station", ops, out _);

string ParseError(string ops, string id = "p1", string grid = "Alpha Station")
{
    PatchFormat.Parse(id, grid, ops, out var error);
    return error;
}

(List<PatchStep> Steps, string Error) Plan(FakeGrid grid, string ops)
{
    var steps = PatchPlan.Check(P(ops), grid, out var error);
    return (steps, error);
}

// --- the format ---

var all = P(
    "set:1,2,3:LargeBlockLight_1corner:enabled=0; del:-4,0,5:CargoContainer/LargeBarrel;"
        + "add:7,8,9:CubeBlock/LargeBlockArmorBlock:Forward:Up:New Block;swap:0,0,-1:Light:InteriorLight/LargeBlockLight_2corner"
);
Check(all?.Ops.Count == 4, "four ops parse");
Check(
    all?.Ops[0] is { Kind: PatchOpKind.Set, Key: "enabled", Value: "0" } s0
        && s0.At == new Cell(1, 2, 3)
        && s0.Expected == "LargeBlockLight_1corner",
    "set: cell, expected block, key and value"
);
Check(
    all?.Ops[1] is { Kind: PatchOpKind.Del, Expected: "CargoContainer/LargeBarrel" } d
        && d.At == new Cell(-4, 0, 5),
    "del: negative cells, Type/Subtype"
);
Check(
    all?.Ops[2]
        is {
            Kind: PatchOpKind.Add,
            Block: "CubeBlock/LargeBlockArmorBlock",
            Forward: "Forward",
            Up: "Up",
            Name: "New Block",
        },
    "add: block, orientation and a name with a space"
);
Check(
    all?.Ops[3]
        is {
            Kind: PatchOpKind.Swap,
            Expected: "Light",
            Block: "InteriorLight/LargeBlockLight_2corner",
        },
    "swap: expected and new block"
);
Check(P("set:0,0,0:LCD:data=a: b")?.Ops[0].Value == "a: b", "the value with its ':'");
Check(
    ParseError("del:0,0,0:X", id: "a,b") != null,
    "an id with a comma is refused (the ledger is a list)"
);
Check(ParseError("del:0,0,0:X", id: "") != null, "an empty id is refused");
Check(ParseError("del:0,0,0:X", grid: " ") != null, "a patch needs a grid");
Check(ParseError(" ; ") == "no ops", "a patch needs ops");
Check(ParseError("move:0,0,0:X")?.Contains("op 1") == true, "an unknown op is refused, by number");
Check(ParseError("del:0,0,0:X;del:1,1:X")?.StartsWith("op 2") == true, "a bad cell, op 2");
Check(ParseError("del:0,0,0") != null, "del needs the expected block");
Check(ParseError("set:0,0,0:X:color=red") != null, "only enabled, name and data are set");
Check(ParseError("set:0,0,0:X:enabled=2") != null, "enabled is 0 or 1");
Check(ParseError("set:0,0,0:X:name") != null, "set needs key=value");
Check(ParseError("add:0,0,0:LargeBlockArmorBlock:Forward:Up") != null, "add needs Type/Subtype");
Check(ParseError("add:0,0,0:CubeBlock/A:Ahead:Up") != null, "add needs known directions");
Check(ParseError("add:0,0,0:CubeBlock/A:Forward:Backward") != null, "forward and up on one axis");
Check(ParseError("swap:0,0,0:X") != null, "swap needs the new block");

// --- the checks before any op applies ---

// A row of armor along x: 0..4
FakeGrid Row() => new FakeGrid().Line(0, 4);

var row = Row();
var plan = Plan(row, "del:4,0,0:LargeBlockArmorBlock");
Check(plan.Steps?.Count == 1, "removing the row's end block passes: " + plan.Error);

plan = Plan(row, "del:2,0,0:LargeBlockArmorBlock");
Check(
    plan.Steps == null && plan.Error.Contains("would split"),
    "removing a block the row hangs on is refused: " + plan.Error
);

plan = Plan(row, "del:4,0,0:LargeBarrel");
Check(
    plan.Steps == null && plan.Error.Contains("holds CubeBlock/LargeBlockArmorBlock"),
    "the cell must hold the expected block"
);
plan = Plan(row, "del:9,0,0:LargeBlockArmorBlock");
Check(plan.Error?.Contains("no block there") == true, "an empty cell is refused");

// All or nothing: a good op followed by a bad one gives no steps at all
plan = Plan(row, "del:4,0,0:LargeBlockArmorBlock;set:3,0,0:LargeBlockArmorBlock:enabled=0");
Check(
    plan.Steps == null && plan.Error.StartsWith("op 2") && plan.Error.Contains("has no enabled"),
    "a later bad op refuses the whole patch: " + plan.Error
);

// Settings
var lit = Row().Put(new Cell(0, 1, 0), "InteriorLight/LargeBlockLight_1corner", functional: true);
plan = Plan(
    lit,
    "set:0,1,0:LargeBlockLight_1corner:enabled=0;set:0,1,0:InteriorLight/LargeBlockLight_1corner:name=A"
);
Check(plan.Steps?.Count == 2, "settings of a light: " + plan.Error);
plan = Plan(lit, "set:0,0,0:LargeBlockArmorBlock:name=A");
Check(plan.Error?.Contains("has no name") == true, "armor has no name");

// Adds
plan = Plan(Row(), "add:5,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up");
Check(plan.Steps?.Count == 1 && plan.Steps[0].New != null, "an add next to the row: " + plan.Error);
plan = Plan(Row(), "add:0,5,0:CubeBlock/LargeBlockArmorBlock:Forward:Up");
Check(plan.Error?.Contains("no mount neighbor") == true, "an add out in the open is refused");
plan = Plan(
    Row(),
    "add:0,1,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;add:0,2,0:CubeBlock/LargeBlockArmorBlock:Forward:Up"
);
Check(plan.Steps?.Count == 2, "an add mounted on another add: " + plan.Error);
plan = Plan(Row(), "add:3,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up");
Check(plan.Error?.Contains("is taken by") == true, "an add on a taken cell is refused");
plan = Plan(
    Row(),
    "del:4,0,0:LargeBlockArmorBlock;add:4,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up"
);
Check(plan.Steps?.Count == 2, "an add on a cell an earlier op freed: " + plan.Error);
plan = Plan(Row(), "add:5,0,0:CubeBlock/Nothing:Forward:Up");
Check(
    plan.Error?.Contains("no block definition") == true,
    "an unknown block definition is refused"
);
plan = Plan(
    Row(),
    "add:5,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;del:5,0,0:LargeBlockArmorBlock"
);
Check(
    plan.Error?.Contains("this patch adds") == true,
    "an op on a block the patch adds is refused"
);
var unmountable = Row();
unmountable.NoMount.Add(new Cell(4, 0, 0));
plan = Plan(unmountable, "add:5,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up");
Check(
    plan.Error?.Contains("no mount neighbor") == true,
    "a neighbor without a mount point doesn't count"
);

// Swaps keep the grid in one piece when the new block mounts where the old one did
plan = Plan(Row(), "swap:2,0,0:LargeBlockArmorBlock:CubeBlock/LargeBlockArmorSlope");
Check(
    plan.Steps?.Count == 1 && plan.Steps[0].Old != null && plan.Steps[0].New != null,
    "a swap in the row's middle: " + plan.Error
);
var cut = Row();
cut.NoMountNew = true;
plan = Plan(cut, "swap:2,0,0:LargeBlockArmorBlock:CubeBlock/LargeBlockArmorSlope");
Check(plan.Steps == null, "a swap whose new block doesn't mount is refused: " + plan.Error);

// Islands: the row and one lone block not joined to it (as the floor plan signs on the bench)
FakeGrid Islanded() => Row().Put(new Cell(0, 5, 0), "TextPanel/FloorPlanSign");

plan = Plan(Islanded(), "del:4,0,0:LargeBlockArmorBlock");
Check(
    plan.Steps == null
        && plan.Error.Contains("1 groups")
        && plan.Error.Contains("FloorPlanSign@0,5,0"),
    "a removal on a grid with an island is refused, naming it: " + plan.Error
);
plan = Plan(Islanded(), "del:0,5,0:FloorPlanSign;del:4,0,0:LargeBlockArmorBlock");
Check(
    plan.Steps?.Count == 2,
    "a removal passes when the patch removes the island too: " + plan.Error
);
plan = Plan(
    Islanded(),
    "add:0,4,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;add:0,3,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;"
        + "add:0,2,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;add:0,1,0:CubeBlock/LargeBlockArmorBlock:Forward:Up;del:4,0,0:LargeBlockArmorBlock"
);
Check(
    plan.Steps?.Count == 5,
    "a removal passes when the patch joins the island back: " + plan.Error
);
plan = Plan(Islanded(), "set:0,5,0:FloorPlanSign:name=Map");
Check(plan.Steps?.Count == 1, "a patch without a removal splits nothing: " + plan.Error);

// Vetoes: a docked ship, a running shipyard order
var docked = Row();
docked.Vetoed[new Cell(4, 0, 0)] = "a ship is docked to the connector";
plan = Plan(docked, "del:4,0,0:LargeBlockArmorBlock");
Check(plan.Error?.Contains("docked") == true, "a veto refuses the op: " + plan.Error);
plan = Plan(docked, "del:0,0,0:LargeBlockArmorBlock;del:4,0,0:LargeBlockArmorBlock");
Check(plan.Steps == null && plan.Error.StartsWith("op 2"), "and the whole patch");
docked.VetoNew = "the shipyard has an order";
plan = Plan(docked, "add:5,0,0:CubeBlock/LargeBlockArmorBlock:Forward:Up");
Check(
    plan.Error?.Contains("shipyard has an order") == true,
    "a veto on a new block refuses the add"
);

// The island check itself
var islanded = Islanded();
var groups = PatchPlan.Groups(islanded.Blocks(), islanded.Neighbours);
Check(
    groups.Count == 2 && groups[0].Count == 5 && groups[1].Count == 1,
    "groups: the row and the lone sign"
);
Check(
    PatchPlan.Describe(groups) == "1 from TextPanel/FloorPlanSign@0,5,0",
    PatchPlan.Describe(groups)
);
var one = Row();
var none = PatchPlan.Groups(one.Blocks(), one.Neighbours);
Check(none.Count == 1, "a row is one group");

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;

/// <summary>1x1x1 blocks joined by mount points across every face, except where NoMount says.</summary>
sealed class FakeGrid : IPatchGrid
{
    private readonly Dictionary<Cell, PatchBlock> blocks = new();
    public readonly HashSet<Cell> NoMount = [];
    public readonly Dictionary<Cell, string> Vetoed = new();
    public bool NoMountNew;
    public string VetoNew;

    public FakeGrid Line(int from, int to)
    {
        for (var x = from; x <= to; x++)
            Put(new Cell(x, 0, 0), "CubeBlock/LargeBlockArmorBlock");
        return this;
    }

    public FakeGrid Put(Cell at, string type, bool functional = false)
    {
        blocks[at] = new PatchBlock
        {
            Min = at,
            Cells = [at],
            Type = type,
            Terminal = functional || type.StartsWith("TextPanel"),
            Functional = functional,
        };
        return this;
    }

    public PatchBlock BlockAt(Cell cell) => blocks.GetValueOrDefault(cell);

    public IEnumerable<PatchBlock> Blocks() => blocks.Values;

    public IEnumerable<PatchBlock> Neighbours(PatchBlock block) =>
        NoMount.Contains(block.Min)
            ? []
            : Cell
                .Directions.Select(d => BlockAt(block.Min + d))
                .Where(b => b != null && !NoMount.Contains(b.Min));

    public PatchBlock NewBlock(PatchOp op, PatchBlock replaced, out string error)
    {
        error = null;
        if (op.Block.EndsWith("/Nothing"))
        {
            error = $"no block definition {op.Block}";
            return null;
        }
        var at = replaced?.Min ?? op.At;
        return new PatchBlock
        {
            Min = at,
            Cells = [at],
            Type = op.Block,
        };
    }

    public bool Mounts(PatchBlock added, PatchBlock other) =>
        !NoMountNew && !NoMount.Contains(other.Min);

    public string Veto(PatchOp op, PatchBlock block) =>
        Vetoed.GetValueOrDefault(block.Min) ?? (blocks.ContainsValue(block) ? null : VetoNew);
}
