using System.Linq;
using PluginSdk.Commands;
using VRage.Game.ModAPI;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace ServerPlugin;

[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class KioskCommands : CommandModule
{
    [Command("kiosks", "Lists the station kiosks with their screen script")]
    [Permission(MyPromoteLevel.Admin)]
    public string List()
    {
        var kiosks = Kiosks.All().ToList();
        if (kiosks.Count == 0)
            return $"No kiosks named '{Plugin.Config.KioskBlockName}' on the stations.";
        return string.Join(
            "\n",
            kiosks
                .OrderBy(k => k.Station)
                .Select(k =>
                    $"{k.Station}: kiosk {k.Kiosk.EntityId}, screen {((IMyTextSurface)k.Kiosk).Script}"
                )
        );
    }
}
