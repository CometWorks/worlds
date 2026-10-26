using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRageMath;

namespace ServerPlugin;

// The support arm of a shipyard build: one of the shipyard's arm rotors (ShipyardArmRotors)
// gets pipes from its rotor head to a new projector next to where the ship will be, routed
// around the ship's volume by ArmPlanner. The ship's main grid is built onto the rotor head's
// grid, starting from the block next to the projector, so the head's grid size must be the
// ship's. With a connector of the ship in reach, the arm docks to it; else the branch of its
// last pipe ends in a cap. The pieces are ShipyardArmLargeBlocks or ShipyardArmSmallBlocks.
internal static class ShipyardArm
{
    // The integrity of a block just placed: a frame with its first component
    private const float FrameIntegrity = 1.5259022E-05f;

    private static readonly Dictionary<string, Layout> cache = new();

    private static OctahedronConfig Config => Plugin.Config;

    /// <summary>An arm planned for a blueprint, ready to place.</summary>
    internal sealed class Layout
    {
        public MyMotorStator Rotor;
        public MyCubeGrid Grid;
        public ArmPlan Plan;

        // Blueprint cells to the rotor head's grid
        public MatrixI ToArm;

        // The arm's blocks, frames, in the order to place them; the projector last
        public List<MyObjectBuilder_CubeBlock> Blocks = [];

        // The blueprint for the projector: the first block next to it, the given up one left out
        public List<MyObjectBuilder_CubeGrid> Projection;
        public MyObjectBuilder_CubeBlock GivenUp;
        public Vector3I Offset;
        public Dictionary<MyDefinitionId, int> Components = new();

        public MyObjectBuilder_Projector Projector => (MyObjectBuilder_Projector)Blocks.Last();
    }

    /// <summary>The shipyard's arm rotors with a rotor head.</summary>
    public static List<MyMotorStator> Rotors(MyCubeGrid shipyard)
    {
        var names = Names(Config.ShipyardArmRotors);
        return shipyard
            .GetFatBlocks<MyMotorStator>()
            .Where(r => r.TopGrid != null && names.Contains(r.CustomName?.ToString() ?? ""))
            .ToList();
    }

    private static HashSet<string> Names(string list) =>
        new(
            list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase
        );

    /// <summary>
    /// The cheapest arm of any rotor for the blueprint, built in the shipyard's free space with
    /// its main grid's orientation in the world; null with the reason in error.
    /// </summary>
    public static Layout Plan(
        MyCubeGrid shipyard,
        MyOrientedBoundingBoxD space,
        MatrixD orientation,
        List<MyObjectBuilder_CubeGrid> blueprint,
        out string error
    )
    {
        var key =
            $"{shipyard.EntityId}|{Config.ShipyardArmTurnCost}|{Signature(blueprint)}|{space.Center:F1}";
        if (cache.TryGetValue(key, out var cached) && cached.Grid?.MarkedForClose == false)
        {
            error = null;
            return cached;
        }
        error =
            $"{shipyard.DisplayName} has no support arm for a {blueprint[0].GridSizeEnum.ToString().ToLowerInvariant()} grid ship.";
        Layout best = null;
        foreach (var rotor in Rotors(shipyard))
        {
            if (rotor.TopGrid.GridSizeEnum != blueprint[0].GridSizeEnum)
                continue;
            var layout = PlanFor(shipyard, rotor, space, orientation, blueprint, out var why);
            if (layout == null)
            {
                Note(rotor.EntityId, $"Shipyard arm on '{rotor.CustomName}': {why}");
                error = why;
                continue;
            }
            if (best == null || Rank(layout.Plan).CompareTo(Rank(best.Plan)) < 0)
                best = layout;
        }
        if (best == null)
            return null;
        error = null;
        if (cache.Count > 32)
            cache.Clear();
        cache[key] = best;
        return best;
    }

    private static (int, double) Rank(ArmPlan plan) =>
        (
            plan.Connector != null ? 0
            : plan.GivenUp == null ? 1
            : 2,
            plan.Cost
        );

