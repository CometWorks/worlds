using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;
using VRageMath;

namespace ServerPlugin;

// A place as MES's EnvironmentEvaluation sees it: the nearest planet, whether the point is in
// its gravity, the altitude above its surface, the gravity in g, the air density and the
// terrain type below
internal readonly struct Spot
{
    public Vector3D Position { get; init; }
    public MyPlanet Planet { get; init; }
    public bool InGravity { get; init; }
    public double Altitude { get; init; }
    public float Gravity { get; init; }
    public float Air { get; init; }
    public string Terrain { get; init; }

    // Whether planetary cargo ships can fly here: a quarter or more of a 7 x 7 grid of
    // points 500 m apart around the player has air density 0.7 at 1.7 km above the ground
    // (MES EnvironmentEvaluation, AirTravelViabilityRatio)
    public bool AirTravel => airTravel?.Value ?? false;

    private Lazy<bool> airTravel { get; init; }

    public static Spot At(Vector3D position)
    {
        var planet = MyGamePruningStructure.GetClosestPlanet(position);
        if (planet == null)
            return new Spot { Position = position };
        var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(position).Length();
        var surface = Placement.Surface(planet, position);
        var material = planet.GetMaterialAt(ref surface);
        var up = Vector3D.Normalize(position - planet.PositionComp.GetPosition());
        var grid = MatrixD.CreateWorld(position, Vector3D.CalculatePerpendicularVector(up), up);
        return new Spot
        {
            airTravel = new Lazy<bool>(() =>
            {
                if (!planet.HasAtmosphere)
                    return false;
                var hits = 0;
                for (var i = -3; i < 4; i++)
                for (var j = -3; j < 4; j++)
                {
                    var ground = Placement.Surface(
                        planet,
                        grid.Right * (i * 500) + grid.Forward * (j * 500) + position
                    );
                    var at =
                        Vector3D.Normalize(ground - planet.PositionComp.GetPosition()) * 1700
                        + ground;
                    if (planet.GetAirDensity(at) >= 0.7f)
                        hits++;
                }
                return hits / 49f >= 0.25f;
            }),
            Position = position,
            Planet = planet,
            InGravity = gravity > 0,
            Altitude = Placement.Altitude(planet, position),
            Gravity = gravity / 9.81f,
            Air = planet.HasAtmosphere ? planet.GetAirDensity(position) : 0,
            Terrain = material?.MaterialTypeName,
        };
    }
}

// MES's spawn placement (Spawning/PathPlacements.cs) with its default distances: where a
// spawn type starts, and the end of a cargo ship's path
internal static class Placement
{
    private static readonly Random Rng = new();

    public static bool Find(
        EncounterType type,
        List<EncounterPrefab> prefabs,
        EncounterCondition condition,
        Spot spot,
        out MatrixD matrix,
        out Vector3D? end
    )
    {
        end = null;
        matrix = MatrixD.Identity;
        switch (type)
        {
            case EncounterType.SpaceCargoShip:
                return SpacePath(prefabs, spot, out matrix, out end);
            case EncounterType.AtmosphericCargoShip:
                return AtmoPath(prefabs, condition, spot, out matrix, out end);
            case EncounterType.GravityCargoShip:
                return GravityPath(prefabs, condition, spot, out matrix, out end);
            case EncounterType.SpaceRandomEncounter:
                return Encounter(prefabs, spot, out matrix);
            case EncounterType.PlanetaryInstallation:
                return Installation(condition, spot, out matrix);
            default:
                return false;
        }
    }

    // A straight path 10–15 km long passing 2–4 km from the player, outside gravity, clear of
    // a 1.6 km box around the player, of grids within 1 km of its start and of asteroids
    private static bool SpacePath(
        List<EncounterPrefab> prefabs,
        Spot spot,
        out MatrixD matrix,
        out Vector3D? end
    )
    {
        var voxels = Voxels(spot.Position);
        var gravity = GravitySphere(spot.Planet);
        for (var i = 0; i < 10; i++)
        {
            var closest = Between(2000, 4000);
            var randomDir = RandomDirection();
            var pathDir = Perpendicular(randomDir);
            var initial = MatrixD.CreateWorld(spot.Position, pathDir, randomDir);
            var half = closest * randomDir + spot.Position;
            var length = Between(10000, 15000);
            var box = PlayerBox(initial);
            matrix = initial;
            matrix.Translation = pathDir * -(length / 2) + half;
            end = matrix.Translation + pathDir * length;
            if (InGravity(matrix.Translation))
                continue;
            var blocked = false;
            foreach (var prefab in prefabs)
            {
                var start = Vector3D.Transform(Offset(prefab), matrix);
                var stop = start + pathDir * length;
                var ray = new RayD(start, pathDir);
                if (
                    (gravity.Radius > 0 && ray.Intersects(gravity).HasValue)
                    || ray.Intersects(box).HasValue
                    || GridWithin(start, 1000)
                    || VoxelIntersecting(voxels, start, stop, 1000)
                )
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked)
                return true;
        }
        matrix = MatrixD.Identity;
        end = null;
        return false;
    }

