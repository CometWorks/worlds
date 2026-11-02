using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace SightProbe;

// Test-only server plugin of the shipyard sight test (tests/test_shipyard_sight.py). Once the
// world has loaded, it casts rays from where people stand in each station's lobby to its
// shipyard's build space, through the game's own physics, and logs a SIGHT line per station
// and one per blocked ray, naming what was in the way. Windows don't block.
public class Probe : IPlugin
{
    // check_sun_clearance.py's LOBBY_SPOTS: the glass dome's back half, in the station's cells
    private static readonly (int X, double Y, int Z)[] Spots = (
        from x in new[] { -6, -3, 0, 3, 6 }
        from z in new[] { 1, 4, 7 }
        from y in new[] { 7.2, 8.6 }
        select (x, y, z)
    ).ToArray();

    private int frames;
    private bool done;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (done || MySession.Static?.Ready != true || ++frames < 600)
            return;
        done = true;
        try
        {
            Report();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLineAndConsole($"SIGHT ERROR {e}");
        }
        MyLog.Default.WriteLineAndConsole("SIGHT DONE");
    }

    private static void Report()
    {
        var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList();
        foreach (
            var station in grids.Where(g =>
                g.DisplayName.EndsWith(" Station") && !g.DisplayName.StartsWith("TL ")
            )
        )
        {
            var yard = grids.FirstOrDefault(g =>
                g.DisplayName == station.DisplayName + " Shipyard"
            );
            if (yard == null)
                continue;
            var space = FreeSpace(yard);
            // Where ships go: the middle, and a short ship's 1/3 point nearer the station
            // (ShipyardOrders.ShipCenter), with the other 1/3 point for comparison
            var along = yard.WorldMatrix.Forward;
            if (Vector3D.Dot(station.PositionComp.WorldAABB.Center - space.Center, along) < 0)
                along = -along;
            var sixth = space.HalfExtent.Z / 3;
            var targets = new (string Name, Vector3D Point)[]
            {
                ("center", space.Center),
                ("near third", space.Center + along * sixth),
                ("far third", space.Center - along * sixth),
            };
            var spots = Spots
                .Where(s => !Solid(station, new Vector3I(s.X, (int)Math.Round(s.Y), s.Z)))
                .Select(s =>
                    Vector3D.Transform(
                        new Vector3D(s.X, s.Y, s.Z) * station.GridSize,
                        station.WorldMatrix
                    )
                )
                .ToList();
            var counts = new List<string>();
            foreach (var (name, point) in targets)
            {
                var clear = 0;
                for (var i = 0; i < spots.Count; i++)
                {
                    var blocker = Blocker(spots[i], point, station, yard);
                    if (blocker == null)
                        clear++;
                    else
                        MyLog.Default.WriteLineAndConsole(
                            $"SIGHT BLOCKED {station.DisplayName}|{name}|spot {i}|{blocker}"
                        );
                }
                counts.Add($"{name} {clear}/{spots.Count}");
            }
            MyLog.Default.WriteLineAndConsole(
                $"SIGHT STATION {station.DisplayName}|{string.Join("|", counts)}"
            );
        }
    }

    private static bool Solid(MyCubeGrid grid, Vector3I cell)
    {
        var block = grid.GetCubeBlock(cell);
        return block != null && !IsWindow(block.BlockDefinition.Id.SubtypeName);
    }

    private static bool IsWindow(string subtype) => subtype.Contains("Window");

    // The first hit along the ray that isn't a window, or null if only windows are in the way
    private static string Blocker(Vector3D from, Vector3D to, MyCubeGrid station, MyCubeGrid yard)
    {
        var hits = new List<MyPhysics.HitInfo>();
        MyPhysics.CastRay(from, to, hits);
        var direction = Vector3D.Normalize(to - from);
        foreach (var hit in hits.OrderBy(h => h.HkHitInfo.HitFraction))
        {
            var entity = hit.HkHitInfo.GetHitEntity();
            var distance = (hit.Position - from).Length();
            if (entity is MyCubeGrid grid)
            {
                var block =
                    grid.GetCubeBlock(grid.WorldToGridInteger(hit.Position + direction * 0.05))
                    ?? grid.GetCubeBlock(grid.WorldToGridInteger(hit.Position - direction * 0.05));
                var subtype = block?.BlockDefinition.Id.SubtypeName ?? "?";
                if (IsWindow(subtype))
                    continue;
                var owner =
                    grid == station ? "station"
                    : grid == yard ? "shipyard"
                    : grid.DisplayName;
                return $"{owner} {subtype} {block?.Position} at {distance:F0} m";
            }
            if (entity is MyVoxelBase voxel)
                return $"voxel {voxel.StorageName} at {distance:F0} m";
        }
        return null;
    }

    // The Octahedron plugin's free build space of the shipyard, where it builds ships, or the
    // shipyard's bounds when the plugin isn't loaded
    private static MyOrientedBoundingBoxD FreeSpace(MyCubeGrid yard)
    {
        var method = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ServerPlugin.ShipyardOrders", false))
            .FirstOrDefault(t => t != null)
            ?.GetMethod(
                "FreeSpace",
                BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(MyCubeGrid)]
            );
        if (method != null)
            return (MyOrientedBoundingBoxD)method.Invoke(null, [yard]);
        MyLog.Default.WriteLineAndConsole(
            "SIGHT no ShipyardOrders.FreeSpace, using the shipyard's bounds"
        );
        return new MyOrientedBoundingBoxD(yard.PositionComp.LocalAABB, yard.WorldMatrix);
    }
}
