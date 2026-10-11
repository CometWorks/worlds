using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Logging;
using VRage.FileSystem;
using VRage.Plugins;

// Magnetar compiles the plugin from source and needs the version here
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "Octahedron";
    public static readonly Logger Log = Logger.Create(Name);

    // The live config; !oct reload replaces it
    public static OctahedronConfig Config { get; private set; } = new();

    // Discovered by the Quasar agent, which looks for a public property of a PluginConfig type
    public OctahedronConfig PluginConfig => Config;

    // OCTAHEDRON_CONFIG lets every node of a cluster read the same file, the checkout's
    private static string ConfigPath =>
        Environment.GetEnvironmentVariable("OCTAHEDRON_CONFIG")
        ?? Path.Combine(MyFileSystem.UserDataPath, $"{Name}.cfg");

    private readonly RefillContainers refillContainers = new();
    private bool failed;

    public void Init(object gameInstance)
    {
        LoadConfig();
        new Harmony(Name).PatchAll(Assembly.GetExecutingAssembly());
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(Commands));
    }

    public static void LoadConfig()
    {
        Config = ConfigStorage.LoadXml<OctahedronConfig>(ConfigPath);
        Log.Info($"Config loaded from {ConfigPath}");
    }

    public void Update()
    {
        if (failed || !Config.Enabled)
            return;

        try
        {
            refillContainers.Update();
        }
        catch (Exception e)
        {
            Log.Critical("Update failed, the plugin stops", e);
            failed = true;
        }
    }

    public void Dispose()
    {
        // Harmony patches stay: unpatching all may break other plugins
    }
}
