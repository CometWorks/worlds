using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;
using HarmonyLib;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Definitions;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ServerPlugin;

// The spawn groups MES spawned in Octahedron, as tools/export_encounters.py wrote them
// to Encounters/SpawnGroups.xml, and their blueprints in Encounters/Blueprints
[XmlRoot("SpawnGroups")]
public class SpawnGroupList
{
    [XmlElement("Group")]
    public List<EncounterGroup> Groups { get; set; } = new();
}

public class EncounterGroup
{
    [XmlAttribute]
    public string Name { get; set; }

    [XmlAttribute]
    public ulong Mod { get; set; }

    [XmlAttribute]
    public string ModName { get; set; }

    [XmlAttribute]
    public float Frequency { get; set; } = 1;

    [XmlAttribute]
    public string FactionOverride { get; set; }

    // Base-game groups: owner tags, or the faction types a random NPC faction is taken from
    [XmlAttribute]
    public string BaseGameFactionTags { get; set; }

    [XmlAttribute]
    public string BaseGameFactionTypes { get; set; }

    [XmlAttribute]
    public bool EraseScripts { get; set; }

    [XmlElement("Persistent")]
    public EncounterCondition Persistent { get; set; }

    [XmlElement("Condition")]
    public List<EncounterCondition> Conditions { get; set; } = new();

    [XmlElement("Prefab")]
    public List<EncounterPrefab> Prefabs { get; set; } = new();

    public bool IsBaseGame => Mod == 0;
}

public class EncounterCondition
{
    // Space-separated EncounterType names
    [XmlAttribute]
    public string Types { get; set; } = "";

    [XmlAttribute]
    public float SpaceCargoShipChance { get; set; } = 100;

    [XmlAttribute]
    public float RandomEncounterChance { get; set; } = 100;

    [XmlAttribute]
    public float AtmosphericCargoShipChance { get; set; } = 100;

    [XmlAttribute]
    public float GravityCargoShipChance { get; set; } = 100;

    [XmlAttribute]
    public float PlanetaryInstallationChance { get; set; } = 100;

    [XmlAttribute]
    public float BossEncounterChance { get; set; } = 100;

    [XmlAttribute]
    public float ChanceCeiling { get; set; } = 100;

    [XmlAttribute]
    public string InstallationSize { get; set; } = "Small";

    // A value of 0 or less switches that side of the threat check off
    [XmlAttribute]
    public float ThreatMinimum { get; set; } = -1;

    [XmlAttribute]
    public float ThreatMaximum { get; set; } = -1;

    [XmlAttribute]
    public float ThreatRange { get; set; } = 5000;

    [XmlAttribute]
    public string PlanetWhitelist { get; set; }

    [XmlAttribute]
    public string PlanetBlacklist { get; set; }

    [XmlAttribute]
    public bool PlanetRequiresAtmo { get; set; }

    [XmlAttribute]
    public bool SkipAirDensityCheck { get; set; }

    [XmlAttribute]
    public float MaxGravity { get; set; } = -1;

    [XmlAttribute]
    public string TerrainTypes { get; set; }

    [XmlAttribute]
    public string SandboxVariables { get; set; }

    [XmlAttribute]
    public string FalseSandboxVariables { get; set; }

    [XmlAttribute]
    public bool UniqueEncounter { get; set; }

    [XmlAttribute]
    public bool ForceStaticGrid { get; set; }

    [XmlAttribute]
    public bool DisableDampeners { get; set; }

    // ReputationRange 0: no reputation check
    [XmlAttribute]
    public float ReputationMinimum { get; set; } = -1501;

    [XmlAttribute]
    public float ReputationMaximum { get; set; } = 1501;

    [XmlAttribute]
    public float ReputationRange { get; set; }

    [XmlAttribute]
    public string RemoteControlCode { get; set; }

    [XmlAttribute]
    public float RemoteControlCodeMinDistance { get; set; } = -1;

