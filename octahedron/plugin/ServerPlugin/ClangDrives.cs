using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Components;
using VRageMath;
using IMyPowerProducer = Sandbox.ModAPI.Ingame.IMyPowerProducer;
using IMyThrust = Sandbox.ModAPI.Ingame.IMyThrust;

namespace ServerPlugin;

// A ship whose speed changes in a way the forces on it don't explain is switched off, as the Y key in
// its cockpit does: a clang drive, or a ship braking by ramming a station or another ship in a safe
// zone. The ship is a physical group, the grids on rotors, pistons, connectors and landing gear,
// and its speed is the group's center of mass's, so parts moving inside it cancel out. Time is game
// time: the rule takes the speed change over ClangDriveWindowFrames, so a single spike adds only its
// share. Every force code applies to a body goes through MyPhysicsBody.AddForceInternal: thrusters
// with their dampeners and overrides, a mod's drag (Relative Top Speed brakes a ship above its cruise
// speed), explosions, hits, artificial mass. The rule adds those up, with natural gravity, and judges
// what is left: a clang drive's contacts and constraints are no such forces. What is left may be
// ClangDriveMinAcceleration, plus ClangDriveFactor - 1 times the thrust of the strongest direction at
// full override over the mass, plus the gravity the ground may be holding up. Ships slower than
// ClangDriveMinSpeed are left alone. Outside safe zones a ship that touched anything outside the
// group within the last second isn't punished: a crash, unless what it touched rides inside its
// bounds or is a floating item, loose things rattling in a cargo bay. Inside one, only the faster of
// two colliding ships is switched off. A ship left on is left alone for five seconds more, the
// aftermath of its hit: its wreck's pieces keep bumping into each other. A velocity set from code, by
// a mod (a Trade Lane pulling the ship in or dropping it out), a teleport, a jump, a grid split or the
// game's speed cap, isn't the ship's doing: no window with such a write in it counts. A merge block's
// pull toward the other is, a merge block clang drive's means. The server
// never sets a ship's velocity from a client, so contacts and thrusters, a clang drive's means, are
// never writes. Skipped: groups with a static grid (docked to a station), with no player's grid but
// an NPC's, in a shipyard's build space or with no power producer on. Each cluster node checks what it
// simulates.
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

        // Each frame's velocity, and the speed change the forces applied since the frame before
        // and gravity explain
        public readonly Queue<(int Frame, Vector3D Velocity, Vector3D Explained)> Samples = new();
    }

    // By the group's grid with the smallest entity id
    private static readonly Dictionary<long, Track> tracks = new();

    // The last frame each grid touched something, by the grid and the other entity. Written on
    // Havok's threads.
    private static readonly ConcurrentDictionary<(long Grid, long Other), int> contacts = new();

    // The last frame each grid's velocity was set from code. Written on any thread a mod runs on.
    private static readonly ConcurrentDictionary<long, int> writes = new();

    // The linear impulse code applied to each grid's body since the rule last looked at it. Written on
    // any thread a mod runs on.
    private static readonly ConcurrentDictionary<long, Vector3D> pushes = new();

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
            writes.Clear();
            pushes.Clear();
            return;
        }
        // Once per simulation frame, whatever the wall clock does
        var now = MySession.Static.GameplayFrameCounter;
        if (now == frame)
            return;
        frame = now;

        var window = Math.Max(1, config.ClangDriveWindowFrames);
        if (frame % 60 == 0)
        {
            foreach (var contact in contacts)
                if (frame - contact.Value > ContactFrames)
                    contacts.TryRemove(contact.Key, out _);
            foreach (var write in writes)
                if (frame - write.Value > window)
                    writes.TryRemove(write.Key, out _);
            // The grids that left the scene
            foreach (var push in pushes)
                if (!MyEntities.EntityExists(push.Key))
                    pushes.TryRemove(push.Key, out _);
        }

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
        // Taken every frame, also from the groups skipped, so a push never counts in a later window
        var pushed = Vector3D.Zero;
        foreach (var grid in group)
            if (pushes.TryRemove(grid.EntityId, out var push))
                pushed += push;
        if (group.Any(g => g.IsStatic || g.Physics == null || g.MarkedForClose || g.IsPreview))
            return;
        var main = group.MaxBy(g => g.BlocksCount);
        if (!Plugin.Simulated(main))
            return;

        double mass = 0;
        var momentum = Vector3D.Zero;
        var weight = Vector3D.Zero;
        long signature = group.Count;
        foreach (var grid in group)
        {
            var own = Mass(grid);
            mass += own;
            momentum += (Vector3D)grid.Physics.LinearVelocity * own;
            // The natural gravity Havok pulls the grid's body with
            weight += (Vector3D)grid.Physics.Gravity * own;
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
        var gravity = weight / mass;
        var written = Written(group, window);
        // An impulse changes the body's velocity at once, so what was applied between two looks
        // explains the change between them, whichever side of the physics step it came
        var explained =
            track.Samples.Count == 0
                ? Vector3D.Zero
                : pushed / mass + gravity * (frame - track.Samples.Last().Frame) / FramesPerSecond;
        track.Samples.Enqueue((frame, velocity, explained));
        while (frame - track.Samples.Peek().Frame > window)
            track.Samples.Dequeue();
        var (then, before, _) = track.Samples.Peek();
        if (frame - then < window)
            return;
        track.Speed = before.Length();
        if (
            Math.Max(track.Speed, velocity.Length()) < config.ClangDriveMinSpeed
            || frame - track.Spared < SparedFrames
            || written
        )
            return;

        var change = velocity - before;
        var forces = Vector3D.Zero;
        foreach (var sample in track.Samples.Skip(1))
            forces += sample.Explained;
        // Havok caps a body's speed: at the cap, the part of a push that would make it faster is lost
        var cap = main.Physics.RigidBody?.MaxLinearVelocity ?? float.MaxValue;
        if (velocity.Length() >= 0.99 * cap)
        {
            var heading = Vector3D.Normalize(velocity);
            forces -= Math.Max(0, Vector3D.Dot(forces, heading)) * heading;
        }
        // Means over the window, as (v[n] - v[n-1]) * 60 of each frame
        var measured = change.Length() * FramesPerSecond / window;
        var unexplained = (change - forces).Length() * FramesPerSecond / window;
        var accel = Math.Max(
            Math.Max(track.Accel, track.LastAccel),
            StrongestThrust(group, main) / mass
        );
        var limit =
            config.ClangDriveMinAcceleration
            + (config.ClangDriveFactor - 1) * accel
            + gravity.Length();
        // Not the power switch: it cuts no power, a ship switched off with Y runs on any battery turned
        // on again in its terminal
        if (unexplained <= limit || !group.Any(g => Producers(g).Any(p => p.Enabled)))
            return;

        // The ship is a player's when any grid of it is: an NPC's derelict a player's tug tows doesn't
        // make the tug an NPC's
        var players = MySession.Static.Players;
        var owned = group
            .Where(g => g.BigOwners.Count > 0 && !players.IdentityIsNpc(g.BigOwners[0]))
            .MaxBy(g => g.BlocksCount);
        var owner = owned?.BigOwners[0] ?? 0;
        var npc = owned == null && group.Any(g => g.BigOwners.Count > 0);
        var inZone = MySessionComponentSafeZones.SafeZones.Any(z =>
            z.Enabled && !z.MarkedForClose && z.Contains(main.PositionComp.WorldAABB.Center)
        );
        var others = Touched(group);
        var skipped =
            npc ? "an NPC's"
            : !inZone && others.Count > 0 ? "a collision"
            : others.Any(o => SpeedOf(o) > track.Speed) ? "hit by a faster ship"
            : ShipyardOrders.InBuildSpace(main) ? "in a shipyard"
            : null;
        var peak = PeakFrame(track);
        // Measured anew: one line per event
        track.Samples.Clear();
        var ownerName = owner == 0 ? "nobody" : players.TryGetIdentity(owner)?.DisplayName;
        var applied = forces.Length() * FramesPerSecond / window;
        Plugin.Log.Info(
            $"Clang drive: {main.DisplayName} ({main.EntityId}) of {ownerName} ({owner}) "
                + (skipped == null ? "switched off, " : $"left on, {skipped}, ")
                + $"{(inZone ? "in a safe zone" : "outside safe zones")}: {unexplained:F2} m/s² unexplained over {window} frames "
                + $"({measured:F2} measured, peak frame {peak:F1}, {applied:F2} from forces and gravity), "
                + $"allowed {limit:F2} m/s² ({config.ClangDriveMinAcceleration:G} + {config.ClangDriveFactor - 1:G}x {accel:F2} thrust + {gravity.Length():F2} gravity), "
                + $"{group.Count} grids, {mass:F0} kg, {velocity.Length():F1} m/s"
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

    // The grid's own mass with its cargo. Physics.Mass is its rigid body's, which a grid welded to
    // another (a locked connector) shares with it: summed, the group would count that body twice.
    private static double Mass(MyCubeGrid grid) =>
        grid.Physics.Shape?.MassProperties?.Mass ?? grid.Physics.Mass;

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

    // Whether code set a grid's velocity within the window. A write at frame w changes the velocity
    // sampled at w or w + 1, whichever side of the plugin's update the writer ran; the window's
    // oldest sample is at frame - window. Disabled physics reads as zero velocity: a write too.
    private static bool Written(List<MyCubeGrid> group, int window)
    {
        var written = false;
        foreach (var grid in group)
        {
            if (!grid.Physics.Enabled)
                writes[grid.EntityId] = frame;
            if (writes.TryGetValue(grid.EntityId, out var at) && frame - at <= window)
                written = true;
        }
        return written;
    }

    // The largest acceleration between two frames in the window
    private static double PeakFrame(Track track)
    {
        var peak = 0.0;
        (int Frame, Vector3D Velocity, Vector3D Explained)? last = null;
        foreach (var sample in track.Samples)
        {
            if (last is var (f, v, _))
                peak = Math.Max(
                    peak,
                    (sample.Velocity - v).Length() * FramesPerSecond / (sample.Frame - f)
                );
            last = sample;
        }
        return peak;
    }

    // The voxels and grids outside the group a grid of it touched within the last second, but not a
    // grid wholly inside the group's bounds: a loose block in a cargo bay rattles against the ship
    // with every pulse of a drive
    private static List<long> Touched(List<MyCubeGrid> group)
    {
        var ids = group.Select(g => g.EntityId).ToHashSet();
        var bounds = BoundingBoxD.CreateInvalid();
        foreach (var grid in group)
            bounds.Include(grid.PositionComp.WorldAABB);
        return contacts
            .Where(c =>
                ids.Contains(c.Key.Grid)
                && !ids.Contains(c.Key.Other)
                && frame - c.Value <= ContactFrames
            )
            .Select(c => c.Key.Other)
            .Distinct()
            .Where(other =>
                !MyEntities.TryGetEntityById(other, out var found)
                || found is not MyCubeGrid grid
                || bounds.Contains(grid.PositionComp.WorldAABB) != ContainmentType.Contains
            )
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

    // The power switch and every power producer off on every grid of the group, whatever state each is
    // in. The switch is for the Y key: pressed, it turns the switch and every producer back on.
    private static void SwitchOff(List<MyCubeGrid> group)
    {
        foreach (var grid in group)
        {
            if (grid.IsPowerSwitchOn)
                grid.SwitchPower();
            foreach (var producer in Producers(grid))
                producer.Enabled = false;
        }
    }

    // Batteries, reactors, hydrogen engines, solar panels, wind turbines and modded producers
    private static IEnumerable<MyFunctionalBlock> Producers(MyCubeGrid grid) =>
        grid.GetFatBlocks<MyFunctionalBlock>().Where(b => b is IMyPowerProducer);

    // Every velocity setter of a body ends here: LinearVelocity, LinearVelocityUnsafe, SetSpeeds
    [HarmonyPatch(typeof(MyPhysicsBody), "SetLinearVelocity")]
    private static class SetVelocity
    {
        private static void Postfix(MyPhysicsBody __instance) => Note(__instance);
    }

    [HarmonyPatch(typeof(MyPhysicsBody), nameof(MyPhysicsBody.ClearSpeed))]
    private static class ClearSpeed
    {
        private static void Postfix(MyPhysicsBody __instance) => Note(__instance);
    }

    private static void Note(MyPhysicsBody body)
    {
        if (active && !merging && body.Entity is MyCubeGrid grid)
            writes[grid.EntityId] = MySession.Static?.GameplayFrameCounter ?? frame;
    }

    // Every force code applies to a body ends here, queued or not: thrusters, mods, explosions,
    // hits, artificial mass. The impulse each one adds, as AddForceInternal applies it.
    [HarmonyPatch(typeof(MyPhysicsBody), "AddForceInternal")]
    private static class Push
    {
        private static void Postfix(
            MyPhysicsBody __instance,
            MyPhysicsForceType type,
            Vector3? force,
            Vector3D? position,
            bool activeOnly
        )
        {
            if (
                !active
                || steering
                || force is not { } push
                || !push.IsValid()
                || position is { } at && !at.IsValid()
                || __instance.Entity is not MyCubeGrid grid
                || __instance.IsStatic
                || activeOnly && !__instance.IsActive
                || __instance.RigidBody is not { } body
            )
                return;
            Vector3D impulse;
            switch (type)
            {
                case MyPhysicsForceType.ADD_BODY_FORCE_AND_BODY_TORQUE:
                    body.GetRigidBodyMatrix(out Matrix matrix);
                    impulse =
                        Vector3.TransformNormal(push, matrix)
                        / 60f
                        * (position.HasValue ? 1 : MyFakes.SIMULATION_SPEED);
                    break;
                case MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE:
                    if (!position.HasValue)
                        return;
                    impulse = push;
                    break;
                case MyPhysicsForceType.APPLY_WORLD_FORCE:
                    impulse = push / 60f;
                    break;
                default:
                    return;
            }
            pushes.AddOrUpdate(grid.EntityId, impulse, (_, sum) => sum + impulse);
        }
    }

    // A merge block matching its grid's speed to the other block's adds the same speed change to one
    // and takes it from the other, whatever their masses: a merge block clang drive. Not a write
    // that excuses a window, so the change stays unexplained.
    [ThreadStatic]
    private static bool merging;

    [HarmonyPatch(typeof(MyShipMergeBlock), nameof(MyShipMergeBlock.UpdateBeforeSimulation))]
    private static class Merging
    {
        private static void Prefix() => merging = true;

        private static void Finalizer() => merging = false;
    }

    // A wheel's steering pushes the ship alone, against whatever the wheel touches, its own hull
    // too: a drive could rub a wheel on its ship. Its pushes stay unexplained.
    [ThreadStatic]
    private static bool steering;

    [HarmonyPatch(typeof(MyWheel), "SteeringLogic")]
    private static class Steering
    {
        private static void Prefix() => steering = true;

        private static void Finalizer() => steering = false;
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
            // A floating item is loose cargo, not something a ship crashes into
            if (other != null && other is not MyCharacter and not MyFloatingObject)
                contacts[(grid.EntityId, other.EntityId)] = frame;
        }
    }
}
