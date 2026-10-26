using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ServerPlugin;

// Checks of the support arm's path planning (ServerPlugin/Builds/ArmPlanner.cs) on made-up
// shipyards: the pipes from the rotor head around the ship to a projector next to it, never
// through the ship or the shipyard, the turn cost, docking at the ship's connector, the cap
// without one, giving up the lightest armor block, and no plan when nothing is reachable, with
// the reason Explain gives.
// Prints one line per failed check and a summary; exit code 0 = all pass.
// Usage: dotnet run -c Release --project tests/arm/ArmTests

int checks = 0;
int failures = 0;

void Check(bool ok, string what)
{
    checks++;
    if (ok)
        return;
    failures++;
    Console.WriteLine("FAIL " + what);
}

Cell C(int x, int y, int z) => new(x, y, z);
var up = C(0, 1, 0);

// A box of ship blocks, every outer face buildable from a projector
HashSet<Cell> Box(Cell min, Cell max)
{
    var cells = new HashSet<Cell>();
    for (var x = min.X; x <= max.X; x++)
    for (var y = min.Y; y <= max.Y; y++)
    for (var z = min.Z; z <= max.Z; z++)
        cells.Add(C(x, y, z));
    return cells;
}

Dictionary<Cell, Cell[]> AllFaces(HashSet<Cell> volume) =>
    volume.ToDictionary(c => c, c => Cell.Directions.Where(d => !volume.Contains(c + d)).ToArray());

ArmProblem Problem(HashSet<Cell> volume, Func<Cell, bool> blocked = null) =>
    new()
    {
        Min = C(-20, -2, -20),
        Max = C(20, 30, 20),
        Volume = volume,
        Touches = AllFaces(volume),
        Blocked = blocked ?? (_ => false),
        Start = C(0, 0, 0),
        Up = up,
    };

// The plan is a chain of blocks whose faces meet, outside the ship and the obstacles, with the
// projector next to the block it builds first
void Valid(ArmPlan plan, ArmProblem p, string name)
{
    if (plan == null)
    {
        Check(false, name + ": no plan");
        return;
    }
    var at = plan.Blocks.ToDictionary(b => b.Cell);
    Check(at.Count == plan.Blocks.Count, name + ": one block a cell");
    Check(
        plan.Blocks[0].Piece == ArmPiece.End && plan.Blocks[0].Cell == p.Start,
        name + ": starts with the pipe end"
    );
    Check(plan.Blocks.Count(b => b.Piece == ArmPiece.Tee) == 1, name + ": one tee");
    foreach (var b in plan.Blocks)
    {
        var projectorInShip = b.Piece == ArmPiece.Projector && plan.GivenUp == b.Cell;
        Check(
            projectorInShip || !p.Volume.Contains(b.Cell),
            $"{name}: {b.Piece} at {b.Cell} is outside the ship"
        );
        Check(!p.Blocked(b.Cell), $"{name}: {b.Piece} at {b.Cell} is not in an obstacle");
        foreach (var face in b.Faces)
        {
            var neighbor = b.Cell + face;
            if (b.Piece == ArmPiece.End && face == -p.Up)
                continue;
            if (b.Piece == ArmPiece.Tee && !at.ContainsKey(neighbor))
                continue;
            Check(
                at.TryGetValue(neighbor, out var other) && other.Faces.Contains(-face),
                $"{name}: {b.Piece} at {b.Cell} meets its neighbor at {neighbor}"
            );
        }
    }
    var projector = plan.Projector;
    Check(
        projector.Cell + projector.Front == plan.Touch,
        name + ": the projector faces its first block"
    );
    Check(
        p.Volume.Contains(plan.Touch) && plan.Touch != plan.GivenUp,
        name + ": the first block is the ship's"
    );
    Check(
        p.Touches.TryGetValue(plan.Touch, out var faces)
            && faces.Contains(projector.Cell - plan.Touch),
        name + ": the first block can be built from the projector"
    );
}

int Turns(ArmPlan plan) => plan.Blocks.Count(b => b.Piece == ArmPiece.Corner);

