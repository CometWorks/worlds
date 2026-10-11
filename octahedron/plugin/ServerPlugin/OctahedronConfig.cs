using PluginSdk.Config;

namespace ServerPlugin;

// Read from Octahedron.cfg in the server's instance folder. The world's copy is
// worlds/octahedron/config/octahedron.cfg.
public class OctahedronConfig : PluginConfig
{
    [BoolOption("Turns all of the plugin's rules on or off.")]
    public bool Enabled
    {
        get;
        set => SetField(ref field, value);
    } = true;
}
