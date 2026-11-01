#!/usr/bin/env python3
"""Builds the Octahedron starting world from the game's Star System scenario.

    build_world.py <output folder> [--seed N] [--ore-mod ID]

Keeps EarthLike and the Moon, applies the world settings and mods, places the
7 stations, the Trade Lanes endpoints, the safe zones and the asteroid ring, and
makes the stations the NPC faction's trade stations.
The asteroids are vanilla procedural ones saved as seed and size only, so the
game generates their shape and ores. The ring lies around EarthLike's magnetic
axis as the Aurora plugin draws it at the world's start: the north pole tilted
25 degrees away from the sun.

--seed (default 42) drives one random generator for everything random in the
world: entity ids, the asteroids' places, sizes, rotations and seeds. The same
seed gives the same world. Build the OctahedronOres mod with the same seed
(make_ore_mod.py), so a world with another seed has its ores elsewhere.

--ore-mod is the Workshop id of the OctahedronOres mod; 0 (default) lists it as
a local mod, which needs the mod folder in the game's Mods folder.
"""

import argparse
import copy
import gzip
import math
import random
import re
import shutil
import struct
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

STEAM = Path.home() / ".steam/steam/steamapps"
SCENARIO = STEAM / "common/SpaceEngineers/Content/CustomWorlds/Star System"
CONFIG = Path(__file__).parent / "config"
STATION_BLUEPRINT = Path(__file__).parent / "blueprints/octahedron-station.sbc"
SHIPYARD_BLUEPRINT = Path(__file__).parent / "blueprints/omni-arc-beam-shipyard.sbc"
MES_MODS = CONFIG / "mes-mods.xml"
MES_STORAGE = CONFIG / "storage"
RELATIVE_TOP_SPEED = CONFIG / "relative-top-speed.xml"

NAME = "Octahedron"
DESCRIPTION = "Octahedron public test cluster: EarthLike and its Moon, 7 stations linked by Trade Lanes, an asteroid ring."
KEEP_PLANETS = ("EarthLike", "Moon")

# Altitudes are from the planet's 60 km (EarthLike) or 9.5 km (Moon) radius
STATION_DISTANCE = 140_000.0
# The Moon's gravity ends 12,314 m from its center (9,785 m highest terrain x 5^(1/7))
MOON_STATION_DISTANCE = 15_000.0
# The ring is a flat band 45 to 75 km above EarthLike: whole asteroids stay out of its
# gravity (it ends about 103 km from the center) and clear of Alpha and Gamma at 140 km
RING_INNER = 105_000.0
RING_OUTER = 135_000.0
RING_THICKNESS = 1_000.0
RING_ASTEROIDS = 2000
# The ring's asteroids are dealt out evenly to this many equal sectors around it, counted
# from Alpha Station's direction as ORES.md counts them (ore_stats.py SEGMENT)
RING_SECTORS = 24
# The 4 ice asteroids sit in the middle of the band
RING_RADIUS = 120_000.0
# Surface to surface, between asteroids
ASTEROID_GAP = 200.0
# The se-aurora plugin's default MagneticAxisTilt: its magnetic axis is the planet's
# north tilted this far away from the sun (AuroraRenderer.ComputeMagneticAxis)
AURORA_TILT_DEGREES = 25.0
# Vanilla generator 4 sizes, pow2 storages; the weights favor the smaller ones
ASTEROID_SIZES = ((256, 5), (512, 4), (1024, 1))
# The largest plain vanilla asteroid shape (1024 m storage), filled with ice
ICE_SHAPE = "SuperRock_001"
ICE_MATERIAL = "Ice_02"
VOXEL_GENERATOR = 4

STATION_SAFE_ZONE_RADIUS = 250.0
LANE_ENDPOINT_DISTANCE = 1_000.0
# The Earth end of a Moon lane, farther out, so its rings stay clear of the station's other
# lane ends (they touched another lane's at Delta Station)
MOON_LANE_ENDPOINT_DISTANCE = 1_500.0
LANE_SPEED = 10_000
LANE_RING_INTERVAL = 10_000
# Asteroid surfaces stay this far from a lane's straight line, before lanes_off_sun moves it
LANE_CLEARANCE = 1_000.0
# The Trade_Lane.mwm model: its bounding sphere (radius 211.5 m) lies 13.4 m along the lane
# from the gate's origin
GATE_RADIUS = 225.0
# TradeLaneComputerBlockLogic.RingOffset: a gate stands this far to its computer's right
GATE_OFFSET = 220.0
# Light strobes when the Sun passes behind a gate or a shipyard (SE-809, SE-720). The Sun circles
# in a plane, so all day its rays to a place stay in a flat slab through that place: whatever
# keeps out of the slab never shades it. check_sun_clearance.py sweeps a built world.
# The equatorial square's lanes run this far north of the stations' plane, all level: the least
# that keeps every gate out of the slab of each station's bounding sphere, 9 m to spare at Beta
# and Delta Station. North is the lobbies' side, and clearing to the south would take 370 m.
EQUATOR_LANE_NORTH = 200.0
# How far north the ends of North Station's Moon lane move, North Station's end and the Moon's,
# for the same reason: 10 m to spare. The other lanes clear without moving.
NORTH_MOON_LANE_NORTH = (-150.0, 160.0)
# A shipyard's blocks stay out of the slab of its station's bounding sphere by this much
SHIPYARD_SUN_CLEARANCE = 15.0
# The bounding sphere of a block cell. Blocks are counted by their Min cell, so this also covers
# the 2-cell blocks' other half.
BLOCK_RADIUS = 6.25