int Pipes(ArmPlan plan) =>
    plan.Blocks.Count(b =>
        b.Piece is ArmPiece.End or ArmPiece.Straight or ArmPiece.Corner or ArmPiece.Tee
    );

// 1. A ship straight above the head: a straight arm, no turns
{
    var p = Problem(Box(C(-2, 10, -2), C(2, 14, 2)));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "straight up");
    Check(plan != null && Turns(plan) == 0, "straight up: no turns");
    Check(
        plan != null && plan.Connector == null && plan.GivenUp == null,
        "straight up: cap, nothing given up"
    );
    Check(
        plan != null && plan.Blocks.Any(b => b.Piece == ArmPiece.Cap),
        "straight up: a cap on the tee"
    );
    // Pipe end at 0, pipes up to the tee at 8, projector at 9 under the ship's 10
    Check(
        plan != null && plan.Projector.Cell == C(0, 9, 0) && Pipes(plan) == 9,
        "straight up: shortest"
    );
}

// 2. A ship off to the side: one turn, and the turn cost picks fewer turns over a staircase
{
    var p = Problem(Box(C(10, 10, -2), C(14, 14, 2)));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "to the side");
    Check(plan != null && Turns(plan) <= 1, "to the side: at most one turn");
    p.TurnCost = 0;
    var cheapTurns = ArmPlanner.Plan(p);
    Valid(cheapTurns, p, "to the side, free turns");
    Check(
        plan != null && cheapTurns != null && Pipes(cheapTurns) <= Pipes(plan),
        "to the side: free turns are never longer"
    );
}

// 3. A wall between the head and the ship: the arm goes around it, never through it or the ship
{
    var wall = Box(C(-6, 5, -6), C(6, 5, 6));
    var p = Problem(Box(C(-2, 10, -2), C(2, 14, 2)), wall.Contains);
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "around a wall");
    Check(plan != null && Turns(plan) >= 2, "around a wall: turns around it");
}

// 4. A connector on the ship's side facing +X: the arm docks there
{
    var volume = Box(C(-2, 10, -2), C(2, 14, 2));
    var p = Problem(volume);
    p.Connectors.Add((C(2, 12, 0), C(1, 0, 0)));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "connector");
    Check(
        plan != null && plan.Connector == C(2, 12, 0),
        "connector: docks at the ship's connector"
    );
    var connector = plan?.Blocks.FirstOrDefault(b => b.Piece == ArmPiece.Connector);
    Check(
        connector != null && connector.Cell == C(3, 12, 0) && connector.Front == C(-1, 0, 0),
        "connector: the arm's connector faces the ship's"
    );
    Check(
        plan != null && plan.Blocks.Any(b => b.Piece == ArmPiece.Support),
        "connector: the projector hangs on a support"
    );
    Check(plan != null && !plan.Blocks.Any(b => b.Piece == ArmPiece.Cap), "connector: no cap");
}

// 5. The first connector is blocked in front, the second is used; with both blocked, a cap
{
    var volume = Box(C(-2, 10, -2), C(2, 14, 2));
    var blocked = new HashSet<Cell> { C(3, 12, 0), C(-3, 12, 0) };
    var p = Problem(volume, blocked.Contains);
    p.Connectors.Add((C(2, 12, 0), C(1, 0, 0)));
    p.Connectors.Add((C(0, 12, 2), C(0, 0, 1)));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "second connector");
    Check(plan != null && plan.Connector == C(0, 12, 2), "second connector: the reachable one");
    p.Connectors.RemoveAt(1);
    p.Connectors.Add((C(-2, 12, 0), C(-1, 0, 0)));
    plan = ArmPlanner.Plan(p);
    Valid(plan, p, "no reachable connector");
    Check(plan != null && plan.Connector == null, "no reachable connector: a cap instead");
}