    private static string Signature(List<MyObjectBuilder_CubeGrid> blueprint)
    {
        var hash = 17L;
        foreach (var grid in blueprint)
        foreach (var block in grid.CubeBlocks)
            hash =
                hash * 31
                + block.SubtypeName.GetHashCode()
                + ((Vector3I)block.Min).GetHashCode() * 7
                + block.BlockOrientation.GetHashCode();
        return $"{blueprint.Count}:{blueprint.Sum(g => g.CubeBlocks.Count)}:{hash}";
    }

    private static Layout PlanFor(
        MyCubeGrid shipyard,
        MyMotorStator rotor,
        MyOrientedBoundingBoxD space,
        MatrixD orientation,
        List<MyObjectBuilder_CubeGrid> blueprint,
        out string error
    )
    {
        var grid = rotor.TopGrid;
        var head = rotor.TopBlock;
        var size = grid.GridSize;
        var armRotation = grid.WorldMatrix.GetOrientation();

        // The ship's main grid turned onto the head's grid, by right angles only
        var turn = orientation * MatrixD.Transpose(armRotation);
        var forward = Base6Directions.GetClosestDirection((Vector3)turn.Forward);
        var up = Base6Directions.GetClosestDirection((Vector3)turn.Up);
        if (
            Base6Directions.GetAxis(forward) == Base6Directions.GetAxis(up)
            || Vector3D.Dot(turn.Forward, Base6Directions.GetVector(forward)) < 0.99
            || Vector3D.Dot(turn.Up, Base6Directions.GetVector(up)) < 0.99
        )
        {
            error = $"{rotor.CustomName} is not turned by right angles to {shipyard.DisplayName}.";
            return null;
        }
        var shipOrientation = new MyBlockOrientation(forward, up);
        shipOrientation.GetMatrix(out Matrix turnMatrix);
        var world = (MatrixD)turnMatrix * armRotation;
        var bounds = TableHologram.Bounds(blueprint);
        var origin = grid.WorldToGridInteger(
            space.Center - Vector3D.TransformNormal(bounds.Center, world)
        );
        var toArm = new MatrixI(origin, forward, up);

        var main = blueprint[0];
        var problem = new ArmProblem { TurnCost = Config.ShipyardArmTurnCost };

        // The ship's volume: every block of every grid, the subgrids where the blueprint has them
        var toMain = MatrixD.Invert(GridMatrix(main));
        var toArmCells =
            MatrixD.CreateScale(1 / MyDefinitionManager.Static.GetCubeSize(main.GridSizeEnum))
            * (MatrixD)turnMatrix
            * MatrixD.CreateTranslation((Vector3D)origin);
        foreach (var g in blueprint)
        {
            var toCells = GridMatrix(g) * toMain * toArmCells;
            var cube = MyDefinitionManager.Static.GetCubeSize(g.GridSizeEnum);
            foreach (var block in g.CubeBlocks)
                if (Box(block, out var min, out var max, out _))
                    Mark(problem.Volume, min, max, cube, toCells);
        }

        // The shipyard and the grids on its rotors and pistons; the rotor head's grid itself
        var blocked = new HashSet<Cell>();
        var stator = new HashSet<Cell>();
        var toHead = grid.PositionComp.WorldMatrixNormalizedInv * MatrixD.CreateScale(1 / size);
        foreach (var other in Grids(shipyard))
        {
            if (other == grid)
                continue;
            var toCells = other.WorldMatrix * toHead;
            foreach (var block in other.GetBlocks())
                Mark(
                    block == rotor.SlimBlock ? stator : blocked,
                    block.Min,
                    block.Max,
                    other.GridSize,
                    toCells
                );
        }
        foreach (var block in grid.GetBlocks())
            for (var x = block.Min.X; x <= block.Max.X; x++)
            for (var y = block.Min.Y; y <= block.Max.Y; y++)
            for (var z = block.Min.Z; z <= block.Max.Z; z++)
                blocked.Add(new Cell(x, y, z));
        problem.Blocked = blocked.Contains;

        // The pipe end goes on the head's top, in the middle
        var upward = Base6Directions.GetIntVector(head.Orientation.Up);
        var start = (head.Min + head.Max) / 2;
        while (start.IsInsideInclusiveEnd(head.Min, head.Max))
            start += upward;
        problem.Start = C(start);
        problem.Up = C(upward);
        // An advanced rotor's head sits inside its stator's two cells, so the pipe end's cell
        // is in the stator's box; the game builds it there ("... Arm Exam" blueprint)
        stator.Remove(problem.Start);
        blocked.UnionWith(stator);

        // Search around the ship and the head, with room to go around
        var margin = (int)Math.Ceiling(Math.Max(4 * size, 10) / size);
        var lo = problem.Start;
        var hi = problem.Start;
        foreach (var c in problem.Volume)
        {
            lo = new Cell(Math.Min(lo.X, c.X), Math.Min(lo.Y, c.Y), Math.Min(lo.Z, c.Z));
            hi = new Cell(Math.Max(hi.X, c.X), Math.Max(hi.Y, c.Y), Math.Max(hi.Z, c.Z));
        }
        problem.Min = lo - new Cell(margin, margin, margin);
        problem.Max = hi + new Cell(margin, margin, margin);

        // Where the projector can start the main grid, its connectors and armor to give up
        var projectorDefinition = Definition(ArmPiece.Projector, grid.GridSizeEnum);
        var connectorDefinition = Definition(ArmPiece.Connector, grid.GridSizeEnum);
        if (projectorDefinition == null)
        {
            error = $"No projector block for a {grid.GridSizeEnum} grid arm in the config.";
            return null;
        }
        var giveUps = Patterns(Config.ShipyardArmGiveUpBlocks);
        var connectors = new List<(Cell Cell, Cell Front, int Reach)>();
        var cargo = Cargo(main);
        foreach (var block in main.CubeBlocks)
        {
            if (!Box(block, out var min, out var max, out var definition) || min != max)
                continue;
            var blockOrientation = Orientation(block);
            var cell = C(Vector3I.Transform(min, toArm));
            var faces = new List<Cell>();
            foreach (var d in Base6Directions.EnumDirections)
                if (
                    Meets(
                        definition,
                        blockOrientation,
                        projectorDefinition,
                        MyBlockOrientation.Identity,
                        d
                    )
                )
                    faces.Add(
                        C(Vector3I.TransformNormal(Base6Directions.GetIntVector(d), ref toArm))
                    );
            if (faces.Count > 0)
                problem.Touches[cell] = faces.ToArray();
            if (
                block is MyObjectBuilder_ShipConnector
                && connectorDefinition != null
                && main.GridSizeEnum == grid.GridSizeEnum
            )
            {
                var front = Base6Directions.GetIntVector(blockOrientation.Forward);
                connectors.Add((cell, C(Vector3I.TransformNormal(front, ref toArm)), cargo(min)));
            }
            if (giveUps != null && giveUps.IsMatch(TypeName(block)))
                problem.GiveUps.Add((cell, definition.Mass));
        }
        problem.Connectors = connectors
            .OrderByDescending(c => c.Reach)
            .Select(c => (c.Cell, c.Front))
            .ToList();
        problem.GiveUps = problem.GiveUps.OrderBy(g => g.Mass).ToList();

        var plan = ArmPlanner.Plan(problem);
        if (plan == null)
        {
            error = $"{shipyard.DisplayName} can't reach the ship with a support arm.";
            var offset = Vector3D.TransformNormal(
                head.WorldMatrix.Translation - rotor.WorldMatrix.Translation,
                MatrixD.Transpose(rotor.WorldMatrix)
            );
            Note(
                -rotor.EntityId,
                $"Shipyard arm on '{rotor.CustomName}' of {shipyard.DisplayName}: {ArmPlanner.Explain(problem)}; "
                    + $"head {offset:F2} m from the rotor in its axes; "
                    + $"pipe end cell taken by {Taker(shipyard, rotor, toHead, start) ?? "nothing"}"
            );
            return null;
        }
        var layout = new Layout
        {
            Rotor = rotor,
            Grid = grid,
            Plan = plan,
            ToArm = toArm,
        };
        error = Blocks(layout, shipOrientation);
        if (error != null)
            return null;
        Projection(layout, blueprint);
        return layout;
    }

