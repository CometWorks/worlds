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
// stops such a group once it has drifted for a while: slow, nobody seated, no power, out of any gravity
// and with no player near. Stopped, it falls asleep like any still grid in space (SE-1326).
internal static class DriftStop
{
    private const int CheckFrames = 60;

    // Below this the group counts as still already (the cluster's offline gate uses 0.05 m/s)
    private const float Still = 0.05f;

    // Checks in a row each drifting group has passed, by its lowest grid id
    private static readonly Dictionary<long, int> Drifting = new();
    private static int frame;

    internal static void Update()
    {
        if (++frame % CheckFrames != 0)
            return;
        var config = Plugin.Config;
        if (config is not { Enabled: true, DriftStopEnabled: true })
        {
            Drifting.Clear();
            return;
        }

        var players = MySession
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
            if (!Drifts(group, players, config))
                continue;
            var key = group.Min(g => g.EntityId);
            seen.Add(key);
            Drifting.TryGetValue(key, out var seconds);
            if (++seconds < config.DriftStopSeconds)
                Drifting[key] = seconds;
            else
                Stop(group);
        }
        foreach (var gone in Drifting.Keys.Where(k => !seen.Contains(k)).ToList())
            Drifting.Remove(gone);
    }

    private static bool Drifts(
        List<MyCubeGrid> group,
        List<Vector3D> players,
        OctahedronConfig config
    )
    {
        var moving = false;
        var nearSquared = (double)config.DriftStopPlayerMeters * config.DriftStopPlayerMeters;
        foreach (var grid in group)
        {
            var physics = grid.Physics;
            if (grid.IsStatic || physics == null || grid.MarkedForClose)
                return false;
            var speed = physics.LinearVelocity.Length();
            if (speed > config.DriftStopMaxSpeed)
                return false;
            moving |= speed > Still || physics.AngularVelocity.Length() > Still;
            var position = grid.PositionComp.GetPosition();
            if (MyGravityProviderSystem.CalculateTotalGravityInPoint(position).LengthSquared() > 0)
                return false;
            if (players.Any(p => Vector3D.DistanceSquared(p, position) <= nearSquared))
                return false;
            if (grid.GridSystems.ControlSystem?.IsControlled == true)
                return false;
            if (grid.GetFatBlocks<MyShipController>().Any(c => c.Pilot != null))
                return false;
            if (
                grid.GridSystems.ResourceDistributor?.ResourceStateByType(
                    MyResourceDistributorComponent.ElectricityId,
                    grid: grid
                )
                is not (null or MyResourceStateEnum.NoPower)
            )
                return false;
            if (!Plugin.Simulated(grid))
                return false;
        }
        return moving;
    }

    private static void Stop(List<MyCubeGrid> group)
    {
        var main = group.OrderByDescending(g => g.BlocksCount).First();
        var speed = group.Max(g => g.Physics.LinearVelocity.Length());
        foreach (var grid in group)
        {
            grid.Physics.LinearVelocity = Vector3.Zero;
            grid.Physics.AngularVelocity = Vector3.Zero;
        }
        Plugin.Log.Info(
            $"Drift stop: {main.DisplayName} ({main.EntityId}, {group.Sum(g => g.BlocksCount)} blocks in {group.Count} grids) stopped at {speed:0.00} m/s"
        );
    }
}