    [XmlAttribute]
    public float RemoteControlCodeMaxDistance { get; set; } = -1;

    [XmlAttribute]
    public string Faction { get; set; }

    // All, Random, SelectedIndexes, RandomSelectedIndexes or RandomFixedCount
    [XmlAttribute]
    public string PrefabSpawningMode { get; set; } = "All";

    [XmlAttribute]
    public string PrefabIndexes { get; set; }

    // Space-separated name:index,index,... groups; one index of each spawns, -1 none
    [XmlAttribute]
    public string PrefabIndexGroups { get; set; }

    [XmlAttribute]
    public int PrefabFixedCount { get; set; }

    [XmlAttribute]
    public bool AllowPrefabIndexReuse { get; set; }

    [XmlIgnore]
    public EncounterType TypeFlags { get; private set; }

    // The prefabs a spawn of this condition takes (MES SpawnGroupCollection.SelectPrefabIndexes)
    public List<EncounterPrefab> SelectPrefabs(List<EncounterPrefab> prefabs, Random rng)
    {
        var picked = new List<int>();
        void Add(int index)
        {
            if (
                index >= 0
                && index < prefabs.Count
                && (AllowPrefabIndexReuse || !picked.Contains(index))
            )
                picked.Add(index);
        }
        switch (PrefabSpawningMode)
        {
            case "Random":
                Add(rng.Next(prefabs.Count));
                break;
            case "SelectedIndexes":
                foreach (var index in List(PrefabIndexes))
                    Add(int.Parse(index));
                break;
            case "RandomSelectedIndexes":
                foreach (var group in List(PrefabIndexGroups))
                {
                    var indexes = group[(group.IndexOf(':') + 1)..].Split(',');
                    Add(int.Parse(indexes[rng.Next(indexes.Length)]));
                }
                break;
            case "RandomFixedCount":
                for (var i = 0; i < PrefabFixedCount; i++)
                    Add(rng.Next(prefabs.Count));
                break;
            default:
                for (var i = 0; i < prefabs.Count; i++)
                    Add(i);
                break;
        }
        return picked.ConvertAll(i => prefabs[i]);
    }

