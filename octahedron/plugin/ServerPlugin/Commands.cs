using PluginSdk.Commands;
using VRage.Game.ModAPI;

namespace ServerPlugin;

[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class Commands : CommandModule
{
    [Command("reload", "Reloads Octahedron.cfg from the instance folder")]
    [Permission(MyPromoteLevel.Admin)]
    public string Reload()
    {
        Plugin.LoadConfig();
        return "Octahedron.cfg reloaded.";
    }
}