NPC_NAME = "Octahedron Authority"
# The game tops it back up every economy tick; with the Octahedron plugin's money rules
# (MoneyEnabled) it is cut to MoneyOctCap at their first run and stays real money. Buying from
# a station needs nothing from its cargo.
NPC_BALANCE = 1_000_000_000
NPC_TAG = "OCT"
# Blocks players use on a station, shared with everyone; everything else stays with the owner's faction
PUBLIC_BLOCKS = {
    "MedicalRoom", "Projector", "StoreBlock", "ContractBlock", "VendingMachine", "Door",
    "AirtightSlideDoor", "ButtonPanel", "Cockpit", "ShipConnector",
}  # fmt: skip
# The one container per station the economy's refill puts loot into, through the Octahedron
# plugin's StationLootContainerName (config/octahedron.cfg). It stays with the faction on every
# station, so the loot it gets each economy tick can't be farmed.
LOOT_CONTAINER = "Station Loot"
LOOT_CONTAINER_BLUEPRINT_NAME = "Cargo - Station - Tools Bottles Ammo (shared)"
# The station's 4 toolboxes by its "Free tools!" signs, renamed, shared with everyone and shown in
# inventory screens. The Octahedron plugin keeps 3 welders, grinders and drills in each, every one
# Enhanced or Proficient at random, topped up every minute (RefillContainers in config/octahedron.cfg).
FREE_TOOLS = "Free Tools"
FREE_TOOLS_BLUEPRINT_NAME = "* Toolbox - Station - Hidden Special (shared)"
FREE_TOOLS_COUNT = 4
# What the Octahedron plugin looks up by name on every station for the shipyard: its hologram
# table, button, queue panel and bill of materials billboard. A new blueprint that lost one
# fails here instead of silently losing the feature.
PLUGIN_BLOCKS = (
    "Console Block - Station ShipyardProjection",
    "Button Panel - Station ShipyardConfirm",
    "Text Panel - Station ShipyardQueue",
    "Billboard LCD Ship Build Details",
)
# Every station has a shipyard (OmniArc beam welders) in sight of its lobby, the station's
# pressurized back half: behind the station and away from the planet, which keeps it off the
# lanes. The pair is centered on the safe zone, and spread apart as far as their block bounds
# stay this far inside the zone's edge.
SAFE_ZONE_MARGIN = 10.0
# The shipyard sits this much higher than the station, along the station's up
SHIPYARD_LIFT = 50.0
# The pole stations' shipyards by Viktor's placement at South Station in game (2026-10-25),
# in the station's (right, up, forward): the direction from the station's block bounds
# center to the shipyard's, and the shipyard's forward and up. Off the station's side with
# the ship build holo table, above the lobby floor, so the windows look down into the middle
# of the shipyard. North Station, upside down, sits like South Station, so its shipyard takes
# the same place. spread_pair centers the pair on the zone along that direction.
POLE_SHIPYARD = (
    (121.3, 72.4, 4.6),
    (0.32292, 0.22092, 0.92028),
    (-0.56464, 0.82534, 0.0),
)
# Then raised this much more, along its own up (the side with its cargo containers), Moon
# Station's along the station's up; then pulled toward its station until it fits the safe zone
# again
SHIPYARD_RAISE = {
    "Moon Station": 125.0,
    "Alpha Station": 30.0,
    "Beta Station": 30.0,
    "Gamma Station": 30.0,
    "Delta Station": 30.0,
}
# Rolled about its long axis, its top leaning toward the station, so the view from the lobby
# passes the near wall into the build volume. Raycast in game from Alpha Station, 5 spots by
# 9 points between the welder walls: 13 m above the station's middle, 33 of 45 clear at +30,
# 20 at -30, 12 unrolled; from the pressurized rooms 9 m up, 15 of 45 at either roll, the
# station's own furniture blocking much of it.
SHIPYARD_ROLL_DEGREES = 30.0
# And turned about the station's up, so the welder wall's middle beam doesn't hide the
# shipyard's center from the lobby. In game from Alpha Station's lobby, 10 spots 9 and 13 m
# up: center clear from 8 at +17.5 and at -17.5 (the station's kitchen blocks the other 2),
# from none unturned.
SHIPYARD_YAW_DEGREES = 17.5
# Its refill stations and lights are shared with everyone. Its containers, connectors and welders go
# to a player for their turn at it, its power never: the Octahedron plugin keeps the reactors at
# SHIPYARD_REACTOR_URANIUM ingots and the batteries charged (RefillContainers in
# config/octahedron.cfg).
SHIPYARD_SHARED = {"MedicalRoom", "ReflectorLight"}
# Off until someone's job turns them on: the connectors and the OmniArc welders
SHIPYARD_OFF = {"ShipConnector", "ConveyorSorter"}
# How far the line between a station and its shipyard is turned about the zone's center, off the
# plane of the Sun's path, so the Sun never goes behind the shipyard (SE-720). Both keep their
# orientation. Picked for the view from the lobby among the turns that clear the Sun: the
# shipyard's center is in sight of 17 to 21 of 26 lobby spots (check_sun_clearance.py), against
# 9 to 16 with the smallest clearing turns, where the near wall's beams get in the way.
SHIPYARD_SUN_TURN = {
    "North Station": 12.0,
    "South Station": 12.0,
    "Alpha Station": 33.0,
    "Gamma Station": 30.0,
}
# Moon Station has no deck to keep level, and turning only the line would take its shipyard out of
# the lobby's view, so the station and its shipyard turn this far as one unit instead.
MOON_SUN_TURN_DEGREES = 22.0
# Delta Station and its shipyard, turned about the zone's center so the station's up leans this
# far toward EarthLike from the ring's axis, which leans 25 degrees away from it there: the lobby
# sees the planet and the deck is as tilted against the Sun as Beta Station's (SE-720)
DELTA_TILT_DEGREES = 50.0
# Each station's color: its shipyard's red frame gets the skin and the color, the station's
# purple walls and furniture only the color. Skin None is the default, painted metal. Colors
# are the game's ColorMaskHSV (hue 0-1, saturation and value offsets -1 to 1); the game adds
# 0.8 to the saturation and 0.45 to the value, so 0.2 is full saturation and 0.25 is 70 %
# brightness.
# The blueprints' accent colors: the shipyard's red and the station's purple
BLUEPRINT_ACCENTS = ((1.0, 0.2, 0.55), (0.778, 0.2, -0.08))
STATION_COLORS = {
    "North Station": (None, (0.0, 0.2, 0.25)),  # red
    "South Station": (None, (0.667, 0.2, 0.25)),  # blue
    "Alpha Station": ("CarbonFibre_Armor", (0.0, -1.0, -0.8)),  # black
    "Beta Station": (None, (0.333, 0.2, 0.25)),  # green
    "Gamma Station": ("CarbonFibre_Armor", (0.5, 0.2, 0.25)),  # cyan
    "Delta Station": (None, (0.083, 0.2, 0.25)),  # orange
    "Moon Station": ("Concrete_Armor", (0.75, 0.2, 0.25)),  # purple
}
SHIPYARD_REACTOR = "Shipyard Reactor"
SHIPYARD_BATTERY = "Shipyard Battery"
# What a station starts with; every other inventory and production queue starts empty
REACTOR_URANIUM = 10_000
# Players get at the shipyard's reactors while they rent it, so those hold only this much
SHIPYARD_REACTOR_URANIUM = 10
# The safe zone block's 5 L, times BlocksInventorySizeMultiplier, of 0.2 L zone chips
ZONE_CHIPS = 75

# Players a server admits (on a cluster: each node)
MAX_PLAYERS = 255  # Viktor 2026-10-23 (#1191 run 3 hit 200 with ~200 pilots)

SETTINGS = {
    "GameMode": "Survival",
    "OnlineMode": "PUBLIC",
    "InventorySizeMultiplier": 10,
    "BlocksInventorySizeMultiplier": 3,
    "AssemblerSpeedMultiplier": 3,
    "AssemblerEfficiencyMultiplier": 3,
    "RefinerySpeedMultiplier": 3,
    "WelderSpeedMultiplier": 3,
    "GrinderSpeedMultiplier": 3,
    "EnvironmentHostility": "NORMAL",
    "WeatherSystem": "true",
    "SyncDistance": 3000,
    "ViewDistance": 15000,
    "EnableAutorespawn": "false",
    "EnableRespawnShips": "false",
    "EnableSpaceSuitRespawn": "false",
    "StartInRespawnScreen": "true",
    # No vanilla world edge: it is a cube that deletes what leaves it, and while it
    # is on, a character or grid that drops out of a Havok cluster's broadphase is
    # killed or deleted (MyPhysics.CreateHkWorld). The 500 km edge is a plugin's.
    "WorldSizeKm": 0,
    # 1M PCU per player: the game gives each player TotalPCU / MaxPlayers, and a
    # dedicated server admits at most MaxPlayers players, so both are set
    "MaxPlayers": MAX_PLAYERS,
    "BlockLimitsEnabled": "PER_PLAYER",
    "TotalPCU": 1_000_000 * MAX_PLAYERS,
    "MaxFactionsCount": 0,
    "EnableCopyPaste": "false",
    "EnableSpectator": "false",
    # Only Scripters and above can edit a programmable block's code; pasted and projected
    # grids lose their scripts for everyone else (MySession.IsUserScripter)
    "EnableIngameScripts": "true",
    "EnableScripterRole": "true",
    # The research progression: every block is unlocked from the start
    "EnableResearch": "false",
    "ExperimentalMode": "true",
    "ProceduralDensity": 0,
    "ProceduralSeed": 0,
    "RandomizeSeed": "false",
    "VoxelGeneratorVersion": VOXEL_GENERATOR,
    "EnableEncounters": "false",
    "CargoShipsEnabled": "false",
    "EnableDrones": "false",
    "GlobalEncounterCap": 0,
    "EnablePlanetaryEncounters": "false",
    # Vanilla drop pods on, but the Octahedron plugin refuses the personal ones ('Unknown
    # Signal', ContainerDropsPersonal): every player gets one every few minutes wherever
    # they fly, the load-test pilots never collect them, and they pile up along the lanes
    # and chain partitions (#1378, #1389). The strong ones ('Strong Unknown Signal') stay.
    "EnableContainerDrops": "true",
    # Our stations trade; the game's own trade factions and their stations stay out
    # (GenerateFactionsOnStart false in the economy component, see add_trade_stations)
    "EnableEconomy": "true",
    "TradeFactionsCount": 0,
    "EnableRadiation": "true",
    "FoodConsumptionRate": 0,
    "EnableSurvivalBuffs": "false",
    "EnableReducedStatsOnRespawn": "false",
    "EnableWolfs": "false",
    "EnableSpiders": "false",
    # Shared limits by block tag (BlockTags.sbc); the OctahedronOres mod adds the OmniArcTools tag
    "LimitBlocksBy": "Tag",
    # No Services Terminal ship storage: on the cluster a stored ship lives only in the
    # world folder of the node that stored it and is lost when that node restarts
    # (SE1-0126). Players park ships with the Hangar plugin instead.
    "GridStorageMaxPerPlayer": 0,
    # Vanilla defaults; the Star System scenario has sparser asteroid ores (1, 60, 0.8)
    "DepositsCountCoefficient": 2,
    "DepositSizeDenominator": 30,
    "HarvestRatioMultiplier": 1,
}

# Per player, by block tag (LimitBlocksBy Tag); vanilla counts built blocks, not
# active ones. OmniArcTools is the six OmniArc tool blocks, from the OctahedronOres mod.
BLOCK_LIMITS = {"OmniArcTools": 4}

