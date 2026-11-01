using System.Globalization;
using System.IO;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

namespace GravityProbe;

// Test only, for tests/test_gravity_drive.py: every 100 ms or so, a line per grid named "GD ...",
// loose object and character with the name, speed and position the server has for it.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 6 != 0 || MySession.Static == null)
            return;
        var text = new StringBuilder();
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
            var p = entity.PositionComp.GetPosition();
            text.AppendLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0};{1};{2};{3};{4}",
                    name,
                    entity.Physics.LinearVelocity.Length(),
                    p.X,
                    p.Y,
                    p.Z
                )
            );
        }
        var path = Path.Combine(MyFileSystem.UserDataPath, "gravity-probe.txt");
        File.WriteAllText(path + ".tmp", text.ToString());
        File.Move(path + ".tmp", path, true);
    }
}