    // A path 10–13 km long at 1.7–2.5 km altitude, its middle 3–5 km from the player, never
    // below 900 m altitude, with air density 0.7 or more at both ends unless the group skips
    // the check
    private static bool AtmoPath(
        List<EncounterPrefab> prefabs,
        EncounterCondition condition,
        Spot spot,
        out MatrixD matrix,
        out Vector3D? end
    )
    {
        var planet = spot.Planet;
        for (var i = 0; i < 25; i++)
        {
            var up = Up(planet, spot.Position);
            var perp = Perpendicular(up);
            var box = PlayerBox(MatrixD.CreateWorld(spot.Position, perp, up));
            var closest = Between(3000, 5000);
            var height = Between(1700, 2500);
            var half = Surface(planet, perp * closest + spot.Position);
            var upHalf = Up(planet, half);
            var start = upHalf * height + half;
            var length = Between(10000, 13000);
            if (Air(planet, start) < 0.7f)
            {
                start = upHalf * 2100 + half;
                if (Air(planet, start) < 0.7f)
                    start = upHalf * 1700 + half;
            }
            matrix = Toward(start, upHalf, spot.Position);
            end = null;
            var blocked = false;
            foreach (var prefab in prefabs)
            {
                var from = Vector3D.Transform(Offset(prefab), matrix);
                var core = Vector3D.Distance(from, planet.PositionComp.GetPosition());
                var roughEnd = matrix.Forward * length + from;
                var to = Up(planet, roughEnd) * core + planet.PositionComp.GetPosition();
                if (Altitude(planet, to) < 1700)
                    to = Up(planet, roughEnd) * 1700 + Surface(planet, to);
                end ??= to;
                if (
                    !condition.SkipAirDensityCheck
                    && (Air(planet, from) < 0.7f || Air(planet, to) < 0.7f)
                )
                {
                    blocked = true;
                    break;
                }
                if (PlanetPathBlocked(planet, box, from, to))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked)
                return true;
        }
        matrix = MatrixD.Identity;
        end = null;
        return false;
    }

    // A path 10–13 km long at the player's altitude ±1.7–2.5 km, its middle 3–5 km away, its
    // start 1.2 km from grids, clear of asteroids, gravity within the group's limit
    private static bool GravityPath(
        List<EncounterPrefab> prefabs,
        EncounterCondition condition,
        Spot spot,
        out MatrixD matrix,
        out Vector3D? end
    )
    {
        var planet = spot.Planet;
        var voxels = Voxels(spot.Position);
        for (var i = 0; i < 25; i++)
        {
            var up = Up(planet, spot.Position);
            var perp = Perpendicular(up);
            var box = PlayerBox(MatrixD.CreateWorld(spot.Position, perp, up));
            var closest = Between(3000, 5000);
            var rough = Between(1700, 2500);
            var height = Between(-rough, rough);
            var half = perp * closest + spot.Position;
            var g = MyGravityProviderSystem.CalculateNaturalGravityInPoint(half).Length() / 9.81f;
            if (condition.MaxGravity > -1 && g > condition.MaxGravity)
                continue;
            var upHalf = Up(planet, half);
            var start = upHalf * height + half;
            matrix = Toward(start, upHalf, spot.Position);
            var length = Between(10000, 13000);
            end = matrix.Forward * length + start;
            var blocked = false;
            foreach (var prefab in prefabs)
            {
                var from = Vector3D.Transform(Offset(prefab), matrix);
                var to = matrix.Forward * length + from;
                if (
                    new RayD(from, matrix.Forward).Intersects(box).HasValue
                    || GridWithin(from, 1200)
                    || VoxelIntersecting(voxels, from, to, 1200)
                )
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked)
                return true;
        }
        matrix = MatrixD.Identity;
        end = null;
        return false;
    }

    // 8–12 km from the player, 3 km from grids and asteroids, the other side if in gravity
    private static bool Encounter(List<EncounterPrefab> prefabs, Spot spot, out MatrixD matrix)
    {
        var voxels = Voxels(spot.Position);
        for (var i = 0; i < 10; i++)
        {
            var dir = RandomDirection();
            var distance = Between(8000, 12000);
            var coords = dir * distance + spot.Position;
            if (spot.Planet != null && InGravity(coords))
                coords = -dir * distance + spot.Position;
            matrix = MatrixD.CreateWorld(coords);
            var blocked = false;
            foreach (var prefab in prefabs)
            {
                var start = Vector3D.Transform(Offset(prefab), matrix);
                if (GridWithin(start, 3000) || VoxelIntersecting(voxels, start, start, 3000))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked)
                return true;
        }
        matrix = MatrixD.Identity;
        return false;
    }

