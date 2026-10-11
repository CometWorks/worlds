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
    } = 20_000;

    [IntOption(
        0,
        description: "Only players with less PCU than this get a starter ship; the others build at the station's shipyard. "
            + "Counts the blocks the player built on every cluster node, not their ships in Hangar.",
        Parent = "starter"
    )]
    public int StarterShipPlayerPcuLimit
    {
        get;
        set => SetField(ref field, value);
    } = 30_000;

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
