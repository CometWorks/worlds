using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Clustering;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Logging;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game.Entity;
using VRage.Plugins;

// Magnetar compiles the plugin from source and needs the version here
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.2.4.0")]
[assembly: AssemblyFileVersion("1.2.4.0")]

#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "Octahedron";
    public const string Id = "F7FB710B-7B41-45D6-B05C-A01DDE0FEE3A";
    public static readonly Logger Log = Logger.Create(Name);

    // The live config; !oct reload replaces it
    public static OctahedronConfig Config { get; private set; } = new();

    // Discovered by the Quasar agent, which looks for a public property of a PluginConfig type
    public OctahedronConfig PluginConfig => Config;

    // OCTAHEDRON_CONFIG lets every node of a cluster read the same file, the checkout's
    internal static string ConfigPath =>
        Environment.GetEnvironmentVariable("OCTAHEDRON_CONFIG")
        ?? Path.Combine(MyFileSystem.UserDataPath, $"{Name}.cfg");

    private readonly RefillContainers refillContainers = new();
    private readonly WorldEdge worldEdge = new();
    private bool failed;
    private bool encountersFailed;
    private bool moneyFailed;
    private bool leaderboardFailed;

    public void Init(object gameInstance)
    {
        LoadConfig();
        // Before the first save moves CurrentPath to where the world is saved
        MySession.AfterLoading += () => LoadedFrom = MySession.Static?.CurrentPath;
        new Harmony(Name).PatchAll(Assembly.GetExecutingAssembly());
        MesCluster.Init();
        ServerCommands.Register(
            Assembly.GetExecutingAssembly(),
            typeof(Commands),
            typeof(MarketCommands),
            typeof(BlueprintCommand)
        );
    }

    // Called by Magnetar with the manifest's AssetFolder, the Encounters folder
    public void LoadAssets(string folder) => EncounterData.SetFolder(folder);

    public static void LoadConfig()
    {
        Config = ConfigStorage.LoadXml<OctahedronConfig>(ConfigPath);
        Log.Info($"Config loaded from {ConfigPath}");
    }

    // The folder the session loaded from. A cluster node loads its world from premade-world and
    // saves it under Saves before the game is ready, without the folders in Storage no loaded mod
    // owns, like Storage/Octahedron with the patch blueprints (SE-1358); a plain server saves where
    // it loaded from.
    internal static string LoadedFrom { get; private set; }

    public void Update()
    {
        if (failed || !Config.Enabled)
            return;

        try
        {
            refillContainers.Update();
            StationRefillPatch.UpdateNode();
            StationConnectors.Update();
            StationCharging.Update();
            StationRelease.Update();
            StationPatches.Update();
            WorldPatches.Update();
            GridMoves.Update();
            worldEdge.Update();
            NoGravityDrives.Update();
            ClangDrives.Update();
            DriftStop.Update();
            BinocularsInZones.Update();
            TableHologram.Update();
            ShipyardOrders.Update();
            ShipyardOwnBlocks.Update();
            ShipyardToolSpeed.Update();
            FreePrints.Update();
            Market.Update();
            Builds.Update();
            HangarFee.Bind();
            OctFaction.Bind();
            OctFaction.Update();
            Contracts.Update();
            OfflineCharacters.Update();
            Reputation.Update();
            Taxi.Update();
            Police.Update();
        }
        catch (Exception e)
        {
            Log.Critical("Update failed, the plugin stops", e);
            failed = true;
        }

        // The MES gate stops on its own too: MES then spawns ungated
        if (!failed && Config.Enabled)
            MesCluster.Update();

        // Money stops on its own too, so a broken settlement leaves the other rules running
        if (!moneyFailed)
        {
            try
            {
                Money.Update();
            }
            catch (Exception e)
            {
                Log.Critical("The money rules failed and stop", e);
                moneyFailed = true;
            }
        }

        // The leaderboard stops on its own too, it only shows what the other rules did
        if (!failed && !leaderboardFailed)
        {
            try
            {
                Leaderboard.Update();
            }
            catch (Exception e)
            {
                Log.Critical("The leaderboard failed and stops", e);
                leaderboardFailed = true;
            }
        }

        // Encounters stop on their own, so a broken spawn leaves the other rules running
        if (encountersFailed)
            return;
        try
        {
            Encounters.Update();
        }
        catch (Exception e)
        {
            Log.Critical("Encounter spawning failed and stops", e);
            encountersFailed = true;
        }
    }

    internal static readonly PluginClusterClient Cluster = PluginCluster.IsClusterProcess
        ? PluginCluster.ForPlugin(Id)
        : null;

    // On a cluster only the node owning an entity's partition changes it. Unknown (a cluster
    // without views) counts as owned.
    public static bool Simulated(MyEntity entity)
    {
        if (Cluster == null)
            return true;
        var residence = Cluster.LocateEntity(entity.EntityId)?.Residence;
        return residence is null or PluginEntityResidence.Local;
    }

    public void Dispose()
    {
        MesCluster.Dispose();
        ShipyardArm.Dispose();
        Reputation.Dispose();
        Taxi.Dispose();
        Police.Dispose();
        // Harmony patches stay: unpatching all may break other plugins
    }
}