// 5b. A connector sticking out of the ship, nothing beside it: no projector spot next to the
// arm's connector touches the ship, so the arm ends in a cap
{
    var volume = Box(C(-2, 10, -2), C(2, 14, 2));
    volume.Add(C(3, 12, 0));
    var p = Problem(volume);
    p.Connectors.Add((C(3, 12, 0), C(1, 0, 0)));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "sticking out");
    Check(plan != null && plan.Connector == null, "sticking out: a cap instead");
}

// 6. No face of the ship can be built from a projector: the lightest armor block gives way,
//    even when a heavier one is closer
{
    var volume = Box(C(-2, 10, -2), C(2, 14, 2));
    var p = Problem(volume);
    p.Touches = volume.ToDictionary(
        c => c,
        c => Cell.Directions.Where(d => volume.Contains(c + d)).ToArray()
    );
    p.GiveUps.Add((C(0, 10, 0), 300)); // right above the head, heavy armor
    p.GiveUps.Add((C(2, 14, 2), 100)); // the far corner, light armor
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "give up");
    Check(plan != null && plan.GivenUp == C(2, 14, 2), "give up: the lightest block");
    Check(
        plan != null && plan.Projector.Cell == C(2, 14, 2),
        "give up: the projector takes its place"
    );
    p.GiveUps.Clear();
    Check(ArmPlanner.Plan(p) == null, "give up: none allowed, no plan");
}

// 7. The head is walled in: no plan; the pipe end can't turn on the head
{
    var p = Problem(Box(C(-2, 10, -2), C(2, 14, 2)), c => c == C(0, 1, 0));
    Check(ArmPlanner.Plan(p) == null, "walled in: no plan");
    p = Problem(Box(C(-2, 10, -2), C(2, 14, 2)), c => c == C(0, -1, 0));
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "pipe end");
    Check(
        plan != null && plan.Blocks[1].Cell == C(0, 1, 0),
        "pipe end: the line leaves the head straight"
    );
}

// 8. A ship around the head's line (a ring): the arm goes around, not through the hole's walls
{
    var ring = Box(C(-4, 10, -4), C(4, 12, 4));
    ring.ExceptWith(Box(C(-1, 10, -1), C(1, 12, 1)));
    var p = Problem(ring);
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "ring");
}

// 9. A large shipyard-sized search ends quickly
{
    var volume = Box(C(-10, 20, -30), C(10, 30, 30));
    var p = Problem(volume);
    p.Min = C(-30, -5, -60);
    p.Max = C(30, 40, 60);
    p.Connectors.Add((C(10, 25, 0), C(1, 0, 0)));
    var watch = Stopwatch.StartNew();
    var plan = ArmPlanner.Plan(p);
    Valid(plan, p, "large");
    Check(watch.ElapsedMilliseconds < 5000, $"large: planned in {watch.ElapsedMilliseconds} ms");
    Console.WriteLine(
        $"large: {plan?.Blocks.Count} blocks, cost {plan?.Cost}, {watch.ElapsedMilliseconds} ms"
    );
}

// 10. Explain names why there is no plan
{
    var ship = Box(C(-2, 10, -2), C(2, 12, 2));
    var p = Problem(ship, c => c == C(0, 0, 0));
    Check(ArmPlanner.Explain(p).Contains("taken by the shipyard"), "explain: start taken");
    p = Problem(Box(C(-1, -1, -1), C(1, 1, 1)));
    Check(ArmPlanner.Explain(p).Contains("volume covers"), "explain: start in the ship");
    p = Problem(ship);
    p.Start = C(0, 40, 0);
    Check(ArmPlanner.Explain(p).Contains("outside"), "explain: start outside the box");
    // Walled in: the pipe end and the cell above it free, everything around them taken
    var walls = Box(C(-1, 0, -1), C(1, 3, 1));
    walls.ExceptWith([C(0, 1, 0), C(0, 2, 0)]);
    p = Problem(ship, walls.Contains);
    p.Start = C(0, 1, 0);
    var why = ArmPlanner.Explain(p);
    Check(why.Contains("reaches 2 free cells, 0 of "), $"explain: walled in: {why}");
}

Console.WriteLine($"{checks - failures}/{checks} arm planner checks passed");
return failures == 0 ? 0 : 1;
