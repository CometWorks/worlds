using System.Globalization;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.ModAPI;
using VRage.Plugins;

namespace JumpProbe;

// Test-only server plugin of the jump test (tests/test_jump.py). A headless client can't reach
// a ship hundreds of km away through a remote control, so this sends the server the request a
// game client sends when its pilot confirms a blind jump.
public class Probe : IPlugin
{
    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update() { }

    public void Dispose() { }
}

[CommandRoot("jprobe", "Jump probe", "Jump test commands")]
public sealed class ProbeCommands : CommandModule
{
    private static readonly MethodInfo RequestFromClient = typeof(MyGridJumpDriveSystem).GetMethod(
        "OnRequestJumpFromClient",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    [Command(
        "jump",
        "Blind jumps the grid forward by the distance, for the caller",
        "!jprobe jump <grid id> <meters>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Jump(long gridId, double meters)
    {
        if (!MyEntities.TryGetEntityById(gridId, out MyCubeGrid grid))
            return "NO GRID";
        var drive = grid.GetFatBlocks<MyJumpDrive>().FirstOrDefault();
        if (drive == null)
            return "NO DRIVE";
        var target = grid.WorldMatrix.Translation + grid.WorldMatrix.Forward * meters;
        // As MyCubeGrid.OnJumpRequested: a remote client's delay is always 10 s
        RequestFromClient.Invoke(
            grid.GridSystems.JumpSystem,
            [target, Context.Caller.IdentityId, 10f, (long?)drive.EntityId]
        );
        return string.Create(
            CultureInfo.InvariantCulture,
            $"REQUESTED {grid.DisplayName} {target.X:F0} {target.Y:F0} {target.Z:F0}"
        );
    }
}