    // The arm's frames, each turned so that its mount points face its neighbors in the arm
    private static string Blocks(Layout layout, MyBlockOrientation ship)
    {
        var size = layout.Grid.GridSizeEnum;
        foreach (var arm in layout.Plan.Blocks.OrderBy(b => b.Piece == ArmPiece.Projector ? 1 : 0))
        {
            var definition = Definition(arm.Piece, size);
            if (definition == null)
                return $"No {arm.Piece} block for a {size} grid arm in the config.";
            MyBlockOrientation? orientation = arm.Piece switch
            {
                ArmPiece.Projector => ship,
                ArmPiece.Support => MyBlockOrientation.Identity,
                ArmPiece.Connector => Facing(arm.Front),
                _ => Fitting(definition, arm.Faces),
            };
            if (orientation == null)
                return $"The {definition.DisplayNameText} can't be turned to fit the arm.";
            var block = (MyObjectBuilder_CubeBlock)
                MyObjectBuilderSerializer.CreateNewObject(definition.Id);
            block.Min = V(arm.Cell);
            block.BlockOrientation = new SerializableBlockOrientation(
                orientation.Value.Forward,
                orientation.Value.Up
            );
            block.IntegrityPercent = block.BuildPercent = FrameIntegrity;
            block.ShareMode = MyOwnershipShareModeEnum.None;
            if (block is MyObjectBuilder_FunctionalBlock functional)
                functional.Enabled = false;
            layout.Blocks.Add(block);
            foreach (var component in definition.Components)
            {
                layout.Components.TryGetValue(component.Definition.Id, out var count);
                layout.Components[component.Definition.Id] = count + component.Count;
            }
        }
        return null;
    }

