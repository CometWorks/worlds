using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

namespace GravityProbe;

// Test only, for tests/test_gravity_drive.py: every 100 ms or so, a line per grid named "GD ...",
// loose object and character with the name, speed, fastest speed so far and position the server has
// for it. The line of an entity that is gone stays.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private readonly Dictionary<string, string> lines = new();
    private readonly Dictionary<string, float> fastest = new();
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 6 != 0 || MySession.Static == null)
            return;
        var path = Path.Combine(MyFileSystem.UserDataPath, "gravity-probe.txt");
        foreach (var entity in MyEntities.GetEntities())
        {
            var name = entity switch
            {
                MyCubeGrid grid when grid.DisplayName?.StartsWith("GD ") == true =>
                    grid.DisplayName,
                MyFloatingObject => "GD Loose",
                MyCharacter => "GD Character",
                _ => null,
            };
            if (name == null || entity.Physics == null)
                continue;
            var speed = entity.Physics.LinearVelocity.Length();
            fastest[name] = fastest.TryGetValue(name, out var most) ? Math.Max(most, speed) : speed;
            var p = entity.PositionComp.GetPosition();
            lines[name] = string.Format(
                CultureInfo.InvariantCulture,
                "{0};{1};{2};{3};{4};{5}",
                name,
                speed,
                fastest[name],
                p.X,
                p.Y,
                p.Z
            );
        }
        File.WriteAllLines(path + ".tmp", lines.Values);
        File.Move(path + ".tmp", path, true);
    }
}
