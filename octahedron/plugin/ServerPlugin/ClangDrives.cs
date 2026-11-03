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
// over 1/60 s, and the rule averages these over the frames of ClangDriveWindowSeconds, so a single
// spike adds only its share. The limit is ClangDriveFactor times the thrust of the strongest
// direction at full override over the mass, plus natural and artificial gravity.
// Outside safe zones a window with a contact with anything outside the group doesn't count: a
// crash isn't punished. Skipped: groups without thrusters, with a static grid (docked to a
// station), owned by an NPC, or in a shipyard's build space. Each cluster node checks what it
// simulates.
internal static class ClangDrives
{
    private const double FramesPerSecond = 60;

    private sealed class Track
    {
        public long Signature;
        public int Seen;
        public readonly Queue<(int Frame, Vector3D Velocity)> Samples = new();
    }

    // By the group's grid with the smallest entity id
    private static readonly Dictionary<long, Track> tracks = new();

    // The last frame each grid touched something, by the grid and the other entity. Written on
    // Havok's threads.
    private static readonly ConcurrentDictionary<(long Grid, long Other), int> contacts = new();

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

        var window = Math.Max(1, (int)Math.Round(config.ClangDriveWindowSeconds * FramesPerSecond));
        if (frame % 60 == 0)
            foreach (var contact in contacts)
                if (frame - contact.Value > window)
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
        long signature = group.Count;
        foreach (var grid in group)
        {
            mass += grid.Physics.Mass;
            momentum += (Vector3D)grid.Physics.LinearVelocity * grid.Physics.Mass;
            signature = signature * 31 + grid.EntityId;
        }
        if (mass <= 0)
            return;

        // A grid joining or leaving the group changes its speed at once: measure anew
        var key = group.Min(g => g.EntityId);
        if (!tracks.TryGetValue(key, out var track) || track.Signature != signature)
            tracks[key] = track = new Track { Signature = signature };
        track.Seen = frame;
        var velocity = momentum / mass;
        track.Samples.Enqueue((frame, velocity));
        while (frame - track.Samples.Peek().Frame > window)
            track.Samples.Dequeue();
        var (then, before) = track.Samples.Peek();
        if (frame - then < window)
            return;

        // The mean of the frames' accelerations, each (v[n] - v[n-1]) * 60
        var seconds = window / FramesPerSecond;
        var measured = (velocity - before).Length() / seconds;
        if (measured < config.ClangDriveMinAcceleration)
            return;
        var thrust = StrongestThrust(group, main);
        if (thrust <= 0)
            return;
        var position = main.Physics.CenterOfMassWorld;
        double gravity =
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(position).Length()
            + MyGravityProviderSystem.CalculateArtificialGravityInPoint(position).Length();
        var limit = config.ClangDriveFactor * thrust / mass + gravity;
        if (measured <= limit)
            return;

        var owner = main.BigOwners.FirstOrDefault();
        var players = MySession.Static.Players;
        if (owner != 0 && players.IdentityIsNpc(owner))
            return;
        var inZone = MySessionComponentSafeZones.SafeZones.Any(z =>
            z.Enabled && !z.MarkedForClose && z.Contains(main.PositionComp.WorldAABB.Center)
        );
        if (!inZone && Touched(group, window))
            return;
        if (!main.IsPowerSwitchOn || ShipyardOrders.InBuildSpace(main))
            return;

        SwitchOff(group);
        var peak = PeakFrame(track);
        track.Samples.Clear();
        var ownerName = owner == 0 ? "nobody" : players.TryGetIdentity(owner)?.DisplayName;
        Plugin.Log.Info(
            $"Clang drive: {main.DisplayName} ({main.EntityId}) of {ownerName} ({owner}) switched off, "
                + $"{(inZone ? "in a safe zone" : "no collision")}: {measured:F2} m/s² over {window} frames (peak frame {peak:F1}), "
                + $"allowed {limit:F2} m/s² ({config.ClangDriveFactor:G}x {thrust / mass:F2} thrust + {gravity:F2} gravity), "
                + $"{group.Count} grids, {mass:F0} kg"
        );
        if (owner != 0)
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

    // Whether a grid of the group touched a voxel or a grid outside it within the window
    private static bool Touched(List<MyCubeGrid> group, int window)
    {
        var ids = group.Select(g => g.EntityId).ToHashSet();
        return contacts.Any(c =>
            ids.Contains(c.Key.Grid) && !ids.Contains(c.Key.Other) && frame - c.Value <= window
        );
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
            var other = value.GetOtherEntity(grid);
            if (other != null && other is not MyCharacter)
                contacts[(grid.EntityId, other.EntityId)] = frame;
        }
    }
}