    // The blueprint for the projector, and its offset: the projection's first block lands on
    // the projector's cell minus the offset, in the projector's own directions
    private static void Projection(Layout layout, List<MyObjectBuilder_CubeGrid> blueprint)
    {
        var projection = blueprint.Select(g => (MyObjectBuilder_CubeGrid)g.Clone()).ToList();
        var main = projection[0];
        MatrixI.Invert(ref layout.ToArm, out var fromArm);
        MyObjectBuilder_CubeBlock At(Cell cell)
        {
            var min = Vector3I.Transform(V(cell), fromArm);
            return main.CubeBlocks.First(b => (Vector3I)b.Min == min);
        }
        var plan = layout.Plan;
        if (plan.GivenUp != null)
        {
            layout.GivenUp = At(plan.GivenUp.Value);
            main.CubeBlocks.Remove(layout.GivenUp);
        }
        var first = At(plan.Touch);
        main.CubeBlocks.Remove(first);
        main.CubeBlocks.Insert(0, first);
        layout.Projection = projection;

        var projector = plan.Projector;
        var toFirst = Base6Directions.GetDirection(V(-projector.Front));
        var ship = layout.Projector.BlockOrientation;
        layout.Offset = Base6Directions.GetIntVector(
            new MyBlockOrientation(ship.Forward, ship.Up).TransformDirectionInverse(toFirst)
        );
    }

    /// <summary>
    /// Places the arm's frames on the rotor head's grid, owned by npc, the projector with the
    /// blueprint loaded but switched off; the head's grid becomes static for the build.
    /// Returns the projector.
    /// </summary>
    public static MyProjectorBase Place(Layout layout, long npc, string name, string data)
    {
        var grid = layout.Grid;
        var projector = layout.Projector;
        projector.CustomName = name;
        projector.ProjectedGrids = layout
            .Projection.Select(g => (MyObjectBuilder_CubeGrid)g.Clone())
            .ToList();
        projector.ProjectionOffset = layout.Offset;
        projector.ProjectionRotation = Vector3I.Zero;
        projector.KeepProjection = true;
        if (!grid.IsStatic)
            MyMultiplayer.RaiseEvent(grid, x => x.ConvertToStatic);
        foreach (var template in layout.Blocks)
        {
            var block = (MyObjectBuilder_CubeBlock)template.Clone();
            block.Owner = npc;
            block.ColorMaskHSV = layout.Rotor.TopBlock.SlimBlock.ColorMaskHSV;
            block.SkinSubtypeId = layout.Rotor.TopBlock.SlimBlock.SkinSubtypeId.String;
            block.EntityId = VRage.MyEntityIdentifier.AllocateId();
            Build(grid, block, npc);
        }
        var placed = grid.GetCubeBlock(projector.Min)?.FatBlock as MyProjectorBase;
        if (placed != null)
            placed.CustomData = data;
        return placed;
    }

