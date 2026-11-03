using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage;
using VRageMath;
using IMyThrust = Sandbox.ModAPI.Ingame.IMyThrust;

namespace ServerPlugin;

// A ship whose speed changes much faster than its thrusters allow is switched off, as the Y key in
// its cockpit does: a clang drive, or a ship braking by ramming a station or another ship in a safe
// zone. The ship is a physical group, the grids on rotors, pistons, connectors and landing gear,
// and its speed is the group's center of mass's, so parts moving inside it cancel out. Time is game
// time: each simulation frame's acceleration comes from its velocity and the one of the frame before,
// over 1/60 s, and the rule averages these over ClangDriveWindowFrames, so a single spike adds only
// its share. The limit is ClangDriveFactor times the thrust of the strongest direction at full
// override over the mass, plus natural and artificial gravity; a ship without thrusters may only
// fall. Ships slower than ClangDriveMinSpeed are left alone. Outside safe zones a ship that touched
// anything outside the group within the last second isn't punished: a crash. Inside one, only the
// faster of two colliding ships is switched off. A ship left on is left alone for five seconds
// more, the aftermath of its hit: its wreck's pieces keep bumping into each other. Skipped: groups with a static grid (docked to a station), owned by an NPC,
// in a shipyard's build space or switched off already. Each cluster node checks what it simulates.
internal static class ClangDrives
{
    private const double FramesPerSecond = 60;
    private const int ContactFrames = 60;
    private const int SparedFrames = 300;
    private const int ChatFrames = 3600;

    private sealed class Track
    {
        public long Signature;
        public int Seen;

        // Thrust over mass, taken each second, the last two: a ramming ship may lose its
        // thrusters in the very hit that is measured
        public double Accel;
        public double LastAccel;
        public int AccelFrame = -1_000_000;

        // The last frame the ship was over the limit and left on
        public int Spared = -1_000_000;

        // The speed at the start of the last full window: before a collision in it
        public double Speed;
        public readonly Queue<(int Frame, Vector3D Velocity)> Samples = new();
    }

    // By the group's grid with the smallest entity id
    private static readonly Dictionary<long, Track> tracks = new();

    // The last frame each grid touched something, by the grid and the other entity. Written on
    // Havok's threads.
    private static readonly ConcurrentDictionary<(long Grid, long Other), int> contacts = new();

    // The last frame each owner was told
    private static readonly Dictionary<long, int> told = new();

    private static volatile bool active;
    private static volatile int frame;

    internal static void Update()
    {
        var config = Plugin.Config;
        active = config.ClangDrivesEnabled;
        if (!active)
        {
            tracks.Clear();
            contacts.Clear();
            return;
        }
        // Once per simulation frame, whatever the wall clock does
        var now = MySession.Static.GameplayFrameCounter;
        if (now == frame)
            return;
        frame = now;

        var window = Math.Max(1, config.ClangDriveWindowFrames);
        if (frame % 60 == 0)
            foreach (var contact in contacts)
                if (frame - contact.Value > ContactFrames)
                    contacts.TryRemove(contact.Key, out _);

        var visited = new HashSet<MyCubeGrid>();
        foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
        {
            if (grid.IsStatic || !visited.Add(grid))
                continue;
            var group = MyCubeGridGroups.Static.Physical.GetGroupNodes(grid);
            visited.UnionWith(group);
            Check(group, window, config);
        }
        foreach (var gone in tracks.Where(t => t.Value.Seen != frame).Select(t => t.Key).ToList())
            tracks.Remove(gone);
    }