    public void Init()
    {
        foreach (var name in Types.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            TypeFlags |= Enum.Parse<EncounterType>(name);
    }

    public float Chance(EncounterType type) =>
        type switch
        {
            EncounterType.SpaceCargoShip => SpaceCargoShipChance,
            EncounterType.SpaceRandomEncounter => RandomEncounterChance,
            EncounterType.AtmosphericCargoShip => AtmosphericCargoShipChance,
            EncounterType.GravityCargoShip => GravityCargoShipChance,
            EncounterType.PlanetaryInstallation => PlanetaryInstallationChance,
            _ => BossEncounterChance,
        };

    public static string[] List(string value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
}

public class EncounterPrefab
{
    [XmlAttribute]
    public string Blueprint { get; set; }

    [XmlAttribute]
    public double X { get; set; }

    [XmlAttribute]
    public double Y { get; set; }

    [XmlAttribute]
    public double Z { get; set; }

    [XmlAttribute]
    public float Speed { get; set; }

    // World booleans the prefab's MES behavior sets true once it spawned
    [XmlAttribute]
    public string SetsVariables { get; set; }
}

[Flags]
public enum EncounterType
{
    None = 0,
    SpaceCargoShip = 1,
    SpaceRandomEncounter = 2,
    AtmosphericCargoShip = 4,
    GravityCargoShip = 8,
    PlanetaryInstallation = 16,
    BossEncounterSpace = 32,
    BossEncounterAtmo = 64,
    BossEncounterAny = 128,
}

// Loads the spawn groups and turns their blueprints into prefab definitions on this server
// only, so the game's prefab manager spawns them as MES spawned the mods' prefabs. Clients
// get the grids, not the definitions.
internal static class EncounterData
{
    public const string PrefabPrefix = "Octahedron-Encounter-";

    public static List<EncounterGroup> Groups { get; private set; } = new();

    private static string folder;

    // From Magnetar's asset folder (Octahedron.xml), else OCTAHEDRON_ENCOUNTERS
    public static void SetFolder(string assetFolder) => folder = assetFolder;

    public static string Folder =>
        Environment.GetEnvironmentVariable("OCTAHEDRON_ENCOUNTERS") ?? folder;

    public static bool Load()
    {
        var path = Folder == null ? null : Path.Combine(Folder, "SpawnGroups.xml");
        if (path == null || !File.Exists(path))
        {
            Plugin.Log.Warning(
                $"No encounter spawn groups at {path ?? "(no asset folder)"}: no encounters"
            );
            Groups = new();
            return false;
        }

        using var stream = File.OpenRead(path);
        var list = (SpawnGroupList)new XmlSerializer(typeof(SpawnGroupList)).Deserialize(stream);
        foreach (var group in list.Groups)
        {
            group.Persistent?.Init();
            foreach (var condition in group.Conditions)
                condition.Init();
        }
        Groups = list.Groups;
        ClaimStorage();
        Plugin.Log.Info($"Loaded {Groups.Count} encounter spawn groups from {path}");
        return true;
    }

    // The game saves only the grid storage keys a mod storage definition claims, so the plugin
    // adds one for its key, as a mod's EntityComponents definition would
    private static void ClaimStorage()
    {
        var builder = new MyObjectBuilder_ModStorageComponentDefinition
        {
            Id = new SerializableDefinitionId(
                typeof(MyObjectBuilder_ModStorageComponent),
                "OctahedronEncounters"
            ),
            RegisteredStorageGuids = [Encounters.StorageKey],
        };
        var definition = new MyModStorageComponentDefinition();
        definition.Init(builder, MyModContext.BaseGame);
        var components = (System.Collections.IDictionary)DefinitionSetField(
            "m_entityComponentDefinitions"
        );
        components[definition.Id] = definition;
    }

    // The prefab definition of a blueprint, registered on first use; null if it can't be read
    public static string PrefabName(string blueprint, bool forceStatic)
    {
        var name = PrefabPrefix + blueprint + (forceStatic ? "-static" : "");
        var prefabs = Prefabs();
        if (prefabs.ContainsKey(name))
            return name;

        var path = Path.Combine(Folder, "Blueprints", blueprint);
        if (!File.Exists(path))
        {
            Plugin.Log.Warning($"Encounter blueprint {path} is missing");
            return null;
        }
        MyObjectBuilder_Definitions definitions;
        using (var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        {
            if (
                !MyObjectBuilderSerializerKeen.DeserializeXML(stream, out definitions)
                || definitions.ShipBlueprints is not { Length: > 0 }
            )
            {
                Plugin.Log.Warning($"Encounter blueprint {path} can't be read");
                return null;
            }
        }
        var grids = definitions.ShipBlueprints[0].CubeGrids;
        if (forceStatic)
            foreach (var grid in grids)
                grid.IsStatic = true;

        var builder = new MyObjectBuilder_PrefabDefinition
        {
            Id = new SerializableDefinitionId(typeof(MyObjectBuilder_PrefabDefinition), name),
            CubeGrids = grids,
        };
        var definition = new MyPrefabDefinition();
        definition.Init(builder, MyModContext.BaseGame);
        definition.InitLazy(builder);
        definition.Initialized = true;
        prefabs[name] = definition;
        return name;
    }

    private static Dictionary<string, MyPrefabDefinition> Prefabs() =>
        (Dictionary<string, MyPrefabDefinition>)DefinitionSetField("m_prefabs");

    // A field of the game's definition set, MyDefinitionManager's internal DefinitionSet
    private static object DefinitionSetField(string name)
    {
        var set = MyDefinitionManager.Static.Definitions;
        return AccessTools.Field(set.GetType(), name).GetValue(set);
    }

    public static string[] Variables(EncounterPrefab prefab) =>
        EncounterCondition.List(prefab.SetsVariables);
}
