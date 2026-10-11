using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Game.Entity;
using VRageMath;

namespace ServerPlugin;

// The world is a sphere. Between the braking radius and the world radius the outward
// component of a grid's or character's velocity is capped, the cap falling to zero at the
// world radius; the inward and sideways components are left alone, so a ship can always
// turn back. Independent of the game's WorldSizeKm, which deletes what crosses its cube.
// Each cluster node brakes what it simulates.
internal sealed class WorldEdge
{
    private const int ScanIntervalTicks = 60;

    // Watched from this far inside the braking radius, so nothing at the game's top speed gets
    // past it between two scans
    private const double ScanMargin = 1000;

    // Beyond the world radius a body is pulled back at 1 m/s per meter, at most this fast
    private const double ReturnSpeed = 10;

    private readonly List<MyEntity> watched = new();
    private long tick;

    public void Update()
    {
        var config = Plugin.Config;
        if (!config.WorldEdgeEnabled)
        {
            watched.Clear();
            return;
        }

        if (tick++ % ScanIntervalTicks == 0)
            Scan(config);

        foreach (var entity in watched)
            Brake(entity, config);
    }

    private void Scan(OctahedronConfig config)
    {
        watched.Clear();
        var radius = Math.Max(0, config.WorldEdgeBrakingRadius - ScanMargin);
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is MyCubeGrid or MyCharacter
                && Vector3D.DistanceSquared(
                    entity.PositionComp.GetPosition(),
                    config.WorldEdgeCenter
                )
                    > radius * radius
            )
                watched.Add(entity);
        }
    }

    private void Brake(MyEntity entity, OctahedronConfig config)
    {
        var physics = entity.Physics;
        if (entity.MarkedForClose || entity.Parent != null || physics == null || physics.IsStatic)
            return;

        var offset = entity.PositionComp.GetPosition() - config.WorldEdgeCenter;
        var distance = offset.Length();
        if (distance <= config.WorldEdgeBrakingRadius)
            return;

        var outward = offset / distance;
        Vector3D velocity = physics.LinearVelocity;
        var speed = Vector3D.Dot(velocity, outward);
        var limit = OutwardSpeedLimit(distance, config);
        if (speed <= limit || !Plugin.Simulated(entity))
            return;

        physics.LinearVelocity = (Vector3)(velocity - outward * (speed - limit));
    }

    // One line per grid and character in the braking zone, the farthest first
    internal static IEnumerable<string> Describe(OctahedronConfig config)
    {
        var found = new List<(double Distance, string Line)>();
        foreach (var entity in MyEntities.GetEntities())
        {
            if (entity is not (MyCubeGrid or MyCharacter) || entity.Parent != null)
                continue;
            var offset = entity.PositionComp.GetPosition() - config.WorldEdgeCenter;
            var distance = offset.Length();
            if (distance <= config.WorldEdgeBrakingRadius)
                continue;
            var speed = Vector3D.Dot(
                entity.Physics?.LinearVelocity ?? Vector3.Zero,
                offset / distance
            );
            var limit = OutwardSpeedLimit(distance, config);
            found.Add(
                (
                    distance,
                    $"{entity.DisplayName ?? entity.EntityId.ToString()}: {distance / 1000:F3} km, outward {speed:F1} m/s, limit {limit:F1} m/s"
                )
            );
        }
        if (found.Count == 0)
            return new[] { "Nothing in the braking zone." };
        found.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        return found.ConvertAll(f => f.Line);
    }

    // The fastest a body may move outward at this distance from the center. Negative beyond
    // the world radius, which pulls it back.
    internal static double OutwardSpeedLimit(double distance, OctahedronConfig config)
    {
        var radius = config.WorldEdgeRadius;
        if (distance >= radius)
            return Math.Max(radius - distance, -ReturnSpeed);

        var width = Math.Max(1, radius - config.WorldEdgeBrakingRadius);
        var left = Math.Min(1, (radius - distance) / width);
        return config.WorldEdgeBrakingSpeed * Math.Pow(left, config.WorldEdgeBrakingExponent);
    }
}
