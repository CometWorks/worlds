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
[Section("weapons", caption: "Weapons")]
[Section("drives", caption: "Gravity Drives")]
[Section("clang", caption: "Clang Drives")]
[Section("pcu", caption: "PCU Limits")]
[Section("shipyard", caption: "Shipyard Orders")]
[Section("money", caption: "Money")]
[Section("offlineCharacters", caption: "Offline Characters")]
[Section("hangar", caption: "Hangar")]
[Section("drops", caption: "Container Drops")]
[Section("encounters", caption: "Encounters")]
[Section("mes", caption: "MES Spawn Pattern")]
[Section("market", caption: "Station Market")]
[Section("contracts", caption: "Contracts")]
[Section("leaderboard", caption: "Leaderboard")]
[Section("faction", caption: "OCT Faction")]
[Section("patches", caption: "Station Patches")]
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

    [BoolOption(
        "Keep the stations' connectors trading and shared with the station owner's faction only: a docked ship gets no gas or conveyor access, "
            + "and players can't turn trading off. A ship docked to one that didn't trade is let go.",
        Parent = "stations"
    )]
    public bool StationConnectorsTrade
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Charge the batteries of a ship docked at a station's trading connector from the station, at their input power. "
            + "While the docked ships want more than the station has to spare, the one drawing the most stops charging until it undocks.",
        Parent = "stations"
    )]
    public bool StationDockCharging
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Station connectors hold a docked ship while one of its biggest owners or their faction (not OCT) is online in the station's safe zone, "
            + "and let it go after a countdown otherwise. While no connector of a station is free, the ship docked there the longest gets "
            + "the countdown anyway, until one frees up. Turns the game's own auto unlock off on the stations' connectors.",
        Parent = "stations"
    )]
    public bool StationConnectorRelease
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [IntOption(
        1,
        3600,
        "How often the stations' connectors check who is there and count down, in seconds of simulation time.",
        Parent = "stations"
    )]
    public int StationConnectorCheckSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 60;

    [IntOption(
        0,
        86400,
        "Seconds of simulation time a station connector waits before letting go of a ship whose owners left, or of the longest docked ship of a full station.",
        Parent = "stations"
    )]
    public int StationConnectorCountdownSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 600;

    [BoolOption(
        "Players use the blocks an NPC shares with everyone (inventories, doors, buttons, seats, medical rooms, stores, contracts, "
            + "loading a hologram table) but can't change their settings, names, texts, toolbars or the grid's groups.",
        Parent = "stations"
    )]
    public bool NpcBlocksLocked
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Put every new player into the faction tagged MoneyAuthorityFactionTag, OCT, as a member. Players can leave it.",
        Parent = "faction"
    )]
    public bool NewPlayersJoinOct
    {
        get;
        set => SetField(ref field, value);
    }

    [BoolOption(
        "Blocks a player in OCT places, welds to working order or gets from a shipyard or contract start as No share instead of "
            + "shared with the faction, which is every player. Other factions keep the game's faction share.",
        Parent = "faction"
    )]
    public bool OctBuildsPrivate
    {
        get;
        set => SetField(ref field, value);
    }

    [StringOption(
        description: "Steam IDs of the players who lead OCT, comma separated. NewPlayersJoinOct puts them into OCT as leaders and "
            + "everyone else as a plain member, an admin too, whom the game would make the leader of a faction of NPCs.",
        Parent = "faction"
    )]
    public string OctLeaders
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [BoolOption(
        "OCT's account takes deposits from its leaders only, who alone can take money out. The faction screen still shows "
            + "every member the Deposit button; the server refuses a member's deposit and tells them why.",
        Parent = "faction"
    )]
    public bool OctNoDeposits
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Nobody but the owner grinds a grid of OCT's NPCs, as the stations are, or of a player OctProtectedSteamIds names. "
            + "Hand, ship and ToolCore grinders do nothing to them, anywhere.",
        Parent = "faction"
    )]
    public bool OctGridsNoGrinding
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [StringOption(
        description: "Steam IDs whose grids OctGridsNoGrinding protects like OCT's own, comma separated; first-last is a range. "
            + "Empty on the Octahedron: players may grind the trader bots' ships.",
        Parent = "faction"
    )]
    public string OctProtectedSteamIds
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [BoolOption(
        "OCT members have a reputation, in SC: damaging or grinding a grid another member owns most of lowers it by the "
            + "value lost, paying into OCT's account and sharing blocks with OCT raise it, unsharing lowers it. A member below "
            + "ReputationKickBelow for ReputationKickAfterSeconds is removed from OCT. !oct rep shows it.",
        Parent = "faction"
    )]
    public bool ReputationEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [LongOption(description: "Reputation every OCT member starts with.", Parent = "faction")]
    public long ReputationStart
    {
        get;
        set => SetField(ref field, value);
    } = 10_000;

    [LongOption(
        description: "A member whose reputation stays below this for ReputationKickAfterSeconds is removed from OCT.",
        Parent = "faction"
    )]
    public long ReputationKickBelow
    {
        get;
        set => SetField(ref field, value);
    } = -10_000;

    [IntOption(
        1,
        int.MaxValue,
        "Seconds a member's reputation may stay below ReputationKickBelow before they are removed from OCT. "
            + "The count runs while the server runs, online or not, and starts over once the reputation is back above.",
        Parent = "faction"
    )]
    public int ReputationKickAfterSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 3600;

    [StringOption(
        description: "Seconds before the removal from OCT at which the member is warned again, comma separated. "
            + "They are warned when the count starts too.",
        Parent = "faction"
    )]
    public string ReputationWarnSeconds
    {
        get;
        set => SetField(ref field, value);
    } = "1800,600,300";

    [IntOption(
        0,
        int.MaxValue,
        "A member's first ram of another member's grid in this many seconds is taken as an accident and costs no reputation. 0: every ram costs.",
        Parent = "faction"
    )]
    public int ReputationRamGraceSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 3600;

    [IntOption(
        1,
        3600,
        "Collisions of one member's ship with other members' grids less than this many seconds apart are one ram.",
        Parent = "faction"
    )]
    public int ReputationRamGapSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 10;

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

    [StructOption(
        "Lore data pads a refill rule's Datapad/Datapad item puts into its blocks, one at random each, "
            + "none twice in a block, and only these count toward its minimum (SE-1308). "
            + "{GPS:StorageName:Label} in a text becomes a GPS line of that voxel map's center; "
            + "a pad whose voxel map isn't loaded isn't given out. Empty by default.",
        Parent = "refill"
    )]
    public List<DataPadEntry> DataPads
    {
        get;
        set => SetField(ref field, value);
    } = new();

    // On by default: a Quasar cluster starts from these defaults until octahedron.cfg is staged over them
    [BoolOption(
        "Keep the world a sphere: brake grids and characters flying out of it, shorten jumps at the braking radius, move back what is beyond the radius. Needs no WorldSizeKm, which should stay 0.",
        Parent = "edge"
    )]
    public bool WorldEdgeEnabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [Vector3DOption("Center of the world sphere, in world coordinates (m).", Parent = "edge")]
    public Vector3D WorldEdgeCenter
    {
        get;
        set => SetField(ref field, value);
    }

    [DoubleOption(
        1,
        description: "Radius of the world sphere (m). Nothing moves outward beyond it; what is beyond it is moved back onto it.",
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

    [BoolOption(
        "Turn Target Neutrals off on a new turret or custom turret controller, however it is built, and on a ship made for a player, like one bought from a store. A player can turn it back on.",
        Parent = "weapons"
    )]
    public bool ClearTargetNeutralsEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [BoolOption(
        "Artificial mass and space balls don't react to gravity generators, and the server turns them off on a ship with one in a generator's field. On game version 1.210.* only.",
        Parent = "drives"
    )]
    public bool NoGravityDrivesEnabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "Switch a ship off, as the Y key in its cockpit does, when its speed changes in a way the forces on it (thrusters, mods, explosions, gravity) don't explain: "
            + "a clang drive anywhere, or a ship braking by ramming in a safe zone. Collisions outside safe zones don't count, and in one only the faster ship is switched off.",
        Parent = "clang"
    )]
    public bool ClangDrivesEnabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [DoubleOption(
        1,
        description: "The acceleration the forces on a ship don't explain may be this minus one times its thrust: its strongest direction at full override, divided by its mass. "
            + "Plus the minimum below and the natural gravity.",
        Parent = "clang"
    )]
    public double ClangDriveFactor
    {
        get;
        set => SetField(ref field, value);
    } = 2;

    [IntOption(
        1,
        600,
        "Simulation frames the acceleration is averaged over (6: 100 ms of game time). Each frame's acceleration is its speed change from the frame before over 1/60 s, so a single physics spike adds only its share and server lag changes nothing.",
        Parent = "clang"
    )]
    public int ClangDriveWindowFrames
    {
        get;
        set => SetField(ref field, value);
    } = 6;

    [DoubleOption(
        0,
        description: "Acceleration the forces on a ship don't explain that never counts, whatever the thrust (m/s²): a heavy ship bumping into a station while docking.",
        Parent = "clang"
    )]
    public double ClangDriveMinAcceleration
    {
        get;
        set => SetField(ref field, value);
    } = 3;

    [DoubleOption(
        0,
        description: "Ships slower than this both before and after the window are left alone (m/s).",
        Parent = "clang"
    )]
    public double ClangDriveMinSpeed
    {
        get;
        set => SetField(ref field, value);
    } = 20;

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
    } = 60_000;

    [IntOption(
        0,
        description: "Players order the blueprint loaded into a station's hologram table with the button next to it, and the "
            + "station's shipyard builds it for at most this many minutes. 0: no orders.",
        Parent = "shipyard"
    )]
    public int ShipyardOrderMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 60;

    [StringOption(
        description: "ShipyardOrderMinutes of the shipyards of some stations, as Station name=minutes, stations separated by ;. "
            + "The Moon Shipyard builds the big ships.",
        Parent = "shipyard"
    )]
    public string ShipyardStationOrderMinutes
    {
        get;
        set => SetField(ref field, value);
    } = "Moon Station=240";

    [IntOption(
        1,
        description: "Minutes of each window of a player's turn at a shipyard: to load the components and press the button for "
            + "a paid build, and to take what is left after a build or a towed grid's breakdown.",
        Parent = "shipyard"
    )]
    public int ShipyardTurnMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [IntOption(
        1,
        description: "Minutes a paid build short of components waits for a top-up before it ends by the stall rules.",
        Parent = "shipyard"
    )]
    public int ShipyardTopUpMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [IntOption(
        1,
        description: "Minutes a grid a tow just gave a player is safe from the shipyard's sweep.",
        Parent = "shipyard"
    )]
    public int ShipyardTowGraceMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [IntOption(
        1,
        description: "Minutes a ship an order built may stay in a shipyard's build space, and any other grid there may block "
            + "the next order, once its owner was told. A ship an order built then goes into its orderer's Hangar, or is "
            + "removed and refunded with no free slot; any other grid is moved out of the way.",
        Parent = "shipyard"
    )]
    public int ShipyardClearMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [IntOption(
        1,
        description: "Minutes a player holds a station's hologram table with the blueprint they loaded, at most; loading another "
            + "restarts them. It is cleared and open to everyone again sooner once its ship is in the queue, or when the player leaves the station.",
        Parent = "shipyard"
    )]
    public int ShipyardTableMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 3;

    [IntOption(
        0,
        description: "Seconds a blueprint the shipyard refused stays on a station's hologram table before it is cleared. "
            + "The clients take a moment to show a blueprint, and one cleared before that shows an error or keeps the hologram.",
        Parent = "shipyard"
    )]
    public int ShipyardTableSettleSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [StringOption(
        description: "SteamCMD that !bp (!oct workshop) downloads Workshop blueprints with. Empty: Quasar's managed SteamCMD "
            + "(ManagedRuntime/Tools/SteamCMD above this server's data folder), else steamcmd on the PATH.",
        Parent = "shipyard"
    )]
    public string ShipyardSteamCmd
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [StringOption(
        description: "Folder of the Workshop blueprints !bp (!oct workshop) downloaded, by item and version, shared by the servers "
            + "that name the same one. Kept 7 days from their last use. Empty: OctahedronBlueprints next to Quasar's "
            + "managed SteamCMD, else in this server's data folder.",
        Parent = "shipyard"
    )]
    public string ShipyardWorkshopCache
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [IntOption(
        0,
        description: "A press of a station's shipyard button without a blueprint books a repair slot: the shipyard's welders, containers and "
            + "connectors are the player's until they press it again or no building or welding happened for this many minutes. 0: no repair slots.",
        Parent = "shipyard"
    )]
    public int ShipyardRepairIdleMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [DoubleOption(
        0,
        10,
        "How many times as fast as their default the station shipyards' welders weld. Their deconstructors take a block apart in the time those welders build it. "
            + "Tools anywhere else, players' too, keep their default speed.",
        Parent = "shipyard"
    )]
    public double ShipyardToolSpeed
    {
        get;
        set => SetField(ref field, value);
    } = 2;

    [IntOption(
        0,
        description: "Minutes a player's character may stay alive at a station with the player offline. Then its inventory is kept in the world, "
            + "on a cluster on the World Authority, its space credits go to the player's bank account, it is removed, and the player's next character gets the "
            + "inventory. 0 leaves them there.",
        Parent = "offlineCharacters"
    )]
    public int OfflineCharacterMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 15;

    [IntOption(
        0,
        description: "Minutes a player waits between two free shipyard builds, to slow abuse down a little. It starts when a free build ends with the ship "
            + "handed over, finished or not; a ship removed before that starts none. Kept in the world, on a cluster on the World Authority.",
        Parent = "shipyard"
    )]
    public int FreeBuildCooldownMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [StringOption(
        description: "Components a free shipyard build can't need, comma separated, by subtype, where * matches any text: "
            + "a ship with a block made of one of them is a paid build. The prototech frame by default, which every prototech block needs.",
        Parent = "shipyard"
    )]
    public string FreeBuildExcludedComponents
    {
        get;
        set => SetField(ref field, value);
    } = "PrototechFrame";

    [StringOption(
        description: "Blocks a free shipyard build can't have besides those of FreeBuildExcludedComponents, comma separated, as type/subtype, "
            + "where * matches any text. The type is without MyObjectBuilder_. None by default.",
        Parent = "shipyard"
    )]
    public string FreeBuildBlockedBlocks
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [LongOption(
        0,
        description: "Labor fee of a paid shipyard build, paid to OCT: SC per 100 PCU of the blueprint, rounded up. 0: no fee.",
        Parent = "shipyard"
    )]
    public long ShipyardLaborFeePer100Pcu
    {
        get;
        set => SetField(ref field, value);
    }

    [IntOption(
        1,
        description: "Most ships in a shipyard's queue, the one being built included. "
            + "The queue panel above the hologram table fits 6.",
        Parent = "shipyard"
    )]
    public int ShipyardQueueMax
    {
        get;
        set => SetField(ref field, value);
    } = 6;

    [StringOption(
        description: "Steam IDs of bots whose shipyard orders give way to human players', comma separated; first-last is a range. "
            + "A human player's order goes ahead of the bots' waiting ones and cancels a bot's build in progress, "
            + "and a full queue drops its last bot order for it. The trader bots' on the Octahedron.",
        Parent = "shipyard"
    )]
    public string ShipyardBotSteamIds
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [StringOption(
        description: "Name of the station button panel next to the hologram table. Its first button orders the blueprint "
            + "loaded into the table, or cancels the presser's queue entry, a running build on a second press within 10 s.",
        Parent = "shipyard"
    )]
    public string ShipyardConfirmButtonName
    {
        get;
        set => SetField(ref field, value);
    } = "Button Panel - Station ShipyardConfirm";

    [StringOption(
        description: "Name of the station text panels that show the shipyard's queue.",
        Parent = "shipyard"
    )]
    public string ShipyardQueuePanelName
    {
        get;
        set => SetField(ref field, value);
    } = "Text Panel - Station ShipyardQueue";

    [StringOption(
        description: "Name of the station billboard whose first screen shows the blueprint's bill of materials, "
            + "under the screen's first line. Without one, the hologram table's screen shows it too.",
        Parent = "shipyard"
    )]
    public string ShipyardDetailsPanelName
    {
        get;
        set => SetField(ref field, value);
    } = "Billboard LCD Ship Build Details";

    [IntOption(
        0,
        description: "The hologram table's screen that shows free or paid, what's wrong and to press the button, "
            + "by index: 1 is the console block's large screen.",
        Parent = "shipyard"
    )]
    public int ShipyardQuoteSurface
    {
        get;
        set => SetField(ref field, value);
    } = 1;

    [IntOption(
        0,
        100,
        "A new ship's H2 tanks are filled to this percent: a paid build's materials include the hydrogen.",
        Parent = "shipyard"
    )]
    public int ShipyardHydrogenFillPercent
    {
        get;
        set => SetField(ref field, value);
    } = 50;

    [DoubleOption(
        0.001,
        description: "Liters of hydrogen per kg of ice, as a gas generator makes it. Hydrogen a paid build buys "
            + "at the station is bought as ice, and hydrogen taken from a ship at the station counts as ice.",
        Parent = "shipyard"
    )]
    public double ShipyardHydrogenLitersPerIce
    {
        get;
        set => SetField(ref field, value);
    } = 20;

    [IntOption(
        0,
        100,
        "A new ship's O2 tanks are filled to this percent: a paid build's materials include the oxygen.",
        Parent = "shipyard"
    )]
    public int ShipyardOxygenFillPercent
    {
        get;
        set => SetField(ref field, value);
    } = 50;

    [DoubleOption(
        0.001,
        description: "Liters of oxygen per kg of ice, as a gas generator makes it. Oxygen a paid build buys "
            + "at the station is bought as ice, and oxygen taken from a ship at the station counts as ice.",
        Parent = "shipyard"
    )]
    public double ShipyardOxygenLitersPerIce
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [StringOption(
        description: "Names of the shipyard's rotors, comma-separated, whose rotor heads carry the support arm "
            + "of a build. A shipyard without them builds the ship from a projector on the ship itself.",
        Parent = "shipyard"
    )]
    public string ShipyardArmRotors
    {
        get;
        set => SetField(ref field, value);
    } =
        "Advanced Rotor Top Arm Large, Advanced Rotor Top Arm Small, Advanced Rotor Bottom Arm Large, Advanced Rotor Bottom Arm Small";

    [StringOption(
        description: "The support arm's blocks on a large grid rotor head, as piece=subtype: End (on the rotor head), "
            + "Straight, Corner, Tee (the last pipe), Cap, Connector, Support (holds the projector next to the connector), Projector.",
        Parent = "shipyard"
    )]
    public string ShipyardArmLargeBlocks
    {
        get;
        set => SetField(ref field, value);
    } =
        "End=LargeBlockConveyorPipeEnd, Straight=LargeBlockConveyorPipeSeamless, Corner=LargeBlockConveyorPipeCorner, "
        + "Tee=LargeBlockConveyorPipeT, Cap=LargeBlockConveyorPipeCap, Connector=Connector, Support=LargeBlockArmorBlock, "
        + "Projector=LargeProjector";

    [StringOption(
        description: "The support arm's blocks on a small grid rotor head, as ShipyardArmLargeBlocks.",
        Parent = "shipyard"
    )]
    public string ShipyardArmSmallBlocks
    {
        get;
        set => SetField(ref field, value);
    } =
        "End=ConveyorTubeSmall, Straight=ConveyorTubeSmall, Corner=ConveyorTubeSmallCurved, Tee=ConveyorTubeSmallT, "
        + "Cap=SmallBlockConveyorCap, Connector=ConnectorSmall, Support=SmallBlockArmorBlock, Projector=SmallProjector";

    [DoubleOption(
        0,
        60,
        "The seconds a support arm's plan may take on its worker thread. A ship with no arm by then is refused.",
        Parent = "shipyard"
    )]
    public double ShipyardArmPlanSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [IntOption(
        0,
        description: "The support arm's route trades length against turns: a turn costs as much as this many pipes.",
        Parent = "shipyard"
    )]
    public int ShipyardArmTurnCost
    {
        get;
        set => SetField(ref field, value);
    } = 3;

    [StringOption(
        description: "Blocks the support arm's projector may take the place of when it can't reach the ship otherwise, "
            + "as Type/Subtype with * wildcards, comma-separated; the lightest is given up and the player builds it later.",
        Parent = "shipyard"
    )]
    public string ShipyardArmGiveUpBlocks
    {
        get;
        set => SetField(ref field, value);
    } = "CubeBlock/*Armor*, CubeBlock/*Plating*";

    [DoubleOption(
        0,
        description: "Seconds the support arm's connector has to lock onto the finished ship's before the plugin "
            + "fills its H2 tanks itself.",
        Parent = "shipyard"
    )]
    public double ShipyardArmDockSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 1;

    [BoolOption(
        "A finished ship leaves its support arm as Essentials' !fixship re-pastes a ship: saved, removed from "
            + "the arm and pasted anew, with no grid split. Off: it splits off the arm, and the clients may not be able to use it.",
        Parent = "shipyard"
    )]
    public bool ShipyardRepasteShips
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [BoolOption(
        "OCT's account is real, without the economy's top-up toward 1e9: new money comes only from the subsidy and OCT's floor. "
            + "Once a day (00:00 UTC) the wealth tax goes into a pool; once an hour a part of it, the subsidy and OCT's balance above its cap "
            + "are split between the active players. "
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
        description: "New money per active player each day, paid out a 24th each hour (SC).",
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
        description: "OCT's balance above this is paid out to the active players each hour (SC).",
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
        8_760,
        description: "A player online at any time in this many last hours is active: gets a share of each hourly payout and counts for the subsidy.",
        Parent = "money"
    )]
    public int MoneyActiveHours
    {
        get;
        set => SetField(ref field, value);
    } = 24;

    [LongOption(
        0,
        description: "What a ship costs to retrieve from Hangar at another station than it was stored at, paid to OCT: "
            + "SC per 10 PCU, rounded up. At the same station it is free. 0: free everywhere. Needs Hangar 1.1.0 or later.",
        Parent = "hangar"
    )]
    public long HangarRetrieveFeePer10Pcu
    {
        get;
        set => SetField(ref field, value);
    } = 1;

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

    // MES reputation triggers (SE-1294): see MesCluster/MesReputation.cs
    [BoolOption(
        "MES damage triggers that change the attacker's reputation ignore collisions and fire at most once per MesReputationCooldownMs, and charge only the attacker, not every OCT member.",
        Parent = "mes"
    )]
    public bool MesReputationFix
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [IntOption(
        0,
        3600000,
        "Shortest time in milliseconds between two reputation changes of one MES ship's damage trigger",
        Parent = "mes"
    )]
    public int MesReputationCooldownMs
    {
        get;
        set => SetField(ref field, value);
    } = 1000;

    [BoolOption(
        "The station markets: the Store blocks of OCT's (MoneyAuthorityFactionTag) stations show its real stock and prices, "
            + "and the game generates no store items for them. Kept on the World Authority of a cluster.",
        Parent = "market"
    )]
    public bool MarketEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [IntOption(
        0,
        description: "OCT's tax: what it sells for is this much above its price, what it pays this much below (%).",
        Parent = "market"
    )]
    public int MarketTaxPercent
    {
        get;
        set => SetField(ref field, value);
    } = 2;

    [StringOption(
        description: "Items OCT buys and sells at every station, comma separated, as type/subtype, where * matches any text.",
        Parent = "market"
    )]
    public string MarketCatalog
    {
        get;
        set => SetField(ref field, value);
    } = "Ore/*, Ingot/*, Component/*";

    [StringOption(
        description: "Items left out of the catalog, as MarketCatalog.",
        Parent = "market"
    )]
    public string MarketCatalogExcluded
    {
        get;
        set => SetField(ref field, value);
    } = "*/*Prototech*, */Scrap, Component/ZoneChip, Component/*Plushie*, Component/InhibitorChip";

    [BoolOption(
        "Leave out of the catalog the ingots and components players can't make: those no public production block "
            + "(refinery, assembler, survival kit) has a blueprint for, like the parts of MES's NPC-only blocks. Ores stay.",
        Parent = "market"
    )]
    public bool MarketCatalogBuildableOnly
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [IntOption(
        0,
        description: "OCT buys catalog items at this share of the reference price, the game's minimal price / 10, less the tax (%).",
        Parent = "market"
    )]
    public int MarketOctBuyPercent
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [IntOption(
        0,
        description: "OCT sells catalog items at this share of the reference price, plus the tax (%).",
        Parent = "market"
    )]
    public int MarketOctSellPercent
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [DoubleOption(
        0,
        description: "OCT stops buying an item at a station when it holds this much of it there (t).",
        Parent = "market"
    )]
    public double MarketOctMaxStockTonnes
    {
        get;
        set => SetField(ref field, value);
    } = 200;

    [StringOption(
        description: "OCT's minimum stock at every station, as Type/Subtype=units, comma separated. Once an hour what is completely out "
            + "comes back to this, at most up to MarketOctMaxStockTonnes. OCT's acquisition contracts ask for what it lacks of these.",
        Parent = "market"
    )]
    public string MarketMinStock
    {
        get;
        set => SetField(ref field, value);
    } =
        "Component/SteelPlate=2000,Component/Construction=4000,Component/InteriorPlate=4000,Component/MetalGrid=1000,Component/SmallTube=4000,Component/LargeTube=400,Component/Motor=1000,Component/Computer=4000,Component/Display=400,Component/BulletproofGlass=400,Component/Girder=1000,Ingot/Iron=30000,Ingot/Nickel=10000,Ingot/Silicon=10000,Ingot/Cobalt=6000,Ore/Ice=40000";

    [StringOption(
        description: "Metals players must mine, comma separated: OCT never restocks their ores and ingots, nor the components "
            + "made from those ingots, and asks for none of them in its acquisition contracts, whatever MarketMinStock and "
            + "MarketStationStock say. It still buys them from players and sells what it bought.",
        Parent = "market"
    )]
    public string MarketPlayerMinedMetals
    {
        get;
        set => SetField(ref field, value);
    } = "Gold,Silver,Platinum,Uranium";

    [StringOption(
        description: "What each station produces, besides or instead of MarketMinStock, as Station name=Type/Subtype=units,..., "
            + "stations separated by ;. Once an hour OCT tops these up to the amount; a station named here uses up "
            + "MarketUsePercent of the rest of its MarketMinStock instead. A station not named here restocks all of "
            + "MarketMinStock once it is completely out.",
        Parent = "market"
    )]
    public string MarketStationStock
    {
        get;
        set => SetField(ref field, value);
    } =
        // The hubs make components, the other stations ingots, and the Moon has the ice: a few cargo ships' worth an
        // hour, five times what the other stations want of them, within OCT's 200 t an item
        "Beta Station=Component/SteelPlate=10000,Component/Construction=20000,Component/InteriorPlate=20000,Component/MetalGrid=5000,Component/SmallTube=20000,Component/LargeTube=2000,Component/Motor=5000,Component/Computer=20000,Component/Display=2000,Component/BulletproofGlass=2000,Component/Girder=5000;"
        + "Delta Station=Component/SteelPlate=10000,Component/Construction=20000,Component/InteriorPlate=20000,Component/MetalGrid=5000,Component/SmallTube=20000,Component/LargeTube=2000,Component/Motor=5000,Component/Computer=20000,Component/Display=2000,Component/BulletproofGlass=2000,Component/Girder=5000;"
        + "Alpha Station=Ingot/Iron=150000,Ingot/Nickel=50000,Ingot/Silicon=50000,Ingot/Cobalt=30000;"
        + "Gamma Station=Ingot/Iron=150000,Ingot/Nickel=50000,Ingot/Silicon=50000,Ingot/Cobalt=30000;"
        + "North Station=Ingot/Iron=150000,Ingot/Nickel=50000,Ingot/Silicon=50000,Ingot/Cobalt=30000;"
        + "South Station=Ingot/Iron=150000,Ingot/Nickel=50000,Ingot/Silicon=50000,Ingot/Cobalt=30000;"
        + "Moon Station=Ingot/Iron=150000,Ingot/Nickel=50000,Ingot/Silicon=50000,Ingot/Cobalt=30000,Ore/Ice=200000";

    [StringOption(
        description: "Each station's specialties, which OCT buys at MarketSpecialtyPercent: Station name=Type/Subtype,..., stations separated by ;.",
        Parent = "market"
    )]
    public string MarketSpecialties
    {
        get;
        set => SetField(ref field, value);
    } =
        "Alpha Station=Ore/Platinum,Ingot/Platinum,Ore/Magnesium,Ingot/Magnesium;"
        + "Gamma Station=Ore/Iron,Ingot/Iron,Ore/Nickel,Ingot/Nickel,Ingot/Stone;"
        + "Beta Station=Ore/Gold,Ingot/Gold,Ore/Silver,Ingot/Silver,Ore/Organic;"
        + "Delta Station=Ore/Cobalt,Ingot/Cobalt,Ore/Silicon,Ingot/Silicon;"
        + "Moon Station=Ore/Uranium,Ingot/Uranium";

    [StringOption(
        description: "Each station's share of the reference prices, which OCT buys and sells at there: "
            + "Station name=Type/Subtype=percent,..., stations separated by ;, where * matches any text and the first "
            + "match counts. Items not named stay at 100%. Prices that differ between stations give traders a reason "
            + "to carry goods, and the price drift ends a route once enough has gone along it.",
        Parent = "market"
    )]
    public string MarketStationPrices
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [IntOption(
        0,
        description: "OCT buys a station's specialties at this share of the reference price, less the tax, and sells them at no less (%).",
        Parent = "market"
    )]
    public int MarketSpecialtyPercent
    {
        get;
        set => SetField(ref field, value);
    } = 120;

    [DoubleOption(
        0,
        description: "OCT buys at most this much of a station's specialties a UTC day at the specialty price, the rest at its normal price (t).",
        Parent = "market"
    )]
    public double MarketSpecialtyDailyTonnes
    {
        get;
        set => SetField(ref field, value);
    } = 50;

    [StringOption(
        description: "Reference prices for items the game has none for, SC per unit: Type/Subtype=price, comma separated.",
        Parent = "market"
    )]
    public string MarketPrices
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [DoubleOption(
        0,
        description: "Net tonnes of an item bought from a station's market that double OCT's prices of it there, sold that halve them. 0: fixed prices.",
        Parent = "market"
    )]
    public double MarketPriceDriftTonnes
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [DoubleOption(
        0,
        description: "Hours in which half of a price drift wears off. 0: it stays.",
        Parent = "market"
    )]
    public double MarketPriceDriftHalfLifeHours
    {
        get;
        set => SetField(ref field, value);
    } = 24;

    [DoubleOption(
        1,
        description: "OCT's prices drift at most this many times above or below the reference.",
        Parent = "market"
    )]
    public double MarketPriceDriftMax
    {
        get;
        set => SetField(ref field, value);
    } = 2;

    [IntOption(
        0,
        description: "OCT's prices of an item at a station that has no stock of it, as a share of its prices at the station's "
            + "MarketMinStock or MarketStationStock amount (%). Between the two the price falls in a straight line.",
        Parent = "market"
    )]
    public int MarketScarcityPercent
    {
        get;
        set => SetField(ref field, value);
    } = 130;

    [IntOption(
        0,
        description: "OCT's prices of an item at a station that holds three times its stock amount, as a share of its "
            + "prices at that amount (%); never lower. Traders earn by carrying goods from where they are plentiful to "
            + "where they are short.",
        Parent = "market"
    )]
    public int MarketSurplusPercent
    {
        get;
        set => SetField(ref field, value);
    } = 80;

    [IntOption(
        0,
        description: "Each hour a station named in MarketStationStock uses up this share of its MarketMinStock amount of what it "
            + "doesn't produce (%).",
        Parent = "market"
    )]
    public int MarketUsePercent
    {
        get;
        set => SetField(ref field, value);
    } = 25;

    [IntOption(
        1,
        description: "Minutes between OCT's restocks; each brings that share of an hour's MarketStationStock production "
            + "and MarketUsePercent use, so a station's stock comes in small parts that several traders can share.",
        Parent = "market"
    )]
    public int MarketRestockMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [StringOption(
        description: "Stations where OCT sells zone chips at ZoneChipPrice, comma separated; empty: nowhere. "
            + "A chip keeps a player safe zone up for an hour.",
        Parent = "market"
    )]
    public string ZoneChipStations
    {
        get;
        set => SetField(ref field, value);
    } = "Moon Station";

    [IntOption(1, description: "OCT's price of a zone chip (SC).", Parent = "market")]
    public int ZoneChipPrice
    {
        get;
        set => SetField(ref field, value);
    } = 1000;

    [IntOption(
        1,
        description: "OCT keeps this many zone chips in stock at each of ZoneChipStations, the most one purchase can take.",
        Parent = "market"
    )]
    public int ZoneChipStock
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [BoolOption(
        "OCT posts the stations' contracts instead of the game: hauling between stations, acquisitions of what its stock at a "
            + "station lacks, bounties on NPC grids near a station and salvage of derelicts towed to a station's shipyard. "
            + "OCT's account pays every reward. Runs on the World Authority of a cluster, where the nodes report the grids.",
        Parent = "contracts"
    )]
    public bool ContractsEnabled
    {
        get;
        set => SetField(ref field, value);
    }

    [IntOption(
        0,
        description: "Open hauling contracts kept at each station.",
        Parent = "contracts"
    )]
    public int ContractHaulingPerStation
    {
        get;
        set => SetField(ref field, value);
    } = 3;

    [LongOption(
        0,
        description: "Reward of a hauling contract, before distance (SC).",
        Parent = "contracts"
    )]
    public long ContractHaulingBaseReward
    {
        get;
        set => SetField(ref field, value);
    } = 2_000;

    [LongOption(
        0,
        description: "Reward of a hauling contract per km between the two stations (SC).",
        Parent = "contracts"
    )]
    public long ContractHaulingRewardPerKm
    {
        get;
        set => SetField(ref field, value);
    } = 20;

    [IntOption(
        1,
        description: "Time limit of a hauling contract before distance, in minutes; it grows by a minute for every 3 km.",
        Parent = "contracts"
    )]
    public int ContractHaulingMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 20;

    [IntOption(
        1,
        description: "The most items one acquisition contract asks for.",
        Parent = "contracts"
    )]
    public int ContractAcquisitionMaxAmount
    {
        get;
        set => SetField(ref field, value);
    } = 5_000;

    [IntOption(
        0,
        1_000,
        description: "Reward of an acquisition contract in percent of the items' reference price (the game's minimal price / 10).",
        Parent = "contracts"
    )]
    public int ContractAcquisitionPricePercent
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [IntOption(
        1,
        description: "Time limit of an acquisition contract, in minutes.",
        Parent = "contracts"
    )]
    public int ContractAcquisitionMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 240;

    [IntOption(
        0,
        description: "Open bounty contracts kept at each station, on the most threatening NPC grids.",
        Parent = "contracts"
    )]
    public int ContractBountiesPerStation
    {
        get;
        set => SetField(ref field, value);
    } = 3;

    [DoubleOption(
        1,
        description: "NPC grids up to this far from a station get bounties there (km).",
        Parent = "contracts"
    )]
    public double ContractBountyRangeKm
    {
        get;
        set => SetField(ref field, value);
    } = 30;

    [DoubleOption(
        0,
        description: "Least threat score (MES's) of an NPC grid that gets a bounty.",
        Parent = "contracts"
    )]
    public double ContractBountyMinThreat
    {
        get;
        set => SetField(ref field, value);
    } = 50;

    [DoubleOption(
        0,
        description: "Reward of a bounty per point of the target's threat score (SC).",
        Parent = "contracts"
    )]
    public double ContractBountyRewardPerThreat
    {
        get;
        set => SetField(ref field, value);
    } = 20;

    [IntOption(
        1,
        description: "Time limit of a bounty contract, in minutes.",
        Parent = "contracts"
    )]
    public int ContractBountyMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 120;

    [IntOption(
        0,
        description: "Open salvage contracts kept at each station.",
        Parent = "contracts"
    )]
    public int ContractSalvagePerStation
    {
        get;
        set => SetField(ref field, value);
    } = 2;

    [IntOption(
        1,
        description: "Least blocks of an unowned, movable grid within the bounty range that gets a salvage contract.",
        Parent = "contracts"
    )]
    public int ContractSalvageMinBlocks
    {
        get;
        set => SetField(ref field, value);
    } = 20;

    [StringOption(
        description: "Tags of the NPC factions whose grids are wrecks, comma separated: they get salvage contracts, not bounties.",
        Parent = "contracts"
    )]
    public string ContractSalvageFactionTags
    {
        get;
        set => SetField(ref field, value);
    } = "WRECKS";

    [LongOption(
        0,
        description: "Reward of a salvage contract per tonne of the derelict (SC).",
        Parent = "contracts"
    )]
    public long ContractSalvageRewardPerTonne
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    [DoubleOption(
        1,
        description: "A derelict this close to the station's shipyard grid (m, center to center) is delivered.",
        Parent = "contracts"
    )]
    public double ContractSalvageRadius
    {
        get;
        set => SetField(ref field, value);
    } = 150;

    [IntOption(
        1,
        description: "Time limit of a salvage contract, in minutes.",
        Parent = "contracts"
    )]
    public int ContractSalvageMinutes
    {
        get;
        set => SetField(ref field, value);
    } = 180;

    [IntOption(
        0,
        description: "On a cluster: open salvage contracts kept on wrecks in offline partitions, at any distance from a station.",
        Parent = "contracts"
    )]
    public int ContractWreckContracts
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [IntOption(
        1,
        description: "Wreck contracts pick at random from this many of the biggest wrecks, by blocks.",
        Parent = "contracts"
    )]
    public int ContractWreckPool
    {
        get;
        set => SetField(ref field, value);
    } = 10;

    [DoubleOption(
        0,
        description: "A movable grid without power counts as a wreck once its partition has been offline for this many days.",
        Parent = "contracts"
    )]
    public double ContractWreckOfflineDays
    {
        get;
        set => SetField(ref field, value);
    } = 7;

    [StringOption(
        description: "Name of the text panels that show the faction leaderboard (case-insensitive). Empty: no leaderboard.",
        Parent = "leaderboard"
    )]
    public string LeaderboardBlockName
    {
        get;
        set => SetField(ref field, value);
    } = "Curved LCD Panel - Station Lounge Leaderboard";

    [BoolOption(
        "Rank OCT's players one by one, by name, with their own bank account and grids. Off: OCT is left out with all its players.",
        Parent = "leaderboard"
    )]
    public bool LeaderboardOctPlayers
    {
        get;
        set => SetField(ref field, value);
    }

    [IntOption(
        10,
        description: "Seconds between two counts and rankings of the leaderboard. A count values every grid of the world, a few per tick. On a cluster, grids in offline partitions count as they were last counted.",
        Parent = "leaderboard"
    )]
    public int LeaderboardIntervalSeconds
    {
        get;
        set => SetField(ref field, value);
    } = 600;

    [IntOption(1, 50, "Factions the leaderboard shows.", Parent = "leaderboard")]
    public int LeaderboardRows
    {
        get;
        set => SetField(ref field, value);
    } = 15;

    [DoubleOption(
        0.1,
        5,
        "Font size of the leaderboard text, Monospace, 45 characters a line.",
        Parent = "leaderboard"
    )]
    public double LeaderboardFontSize
    {
        get;
        set => SetField(ref field, value);
    } = 0.45;

    [StructOption(
        "Station patches every process applies to the station and shipyard grids it loads, in this order, "
            + "each once per grid by the grid's ledger (SE-1234). Empty by default.",
        Parent = "patches"
    )]
    public List<StationPatchEntry> StationPatches
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
        StructMember(
            "Item id as Type/Subtype, for example PhysicalGunObject/Welder4Item. Several, separated by commas, count together and each one missing is one of them at random."
        ),
        StructCaption
    ]
    public string Item { get; set; }

    [StructMember("Amount each container holds at least after a refill.")]
    public int MinimumAmount { get; set; }
}

