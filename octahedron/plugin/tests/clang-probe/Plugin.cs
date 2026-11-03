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
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace ClangProbe;

// Test only, for tests/test_clang_drive.py. Every grid named "CL ..." gets a line in clang-probe.txt with
// its name, speed, fastest speed so far, power switch, working power producers, thrust over mass, gravity
// and position; the line of a grid that is gone stays. While its power is on, a grid named
// "CL Push <k> ..." is pushed forward each frame by k times its thrust over mass, as a clang drive would,
// "CL Spike <k> ..." gets k times its thrust over mass, times a second, as one frame's speed change once,
// 5 s after it is first seen, and "CL Thrust ..." has its thrusters at full override. 5 s after a grid is
// first seen, a timer block on it named "CL Start" is triggered and a piston so named is reversed: a
// clang drive's starter.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const int SpikeFrames = 300;

    private readonly Dictionary<string, string> lines = new();
    private readonly Dictionary<string, float> fastest = new();
    private readonly Dictionary<long, int> firstSeen = new();
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
            var accel =
                thrusters.Sum(t => ((IMyThrust)t).MaxEffectiveThrust)
                / Math.Max(1, grid.Physics.Mass);
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
            if (grid.IsPowerSwitchOn)
            {
                if (words[1] == "Push")
                    grid.Physics.LinearVelocity += (Vector3)(
                        grid.WorldMatrix.Forward * (k * accel / 60)
                    );
                else if (words[1] == "Spike" && frame - firstSeen[grid.EntityId] == SpikeFrames)
                    grid.Physics.LinearVelocity += (Vector3)(
                        grid.WorldMatrix.Forward * (k * accel)
                    );
                else if (words[1] == "Thrust")
                    foreach (var t in thrusters)
                        ((IMyThrust)t).ThrustOverridePercentage = 1;
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
            var producers = grid.GetFatBlocks<MyFunctionalBlock>()
                .Count(b => b is IMyPowerProducer && b.Enabled);
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