    private static void Check(List<MyCubeGrid> group, int window, OctahedronConfig config)
    {
        if (group.Any(g => g.IsStatic || g.Physics == null || g.MarkedForClose || g.IsPreview))
            return;
        var main = group.MaxBy(g => g.BlocksCount);
        if (!Plugin.Simulated(main))
            return;

        double mass = 0;
        var momentum = Vector3D.Zero;
        var center = Vector3D.Zero;
        long signature = group.Count;
        foreach (var grid in group)
        {
            mass += grid.Physics.Mass;
            momentum += (Vector3D)grid.Physics.LinearVelocity * grid.Physics.Mass;
            center += grid.Physics.CenterOfMassWorld * grid.Physics.Mass;
            signature = signature * 31 + grid.EntityId;
        }
        if (mass <= 0)
            return;

        // A grid joining or leaving the group changes its speed at once: measure anew, but keep
        // what it did before, so a ship hit by one breaking up still counts as the slower one
        var key = group.Min(g => g.EntityId);
        if (!tracks.TryGetValue(key, out var track) || track.Signature != signature)
            tracks[key] = track = new Track { Signature = signature, Speed = track?.Speed ?? 0 };
        track.Seen = frame;
        if (frame - track.AccelFrame >= FramesPerSecond)
        {
            track.LastAccel = track.Accel;
            track.Accel = StrongestThrust(group, main) / mass;
            track.AccelFrame = frame;
        }
        var velocity = momentum / mass;
        track.Samples.Enqueue((frame, velocity));
        while (frame - track.Samples.Peek().Frame > window)
            track.Samples.Dequeue();
        var (then, before) = track.Samples.Peek();
        if (frame - then < window)
            return;
        track.Speed = before.Length();
        if (
            Math.Max(track.Speed, velocity.Length()) < config.ClangDriveMinSpeed
            || frame - track.Spared < SparedFrames
        )
            return;

        // The mean of the frames' accelerations, each (v[n] - v[n-1]) * 60
        var measured = (velocity - before).Length() * FramesPerSecond / window;
        var position = center / mass;
        double gravity =
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(position).Length()
            + MyGravityProviderSystem.CalculateArtificialGravityInPoint(position).Length();
        if (measured < config.ClangDriveMinAcceleration + gravity)
            return;
        var accel = Math.Max(
            Math.Max(track.Accel, track.LastAccel),
            StrongestThrust(group, main) / mass
        );
        var limit = config.ClangDriveFactor * accel + gravity;
        if (measured <= limit || !main.IsPowerSwitchOn)
            return;

        var owner = main.BigOwners.FirstOrDefault();
        var players = MySession.Static.Players;
        var inZone = MySessionComponentSafeZones.SafeZones.Any(z =>
            z.Enabled && !z.MarkedForClose && z.Contains(main.PositionComp.WorldAABB.Center)
        );
        var others = Touched(group);
        var skipped =
            owner != 0 && players.IdentityIsNpc(owner) ? "an NPC's"
            : !inZone && others.Count > 0 ? "a collision"
            : others.Any(o => SpeedOf(o) > track.Speed) ? "hit by a faster ship"
            : ShipyardOrders.InBuildSpace(main) ? "in a shipyard"
            : null;
        var peak = PeakFrame(track);
        // Measured anew: one line per event
        track.Samples.Clear();
        var ownerName = owner == 0 ? "nobody" : players.TryGetIdentity(owner)?.DisplayName;
        Plugin.Log.Info(
            $"Clang drive: {main.DisplayName} ({main.EntityId}) of {ownerName} ({owner}) "
                + (skipped == null ? "switched off, " : $"left on, {skipped}, ")
                + $"{(inZone ? "in a safe zone" : "outside safe zones")}: {measured:F2} m/s² over {window} frames (peak frame {peak:F1}), "
                + $"allowed {limit:F2} m/s² ({config.ClangDriveFactor:G}x {accel:F2} thrust + {gravity:F2} gravity), "
                + $"{group.Count} grids, {mass:F0} kg"
        );
        if (skipped != null)
        {
            track.Spared = frame;
            return;
        }
        SwitchOff(group);
        if (owner == 0 || told.TryGetValue(owner, out var last) && frame - last < ChatFrames)
            return;
        told[owner] = frame;
        MyVisualScriptLogicProvider.SendChatMessage(
            $"{main.DisplayName} was switched off: its speed changed faster than its thrusters can change it"
                + (inZone ? ", ramming in a safe zone." : ", a clang drive."),
            "Octahedron",
            owner
        );
    }

    // The thrust of the direction the group's functional thrusters, on or off, push most along,
    // the main grid's axes being the directions. Ion and atmospheric thrusters count as
    // effective as they are where the ship is.
    private static double StrongestThrust(List<MyCubeGrid> group, MyCubeGrid main)
    {
        var matrix = main.WorldMatrix;
        Vector3D[] axes =
        [
            matrix.Forward,
            matrix.Backward,
            matrix.Left,
            matrix.Right,
            matrix.Up,
            matrix.Down,
        ];
        var sums = new double[6];
        foreach (var grid in group)
        foreach (var thruster in grid.GetFatBlocks<MyThrust>())
        {
            if (!thruster.IsFunctional)
                continue;
            var force = thruster.WorldMatrix.Backward * ((IMyThrust)thruster).MaxEffectiveThrust;
            for (var i = 0; i < 6; i++)
                sums[i] += Math.Max(0, Vector3D.Dot(force, axes[i]));
        }
        return sums.Max();
    }

    // The largest acceleration between two frames in the window
    private static double PeakFrame(Track track)
    {
        var peak = 0.0;
        (int Frame, Vector3D Velocity)? last = null;
        foreach (var sample in track.Samples)
        {
            if (last is var (f, v))
                peak = Math.Max(
                    peak,
                    (sample.Velocity - v).Length() * FramesPerSecond / (sample.Frame - f)
                );
            last = sample;
        }
        return peak;
    }

    // The voxels and grids outside the group a grid of it touched within the last second
    private static List<long> Touched(List<MyCubeGrid> group)
    {
        var ids = group.Select(g => g.EntityId).ToHashSet();
        return contacts
            .Where(c =>
                ids.Contains(c.Key.Grid)
                && !ids.Contains(c.Key.Other)
                && frame - c.Value <= ContactFrames
            )
            .Select(c => c.Key.Other)
            .Distinct()
            .ToList();
    }

    // The speed of a touched entity's ship before the window; a voxel or a station stands still
    private static double SpeedOf(long entity)
    {
        if (!MyEntities.TryGetEntityById(entity, out var found) || found is not MyCubeGrid grid)
            return 0;
        var key = MyCubeGridGroups.Static.Physical.GetGroupNodes(grid).Min(g => g.EntityId);
        return tracks.TryGetValue(key, out var track) ? track.Speed : 0;
    }

    // What the Y key does, on every grid of the group: the power switch and every power producer off
    private static void SwitchOff(List<MyCubeGrid> group)
    {
        foreach (var grid in group)
        {
            if (grid.IsPowerSwitchOn)
                grid.SwitchPower();
            grid.ChangePowerProducerStateInternal(MyMultipleEnabledEnum.AllDisabled, -1);
        }
    }

    [HarmonyPatch(typeof(MyGridPhysics), "RigidBody_ContactPointCallback")]
    private static class Contact
    {
        private static void Postfix(MyGridPhysics __instance, ref HkContactPointEvent value)
        {
            if (!active || __instance.Entity is not MyCubeGrid grid)
                return;
            // A door's panels are subparts of their grid: the ship's own
            var other = value.GetOtherEntity(grid)?.GetTopMostParent();
            if (other != null && other is not MyCharacter)
                contacts[(grid.EntityId, other.EntityId)] = frame;
        }
    }
}
