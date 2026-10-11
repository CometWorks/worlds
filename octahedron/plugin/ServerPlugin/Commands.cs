using PluginSdk.Commands;
using VRage.Game.ModAPI;

namespace ServerPlugin;

[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class Commands : CommandModule
{
    [Command("reload", "Rereads the plugin's config file")]
    [Permission(MyPromoteLevel.Admin)]
    public string Reload()
    {
        Plugin.LoadConfig();
        return "Octahedron config reloaded.";
    }
}