# (Workshop id, name), MES and its encounter mods come from mes-mods.xml
MODS = [
    (3415912276, "Skybox: SE1 Alpha Release"),
    (1359618037, "Relative Top Speed"),
    (3816175430, "Trade Lanes MP"),
    # Blocks for the new station
    (1359954841, "Rotary Airlock"),
    # Workshop dependencies directly before the mods that need them
    (3040438530, "ToolCore"),
    (3412657089, "OmniCorp The OmniArc Project: Adv Tools"),
    # Quality of life, mostly client side; the HUD framework before the mods using it
    (3237260730, "Block Culling"),
    (758597413, "Text HUD API"),
    (3170315974, "Leak Finder"),
    (514062285, "Build Info"),
    (
        806331071,
        "Camera Panning - rotate camera view (+reset 1st person character view)",
    ),
    (2958267813, "GPS Extended"),
    (1469072169, "HUD Compass"),
    (
        3032417765,
        "MultiplayerPredictionSwitcher (Interpolated serverside grid control)",
    ),
    (2360788836, "Welder Eyestrain Reduction"),
    # The binoculars in the stations' Free Tools
    (2754014019, "Scope Framework"),
    (2777644246, "Binoculars"),
    # Visuals: meteor effects, and the Grass voxel material's textures (not EarthLike's definition)
    (3685862625, "Enhanced Meteor Visuals"),
    (756153867, "Grass replacement for Earthlike Environment"),
    # LCD scripts; Radio Spectrometry needs the Definition Extension API before it
    (2894286645, "SPY-InfoLCD"),
    (2756894170, "Definition Extension API"),
    (2687324923, "[QoL] Radio Spectrometry"),
    (3730092649, "Arthur's Lcd Mod"),
    # Inventory sorting and assembler quotas, worked out on the client
    (3082595868, "GV Inventory Sorter/Assembly Quota"),
    # Two Kevin MacLeod songs for the Jukebox, our own mod (CometWorks/octahedron-music)
    (3817490449, "Octahedron Music"),
]

XSI = "http://www.w3.org/2001/XMLSchema-instance"
XSI_TYPE = f"{{{XSI}}}type"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


# Vector helpers on 3-tuples


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def mul(a, s):
    return tuple(x * s for x in a)


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def norm(a):
    return mul(a, 1 / math.sqrt(dot(a, a)))


def dist(a, b):
    return math.sqrt(dot(sub(a, b), sub(a, b)))


def perpendicular(a):
    return norm(cross(a, (0, 0, 1) if abs(a[2]) < 0.9 else (1, 0, 0)))


def segment_distance(p, a, b):
    ab = sub(b, a)
    t = max(0.0, min(1.0, dot(sub(p, a), ab) / dot(ab, ab)))
    return dist(p, add(a, mul(ab, t)))


def random_basis(rng):
    """Random orthonormal (forward, up)."""
    forward = norm(tuple(rng.gauss(0, 1) for _ in range(3)))
    return forward, perpendicular(forward)


# XML helpers


def el(parent, tag, text=None, **attrib):
    child = ET.SubElement(parent, tag, {k: str(v) for k, v in attrib.items()})
    if text is not None:
        child.text = str(text)
    return child


def vector(parent, tag, v):
    return el(
        parent, tag, x=repr(float(v[0])), y=repr(float(v[1])), z=repr(float(v[2]))
    )


def set_position(entity, position, forward, up):
    """Writes PositionAndOrientation without the quaternion, which would override Forward and Up."""
    old = entity.find("PositionAndOrientation")
    index = list(entity).index(old) if old is not None else 1
    if old is not None:
        entity.remove(old)
    po = ET.Element("PositionAndOrientation")
    vector(po, "Position", position)
    vector(po, "Forward", forward)
    vector(po, "Up", up)
    entity.insert(index, po)


def set_child(parent, tag, value):
    child = parent.find(tag)
    if child is None:
        child = el(parent, tag)
    child.text = str(value)


class Ids:
    """Entity and identity ids, MyEntityIdentifier style: object type in the top byte."""

    def __init__(self, rng):
        self.rng = rng
        self.used = set()

    def new(self, object_type=1):
        while True:
            value = (object_type << 56) | self.rng.getrandbits(56)
            if value not in self.used:
                self.used.add(value)
                return value


# Voxel storages