    // On level ground 3–6 km from the player, 2 km farther for a medium and 4 km for a large
    // installation, 2.5 km from grids. The ground may vary by 2.5 m within 40, 70 or 100 m.
    private static bool Installation(EncounterCondition condition, Spot spot, out MatrixD matrix)
    {
        var planet = spot.Planet;
        var (extra, flat) = condition.InstallationSize switch
        {
            "Medium" => (2000, 70),
            "Large" => (4000, 100),
            _ => (0, 40),
        };
        var up = Up(planet, spot.Position);
        var search = MatrixD.CreateWorld(spot.Position, Perpendicular(up), up);
        foreach (var direction in Directions(search))
        {
            for (double d = 3000 + extra; d <= 6000 + extra; d += 150)
            {
                var coords = Surface(planet, direction * d + spot.Position);
                if (GridWithin(coords, 2500))
                    continue;
                var checkUp = Up(planet, coords);
                var check = MatrixD.CreateWorld(coords, Perpendicular(checkUp), checkUp);
                var core = Vector3D.Distance(coords, planet.PositionComp.GetPosition());
                var level = true;
                foreach (var checkDirection in Directions(check))
                {
                    for (double step = 0; step < flat && level; step += 10)
                    {
                        var ground = Surface(planet, checkDirection * step + coords);
                        var diff =
                            Vector3D.Distance(ground, planet.PositionComp.GetPosition()) - core;
                        level = diff >= -2.5 && diff <= 2.5;
                    }
                    if (!level)
                        break;
                }
                if (!level)
                    continue;
                var finalUp = Up(planet, coords);
                matrix = MatrixD.CreateWorld(coords, Perpendicular(finalUp), finalUp);
                return true;
            }
        }
        matrix = MatrixD.Identity;
        return false;
    }

    // A boss signal 6–8 km away, 2 km from grids; on a planet 1.5 km above the ground
    public static bool BossSignal(Spot spot, out Vector3D position)
    {
        for (var i = 0; i < 25; i++)
        {
            var distance = Between(6000, 8000);
            if (!spot.InGravity)
            {
                position = RandomDirection() * distance + spot.Position;
                if (InGravity(position))
                    continue;
            }
            else
            {
                var up = Up(spot.Planet, spot.Position);
                var ground = Surface(spot.Planet, Perpendicular(up) * distance + spot.Position);
                position = Up(spot.Planet, ground) * 1500 + ground;
            }
            if (!GridWithin(position, 2000))
                return true;
        }
        position = default;
        return false;
    }

    // The boss itself, 2.5–4 km from the signal
    public static bool Boss(Spot signal, out MatrixD matrix)
    {
        for (var i = 0; i < 25; i++)
        {
            var distance = Between(2500, 4000);
            Vector3D coords;
            Vector3D up;
            if (!signal.InGravity)
            {
                coords = RandomDirection() * distance + signal.Position;
                up = Perpendicular(Vector3D.Normalize(signal.Position - coords));
                if (InGravity(coords))
                    continue;
            }
            else
            {
                var signalUp = Up(signal.Planet, signal.Position);
                var ground = Surface(
                    signal.Planet,
                    Perpendicular(signalUp) * distance + signal.Position
                );
                up = Up(signal.Planet, ground);
                coords = up * 1500 + ground;
            }
            if (GridWithin(coords, 1000))
                continue;
            matrix = MatrixD.CreateWorld(
                coords,
                Vector3D.Normalize(Vector3D.Reject(signal.Position - coords, up)),
                up
            );
            return true;
        }
        matrix = MatrixD.Identity;
        return false;
    }

    // Facing the next clockwise of the four directions closest to the player, as MES turns a
    // planetary cargo ship's path past the player
    private static MatrixD Toward(Vector3D start, Vector3D up, Vector3D player)
    {
        var m = MatrixD.CreateWorld(start, Perpendicular(up), up);
        var toPlayer = Vector3D.Normalize(player - start);
        Vector3D[] dirs = [m.Forward, m.Right, m.Backward, m.Left];
        var best = 0;
        for (var i = 1; i < 4; i++)
            if (Vector3D.Dot(dirs[i], toPlayer) > Vector3D.Dot(dirs[best], toPlayer))
                best = i;
        return MatrixD.CreateWorld(start, dirs[(best + 1) % 4], up);
    }

