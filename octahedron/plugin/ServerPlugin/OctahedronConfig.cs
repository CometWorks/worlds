using System.Collections.Generic;
using PluginSdk.Config;

namespace ServerPlugin;

// Read from Octahedron.cfg in the server's instance folder, or from the file
// OCTAHEDRON_CONFIG names. The world's copy is worlds/octahedron/config/octahedron.cfg.
[Section("stations", caption: "Economy Stations")]
[Section("refill", caption: "Refill Containers")]
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
