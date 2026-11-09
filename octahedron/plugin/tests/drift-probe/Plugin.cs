using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

namespace DriftProbe;

// Test only, for tests/test_drift_stop.py: every 100 ms or so, a line per grid named "DS ..." with
// its speed, spin, whether its body is awake, and its position. The line of a grid that is gone stays.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private readonly Dictionary<string, string> lines = new();
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 6 != 0 || MySession.Static == null)
            return;
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.DisplayName?.StartsWith("DS ") != true
                || grid.Physics == null
            )
                continue;
            var p = grid.PositionComp.GetPosition();
            lines[grid.DisplayName] = string.Format(
                CultureInfo.InvariantCulture,
                "{0};{1};{2};{3};{4};{5};{6}",
                grid.DisplayName,
                grid.Physics.LinearVelocity.Length(),
                grid.Physics.AngularVelocity.Length(),
                grid.Physics.IsActive ? 1 : 0,
                p.X,
                p.Y,
                p.Z
            );
        }
        var path = Path.Combine(MyFileSystem.UserDataPath, "drift-probe.txt");
        File.WriteAllLines(path + ".tmp", lines.Values);
        File.Move(path + ".tmp", path, true);
    }
}