public struct DataPadEntry
{
    [
        StructMember("Title the pad shows in the inventory, unique: it tells the pads apart."),
        StructCaption
    ]
    public string Title { get; set; }

    [StructMember(
        "Text of the pad. {GPS:StorageName:Label} becomes a GPS line of that voxel map's center."
    )]
    public string Text { get; set; }
}

public struct StationPatchEntry
{
    [
        StructMember(
            "Patch id: letters, digits, '.', '_' and '-'. The grid's ledger keeps it once applied."
        ),
        StructCaption
    ]
    public string Id { get; set; }

    [StructMember("Name of the station or shipyard grid, for example Alpha Station Shipyard.")]
    public string Grid { get; set; }

    [StructMember(
        "Ops by grid cell, separated by ';': set:X,Y,Z:Expected:enabled=0|1, name=Text or data=Text; "
            + "del:X,Y,Z:Expected; add:X,Y,Z:Type/Subtype:Forward:Up[:Name]; swap:X,Y,Z:Expected:Type/Subtype. "
            + "Expected is the block's Type/Subtype or Subtype."
    )]
    public string Ops { get; set; }

    [StructMember(
        "Instead of Ops: the blueprint the grid was built from, prepared for it by station_patch.py, "
            + "next to this config file unless the path is absolute. The patch turns the grid into To by the blueprints' diff."
    )]
    public string From { get; set; }

    [StructMember("The new blueprint of the grid, prepared like From.")]
    public string To { get; set; }
}