    private static bool PlanetPathBlocked(
        MyPlanet planet,
        BoundingBoxD box,
        Vector3D from,
        Vector3D to
    )
    {
        var dir = Vector3D.Normalize(to - from);
        if (new RayD(from, dir).Intersects(box).HasValue || GridWithin(from, 1200))
            return true;
        var length = Vector3D.Distance(from, to);
        for (double step = 0; step <= length; step += 100)
            if (Altitude(planet, dir * step + from) < 900)
                return true;
        return false;
    }

    private static IEnumerable<Vector3D> Directions(MatrixD m)
    {
        yield return m.Forward;
        yield return m.Backward;
        yield return m.Left;
        yield return m.Right;
        yield return Vector3D.Normalize(m.Forward + m.Left);
        yield return Vector3D.Normalize(m.Forward + m.Right);
        yield return Vector3D.Normalize(m.Backward + m.Left);
        yield return Vector3D.Normalize(m.Backward + m.Right);
    }

    private static Vector3D Offset(EncounterPrefab prefab) => new(prefab.X, prefab.Y, prefab.Z);

    private static BoundingBoxD PlayerBox(MatrixD m) =>
        new(
            Vector3D.Transform(new Vector3D(-800, -800, -800), m),
            Vector3D.Transform(new Vector3D(800, 800, 800), m)
        );

    private static bool InGravity(Vector3D position) =>
        MyGravityProviderSystem.CalculateNaturalGravityInPoint(position).LengthSquared() > 0;

    // The planet's gravity sphere, where it falls to 0.05 g (MES GetGravitySphere(0.05))
    private static BoundingSphereD GravitySphere(MyPlanet planet) =>
        planet?.Components.Get<MyGravityProviderComponent>()
            is MySphericalNaturalGravityComponent gravity
            ? new BoundingSphereD(planet.PositionComp.GetPosition(), gravity.GravityLimit)
            : new BoundingSphereD(Vector3D.Zero, 0);

    private static List<MyVoxelBase> Voxels(Vector3D position)
    {
        var list = new List<MyVoxelBase>();
        var sphere = new BoundingSphereD(position, 20000);
        MyGamePruningStructure.GetAllVoxelMapsInSphere(ref sphere, list);
        list.RemoveAll(v => v is MyPlanet || v.Closed);
        return list;
    }

    // MES checks the distance to each asteroid's center and whether the path's ray meets its box
    private static bool VoxelIntersecting(
        List<MyVoxelBase> voxels,
        Vector3D start,
        Vector3D end,
        double minDistance
    )
    {
        foreach (var voxel in voxels)
        {
            var box = voxel.PositionComp.WorldAABB;
            if (Vector3D.Distance(start, box.Center) < minDistance)
                return true;
            if (start != end)
            {
                if (new RayD(start, Vector3D.Normalize(end - start)).Intersects(box).HasValue)
                    return true;
            }
            else if (box.Contains(start) != ContainmentType.Disjoint)
                return true;
        }
        return false;
    }

    private static bool GridWithin(Vector3D position, double distance)
    {
        var sphere = new BoundingSphereD(position, distance);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var found = false;
        foreach (MyEntity entity in entities)
        {
            if (
                entity is MyCubeGrid grid
                && Vector3D.Distance(grid.PositionComp.GetPosition(), position) < distance
            )
            {
                found = true;
                break;
            }
        }
        entities.Clear();
        return found;
    }

    public static Vector3D Surface(MyPlanet planet, Vector3D position)
    {
        var p = position;
        return planet.GetClosestSurfacePointGlobal(ref p);
    }

    public static double Altitude(MyPlanet planet, Vector3D position)
    {
        var center = planet.PositionComp.GetPosition();
        return Vector3D.Distance(position, center)
            - Vector3D.Distance(Surface(planet, position), center);
    }

    private static Vector3D Up(MyPlanet planet, Vector3D position) =>
        Vector3D.Normalize(position - planet.PositionComp.GetPosition());

    private static float Air(MyPlanet planet, Vector3D position) =>
        planet.HasAtmosphere ? planet.GetAirDensity(position) : 0;

    private static double Between(double min, double max) => min + Rng.NextDouble() * (max - min);

    private static Vector3D RandomDirection()
    {
        Vector3D v;
        do v = new Vector3D(
            Rng.NextDouble() * 2 - 1,
            Rng.NextDouble() * 2 - 1,
            Rng.NextDouble() * 2 - 1
        );
        while (v.LengthSquared() is > 1 or < 1e-6);
        return Vector3D.Normalize(v);
    }

    private static Vector3D Perpendicular(Vector3D v)
    {
        var p = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(v));
        var angle = Rng.NextDouble() * Math.PI * 2;
        return Vector3D.Normalize(Vector3D.Transform(p, MatrixD.CreateFromAxisAngle(v, angle)));
    }
}
