using System.Collections.Generic;
using PluginSdk.Config;
using VRageMath;

namespace ServerPlugin;

// Read from Octahedron.cfg in the server's instance folder, or from the file
// OCTAHEDRON_CONFIG names. The world's copy is worlds/octahedron/config/octahedron.cfg.
[Section("stations", caption: "Economy Stations")]
[Section("refill", caption: "Refill Containers")]
[Section("edge", caption: "World Edge")]
[Section("starter", caption: "Starter Ship")]
[Section("projections", caption: "Projections")]
[Section("pcu", caption: "PCU Limits")]
[Section("shipyard", caption: "Shipyard Orders")]
[Section("money", caption: "Money")]
[Section("drops", caption: "Container Drops")]
[Section("encounters", caption: "Encounters")]
[Section("mes", caption: "MES Spawn Pattern")]
public class OctahedronConfig : PluginConfig
{
    [BoolOption("Turns all of the plugin's rules on or off.")]
    public bool Enabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Replace the economy's refill of NPC trade stations with the options below. Off: the game refills every station block it knows.",
        Parent = "stations"
    )]
    public bool StationRefillEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [StringOption(
        description: "Name of the station cargo containers and cryo chambers the refill puts loot into and hides from inventory screens. "
            + "Empty: no block gets loot or is hidden.",
        Parent = "stations"
    )]
    public string StationLootContainerName
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [BoolOption(
        "Fill the empty loot containers with the station type's loot.",
        Parent = "stations"
    )]
    public bool StationRefillLoot
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Hide the loot containers from inventory screens. Players can still open them directly.",
        Parent = "stations"
    )]
    public bool StationHideLootContainers
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption("Fill the reactors with fuel.", Parent = "stations")]
    public bool StationRefuelReactors
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption("Charge the batteries fully.", Parent = "stations")]
    public bool StationRechargeBatteries
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption("Fill the hydrogen and oxygen tanks.", Parent = "stations")]
    public bool StationRefillGasTanks
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption("Add 10,000 ice to every gas generator.", Parent = "stations")]
    public bool StationAddIceToGasGenerators
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption("Fill the turrets with ammo.", Parent = "stations")]
    public bool StationReloadTurrets
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [StructOption(
        "NPC-owned blocks topped up on a timer, matched by name: blocks with an inventory to a minimum stock, batteries to a full charge. Empty by default.",
        Parent = "refill"
    )]
    public List<RefillContainer> RefillContainers
    {
        get;
        set => SetField(ref field, value);
    } = new();

    [BoolOption(
        "Brake grids and characters flying out of the world sphere below. Needs no WorldSizeKm, which should stay 0.",
        Parent = "edge"
    )]
    public bool WorldEdgeEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [Vector3DOption("Center of the world sphere, in world coordinates (m).", Parent = "edge")]
    public Vector3D WorldEdgeCenter
    {
        get;
        set => SetField(ref field, value);
    }

    [DoubleOption(
        1,
        description: "Radius of the world sphere (m). Nothing moves outward beyond it; what is beyond it drifts back.",
        Parent = "edge"
    )]
    public double WorldEdgeRadius
    {
        get;
        set => SetField(ref field, value);
    } = 500_000;

    [DoubleOption(
        0,
        description: "Distance from the center where braking starts (m).",
        Parent = "edge"
    )]
    public double WorldEdgeBrakingRadius
    {
        get;
        set => SetField(ref field, value);
    } = 450_000;

    [DoubleOption(
        0,
        description: "Outward speed allowed where braking starts (m/s). Set it to the top speed, so braking sets in gradually.",
        Parent = "edge"
    )]
    public double WorldEdgeBrakingSpeed
    {
        get;
        set => SetField(ref field, value);
    } = 360;

    [DoubleOption(
        0.01,
        10,
        "Shape of the braking curve: the outward speed allowed is the speed above times (distance left to the edge / braking zone width) to this power. "
            + "0.5 brakes at a constant deceleration, 1 cuts the allowed speed linearly.",
        Parent = "edge"
    )]
    public double WorldEdgeBrakingExponent
    {
        get;
        set => SetField(ref field, value);
    } = 0.5;

    [BoolOption(
        "Players get a starter ship with !oct ship: the blueprint loaded into a station's hologram table, built outside the station's safe zone.",
        Parent = "starter"
    )]
    public bool StarterShipEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [StringOption(
        description: "Block subtype of the hologram tables. Only NPC-owned ones give ships.",
        Parent = "starter"
    )]
    public string StarterShipTableSubtype
    {
        get;
        set => SetField(ref field, value);
    } = "LargeBlockConsole";

    [DoubleOption(1, 100, "The player must stand this close to the table (m).", Parent = "starter")]
    public double StarterShipTableReach
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [IntOption(
        0,
        description: "Most PCU a starter ship may have. It is a single grid.",
        Parent = "starter"
    )]
    public int StarterShipMaxShipPcu
    {
        get;
        set => SetField(ref field, value);
    } = 10_000;

    [IntOption(
        0,
        description: "Minutes a player waits between two starter ships. Counted on each server node on its own, and reset by a restart.",
        Parent = "starter"
    )]
    public int StarterShipCooldownMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 15;

    [DoubleOption(
        0,
        description: "Gap between the station's safe zone and the ship (m).",
        Parent = "starter"
    )]
    public double StarterShipClearance
    {
        get;
        set => SetField(ref field, value);
    } = 50;

    [BoolOption(
        "Charge the new ship's batteries. Its cargo, fuel and gas tanks are empty either way.",
        Parent = "starter"
    )]
    public bool StarterShipChargeBatteries
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [DoubleOption(
        description: "Height of the hologram table's top above its block's center (m); negative below it. "
            + "A loaded blueprint is fitted between this and the top of the table's block.",
        Parent = "starter"
    )]
    public double StarterShipHologramTableTop
    {
        get;
        set => SetField(ref field, value);
    } = -0.33;

    [DoubleOption(
        0,
        1,
        "Space kept free around a fitted hologram, on every side (m).",
        Parent = "starter"
    )]
    public double StarterShipHologramMargin
    {
        get;
        set => SetField(ref field, value);
    } = 0.05;

    [BoolOption(
        "Check the world's block limits on the server when a projected block is welded, and refuse it over them. The game's welders check them themselves, mod tools like ToolCore's don't.",
        Parent = "projections"
    )]
    public bool ProjectionLimitsEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [IntOption(
        0,
        description: "Most PCU of a ship or station: the grids joined by rotors, pistons and hinges, not by connectors. "
            + "Placing, welding, projecting, attaching, merging or pasting past it is refused. 0: no limit.",
        Parent = "pcu"
    )]
    public int GroupPcuLimit
    {
        get;
        set => SetField(ref field, value);
    } = 60_000;

    [IntOption(
        0,
        description: "Players get a ship built for free, a starter ship or a shipyard order, only while they have less PCU than this. "
            + "Counts the blocks the player built on every cluster node and their ships in Hangar.",
        Parent = "pcu"
    )]
    public int FreeBuildPcuLimit
    {
        get;
        set => SetField(ref field, value);
    } = 10_000;

    [IntOption(
        0,
        description: "Players order the blueprint loaded into a station's hologram table with !oct order, and the station's shipyard "
            + "builds it for at most this many minutes. 0: no orders.",
        Parent = "shipyard"
    )]
    public int ShipyardOrderMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 60;

    [BoolOption(
        "OCT's account is real, without the economy's top-up toward 1e9: new money comes only from the daily subsidy and OCT's floor, "
            + "and once a day (00:00 UTC) the wealth tax, the subsidy and OCT's balance above its cap are split between the active players. "
            + "Runs on the World Authority of a cluster.",
        Parent = "money"
    )]
    public bool MoneyEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [StringOption(
        description: "Tag of the NPC faction that runs the stations, OCT.",
        Parent = "money"
    )]
    public string MoneyAuthorityFactionTag
    {
        get;
        set => SetField(ref field, value);
    } = "OCT";

    [LongOption(0, description: "Balance of a new player account (SC).", Parent = "money")]
    public long MoneyStartingBalance
    {
        get;
        set => SetField(ref field, value);
    } = 25_000;

    [LongOption(
        0,
        description: "New money per active player each day, into the day's pool (SC).",
        Parent = "money"
    )]
    public long MoneySubsidyPerActivePlayer
    {
        get;
        set => SetField(ref field, value);
    } = 5_000;

    [LongOption(
        0,
        description: "OCT's account is topped up to this whenever it drops below (checked every minute, SC).",
        Parent = "money"
    )]
    public long MoneyOctFloor
    {
        get;
        set => SetField(ref field, value);
    } = 2_000_000;

    [LongOption(
        0,
        description: "OCT's balance above this goes into the day's pool (SC).",
        Parent = "money"
    )]
    public long MoneyOctCap
    {
        get;
        set => SetField(ref field, value);
    } = 20_000_000;

    [IntOption(
        0,
        10_000,
        description: "Daily wealth tax on the part of a balance above its threshold, in hundredths of a percent: 100 = 1 %.",
        Parent = "money"
    )]
    public int MoneyWealthTaxBasisPoints
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [LongOption(
        0,
        description: "Player balances above this pay the wealth tax (SC).",
        Parent = "money"
    )]
    public long MoneyPlayerTaxThreshold
    {
        get;
        set => SetField(ref field, value);
    } = 200_000;

    [LongOption(
        0,
        description: "Faction balances above this pay the wealth tax (SC). OCT pays none, its cap applies instead.",
        Parent = "money"
    )]
    public long MoneyFactionTaxThreshold
    {
        get;
        set => SetField(ref field, value);
    } = 1_000_000;

    [IntOption(
        1,
        365,
        description: "A player online at any time in this many last days is active: gets a share of the day's pool and counts for the subsidy.",
        Parent = "money"
    )]
    public int MoneyActiveDays
    {
        get;
        set => SetField(ref field, value);
    } = 7;

    [BoolOption(
        "Spawn the game's personal drop pods (Unknown Signal), each only for its player. Off: only the strong ones "
            + "(Strong Unknown Signal), the game's 5 % share of the drops, still spawn. Needs EnableContainerDrops in the world settings.",
        Parent = "drops"
    )]
    public bool ContainerDropsPersonal
    {
        get;
        set => SetField(ref field, value);
    }

    [BoolOption(
        "Spawn the NPC encounters Modular Encounters Systems spawned, with its rules, from the plugin's own blueprints. "
            + "Off when the world still runs MES.",
        Parent = "encounters"
    )]
    public bool EncountersEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    // MES spawn gate (MesCluster, CometWorks/mod-mes): see MesCluster/MesSpawnGate.cs
    [BoolOption(
        "Gate MES spawn placements to the cell pattern and density limits below (off: MES spawns as it would without this plugin). The pattern settings are read once, at start.",
        Parent = "mes"
    )]
    public bool MesGateEnabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [DoubleOption(
        1000,
        1e7,
        "Voronoi cell size in metres: the spacing of the cell sites",
        Parent = "mes"
    )]
    public double MesCellSize
    {
        get;
        set => SetField(ref field, value);
    } = 40000;

    [DoubleOption(
        0,
        1e7,
        "Width of the empty band between cell cores in metres; 0 = MesGapSyncDistances x the world's sync distance",
        Parent = "mes"
    )]
    public double MesGap
    {
        get;
        set => SetField(ref field, value);
    } = 0;

    [DoubleOption(
        0,
        100,
        "Gap in sync distances, used when MesGap is 0. 4 = the cluster's automatic partition split distance",
        Parent = "mes"
    )]
    public double MesGapSyncDistances
    {
        get;
        set => SetField(ref field, value);
    } = 4;

    [DoubleOption(
        0,
        1,
        "How far cell sites move off the regular lattice, as a fraction of the cell size",
        Parent = "mes"
    )]
    public double MesJitter
    {
        get;
        set => SetField(ref field, value);
    } = 0.5;

    [LongOption(
        description: "Seed of the cell pattern; every node of a cluster must use the same one",
        Parent = "mes"
    )]
    public long MesSeed
    {
        get;
        set => SetField(ref field, value);
    } = 1321;

    [IntOption(
        1,
        100,
        "How many placements MES may compute for one spawn until one fits a cell core",
        Parent = "mes"
    )]
    public int MesPlacementAttempts
    {
        get;
        set => SetField(ref field, value);
    } = 16;

    [StringOption(
        description: "MES spawning types left alone: SpawningType names, comma separated",
        Parent = "mes"
    )]
    public string MesExemptSpawnTypes
    {
        get;
        set => SetField(ref field, value);
    } = "Creature";

    [DoubleOption(
        0,
        1e7,
        "Radius of the density check around a spawn point in metres",
        Parent = "mes"
    )]
    public double MesDensityRadius
    {
        get;
        set => SetField(ref field, value);
    } = 15000;

    [IntOption(
        0,
        100000,
        "No spawn where this many NPC grids are already within MesDensityRadius (0 = no density limit)",
        Parent = "mes"
    )]
    public int MesMaxNpcGridsInRadius
    {
        get;
        set => SetField(ref field, value);
    } = 12;

    [IntOption(
        0,
        1000000,
        "No spawn once this many NPC grids are online in the cluster (0 = no limit; a cluster build without the online census counts offline partitions too)",
        Parent = "mes"
    )]
    public int MesMaxClusterNpcGrids
    {
        get;
        set => SetField(ref field, value);
    } = 300;

    [StringOption(
        description: "MES versions (MES_SessionCore.ModVersion) the patches were checked against, comma separated",
        Parent = "mes"
    )]
    public string MesAcceptedVersions
    {
        get;
        set => SetField(ref field, value);
    } = "2.75.02";

    [StringOption(
        description: "Accepted instruction hashes of the MES methods the plugin depends on, comma separated (each startup logs the current one)",
        Parent = "mes"
    )]
    public string MesAcceptedMethodHashes
    {
        get;
        set => SetField(ref field, value);
    } = "b6de8061483bbd9a";
}

public struct RefillContainer
{
    [
        StructMember(
            "Name of the blocks to refill (case-insensitive): cargo containers, reactors and other blocks with an inventory get the items, batteries a full charge. Only blocks owned by an NPC are refilled."
        ),
        StructCaption
    ]
    public string ContainerName { get; set; }

    [StructMember("Seconds between refills.")]
    public int IntervalSeconds { get; set; }

    [StructMember("Items each block with an inventory is topped up to. Empty for batteries.")]
    public List<RefillItem> Items { get; set; }
}

public struct RefillItem
{
    [
        StructMember("Item id as Type/Subtype, for example PhysicalGunObject/Welder4Item."),
        StructCaption
    ]
    public string Item { get; set; }

    [StructMember("Amount each container holds at least after a refill.")]
    public int MinimumAmount { get; set; }
}