def write_7bit(value):
    out = bytearray()
    while value >= 0x80:
        out.append(value & 0x7F | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def chunk(chunk_type, version, payload):
    return (
        write_7bit(chunk_type)
        + write_7bit(version)
        + write_7bit(len(payload))
        + payload
    )


def provider_storage(provider_id, provider_state, size):
    """An unmodified MyOctreeStorage over a data provider, as the game saves one.

    Storage metadata, the data provider, one content and one material provider
    leaf covering the whole storage, end of file. See MyOctreeStorage.SaveInternal.
    """
    tree_height = max(size.bit_length() - 5, 1)
    leaf_key = struct.pack("<Q", tree_height << 60)
    body = b"\x06Octree" + write_7bit(1)
    body += chunk(1, 1, struct.pack("<i3iB", 4, size, size, size, 0))
    body += chunk(9, 2, struct.pack("<i", provider_id) + provider_state)
    body += chunk(5, 3, leaf_key) + chunk(7, 3, leaf_key)
    body += write_7bit(0xFFFF) + write_7bit(0) + write_7bit(0)
    return gzip.compress(body, mtime=0)


def procedural_asteroid(seed, generator_seed, size):
    """MyCompositeShapeProvider, state version 3."""
    state = struct.pack(
        "<IiifIi", 3, VOXEL_GENERATOR, seed, float(size), 0, generator_seed
    )
    return provider_storage(10002, state, size)


def predefined_asteroid(shape, material, size):
    """MyPredefinedDataProvider: a vanilla prefab shape filled with one material."""
    state = struct.pack("<i", 0)
    for text in (shape, material):
        data = text.encode()
        state += write_7bit(len(data)) + data
    return provider_storage(10102, state, size)


def storage_size(path):
    """Edge length of a saved octree storage, from its metadata chunk."""
    data = gzip.open(path).read()
    i = data.index(b"\x01\x01\x11") + 3
    return struct.unpack_from("<4i", data, i)[1]


def voxel_map(ids, storage_name, center, size, forward, up):
    """A VoxelMap sector object; its position is the storage's min corner."""
    right = cross(forward, up)
    half = size / 2
    # Corner = center - R * half extent, R columns are Right, Up, Backward
    corner = sub(center, add(add(mul(right, half), mul(up, half)), mul(forward, -half)))
    voxel = ET.Element(
        "MyObjectBuilder_EntityBase", {XSI_TYPE: "MyObjectBuilder_VoxelMap"}
    )
    el(voxel, "SubtypeName")
    el(voxel, "EntityId", ids.new(6))
    el(voxel, "PersistentFlags", "CastShadows InScene")
    set_position(voxel, corner, forward, up)
    el(voxel, "ContentChanged", "false")
    el(voxel, "StorageName", storage_name)
    el(voxel, "MutableStorage", "true")
    return voxel


# Grids


def clone_grid(grid, ids):
    """Copies a grid with every entity id replaced, references included."""
    return clone_grids([grid], ids)[0]


def clone_grids(grids, ids):
    """Copies grids with every entity id replaced, references between them included, like a
    rotor's TopBlockId to its head on another grid."""
    grids = [copy.deepcopy(grid) for grid in grids]
    old_ids = {
        node.text
        for grid in grids
        for node in grid.iter()
        if node.tag == "EntityId" and node.text and node.text != "0"
    }
    mapping = {old: str(ids.new()) for old in sorted(old_ids)}
    for grid in grids:
        for node in grid.iter():
            if len(node) == 0 and node.text in mapping:
                node.text = mapping[node.text]
    return grids


def grid_bounds(grid):
    """Lowest and highest block Min of the grid, in cells."""
    mins = [
        tuple(int(b.find("Min").get(k)) for k in "xyz")
        for b in grid.find("CubeBlocks")
        if b.find("Min") is not None
    ]
    lo = [min(m[i] for m in mins) for i in range(3)]
    hi = [max(m[i] for m in mins) for i in range(3)]
    return lo, hi


def grid_center(grid):
    """Local center of the grid's block bounds, in meters."""
    lo, hi = grid_bounds(grid)
    return tuple((lo[i] + hi[i]) / 2 * 2.5 for i in range(3))


def place_grid(grid, center, forward, up):
    """Moves the grid so its block bounds' center lands on center."""
    right = cross(forward, up)
    local = grid_center(grid)
    # Grid local axes: X = Right, Y = Up, Z = Backward
    offset = add(add(mul(right, local[0]), mul(up, local[1])), mul(forward, -local[2]))
    set_position(grid, sub(center, offset), forward, up)


def frame(grid):
    """(position, forward, up) of a grid's PositionAndOrientation."""
    po = grid.find("PositionAndOrientation")
    return tuple(
        tuple(float(po.find(tag).get(k)) for k in "xyz")
        for tag in ("Position", "Forward", "Up")
    )


def follow(grid, before, after):
    """Moves the grid with its main grid, which moved from frame before to frame after: a
    subgrid, like a rotor's head, keeps its place relative to the main grid."""
    axes = [
        (cross(f, u), u, mul(f, -1)) for _, f, u in (before, after)
    ]  # Right, Up, Backward

    def move(v):
        local = [dot(v, axis) for axis in axes[0]]
        return add(
            add(mul(axes[1][0], local[0]), mul(axes[1][1], local[1])),
            mul(axes[1][2], local[2]),
        )

    position, forward, up = frame(grid)
    set_position(
        grid,
        add(after[0], move(sub(position, before[0]))),
        norm(move(forward)),
        norm(move(up)),
    )


def as_sector_object(grid):
    grid.tag = "MyObjectBuilder_EntityBase"
    grid.set(XSI_TYPE, "MyObjectBuilder_CubeGrid")
    return grid


def set_items(inventory, items):
    """Replaces an inventory's items with (builder type, subtype, amount) tuples."""
    stack = inventory.find("Items")
    stack.clear()
    for item_id, (kind, subtype, amount) in enumerate(items):
        item = el(stack, "MyObjectBuilder_InventoryItem")
        el(item, "Amount", amount)
        content = el(item, "PhysicalContent")
        content.set(XSI_TYPE, kind)
        el(content, "SubtypeName", subtype)
        el(item, "ItemId", item_id)
    set_child(inventory, "nextItemId", str(len(items)))


def recolor(block, color, skin=False):
    """Paints a block of a blueprint's accent color: color is (skin, hsv) from STATION_COLORS; the
    skin changes only when skin is true."""
    mask = block.find("ColorMaskHSV")
    if mask is None:
        return
    hsv = tuple(float(mask.get(k)) for k in "xyz")
    if not any(
        all(abs(hsv[i] - accent[i]) <= 0.01 for i in range(3))
        for accent in BLUEPRINT_ACCENTS
    ):
        return
    for k, v in zip("xyz", color[1]):
        mask.set(k, repr(v))
    if skin:
        old = block.find("SkinSubtypeId")
        if not color[0]:
            if old is not None:
                block.remove(old)
        elif old is not None:
            old.text = color[0]
        else:
            # Right after the color, where the game writes it
            node = ET.Element("SkinSubtypeId")
            node.text = color[0]
            block.insert(list(block).index(mask) + 1, node)


def stock(block, kind, uranium=REACTOR_URANIUM):
    """Empties the block's inventories and production queue, then loads its starting items.
    Gas tanks keep their gas: it isn't an item."""
    items = []
    if (
        kind == "Reactor"
        and block.findtext("SubtypeName") == "LargeBlockLargeGenerator"
    ):
        items = [("MyObjectBuilder_Ingot", "Uranium", uranium)]
    if kind == "SafeZoneBlock":
        items = [("MyObjectBuilder_Component", "ZoneChip", ZONE_CHIPS)]
    for inventory in block.iter():
        if inventory.get(XSI_TYPE) == "MyObjectBuilder_Inventory":
            set_items(inventory, items)
            items = []
    if block.find("Queue") is not None:
        block.find("Queue").clear()


def clear_program(block):
    """The blueprint's programmable block carries MMaster's Automatic LCDs 2, which the
    stations don't need (SE1-0121). The block stays, empty and off. Older versions of the
    blueprint also had Isy's Inventory Manager, which would sort the Free Tools out of
    their boxes; this clears any programmable block the same way."""
    for tag in ("Program", "Storage", "DefaultRunArgument"):
        for child in block.findall(tag):
            block.remove(child)
    set_child(block, "Enabled", "false")


def station(template, ids, name, center, forward, up, owner):
    grid = clone_grid(template, ids)
    place_grid(grid, center, forward, up)
    set_child(grid, "DisplayName", name)
    set_child(grid, "IsStatic", "true")
    # Blueprints save false here; a grid loaded without physics can't respawn anyone
    set_child(grid, "CreatePhysics", "true")
    set_child(grid, "DestructibleBlocks", "false")
    set_child(grid, "Editable", "false")
    loot, free_tools = None, 0
    named = [block.findtext("CustomName") for block in grid.find("CubeBlocks")]
    for wanted in PLUGIN_BLOCKS:
        assert wanted in named, f"the station blueprint has no {wanted}"
    for block in grid.find("CubeBlocks"):
        recolor(block, STATION_COLORS[name])
        if block.find("Owner") is None:
            continue
        block.find("Owner").text = str(owner)
        kind = block.get(XSI_TYPE).replace("MyObjectBuilder_", "")
        block.find("ShareMode").text = "All" if kind in PUBLIC_BLOCKS else "Faction"
        if block.findtext("CustomName") == LOOT_CONTAINER_BLUEPRINT_NAME:
            loot = block
            set_child(block, "CustomName", LOOT_CONTAINER)
            block.find("ShareMode").text = "Faction"
        if block.findtext("CustomName") == FREE_TOOLS_BLUEPRINT_NAME:
            free_tools += 1
            set_child(block, "CustomName", FREE_TOOLS)
            set_child(block, "ShowInInventory", "true")
            block.find("ShareMode").text = "All"
        stock(block, kind)
        if kind == "MyProgrammableBlock":
            clear_program(block)
        if (
            kind == "MedicalRoom"
            and block.findtext("SubtypeName") == "LargeMedicalRoom"
        ):
            # The spawn selector lists the room by its name: plain "Delta Station"
            set_child(block, "CustomName", name)
            set_child(block, "SpawnName", name)
        if kind == "RadioAntenna":
            set_child(block, "HudText", name)
    assert loot is not None, f"no {LOOT_CONTAINER_BLUEPRINT_NAME} for {LOOT_CONTAINER}"
    assert free_tools == FREE_TOOLS_COUNT, f"{free_tools} {FREE_TOOLS_BLUEPRINT_NAME}"
    return as_sector_object(grid)


def box_corners(grid, center, forward, up):
    """World corners of the grid's block bounds, with the bounds centered on center."""
    lo, hi = grid_bounds(grid)
    right = cross(forward, up)
    corners = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                offset = [
                    s * (hi[i] - lo[i] + 1) * 1.25 for i, s in enumerate((sx, sy, sz))
                ]
                corners.append(
                    add(
                        center,
                        add(
                            add(mul(right, offset[0]), mul(up, offset[1])),
                            mul(forward, -offset[2]),
                        ),
                    )
                )
    return corners


def station_and_shipyard(station_grid, zone, forward, up, away):
    """(station center, (shipyard center, forward, up)) around the safe zone's center.

    The shipyard goes on the side between the station's back, where the lobby is, and away,
    the space side (the Moon side for Moon Station): straight behind the equatorial stations
    and Moon Station, 45 degrees off for the pole stations. The view from the lobby falls on
    its side, along its local X, through its open frame, rolled by SHIPYARD_ROLL_DEGREES.
    spread_pair places the pair along that side, the shipyard lifted by SHIPYARD_LIFT.
    """
    side = norm(add(mul(forward, -1), away))
    level = norm(perpendicular_part(up, side))
    roll = math.radians(SHIPYARD_ROLL_DEGREES)
    yard_right = add(mul(side, math.cos(roll)), mul(level, math.sin(roll)))
    yard_up = add(mul(level, math.cos(roll)), mul(side, -math.sin(roll)))
    # The long axis, which the roll turns about
    yard_forward = cross(level, side)
    # Then turned about the station's up
    yaw = math.radians(SHIPYARD_YAW_DEGREES)
    yard_right, yard_up, yard_forward = (
        rotate(v, up, yaw) for v in (yard_right, yard_up, yard_forward)
    )
    return spread_pair(
        station_grid, zone, forward, up, side, yard_forward, yard_up, SHIPYARD_LIFT
    )


def spread_pair(station_grid, zone, forward, up, side, yard_forward, yard_up, lift):
    """(station center, (shipyard center, forward, up)): the station and its shipyard, as
    oriented, apart along side, the pair's block bounds centered on the zone's center along
    it, the shipyard then lifted by lift along the station's up, and the gap between them the
    largest that keeps every corner of both bounds SAFE_ZONE_MARGIN inside the zone."""
    right = cross(forward, up)
    lo, hi = grid_bounds(station_grid)
    # How far the station's block bounds reach toward side
    reach = sum(
        (hi[i] - lo[i] + 1) * 1.25 * abs(dot(side, axis))
        for i, axis in enumerate((right, up, mul(forward, -1)))
    )
    yard_right = cross(yard_forward, yard_up)
    yard = shipyard_template()
    lo, hi = grid_bounds(yard)
    # How far the shipyard's block bounds reach toward side from their center
    half = sum(
        (hi[i] - lo[i] + 1) * 1.25 * abs(dot(side, axis))
        for i, axis in enumerate((yard_right, yard_up, mul(yard_forward, -1)))
    )

    def place(gap):
        # Bounds from -extent to +extent along side, the station's end first
        extent = (2 * reach + gap + 2 * half) / 2
        station_center = add(zone, mul(side, reach - extent))
        yard_center = add(zone, mul(side, extent - half))
        return station_center, add(yard_center, mul(up, lift))

    def fits(gap):
        station_center, yard_center = place(gap)
        corners = box_corners(station_grid, station_center, forward, up) + box_corners(
            yard, yard_center, yard_forward, yard_up
        )
        return (
            max(dist(c, zone) for c in corners)
            <= STATION_SAFE_ZONE_RADIUS - SAFE_ZONE_MARGIN
        )

    assert fits(0), "the station and its shipyard don't fit the safe zone"
    low, high = 0.0, 2 * STATION_SAFE_ZONE_RADIUS
    while high - low > 0.5:
        if fits((low + high) / 2):
            low = (low + high) / 2
        else:
            high = (low + high) / 2
    station_center, yard_center = place(math.floor(low))
    return station_center, (yard_center, yard_forward, yard_up)


def raise_shipyard(yard, station_center, zone, height, up):
    """(center, forward, up) of the shipyard moved height along up, then pulled straight
    toward the station's center by as little as keeps every corner of its block bounds
    SAFE_ZONE_MARGIN inside the zone."""
    center, forward, yard_up = yard
    center = add(center, mul(up, height))
    toward = norm(sub(station_center, center))
    template = shipyard_template()

    def fits(pull):
        corners = box_corners(
            template, add(center, mul(toward, pull)), forward, yard_up
        )
        return (
            max(dist(c, zone) for c in corners)
            <= STATION_SAFE_ZONE_RADIUS - SAFE_ZONE_MARGIN
        )

    # The fit gets worse again past the zone's center, so step out from no pull
    pull = next(
        (p for p in range(int(dist(center, station_center)) + 1) if fits(p)), None
    )
    assert pull is not None, "the raised shipyard doesn't fit the safe zone"
    return add(center, mul(toward, pull)), forward, yard_up


def block_offsets(grid, forward, up):
    """Where the grid's blocks are from the center of its block bounds, as oriented."""
    right = cross(forward, up)
    local = grid_center(grid)
    return [
        add(
            add(mul(right, x * 2.5 - local[0]), mul(up, y * 2.5 - local[1])),
            mul(forward, local[2] - z * 2.5),
        )
        for x, y, z in (
            tuple(int(m.get(k)) for k in "xyz")
            for m in (b.find("Min") for b in grid.find("CubeBlocks"))
            if m is not None
        )
    ]


def turn_pair(yard, station_grid, station_center, forward, up, zone, sun, degrees):
    """(station center, (shipyard center, forward, up)) with the line between the two turned
    about the zone's center by degrees, up or down along the Sun's axis the way the shipyard
    already lies. Both keep their orientation; raise_shipyard pulls the shipyard back inside
    the zone when the turn takes a corner out."""
    line = sub(yard[0], station_center)
    axis = norm(cross(line, mul(sun, 1 if dot(line, sun) >= 0 else -1)))
    angle = math.radians(degrees)
    turned = add(zone, rotate(sub(station_center, zone), axis, angle))
    assert (
        max(dist(c, zone) for c in box_corners(station_grid, turned, forward, up))
        <= STATION_SAFE_ZONE_RADIUS - SAFE_ZONE_MARGIN
    ), "the turned station doesn't fit the safe zone"
    yard_center = add(zone, rotate(sub(yard[0], zone), axis, angle))
    return turned, raise_shipyard((yard_center, *yard[1:]), turned, zone, 0, up)


def assert_shipyard_off_sun(yard, station_grid, station_center, forward, up, sun):
    """The shipyard's blocks stay out of the slab of the Sun's rays through the station's
    bounding sphere, so from no place on the station the Sun goes behind the shipyard.
    """
    radius = max(math.sqrt(dot(o, o)) for o in block_offsets(station_grid, forward, up))
    heights = [
        dot(add(sub(yard[0], station_center), o), sun)
        for o in block_offsets(shipyard_template(), yard[1], yard[2])
    ]
    # The bounding sphere of a block, counted by its Min, as in check_sun_clearance.py
    slab = radius + 2 * BLOCK_RADIUS + SHIPYARD_SUN_CLEARANCE
    assert min(heights) >= slab or max(heights) <= -slab, (
        f"the Sun goes behind a shipyard: its blocks are {min(heights):.0f} to "
        f"{max(heights):.0f} m from its station's level, must be {slab:.0f} m off"
    )


def shipyard_template():
    """The shipyard blueprint's main grid."""
    return shipyard_grids()[0]


def shipyard_grids():
    """The shipyard blueprint's grids: the main grid first, then its arm rotors' heads."""
    return ET.parse(SHIPYARD_BLUEPRINT).getroot().findall(".//CubeGrid")


def shipyard(template, ids, name, center, forward, up, owner, color):
    """The shipyard's main grid and its rotor heads, from template, shipyard_grids()."""
    grid, *heads = clone_grids(template, ids)
    before = frame(grid)
    place_grid(grid, center, forward, up)
    for head in heads:
        follow(head, before, frame(grid))
        set_child(head, "CreatePhysics", "true")
        set_child(head, "DestructibleBlocks", "false")
        for block in head.find("CubeBlocks"):
            recolor(block, color, skin=True)
    set_child(grid, "DisplayName", name)
    set_child(grid, "IsStatic", "true")
    set_child(grid, "CreatePhysics", "true")
    set_child(grid, "DestructibleBlocks", "false")
    set_child(grid, "Editable", "false")
    for block in grid.find("CubeBlocks"):
        recolor(block, color, skin=True)
        if block.find("Owner") is None:
            continue
        block.find("Owner").text = str(owner)
        kind = block.get(XSI_TYPE).replace("MyObjectBuilder_", "")
        block.find("ShareMode").text = "All" if kind in SHIPYARD_SHARED else "Faction"
        if kind == "Reactor":
            set_child(block, "CustomName", SHIPYARD_REACTOR)
        if kind == "BatteryBlock":
            set_child(block, "CustomName", SHIPYARD_BATTERY)
        if kind in SHIPYARD_OFF:
            set_child(block, "Enabled", "false")
        stock(block, kind, SHIPYARD_REACTOR_URANIUM)
    return [as_sector_object(g) for g in (grid, *heads)]


EQUATOR_STATIONS = {"Alpha Station", "Beta Station", "Gamma Station", "Delta Station"}
POLAR_STATIONS = {"Beta Station", "North Station", "Delta Station", "South Station"}


def lane_up(direction, home, target, north, polar_normal):
    """Up of a lane's computers, the same at both ends. The Trade Lanes mod puts a lane's two
    rings side by side along the computers' right axis: level with the equator on the lanes
    of the equatorial square and from Alpha and Delta Station to the Moon, one above the
    other on the others (to the poles and from North Station to the Moon). Each computer's
    rings toward the other end lie on its right (Forward x Up). On the equatorial square (up
    north) and the polar ring through Beta, North, Delta and South (up toward Alpha Station,
    polar_normal) the inner track runs Alpha, Beta, Gamma, Delta and Beta, North, Delta,
    South, the outer one the other way, so a ship going around keeps its track at every
    station. A Moon lane takes the up of its Earth station's circle: the equator's from Alpha
    and Delta, the polar ring's from North, so a ship that turns off toward the Moon keeps
    the side of the track it was on."""
    # A Moon lane follows its Earth station's circle (Delta's: the equator's)
    ends = {home, target} - {"Moon Station"}
    if ends <= EQUATOR_STATIONS:
        return norm(perpendicular_part(north, direction))
    assert ends <= POLAR_STATIONS, ends
    return norm(perpendicular_part(polar_normal, direction))


def lane_computer(ids, position, forward, up, lane_id, home, target, partner):
    """partner: the other end's computer. Its place goes in as a Target GPS, the last line (the
    mod finds the other keys by substring): a computer whose grid the cluster loads before its
    partner's or the lane registry's answer is paired at once, instead of telling the clients
    the lane has no target, which takes its tunnel off their screens. GpsMarkers:false keeps the
    lanes off the players' GPS lists (DF-716)."""
    x, y, z = partner
    data = (
        f"TLID:{lane_id}\nFrom:{home}\nTo:{target}\nSpeed:{LANE_SPEED}\nRingInterval:{LANE_RING_INTERVAL}"
        f"\nGpsMarkers:false\nTarget:GPS:TL {lane_id} {target}:{x:.2f}:{y:.2f}:{z:.2f}:"
    )
    grid = ET.Element(
        "MyObjectBuilder_EntityBase", {XSI_TYPE: "MyObjectBuilder_CubeGrid"}
    )
    el(grid, "SubtypeName")
    el(grid, "EntityId", ids.new())
    el(grid, "PersistentFlags", "CastShadows InScene")
    set_position(grid, position, forward, up)
    el(grid, "GridSizeEnum", "Large")
    blocks = el(grid, "CubeBlocks")
    block = el(
        blocks,
        "MyObjectBuilder_CubeBlock",
        **{XSI_TYPE: "MyObjectBuilder_BatteryBlock"},
    )
    el(block, "SubtypeName", "TradeLaneComputer")
    el(block, "EntityId", ids.new())
    el(block, "Min", x=0, y=0, z=0)
    component_data = el(
        el(el(block, "ComponentContainer"), "Components"), "ComponentData"
    )
    el(component_data, "TypeId", "MyModStorageComponent")
    component = el(
        component_data, "Component", **{XSI_TYPE: "MyObjectBuilder_ModStorageComponent"}
    )
    item = el(el(el(component, "Storage"), "dictionary"), "item")
    # The vanilla Custom Data key
    el(item, "Key", "74de02b3-27f9-4960-b1c4-27351f2b06d1")
    el(item, "Value", data)
    el(block, "CustomName", "Trade Lane Computer")
    el(block, "ShowOnHUD", "false")
    el(block, "Enabled", "true")
    el(grid, "IsStatic", "true")
    el(grid, "IsUnsupportedStation", "true")
    el(grid, "DestructibleBlocks", "false")
    el(grid, "Editable", "false")
    el(grid, "DisplayName", f"TL {lane_id} {home} > {target}")
    return grid


def safe_zone(ids, center, radius, name):
    """An admin safe zone: no damage, shooting, welding, grinding or block placing. The
    shipyard's OmniArc welders obey it too; the Octahedron plugin is to let them weld
    (SE1-0204)."""
    zone = ET.Element(
        "MyObjectBuilder_EntityBase", {XSI_TYPE: "MyObjectBuilder_SafeZone"}
    )
    el(zone, "SubtypeName")
    entity_id = ids.new()
    el(zone, "EntityId", entity_id)
    el(zone, "PersistentFlags", "CastShadows InScene")
    el(zone, "Name", entity_id)
    set_position(zone, center, (0, 0, -1), (0, 1, 0))
    el(zone, "DisplayName", name)
    el(zone, "Radius", radius)
    el(zone, "Enabled", "true")
    el(zone, "SafeZoneBlockId", 0)
    for kind in ("Players", "Factions", "Grids", "FloatingObjects"):
        el(zone, f"AccessType{kind}", "Blacklist")
    el(
        zone,
        "AllowedActions",
        "Drilling LandingGearLock",
    )
    for tag in ("Factions", "Players", "Entities"):
        el(zone, tag)
    el(zone, "Shape", "Sphere")
    el(zone, "IsVisible", "true")
    el(zone, "Texture", "SafeZone_Texture_Default")
    return zone


# The world


class Planet:
    def __init__(self, entity):
        self.entity = entity
        self.storage = entity.findtext("StorageName")
        po = entity.find("PositionAndOrientation")
        self.forward, self.up = (
            tuple(float(po.find(tag).get(k)) for k in "xyz")
            for tag in ("Forward", "Up")
        )
        right = cross(self.forward, self.up)
        corner = tuple(float(po.find("Position").get(k)) for k in "xyz")
        half = storage_size(SCENARIO / f"{self.storage}.vx2") / 2
        # Same corner rule as voxel_map: center = corner + R * half extent
        self.center = add(
            corner,
            add(add(mul(right, half), mul(self.up, half)), mul(self.forward, -half)),
        )


def load_planets(sector):
    """Planet name -> Planet."""
    return {
        entity.findtext("StorageName").split("-")[0]: Planet(entity)
        for entity in sector.find("SectorObjects")
        if entity.get(XSI_TYPE) == "MyObjectBuilder_Planet"
    }


def rotate(v, axis, angle):
    """v turned by angle (radians) about the unit vector axis, right-handed (Rodrigues)"""
    return add(
        add(mul(v, math.cos(angle)), mul(cross(axis, v), math.sin(angle))),
        mul(axis, dot(axis, v) * (1 - math.cos(angle))),
    )


def reframe(v, frame, target):
    """v turned by the rotation that takes frame's (forward, up) to target's"""
    (forward, up), (forward2, up2) = frame, target
    right, right2 = cross(forward, up), cross(forward2, up2)
    return add(
        add(mul(forward2, dot(v, forward)), mul(up2, dot(v, up))),
        mul(right2, dot(v, right)),
    )


def perpendicular_part(v, axis):
    """v without its component along the unit vector axis"""
    return sub(v, mul(axis, dot(v, axis)))


def facing(planet, center, pole_forward):
    """(forward, up) for a station: up is the planet's north, forward faces the planet's axis
    if it can, pole_forward over the poles."""
    inward = sub(planet.center, center)
    level = sub(inward, mul(planet.up, dot(inward, planet.up)))
    forward = norm(level) if dot(level, level) > 1 else pole_forward
    return forward, planet.up


def moon_turn(earth, moon):
    """The angle (radians, about EarthLike's north) the octahedron and the asteroid ring are
    turned by, so that the outward normal of the North, Delta, Alpha face points as near to
    the Moon as it can: Delta and Alpha Station's bisector lies over the Moon. That face's
    three stations have the Moon's lanes."""
    north = earth.up
    bisector = norm(add(earth.forward, cross(earth.forward, north)))
    over_moon = norm(perpendicular_part(sub(moon.center, earth.center), north))
    return math.atan2(dot(cross(bisector, over_moon), north), dot(bisector, over_moon))


def layout(earth, moon):
    """Station name -> (center, forward, up), and the lane edges. Directions are in EarthLike's
    frame."""
    north = earth.up
    front = rotate(earth.forward, north, moon_turn(earth, moon))
    right = cross(front, north)
    stations = {
        "North": north,
        "South": mul(north, -1),
        "Alpha": right,
        "Beta": mul(front, -1),
        "Gamma": mul(right, -1),
        "Delta": front,
    }
    places = {}
    for name, direction in stations.items():
        center = add(earth.center, mul(direction, STATION_DISTANCE))
        places[f"{name} Station"] = (center, *facing(earth, center, front))
    # Moon Station stays where it was before the turn: halfway between the directions to
    # where Delta and Alpha Station were then (along EarthLike's own forward and right)
    delta, alpha = (
        add(earth.center, mul(direction, STATION_DISTANCE))
        for direction in (earth.forward, cross(earth.forward, north))
    )
    toward = norm(add(*(norm(sub(place, moon.center)) for place in (delta, alpha))))
    center = add(moon.center, mul(toward, MOON_STATION_DISTANCE))
    # Its up is square to the Earth-Moon line, the one of those directions nearest to where
    # Delta Station was; it faces away from the Moon, so its lobby and its shipyard are on the
    # Moon side
    axis = norm(sub(moon.center, earth.center))
    up = norm(perpendicular_part(sub(delta, center), axis))
    forward = norm(perpendicular_part(sub(center, moon.center), up))
    places["Moon Station"] = (center, forward, up)
    # Around the equator, then the Moon's, then Beta and Delta to both poles, then the Moon's
    # third (last, so the other lanes keep their ids)
    edges = [
        ("Alpha Station", "Beta Station"),
        ("Alpha Station", "Delta Station"),
        ("Beta Station", "Gamma Station"),
        ("Gamma Station", "Delta Station"),
        ("Delta Station", "Moon Station"),
        ("North Station", "Moon Station"),
        ("Beta Station", "North Station"),
        ("Beta Station", "South Station"),
        ("Delta Station", "South Station"),
        ("Delta Station", "North Station"),
        ("Alpha Station", "Moon Station"),
    ]
    return places, edges


def lanes(places, edges):
    """(lane id, home, target, home endpoint, target endpoint), one per edge. A Trade Lane
    goes both ways: each of its computers is an entry toward the other one."""
    result = []
    for a, b in edges:
        pa, pb = places[a][0], places[b][0]
        direction = norm(sub(pb, pa))
        moon = b == "Moon Station"
        start = add(
            pa,
            mul(
                direction,
                MOON_LANE_ENDPOINT_DISTANCE if moon else LANE_ENDPOINT_DISTANCE,
            ),
        )
        end = sub(pb, mul(direction, LANE_ENDPOINT_DISTANCE))
        result.append((f"{len(result) + 1:02d}", a, b, start, end))
    return result


def gate_points(start, end, interval=LANE_RING_INTERVAL):
    """Gate places along a lane without the sideways offset: every interval, both ends included
    (TradeLaneComputerBlockLogic.UpdateRings, AddPointsBetweenGates)."""
    count = max(1, round(dist(start, end) / interval))
    return [
        (i / count, add(start, mul(sub(end, start), i / count)))
        for i in range(count + 1)
    ]


def lanes_off_sun(lane_list, sun):
    """The lanes with their ends moved along the Sun's axis, so every gate stays out of the slab
    of the Sun's rays through every station."""

    def shift(home, target):
        if {home, target} <= EQUATOR_STATIONS:
            return EQUATOR_LANE_NORTH, EQUATOR_LANE_NORTH
        if (home, target) == ("North Station", "Moon Station"):
            return NORTH_MOON_LANE_NORTH
        return 0.0, 0.0

    return [
        (
            lane_id,
            home,
            target,
            add(start, mul(sun, shift(home, target)[0])),
            add(end, mul(sun, shift(home, target)[1])),
        )
        for lane_id, home, target, start, end in lane_list
    ]


def sun_axis(sector):
    """The axis the Sun circles the world about (MySunProperties.SunRotationAxis)."""
    sun = sun_direction(sector)
    reference = (0.0, 1.0, 0.0) if abs(sun[1]) <= 0.95 else (-1.0, 0.0, 0.0)
    return norm(cross(cross(sun, reference), sun))


def sun_direction(sector):
    """Direction to the sun at the world's start (MySector, Vector3.CreateFromAzimuthAndElevation)."""
    environment = sector.find("Environment")
    azimuth = float(environment.findtext("SunAzimuth"))
    elevation = float(environment.findtext("SunElevation"))
    return (
        -math.cos(elevation) * math.sin(azimuth),
        math.sin(elevation),
        -math.cos(elevation) * math.cos(azimuth),
    )


def magnetic_axis(planet, sun):
    """The planet's north tilted away from the sun, as se-aurora computes it."""
    away = sub(sun, mul(planet.up, dot(sun, planet.up)))
    if dot(away, away) < 1e-6:
        return planet.up
    tilt = math.radians(AURORA_TILT_DEGREES)
    return norm(sub(mul(planet.up, math.cos(tilt)), mul(norm(away), math.sin(tilt))))


def asteroid_ring(seed, earth, axis, places, lane_list):
    """(storage name, center, size, forward, up, vx2 bytes) for the ring around axis and its 4
    ice asteroids. Each asteroid draws from its own random generator, so a station or lane
    added later moves only the asteroids in its way."""

    def own_rng(name):
        return random.Random(f"{seed}:{name}")

    asteroids = []
    taken = []
    equator = [
        name
        for name, (center, _, _) in places.items()
        if abs(dot(sub(center, earth.center), earth.up)) < 1
    ]
    for i, name in enumerate(equator):
        # Halfway between two stations, brought into the ring's plane
        a = sub(places[name][0], earth.center)
        b = sub(places[equator[(i + 1) % len(equator)]][0], earth.center)
        halfway = add(a, b)
        in_plane = sub(halfway, mul(axis, dot(halfway, axis)))
        center = add(earth.center, mul(norm(in_plane), RING_RADIUS))
        size = storage_size(SCENARIO.parents[1] / f"VoxelMaps/{ICE_SHAPE}.vx2")
        storage = f"Octahedron-Ice-{i + 1}"
        asteroids.append(
            (
                storage,
                center,
                size,
                *random_basis(own_rng(storage)),
                predefined_asteroid(ICE_SHAPE, ICE_MATERIAL, size),
            )
        )
        taken.append((center, size / 2))

    # Asteroid centers by 2 km cell, so the overlap check looks only nearby
    cell = 2_000.0
    grid = defaultdict(list)

    def key(point):
        return tuple(int(math.floor(c / cell)) for c in point)

    def free(center, size):
        x, y, z = key(center)
        return not any(
            dist(center, c) < r + size / 2 + ASTEROID_GAP
            for dx in (-1, 0, 1)
            for dy in (-1, 0, 1)
            for dz in (-1, 0, 1)
            for c, r in grid[(x + dx, y + dy, z + dz)]
        )

    for center, radius in taken:
        grid[key(center)].append((center, radius))

    u = norm(perpendicular_part(sub(places["Alpha Station"][0], earth.center), axis))
    v = cross(axis, u)
    sizes, weights = zip(*ASTEROID_SIZES)
    for number in range(1, RING_ASTEROIDS + 1):
        storage = f"Octahedron-Asteroid-{number:04d}"
        rng = own_rng(storage)
        sector = (number - 1) % RING_SECTORS
        while True:
            size = rng.choices(sizes, weights)[0]
            angle = rng.uniform(sector, sector + 1) * 2 * math.pi / RING_SECTORS
            # Uniform over the band's area
            radius = math.sqrt(rng.uniform(RING_INNER**2, RING_OUTER**2))
            height = rng.uniform(-RING_THICKNESS / 2, RING_THICKNESS / 2)
            radial = add(mul(u, math.cos(angle)), mul(v, math.sin(angle)))
            center = add(earth.center, add(mul(radial, radius), mul(axis, height)))
            if free(center, size) and not any(
                segment_distance(center, s, e) < size / 2 + LANE_CLEARANCE
                for _, _, _, s, e in lane_list
            ):
                break
        voxel_seed = rng.getrandbits(31)
        asteroids.append(
            (
                storage,
                center,
                size,
                *random_basis(rng),
                procedural_asteroid(voxel_seed, rng.getrandbits(31), size),
            )
        )
        grid[key(center)].append((center, size / 2))
    return asteroids


def mod_items(ore_mod):
    mods = ET.Element("Mods")
    for workshop_id, name in MODS:
        item = el(mods, "ModItem", FriendlyName=name)
        el(item, "Name", f"{workshop_id}.sbm")
        el(item, "PublishedFileId", workshop_id)
        el(item, "PublishedServiceName", "Steam")
    mods.extend(ET.parse(MES_MODS).getroot())
    item = el(mods, "ModItem", FriendlyName="OctahedronOres")
    el(item, "Name", f"{ore_mod}.sbm" if ore_mod else "OctahedronOres")
    el(item, "PublishedFileId", ore_mod)
    el(item, "PublishedServiceName", "Steam")
    return mods


def apply_settings(root, ore_mod):
    settings = root.find("Settings")
    for key, value in SETTINGS.items():
        set_child(settings, key, value)
    limits = settings.find("BlockTypeLimits/dictionary")
    for child in list(limits):
        limits.remove(child)
    for block, count in BLOCK_LIMITS.items():
        item = el(limits, "item")
        el(item, "Key", block)
        el(item, "Value", count)
    set_child(root, "SessionName", NAME)
    set_child(root, "Description", DESCRIPTION)
    old = root.find("Mods")
    index = list(root).index(old)
    root.remove(old)
    root.insert(index, mod_items(ore_mod))


def add_npc(checkpoint, ids):
    """The stations' owner: an NPC identity and its faction."""
    identity_id, faction_id = ids.new(2), ids.new(3)
    identity = el(checkpoint.find("Identities"), "MyObjectBuilder_Identity")
    el(identity, "IdentityId", identity_id)
    el(identity, "DisplayName", NPC_NAME)
    el(identity, "CharacterEntityId", 0)
    el(checkpoint.find("NonPlayerIdentities"), "long", identity_id)
    factions = checkpoint.find("Factions")
    faction = el(factions.find("Factions"), "MyObjectBuilder_Faction")
    el(faction, "FactionId", faction_id)
    el(faction, "Tag", NPC_TAG)
    el(faction, "Name", NPC_NAME)
    el(faction, "Description", "Keeps the Octahedron stations and Trade Lanes running.")
    el(faction, "PrivateInfo")
    member = el(el(faction, "Members"), "MyObjectBuilder_FactionMember")
    el(member, "PlayerId", identity_id)
    el(member, "IsLeader", "true")
    el(member, "IsFounder", "true")
    el(faction, "JoinRequests")
    el(faction, "AutoAcceptMember", "false")
    el(faction, "AutoAcceptPeace", "true")
    el(faction, "AcceptHumans", "false")
    el(faction, "EnableFriendlyFire", "false")
    # A trade faction: the economy stocks its stations' stores and offers contracts there
    el(faction, "FactionType", "Trader")
    el(faction, "FactionTypeString", "Trader")
    item = el(factions.find("Players/dictionary"), "item")
    el(item, "Key", identity_id, **{XSI_TYPE: "xsd:long"})
    el(item, "Value", faction_id, **{XSI_TYPE: "xsd:long"})
    return identity_id, faction


def add_trade_stations(checkpoint, ids, faction, stations):
    """Makes the station grids the faction's trade stations, with stations given as
    name -> (grid entity id, center, forward, up), and gives the faction its money.

    SafeZoneEntityId stays 0: with a live safe zone there, the economy closes the
    station grid whenever no player is near and spawns its prefab on the next visit.
    The cost is the Services Terminal's repair and salvage, which list grids in
    that safe zone only, and the station bots."""
    faction_id = faction.findtext("FactionId")
    entries = el(faction, "Stations")
    for name, (grid_id, center, forward, up) in stations.items():
        entry = el(entries, "MyObjectBuilder_Station")
        el(entry, "Id", ids.new(12))
        vector(entry, "Position", center)
        vector(entry, "Up", up)
        vector(entry, "Forward", forward)
        el(entry, "StationType", "SpaceStation")
        el(entry, "IsDeepSpaceStation", "false")
        el(entry, "StationEntityId", grid_id)
        el(entry, "FactionId", faction_id)
        el(entry, "PrefabName")
        el(entry, "SafeZoneEntityId", 0)
        el(entry, "StoreItems")
        el(entry, "IsOnPlanetWithAtmosphere", "false")
        el(entry, "Name", name)
    components = checkpoint.find("SessionComponents")
    bank = el(components, "MyObjectBuilder_SessionComponent")
    bank.set(XSI_TYPE, "MyObjectBuilder_BankingSystem")
    account = el(el(bank, "Accounts"), "MyObjectBuilder_AccountEntry")
    el(account, "OwnerIdentifier", faction_id)
    details = el(account, "Account")
    el(details, "OwnerIdentifier", faction_id)
    el(details, "Balance", NPC_BALANCE)
    el(bank, "OverallBalance", NPC_BALANCE)
    economy = el(components, "MyObjectBuilder_SessionComponent")
    economy.set(XSI_TYPE, "MyObjectBuilder_SessionComponentEconomy")
    el(economy, "GenerateFactionsOnStart", "false")


def clean_checkpoint(checkpoint):
    """Drops the scenario's leftovers: its player, controlled entity and respawn cooldowns.
    Its NPC factions (Space Pirates, Space spiders) stop accepting players."""
    for faction in checkpoint.iterfind("Factions/Factions/MyObjectBuilder_Faction"):
        set_child(faction, "AcceptHumans", "false")
    for tag in ("CameraEntity", "ControlledObject"):
        set_child(checkpoint, tag, 0)
    for tag in (
        "ControlledEntities/dictionary",
        "AllPlayersColors/dictionary",
        "RespawnCooldowns",
    ):
        node = checkpoint.find(tag)
        for child in list(node):
            node.remove(child)


def write_xml(tree, path):
    tree.write(path, encoding="utf-8", xml_declaration=True)
    # xsi:type values name xsd types, so the root needs xsd declared even where ElementTree sees no use for it
    text = path.read_text(encoding="utf-8")
    if 'xmlns:xsd="' not in text[:500]:
        text = re.sub(
            r"(<MyObjectBuilder_\w+)",
            r'\1 xmlns:xsd="http://www.w3.org/2001/XMLSchema"',
            text,
            count=1,
        )
        path.write_text(text, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--seed", type=int, default=42, help="seed of everything random in the world"
    )
    parser.add_argument(
        "--ore-mod",
        type=int,
        default=0,
        help="Workshop id of OctahedronOres, 0 = local mod",
    )
    args = parser.parse_args()

    out = args.output
    shutil.rmtree(out, ignore_errors=True)
    shutil.copytree(
        SCENARIO, out, ignore=shutil.ignore_patterns("*.sbsB5", "*.scf", "*.vx2")
    )

    sector_tree = ET.parse(out / "SANDBOX_0_0_0_.sbs")
    sector = sector_tree.getroot()
    objects = sector.find("SectorObjects")
    planets = load_planets(sector)
    for name, planet in planets.items():
        if name in KEEP_PLANETS:
            shutil.copy(SCENARIO / f"{planet.storage}.vx2", out)
        else:
            objects.remove(planet.entity)
    earth, moon = planets["EarthLike"], planets["Moon"]

    checkpoint_tree = ET.parse(out / "Sandbox.sbc")
    checkpoint = checkpoint_tree.getroot()
    config_tree = ET.parse(out / "Sandbox_config.sbc")
    clean_checkpoint(checkpoint)
    apply_settings(checkpoint, args.ore_mod)
    apply_settings(config_tree.getroot(), args.ore_mod)

    # Entity and identity ids come from one generator, in the order they are drawn below
    ids = Ids(random.Random(args.seed))
    places, edges = layout(earth, moon)
    sun = sun_axis(sector)
    polar_normal = norm(sub(places["Alpha Station"][0], earth.center))
    straight_lanes = lanes(places, edges)
    lane_list = lanes_off_sun(straight_lanes, sun)
    # The ring's axis turns with the octahedron, so the ring still passes Beta and Delta
    # Station on the same sides
    axis = rotate(
        magnetic_axis(earth, sun_direction(sector)), earth.up, moon_turn(earth, moon)
    )
    # The asteroids take the first ids, so stations and grids added later leave their ids as
    # they are; their places come from their own generators (asteroid_ring). They keep clear of
    # the lanes before lanes_off_sun moves them, so moving a lane moves no asteroid.
    for storage, center, size, forward, up, data in asteroid_ring(
        args.seed, earth, axis, places, straight_lanes
    ):
        (out / f"{storage}.vx2").write_bytes(data)
        objects.append(voxel_map(ids, storage, center, size, forward, up))

    owner, faction = add_npc(checkpoint, ids)
    template = ET.parse(STATION_BLUEPRINT).getroot().find(".//CubeGrid")
    trade_stations = {}
    shipyards = {}
    for name, (center, forward, up) in places.items():
        planet = moon if name == "Moon Station" else earth
        away = norm(sub(center, planet.center))
        if planet is moon:
            away = mul(away, -1)
        if name == "North Station":
            # Upside down, like South Station: its up toward EarthLike, so the lobby looks
            # down on the ring, and its shipyard below it on the station, out in space
            up = mul(up, -1)
        # The station and its shipyard share the safe zone around the station's place
        if name in ("North Station", "South Station"):
            right = cross(forward, up)
            local = lambda v: add(
                add(mul(right, v[0]), mul(up, v[1])), mul(forward, v[2])
            )
            offset, yard_forward, yard_up = map(local, POLE_SHIPYARD)
            station_center, yard = spread_pair(
                template, center, forward, up, norm(offset), yard_forward, yard_up, 0
            )
        else:
            station_center, yard = station_and_shipyard(
                template, center, forward, up, away
            )
        if name in EQUATOR_STATIONS:
            # Turn the pair about the zone's center until the station's deck lies parallel to
            # the asteroid ring (up along the ring's axis, the north side), so the sun doesn't
            # shine sideways through it all day, and its front faces EarthLike in the ring's
            # plane, with the shipyard behind it out in space. The pole stations stay level
            # with the equator.
            level = norm(perpendicular_part(sub(earth.center, center), axis))
            turn = lambda v: reframe(v, (forward, up), (level, axis))
            station_center = add(center, turn(sub(station_center, center)))
            yard_center, yard_forward, yard_up = yard
            yard = (
                add(center, turn(sub(yard_center, center))),
                turn(yard_forward),
                turn(yard_up),
            )
            forward, up = level, axis
        if name == "Delta Station":
            # Up toward EarthLike; the shipyard goes along as one unit
            tilt = lambda v: rotate(
                v, mul(cross(forward, up), -1), math.radians(DELTA_TILT_DEGREES)
            )
            station_center = add(center, tilt(sub(station_center, center)))
            yard_center, yard_forward, yard_up = yard
            yard = (
                add(center, tilt(sub(yard_center, center))),
                tilt(yard_forward),
                tilt(yard_up),
            )
            forward, up = tilt(forward), tilt(up)
        if name == "Moon Station":
            # Turned about its up, its front toward the Moon and the lobby toward EarthLike
            forward = mul(forward, -1)
        if name in SHIPYARD_RAISE:
            yard = raise_shipyard(
                yard,
                station_center,
                center,
                SHIPYARD_RAISE[name],
                up if name == "Moon Station" else yard[2],
            )
        if name in SHIPYARD_SUN_TURN:
            station_center, yard = turn_pair(
                yard,
                template,
                station_center,
                forward,
                up,
                center,
                sun,
                SHIPYARD_SUN_TURN[name],
            )
        if name == "Moon Station":
            # Turned with its shipyard as one unit: the lobby keeps its view
            line = sub(yard[0], station_center)
            axis = norm(cross(line, sun))
            turn = lambda v: rotate(v, axis, math.radians(MOON_SUN_TURN_DEGREES))
            station_center = add(center, turn(sub(station_center, center)))
            yard = (
                add(center, turn(sub(yard[0], center))),
                turn(yard[1]),
                turn(yard[2]),
            )
            forward, up = turn(forward), turn(up)
        assert_shipyard_off_sun(yard, template, station_center, forward, up, sun)
        shipyards[name] = yard
        grid = station(template, ids, name, station_center, forward, up, owner)
        objects.append(grid)
        trade_stations[name] = (grid.findtext("EntityId"), station_center, forward, up)
        objects.append(safe_zone(ids, center, STATION_SAFE_ZONE_RADIUS, name))

    for lane_id, home, target, start, end in lane_list:
        direction = norm(sub(end, start))
        up = lane_up(direction, home, target, earth.up, polar_normal)
        for position, here, there, facing, partner in (
            (start, home, target, direction, end),
            (end, target, home, mul(direction, -1), start),
        ):
            # No safe zone: the computer can't be damaged and the lane's rings have no physics
            objects.append(
                lane_computer(ids, position, facing, up, lane_id, here, there, partner)
            )

    add_trade_stations(checkpoint, ids, faction, trade_stations)

    yard = shipyard_grids()
    for name, placement in shipyards.items():
        objects.extend(
            shipyard(
                yard, ids, f"{name} Shipyard", *placement, owner, STATION_COLORS[name]
            )
        )

    for tree, name in (
        (sector_tree, "SANDBOX_0_0_0_.sbs"),
        (checkpoint_tree, "Sandbox.sbc"),
        (config_tree, "Sandbox_config.sbc"),
    ):
        ET.indent(tree, "  ")
        write_xml(tree, out / name)

    shutil.copytree(MES_STORAGE, out / "Storage", dirs_exist_ok=True)
    rts = out / "Storage/1359618037.sbm_RelativeTopSpeed"
    rts.mkdir(parents=True, exist_ok=True)
    shutil.copy(RELATIVE_TOP_SPEED, rts / "RelativeTopSpeed.cfg")
    print(
        f"{out}: {len(places)} stations, {len(lane_list)} lanes, {RING_ASTEROIDS} + 4 asteroids, seed {args.seed}"
    )


if __name__ == "__main__":
    main()
