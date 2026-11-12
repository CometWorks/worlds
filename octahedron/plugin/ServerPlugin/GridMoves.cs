using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRageMath;

namespace ServerPlugin;

/// <summary>
/// Moves a station's shipyard of a kept world to where a world built now has it (SE-1369): what
/// neither a station patch, which changes blocks, nor a world patch, which adds grids, can do.
/// The process that simulates the shipyard moves and turns it, with the grids on its rotors, once it
/// is idle: no job running or coming, no grid in its build space, none docked, nobody in its
/// bounds or where it goes, and no other grid where it goes. The id goes into the grid's station
/// patch ledger, so it moves once wherever the grid goes. A shipyard that is where the entry puts
/// it already, as in a world built with the move, only gets the id.
/// </summary>
internal static class GridMoves
{
    private const int CheckSeconds = 5;
    private const int RetrySeconds = 60;
    private const double SamePosition = 0.5;
    private const double SameDirection = 0.9999;

    private record Move(string Id, string Grid, MatrixD Target);

    private static DateTime nextCheck;
    private static bool reported;
    private static OctahedronConfig parsedFrom;
    private static List<Move> staged = [];
    private static readonly Dictionary<(string, long), (DateTime Next, string Why)> waiting = new();

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextCheck || MySession.Static?.Ready != true)
            return;
        nextCheck = now.AddSeconds(CheckSeconds);
        if (parsedFrom != Plugin.Config)
            Parse(Plugin.Config);
        if (staged.Count == 0)
            return;

        var names = staged.Select(m => m.Grid).ToHashSet();
        var grids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.Closed && names.Contains(g.DisplayName))
            .Where(g => ShipyardZoneWelding.IsShipyard(g))
            .Where(g => Plugin.Simulated(g))
            .ToList();
        if (!reported)
        {
            // For the deploy's report and the tests: what this process found at its first check
            reported = true;
            var done = grids.Sum(g =>
                staged.Count(m =>
                    m.Grid == g.DisplayName && StationPatches.Ledger(g).Contains(m.Id)
                )
            );
            Plugin.Log.Info(
                $"Grid move READY: {staged.Count} moves staged, {grids.Count} of their grids here, {done} moves in their ledgers"
            );
        }
        foreach (var grid in grids)
        foreach (var move in staged.Where(m => m.Grid == grid.DisplayName))
        {
            if (StationPatches.Ledger(grid).Contains(move.Id))
                continue;
            var key = (move.Id, grid.EntityId);
            if (waiting.TryGetValue(key, out var last) && now < last.Next)
                break;
            string result;
            try
            {
                result = Run(grid, move);
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Grid move {move.Id} on {grid.DisplayName} failed", e);
                result = $"FAILED {move.Id} on {grid.DisplayName}: {e.Message}";
            }
            if (result.StartsWith("APPLIED") || result.StartsWith("THERE"))
            {
                waiting.Remove(key);
                Plugin.Log.Info($"Grid move {result}");
                continue;
            }
            if (last.Why != result)
                Plugin.Log.Warning($"Grid move {result}; trying again in {RetrySeconds} s");
            waiting[key] = (now.AddSeconds(RetrySeconds), result);
            // The grid's later moves wait for this one
            break;
        }
    }

    private static string Run(MyCubeGrid grid, Move move)
    {
        var where = $"{move.Id} on {grid.DisplayName}";
        var from = grid.WorldMatrix;
        if (
            Vector3D.Distance(from.Translation, move.Target.Translation) < SamePosition
            && Vector3D.Dot(from.Forward, move.Target.Forward) > SameDirection
            && Vector3D.Dot(from.Up, move.Target.Up) > SameDirection
        )
        {
            Record(grid, move.Id);
            return $"THERE {where}: the grid is where the move puts it, ledger only";
        }
        var why = NotIdle(grid, move.Target);
        if (why != null)
            return $"WAITING {where}: {why}";

        Place(grid, move.Target);
        Record(grid, move.Id);
        var to = grid.WorldMatrix;
        var cos =
            (
                Vector3D.Dot(from.Forward, to.Forward)
                + Vector3D.Dot(from.Up, to.Up)
                + Vector3D.Dot(from.Right, to.Right)
                - 1
            ) / 2;
        var turned = MathHelper.ToDegrees(Math.Acos(Math.Clamp(cos, -1, 1)));
        var moved = Vector3D.Distance(from.Translation, to.Translation);
        return $"APPLIED {where}: moved {moved:F1} m, turned {turned:F1} deg, now at {Format(to.Translation)}";
    }

    // The grid and the grids on its rotors to target, each where it was relative to the grid.
    // MyCubeGrid.Teleport only moves a group, it doesn't turn it, and taking the bodies out of
    // the physics world the way it does left the connectors' collision filters to fail
    // (MyShipConnector.UpdateHavokCollisionSystemID). A static grid's body just follows.
    private static void Place(MyCubeGrid grid, MatrixD target)
    {
        var grids =
            MyCubeGridGroups
                .Static.Mechanical.GetGroup(grid)
                ?.Nodes.Select(n => n.NodeData)
                .ToList()
            ?? [grid];
        var toTarget = MatrixD.Invert(grid.WorldMatrix) * target;
        foreach (var g in grids)
        {
            var matrix = g.WorldMatrix * toTarget;
            g.PositionComp.SetWorldMatrix(ref matrix);
        }
    }

    // Why the shipyard can't move now, or null
    private static string NotIdle(MyCubeGrid grid, MatrixD target)
    {
        if (ShipyardOrders.Busy(grid.EntityId))
            return "the shipyard has a job";
        var obstacle = ShipyardOrders.Obstacle(grid);
        if (obstacle != null)
            return $"{obstacle.DisplayName} is in its build space";
        var own =
            MyCubeGridGroups
                .Static.Mechanical.GetGroup(grid)
                ?.Nodes.Select(n => n.NodeData)
                .ToHashSet()
            ?? [grid];
        var docked = MyCubeGridGroups
            .Static.Physical.GetGroup(grid)
            ?.Nodes.Select(n => n.NodeData)
            .FirstOrDefault(g => !own.Contains(g));
        if (docked != null)
            return $"{docked.DisplayName} is docked to it";

        var bounds = grid.PositionComp.LocalAABB;
        foreach (var (matrix, there) in new[] { (grid.WorldMatrix, false), (target, true) })
        {
            var box = new MyOrientedBoundingBoxD((BoundingBoxD)bounds, matrix);
            var entities = MyEntities.GetEntitiesInOBB(ref box);
            var character = entities.OfType<MyCharacter>().FirstOrDefault(c => !c.IsDead);
            var other = there
                ? entities
                    .OfType<MyCubeGrid>()
                    .FirstOrDefault(g =>
                        !own.Contains(g)
                        && Stations.StationOf(g) == null
                        && ShipyardOrders.HasBlockIn(g, box)
                    )
                : null;
            entities.Clear();
            if (character != null)
                return $"{character.DisplayName} is in its bounds or where it goes";
            if (other != null)
                return $"{other.DisplayName} is where it goes";
        }
        return null;
    }

    private static void Record(MyCubeGrid grid, string id)
    {
        StationPatches.Claim();
        grid.Storage ??= new MyModStorageComponent();
        grid.Storage[StationPatches.LedgerKey] = string.Join(
            ",",
            StationPatches.Ledger(grid).Append(id)
        );
    }

    private static void Parse(OctahedronConfig config)
    {
        parsedFrom = config;
        staged = [];
        waiting.Clear();
        foreach (var entry in config.GridMoves ?? [])
        {
            var move = Parse(entry, out var error);
            if (move == null)
                Plugin.Log.Error($"Grid move '{entry.Id}' in the config is left out: {error}");
            else
                staged.Add(move);
        }
    }

    private static readonly Regex IdPattern = new("^[A-Za-z0-9._-]+$");

    private static Move Parse(GridMoveEntry entry, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(entry.Id) || !IdPattern.IsMatch(entry.Id))
            error = "the id may only have letters, digits, '.', '_' and '-'";
        else if (string.IsNullOrEmpty(entry.Grid))
            error = "no grid name";
        else if (
            !TryVector(entry.Position, out var position)
            || !TryVector(entry.Forward, out var forward)
            || !TryVector(entry.Up, out var up)
        )
            error = "Position, Forward and Up must each be three numbers: X,Y,Z";
        else if (
            Math.Abs(forward.Length() - 1) > 0.001
            || Math.Abs(up.Length() - 1) > 0.001
            || Math.Abs(Vector3D.Dot(forward, up)) > 0.001
        )
            error = "Forward and Up must be unit vectors at right angles";
        else
            return new Move(entry.Id, entry.Grid, MatrixD.CreateWorld(position, forward, up));
        return null;
    }

    private static bool TryVector(string text, out Vector3D vector)
    {
        vector = default;
        var parts = (text ?? "").Split(',');
        if (parts.Length != 3)
            return false;
        var values = new double[3];
        for (var i = 0; i < 3; i++)
            if (
                !double.TryParse(
                    parts[i].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[i]
                )
            )
                return false;
        vector = new Vector3D(values[0], values[1], values[2]);
        return true;
    }

    private static string Format(Vector3D v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:F2},{v.Y:F2},{v.Z:F2}");
}
