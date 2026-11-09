using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage;
using VRageMath;

namespace ServerPlugin;

// Havok puts a body to sleep only at rest, so an unpowered wreck or a cloud of debris left with some
// speed drifts on awake forever. It costs game thread time, and its partition never goes offline. This
// stops such groups: no power, nobody seated, out of any gravity and no player right next to them.
// With no player within sync distance nobody sees it, so a group up to DriftStopMaxSpeed stops at once,
// one group a frame, and its partition can go offline. With one there only slow groups are slowed down,
// a step each in turn, until they stop (SE-1326).
internal static class DriftStop
{
    private const int ScanFrames = 60;

    // Below this a group counts as still (the cluster's offline gate takes 0.05 m/s)
    private const float Still = 0.05f;

    private enum Mode
    {
        None,
        Near,
        Far,
    }

    // The frame each drifting group was first seen, by its lowest grid id
    private static readonly Dictionary<long, int> Since = new();

    // Worked off one group a frame; a scan fills it again once it is empty
    private static readonly Queue<MyCubeGrid> Work = new();
    private static List<Vector3D> players = new();
    private static int frame;

    internal static void Update()
    {
        ++frame;
        var config = Plugin.Config;
        if (config is not { Enabled: true, DriftStopEnabled: true })
        {
            Since.Clear();
            Work.Clear();
            return;
        }
        if (Work.Count > 0)
            Step(Work.Dequeue(), config);
        else if (frame % ScanFrames == 0)
            Scan(config);
    }

    private static void Scan(OctahedronConfig config)
    {
        players = MySession
            .Static.Players.GetOnlinePlayers()
            .Select(p => p.Controller?.ControlledEntity?.Entity)
            .Where(e => e != null)
            .Select(e => e.PositionComp.GetPosition())
            .ToList();
        var seen = new HashSet<long>();
        var visited = new HashSet<MyCubeGrid>();
        foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
        {
            // A sleeping body skips the group walk: asleep is the goal
            if (grid.IsStatic || grid.Physics is not { IsActive: true } || !visited.Add(grid))
                continue;
            var group = MyCubeGridGroups.Static.Physical.GetGroupNodes(grid);
            visited.UnionWith(group);
            if (Drifts(group, config) == Mode.None)
                continue;
            var key = group.Min(g => g.EntityId);
            seen.Add(key);
            Since.TryAdd(key, frame);
            Work.Enqueue(grid);
        }
        foreach (var gone in Since.Keys.Where(k => !seen.Contains(k)).ToList())
            Since.Remove(gone);
    }

    // Checked again for each step: the group may have changed since the scan
    private static void Step(MyCubeGrid grid, OctahedronConfig config)
    {
        if (grid.MarkedForClose || grid.Physics == null)
            return;
        var group = MyCubeGridGroups.Static.Physical.GetGroupNodes(grid);
        var mode = Drifts(group, config);
        if (mode == Mode.None)
            return;
        var speed = group.Max(g => g.Physics.LinearVelocity.Length());
        if (mode == Mode.Far)
        {
            Stop(group, $"stopped at {speed:0.00} m/s, no player within sync distance");
            return;
        }
        if (
            !Since.TryGetValue(group.Min(g => g.EntityId), out var since)
            || frame - since < config.DriftStopSeconds * 60
        )
            return;
        // One step a scan, as if each second
        var step = (float)config.DriftStopDeceleration * ScanFrames / 60;
        if (speed <= step)
        {
            Stop(group, "slowed to a stop, a player within sync distance");
            return;
        }
        var keep = 1 - step / speed;
        foreach (var member in group)
        {
            member.Physics.LinearVelocity *= keep;
            member.Physics.AngularVelocity *= keep;
        }
    }

    private static Mode Drifts(List<MyCubeGrid> group, OctahedronConfig config)
    {
        var moving = false;
        var near = false;
        var speed = 0f;
        var touch = (double)config.DriftStopPlayerMeters * config.DriftStopPlayerMeters;
        var sync = (double)MySession.Static.Settings.SyncDistance;
        sync *= sync;
        foreach (var grid in group)
        {
            var physics = grid.Physics;
            if (grid.IsStatic || physics == null || grid.MarkedForClose)
                return Mode.None;
            speed = Math.Max(speed, physics.LinearVelocity.Length());
            moving |=
                physics.LinearVelocity.Length() > Still || physics.AngularVelocity.Length() > Still;
            var position = grid.PositionComp.GetPosition();
            foreach (var player in players)
            {
                var distance = Vector3D.DistanceSquared(player, position);
                if (distance <= touch)
                    return Mode.None;
                near |= distance <= sync;
            }
            if (MyGravityProviderSystem.CalculateTotalGravityInPoint(position).LengthSquared() > 0)
                return Mode.None;
            if (grid.GridSystems.ControlSystem?.IsControlled == true)
                return Mode.None;
            if (grid.GetFatBlocks<MyShipController>().Any(c => c.Pilot != null))
                return Mode.None;
            if (
                grid.GridSystems.ResourceDistributor?.ResourceStateByType(
                    MyResourceDistributorComponent.ElectricityId,
                    grid: grid
                )
                is not (null or MyResourceStateEnum.NoPower)
            )
                return Mode.None;
            if (!Plugin.Simulated(grid))
                return Mode.None;
        }
        if (!moving)
            return Mode.None;
        if (near)
            return speed <= config.DriftStopMaxSpeedNearPlayer ? Mode.Near : Mode.None;
        return speed <= config.DriftStopMaxSpeed ? Mode.Far : Mode.None;
    }

    private static void Stop(List<MyCubeGrid> group, string how)
    {
        foreach (var grid in group)
        {
            grid.Physics.LinearVelocity = Vector3.Zero;
            grid.Physics.AngularVelocity = Vector3.Zero;
        }
        Since.Remove(group.Min(g => g.EntityId));
        var main = group.OrderByDescending(g => g.BlocksCount).First();
        Plugin.Log.Info(
            $"Drift stop: {main.DisplayName} ({main.EntityId}, {group.Sum(g => g.BlocksCount)} blocks in {group.Count} grids) {how}"
        );
    }
}
