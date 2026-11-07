using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.FileSystem;
using VRage.Game.Components;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace ClangProbe;

// Test only, for tests/test_clang_drive.py. Every grid named "CL ..." gets a line in clang-probe.txt with
// its name, speed, fastest speed so far, power switch, working power producers, thrust over mass, gravity
// and position; the line of a grid that is gone stays. While its power is on, any power producer on
// whatever the power switch says, a grid named
// "CL Push <k> ..." is pushed forward each frame by k times its thrust over mass, as a clang drive would,
// "CL Spike <k> ..." gets k times its thrust over mass, times a second, as one frame's speed change once,
// 5 s after it is first seen, and "CL Thrust ..." has its thrusters at full override. Pushes and spikes are
// impulses straight on the Havok body, past MyPhysicsBody.AddForce, as a clang drive's contacts are.
// "CL Force <k> ..." gets the push of Push through AddForce, as a mod's force comes (Relative Top Speed's
// drag). "CL Drag <k> ..." and "CL Set <k> ..." get the same as Push and Spike, but by setting the
// velocity, as a mod moving the ship does (a Trade Lane pulling it in or dropping it out).
// "CL Reverse <s> ..." has its thrusters pushing forward at full override, and s seconds in the ones pushing
// backward instead: flat out in its best direction, then full throttle the other way. "CL Missile ..."
// has every thruster at full override from the start. 5 s after a grid is
// first seen, a timer block on it named "CL Start" is triggered and a piston so named is reversed: a
// clang drive's starter. A grid named "... Again" is switched back on 1 s after the rule switched it off,
// as the grid event the Y key sends does it on the server (a sebot trader sends it, SE-1132), and isn't
// pushed after that.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const int SpikeFrames = 300;

    private readonly Dictionary<string, string> lines = new();
    private readonly Dictionary<string, float> fastest = new();
    private readonly Dictionary<long, int> firstSeen = new();
    private readonly Dictionary<long, int> switchedOff = new();
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (MySession.Static == null)
            return;
        frame++;
        foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
        {
            var name = grid.DisplayName;
            if (name?.StartsWith("CL ") != true)
                continue;
            if (!firstSeen.ContainsKey(grid.EntityId))
            {
                firstSeen[grid.EntityId] = frame;
                MyLog.Default.WriteLine(
                    $"ClangProbe: {name} ({grid.EntityId}) first seen, static {grid.IsStatic}, physics {grid.Physics != null}, {grid.BlocksCount} blocks"
                );
            }
            if (grid.Physics == null || grid.IsStatic)
                continue;
            var thrusters = grid.GetFatBlocks<MyThrust>().Where(t => t.IsFunctional).ToList();
            // The forward thrust over mass: what Push and Spike are multiples of
            var accel =
                thrusters
                    .Where(t =>
                        Vector3D.Dot(t.WorldMatrix.Backward, grid.WorldMatrix.Forward) > 0.5
                    )
                    .Sum(t => ((IMyThrust)t).MaxEffectiveThrust) / Math.Max(1, grid.Physics.Mass);
            var words = name.Split(' ');
            var k =
                words.Length > 2
                && double.TryParse(
                    words[2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var x
                )
                    ? x
                    : 0;
            var producers = grid.GetFatBlocks<MyFunctionalBlock>()
                .Count(b => b is IMyPowerProducer && b.Enabled);
            var again = name.EndsWith(" Again");
            if (again && producers == 0 && !switchedOff.ContainsKey(grid.EntityId))
                switchedOff[grid.EntityId] = frame;
            if (again && switchedOff.TryGetValue(grid.EntityId, out var off) && frame - off == 60)
                grid.ChangePowerProducerStateInternal(MyMultipleEnabledEnum.AllEnabled, -1);
            if (producers > 0 && !switchedOff.ContainsKey(grid.EntityId))
            {
                var spike = frame - firstSeen[grid.EntityId] == SpikeFrames;
                var forward = (Vector3)grid.WorldMatrix.Forward;
                var mass = grid.Physics.Mass;
                if (words[1] == "Push")
                    grid.Physics.RigidBody.ApplyLinearImpulse(
                        forward * (float)(k * accel * mass / 60)
                    );
                else if (words[1] == "Force")
                    grid.Physics.AddForce(
                        MyPhysicsForceType.APPLY_WORLD_FORCE,
                        forward * (float)(k * accel * mass),
                        null,
                        null
                    );
                else if (words[1] == "Spike" && spike)
                    grid.Physics.RigidBody.ApplyLinearImpulse(forward * (float)(k * accel * mass));
                else if (words[1] == "Drag")
                    grid.Physics.LinearVelocity += forward * (float)(k * accel / 60);
                else if (words[1] == "Set" && spike)
                    grid.Physics.LinearVelocity += forward * (float)(k * accel);
                else if (words[1] is "Thrust" or "Missile")
                    foreach (var t in thrusters)
                        ((IMyThrust)t).ThrustOverridePercentage = 1;
                else if (words[1] == "Reverse")
                {
                    // A thruster pushes toward its back, away from its flame
                    var reversed = frame - firstSeen[grid.EntityId] >= k * 60;
                    foreach (var t in thrusters)
                        ((IMyThrust)t).ThrustOverridePercentage =
                            Vector3.Dot(t.WorldMatrix.Backward, forward) > 0 != reversed ? 1 : 0;
                }
            }
            // A clang drive's starter, as its pilot would use it
            if (frame - firstSeen[grid.EntityId] == SpikeFrames)
                foreach (var block in grid.GetFatBlocks<MyTerminalBlock>())
                    if (block.CustomName.ToString() == "CL Start")
                        switch (block)
                        {
                            case MyTimerBlock timer:
                                ((IMyTimerBlock)timer).Trigger();
                                break;
                            case MyPistonBase piston:
                                ((IMyPistonBase)piston).Reverse();
                                break;
                        }
            if (frame % 6 != 0)
                continue;
            var speed = grid.Physics.LinearVelocity.Length();
            fastest[name] = fastest.TryGetValue(name, out var most) ? Math.Max(most, speed) : speed;
            var p = grid.Physics.CenterOfMassWorld;
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(p).Length();
            File.AppendAllText(
                Path.Combine(MyFileSystem.UserDataPath, "clang-trace.txt"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0};{1};{2:F2};{3};{4};{5};{6:F1};{7:F1};{8:F1}\n",
                    frame,
                    name,
                    speed,
                    grid.BlocksCount,
                    thrusters.Count,
                    producers,
                    p.X,
                    p.Y,
                    p.Z
                )
            );
            lines[name] = string.Format(
                CultureInfo.InvariantCulture,
                "{0};{1};{2};{3};{4};{5};{6};{7};{8};{9}",
                name,
                speed,
                fastest[name],
                grid.IsPowerSwitchOn,
                producers,
                accel,
                gravity,
                p.X,
                p.Y,
                p.Z
            );
        }
        if (frame % 6 != 0)
            return;
        var path = Path.Combine(MyFileSystem.UserDataPath, "clang-probe.txt");
        File.WriteAllLines(path + ".tmp", lines.Values);
        File.Move(path + ".tmp", path, true);
    }
}