    private static readonly System.Reflection.MethodInfo buildBlockSuccess = AccessTools.Method(
        typeof(MyCubeGrid),
        "BuildBlockSuccess"
    );

    // The game's own path of a block built from an object builder, without the checks of a
    // player's request: the safe zone forbids building in the shipyard
    private static void Build(MyCubeGrid grid, MyObjectBuilder_CubeBlock block, long owner)
    {
        var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId());
        var orientation = new MyBlockOrientation(
            block.BlockOrientation.Forward,
            block.BlockOrientation.Up
        );
        orientation.GetQuaternion(out var rotation);
        Vector3I min = block.Min;
        var turn = new MatrixI(orientation);
        var max = min + Vector3I.Abs(Vector3I.TransformNormal(definition.Size - 1, ref turn));
        var location = new MyCubeGrid.MyBlockLocation(
            definition.Id,
            min,
            max,
            min,
            rotation,
            block.EntityId,
            owner
        );
        var visuals = new MyCubeGrid.MyBlockVisuals(
            ((Vector3)block.ColorMaskHSV).PackHSVToUint(),
            MyStringHash.GetOrCompute(block.SkinSubtypeId)
        );
        object[] args =
        [
            ColorExtensions.UnpackHSVFromUint(visuals.ColorMaskHSV),
            visuals.SkinId,
            location,
            block.Clone(),
            null,
            null,
            false,
            owner,
        ];
        buildBlockSuccess.Invoke(grid, args);
        MyMultiplayer.RaiseEvent(
            grid,
            x => x.BuildBlockSucess,
            visuals,
            location,
            (MyObjectBuilder_CubeBlock)block.Clone(),
            0L,
            false,
            owner
        );
    }

    // --- geometry ---

    private static Cell C(Vector3I v) => new(v.X, v.Y, v.Z);

    private static Vector3I V(Cell c) => new(c.X, c.Y, c.Z);

    private static MatrixD GridMatrix(MyObjectBuilder_CubeGrid grid) =>
        grid.PositionAndOrientation?.GetMatrix() ?? MatrixD.Identity;

    internal static List<MyCubeGrid> Grids(MyCubeGrid grid)
    {
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        return group == null ? [grid] : group.Nodes.Select(n => n.NodeData).ToList();
    }

    private static MyBlockOrientation Orientation(MyObjectBuilder_CubeBlock block) =>
        new(block.BlockOrientation.Forward, block.BlockOrientation.Up);

    // The cells of a blueprint block
    private static bool Box(
        MyObjectBuilder_CubeBlock block,
        out Vector3I min,
        out Vector3I max,
        out MyCubeBlockDefinition definition
    )
    {
        min = block.Min;
        max = min;
        if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out definition))
            return false;
        var turn = new MatrixI(Orientation(block));
        max = min + Vector3I.Abs(Vector3I.TransformNormal(definition.Size - 1, ref turn));
        return true;
    }

    private static readonly Dictionary<long, string> noted = [];

    // Logs why a rotor has no arm once, not on every replan of a shown quote
    private static void Note(long key, string text)
    {
        if (noted.TryGetValue(key, out var had) && had == text)
            return;
        noted[key] = text;
        Plugin.Log.Info(text);
    }

    // The block whose box takes the pipe end's cell c of the head's grid, for the log; the
    // arm's own stator may hold it
    private static string Taker(
        MyCubeGrid shipyard,
        MyMotorStator rotor,
        MatrixD toHead,
        Vector3I c
    )
    {
        foreach (var other in Grids(shipyard))
        foreach (var block in other.GetBlocks())
        {
            if (block == rotor.SlimBlock)
                continue;
            var cells = new HashSet<Cell>();
            Mark(cells, block.Min, block.Max, other.GridSize, other.WorldMatrix * toHead);
            if (cells.Contains(C(c)))
                return $"{block.BlockDefinition.Id.SubtypeName} at {block.Min} of "
                    + (other == rotor.TopGrid ? "the head's grid" : other.DisplayName);
        }
        return null;
    }

    // Adds the cells of the arm's grid a box of cells of another grid overlaps; toCells takes
    // the other grid's local meters to the arm grid's cells
    private static void Mark(
        HashSet<Cell> cells,
        Vector3I min,
        Vector3I max,
        double cube,
        MatrixD toCells
    )
    {
        var box = new BoundingBoxD(((Vector3D)min - 0.5) * cube, ((Vector3D)max + 0.5) * cube);
        var lo = new Vector3D(double.MaxValue);
        var hi = new Vector3D(double.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = Vector3D.Transform(box.GetCorner(i), toCells);
            lo = Vector3D.Min(lo, corner);
            hi = Vector3D.Max(hi, corner);
        }
        const double eps = 0.05;
        for (
            var x = (int)Math.Ceiling(lo.X - 0.5 + eps);
            x <= (int)Math.Floor(hi.X + 0.5 - eps);
            x++
        )
        for (
            var y = (int)Math.Ceiling(lo.Y - 0.5 + eps);
            y <= (int)Math.Floor(hi.Y + 0.5 - eps);
            y++
        )
        for (
            var z = (int)Math.Ceiling(lo.Z - 0.5 + eps);
            z <= (int)Math.Floor(hi.Z + 0.5 - eps);
            z++
        )
            cells.Add(new Cell(x, y, z));
    }

    // Do the mount points of block a, at its cell, and block b, in the next cell along the
    // grid's direction d, touch? Only blocks of one cell.
    private static bool Meets(
        MyCubeBlockDefinition a,
        MyBlockOrientation aOrientation,
        MyCubeBlockDefinition b,
        MyBlockOrientation bOrientation,
        Base6Directions.Direction d
    )
    {
        var normal = Base6Directions.GetIntVector(d);
        var ra = Rects(a, aOrientation, normal);
        var rb = Rects(b, bOrientation, -normal);
        const float eps = 0.01f;
        return ra.Any(p =>
            rb.Any(q =>
                p.Min.X < q.Max.X - eps
                && q.Min.X < p.Max.X - eps
                && p.Min.Y < q.Max.Y - eps
                && q.Min.Y < p.Max.Y - eps
            )
        );
    }

    // The block's mount points on the face of grid direction normal, in that face's two other
    // grid axes
    private static List<BoundingBox2> Rects(
        MyCubeBlockDefinition definition,
        MyBlockOrientation orientation,
        Vector3I normal
    )
    {
        orientation.GetMatrix(out Matrix m);
        var turn = new MatrixI(orientation);
        var rects = new List<BoundingBox2>();
        if (definition.MountPoints == null)
            return rects;
        foreach (var mount in definition.MountPoints)
        {
            if (!mount.Enabled || Vector3I.TransformNormal(mount.Normal, ref turn) != normal)
                continue;
            var s = Vector3.TransformNormal(mount.Start - 0.5f, m) + 0.5f;
            var e = Vector3.TransformNormal(mount.End - 0.5f, m) + 0.5f;
            Vector2 Flat(Vector3 v) =>
                normal.X != 0 ? new Vector2(v.Y, v.Z)
                : normal.Y != 0 ? new Vector2(v.X, v.Z)
                : new Vector2(v.X, v.Y);
            rects.Add(
                new BoundingBox2(Vector2.Min(Flat(s), Flat(e)), Vector2.Max(Flat(s), Flat(e)))
            );
        }
        return rects;
    }

    // An orientation whose mounted faces are exactly the given grid directions
    private static MyBlockOrientation? Fitting(MyCubeBlockDefinition definition, Cell[] faces)
    {
        var wanted = new HashSet<Vector3I>(faces.Select(V));
        var mounted = (definition.MountPoints ?? [])
            .Where(p => p.Enabled)
            .Select(p => p.Normal)
            .Distinct()
            .ToList();
        foreach (var forward in Base6Directions.EnumDirections)
        foreach (var up in Base6Directions.EnumDirections)
        {
            if (Base6Directions.GetAxis(forward) == Base6Directions.GetAxis(up))
                continue;
            var matrix = new MatrixI(forward, up);
            var turned = new HashSet<Vector3I>(
                mounted.Select(n => Vector3I.TransformNormal(n, ref matrix))
            );
            if (turned.SetEquals(wanted))
                return new MyBlockOrientation(forward, up);
        }
        return null;
    }

    // A block facing the grid direction, any way up
    private static MyBlockOrientation Facing(Cell front)
    {
        var forward = Base6Directions.GetDirection(V(front));
        var up = Base6Directions.EnumDirections.First(u =>
            Base6Directions.GetAxis(u) != Base6Directions.GetAxis(forward)
        );
        return new MyBlockOrientation(forward, up);
    }

    // The arm piece's block for the grid size, by the config
    internal static MyCubeBlockDefinition Definition(ArmPiece piece, MyCubeSize size)
    {
        var list =
            size == MyCubeSize.Large
                ? Config.ShipyardArmLargeBlocks
                : Config.ShipyardArmSmallBlocks;
        var subtype = list.Split(',')
            .Select(p => p.Split(['='], 2))
            .Where(p =>
                p.Length == 2
                && p[0].Trim().Equals(piece.ToString(), StringComparison.OrdinalIgnoreCase)
            )
            .Select(p => p[1].Trim())
            .FirstOrDefault();
        if (string.IsNullOrEmpty(subtype))
            return null;
        return MyDefinitionManager
            .Static.GetAllDefinitions()
            .OfType<MyCubeBlockDefinition>()
            .FirstOrDefault(d =>
                d.CubeSize == size && d.Id.SubtypeName == subtype && d.Size == Vector3I.One
            );
    }

    // The blocks of the ship's main grid each block reaches through conveyors, approximated by
    // the blocks with an inventory or a conveyor port touching each other
    private static Func<Vector3I, int> Cargo(MyObjectBuilder_CubeGrid main)
    {
        var at = new Dictionary<Vector3I, int>();
        var count = 0;
        foreach (var block in main.CubeBlocks)
        {
            if (!CargoTypes.Contains(block.TypeId.ToString().Replace("MyObjectBuilder_", "")))
                continue;
            if (!Box(block, out var min, out var max, out _))
                continue;
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
                at[new Vector3I(x, y, z)] = count;
            count++;
        }
        var reach = new Dictionary<Vector3I, int>();
        return cell =>
        {
            if (!at.ContainsKey(cell))
                return 0;
            if (reach.TryGetValue(cell, out var known))
                return known;
            var seen = new HashSet<Vector3I> { cell };
            var blocks = new HashSet<int>();
            var queue = new Queue<Vector3I>([cell]);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                blocks.Add(at[c]);
                foreach (var d in Base6Directions.IntDirections)
                    if (at.ContainsKey(c + d) && seen.Add(c + d))
                        queue.Enqueue(c + d);
            }
            foreach (var c in seen)
                reach[c] = blocks.Count;
            return blocks.Count;
        };
    }

    private static readonly HashSet<string> CargoTypes =
    [
        "CargoContainer",
        "Conveyor",
        "ConveyorConnector",
        "ConveyorSorter",
        "ShipConnector",
        "OxygenTank",
        "OxygenGenerator",
        "Assembler",
        "Refinery",
        "SurvivalKit",
        "Reactor",
        "Cockpit",
        "CryoChamber",
        "Collector",
        "ShipWelder",
        "ShipGrinder",
        "Drill",
        "HydrogenEngine",
        "SmallMissileLauncher",
        "SmallMissileLauncherReload",
        "SmallGatlingGun",
        "LargeGatlingTurret",
        "LargeMissileTurret",
        "InteriorTurret",
        "Parachute",
    ];

    private static string TypeName(MyObjectBuilder_CubeBlock block) =>
        $"{block.TypeId.ToString().Replace("MyObjectBuilder_", "")}/{block.SubtypeName}";

    private static System.Text.RegularExpressions.Regex Patterns(string patterns)
    {
        var globs = patterns
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*"))
            .ToList();
        return globs.Count == 0
            ? null
            : new System.Text.RegularExpressions.Regex(
                $"^(?:{string.Join("|", globs)})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
    }
}
