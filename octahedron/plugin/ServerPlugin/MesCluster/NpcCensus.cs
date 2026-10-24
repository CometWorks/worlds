using System;
using System.Collections.Generic;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace ServerPlugin;

/// <summary>
/// How many NPC grids are around a point. On a cluster the cluster plugin publishes a census that also
/// counts the NPC grids of offline partitions (#1354); this plugin only reads it. Without it (a plain
/// server, or a cluster build that predates the census) the count covers this process only.
///
/// The census is a BCL delegate in an AppDomain data slot, so neither plugin references the other:
/// <c>Func&lt;double x, double y, double z, double radius, int count&gt;</c>, -1 when the cluster does not
/// know (no snapshot yet, or a stale one). Called on the game thread; the cluster answers from a local
/// snapshot.
///
/// MES spawns the prefabs of an accepted spawn over the next frames, and the cluster's snapshot lags by
/// seconds, so every accepted spawn is also counted as a reservation for <see cref="ReservationTime"/>:
/// two spawns in quick succession cannot both pass a limit the first one filled.
/// </summary>
internal static class NpcCensus
{
    internal const string CensusSlot = "CometWorks.Cluster.NpcCensus.v1";

    private static readonly TimeSpan ReservationTime = TimeSpan.FromSeconds(30);
    private static readonly List<(Vector3D Position, int Grids, DateTime Expires)> reservations =
        new();

    public static bool ClusterWide =>
        AppDomain.CurrentDomain.GetData(CensusSlot) is Func<double, double, double, double, int>;

    /// <summary>NPC grids within <paramref name="radius"/> metres of the point; -1 = unknown.</summary>
    public static int Count(Vector3D point, double radius)
    {
        int count = AppDomain.CurrentDomain.GetData(CensusSlot)
            is Func<double, double, double, double, int> census
            ? census(point.X, point.Y, point.Z, radius)
            : CountLocal(point, radius);
        if (count < 0)
            return count;

        var now = DateTime.UtcNow;
        reservations.RemoveAll(r => r.Expires <= now);
        foreach (var reservation in reservations)
        {
            if (Vector3D.DistanceSquared(reservation.Position, point) <= radius * radius)
                count += reservation.Grids;
        }

        return count;
    }

    /// <summary>Counts a spawn MES is about to make until its grids show up in the census.</summary>
    public static void Reserve(Vector3D position, int grids) =>
        reservations.Add((position, grids, DateTime.UtcNow + ReservationTime));

    private static int CountLocal(Vector3D point, double radius)
    {
        double radiusSquared = radius * radius;
        int count = 0;
        foreach (var entity in MyEntities.GetEntities())
        {
            if (entity is not MyCubeGrid grid || grid.Closed || grid.IsPreview)
                continue;
            if (MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid || !IsNpc(grid))
                continue;
            if (
                Vector3D.DistanceSquared(grid.PositionComp.WorldAABB.Center, point) <= radiusSquared
            )
                count++;
        }

        return count;
    }

    /// <summary>The same definition as the cluster's census: owned, and every big owner is an NPC identity.</summary>
    public static bool IsNpc(MyCubeGrid grid)
    {
        var owners = grid.BigOwners;
        if (owners.Count == 0)
            return false;

        foreach (var owner in owners)
        {
            if (!MySession.Static.Players.IdentityIsNpc(owner))
                return false;
        }

        return true;
    }
}
