#!/usr/bin/env python3
"""Builds the Octahedron starting world from the game's Star System scenario.

    build_world.py <output folder> [--seed N] [--ore-mod ID]

Keeps EarthLike and the Moon, applies the world settings and mods, and places
the 7 stations, the Trade Lanes endpoints, the safe zones and the asteroid ring.
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
from pathlib import Path

STEAM = Path.home() / ".steam/steam/steamapps"
SCENARIO = STEAM / "common/SpaceEngineers/Content/CustomWorlds/Star System"
STATION_BLUEPRINT = STEAM / "workshop/content/244850/3816368704/bp.sbc"
CONFIG = Path(__file__).parent / "config"
MES_MODS = CONFIG / "mes-mods.xml"
MES_STORAGE = CONFIG / "storage"
RELATIVE_TOP_SPEED = CONFIG / "relative-top-speed.xml"

NAME = "Octahedron"
DESCRIPTION = "Octahedron public test cluster: EarthLike and its Moon, 7 stations linked by Trade Lanes, an asteroid ring."
KEEP_PLANETS = ("EarthLike", "Moon")

# Altitudes are from the planet's 60 km (EarthLike) or 9.5 km (Moon) radius
STATION_DISTANCE = 140_000.0
MOON_STATION_DISTANCE = 39_500.0
RING_RADIUS = 120_000.0
RING_WIDTH = 4_000.0
RING_HEIGHT_SIGMA = 300.0
RING_ASTEROIDS = 400
# The se-aurora plugin's default MagneticAxisTilt: its magnetic axis is the planet's
# north tilted this far away from the sun (AuroraRenderer.ComputeMagneticAxis)
AURORA_TILT_DEGREES = 25.0
# Vanilla generator 4 sizes, pow2 storages; the weights favor the smaller ones
ASTEROID_SIZES = ((256, 5), (512, 4), (1024, 1))
ICE_SHAPE = "Asteroid512-001"
ICE_MATERIAL = "Ice_02"
VOXEL_GENERATOR = 4

STATION_SAFE_ZONE_RADIUS = 250.0
LANE_SAFE_ZONE_RADIUS = 50.0
LANE_ENDPOINT_DISTANCE = 1_000.0
# Each edge has two parallel lanes this far apart, so their 100 m safe zones don't touch
LANE_SPACING = 200.0
LANE_SPEED = 10_000
LANE_RING_INTERVAL = 10_000
# Asteroid surfaces stay this far from a lane's straight line
LANE_CLEARANCE = 1_000.0

NPC_NAME = "Octahedron Authority"
NPC_TAG = "OCT"
# Blocks players use on a station, shared with everyone; everything else stays with the owner's faction
PUBLIC_BLOCKS = {
    "MedicalRoom", "Projector", "StoreBlock", "ContractBlock", "VendingMachine", "Door",
    "AirtightSlideDoor", "ButtonPanel", "Cockpit", "ShipConnector",
}  # fmt: skip

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
    # killed or deleted (MyPhysics.CreateHkWorld). The 1000 km edge is a plugin's.
    "WorldSizeKm": 0,
    "BlockLimitsEnabled": "PER_PLAYER",
    "TotalPCU": 1_000_000,
    "MaxFactionsCount": 0,
    "EnableCopyPaste": "false",
    "EnableSpectator": "false",
    "EnableIngameScripts": "false",
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
    "EnableRadiation": "false",
    "FoodConsumptionRate": 0,
    "EnableSurvivalBuffs": "false",
    "EnableReducedStatsOnRespawn": "false",
    "EnableWolfs": "false",
    "EnableSpiders": "false",
    "EnableScripterRole": "false",
    # Vanilla defaults; the Star System scenario has sparser asteroid ores (1, 60, 0.8)
    "DepositsCountCoefficient": 2,
    "DepositSizeDenominator": 30,
    "HarvestRatioMultiplier": 1,
}

# Per player, by block pair name; vanilla counts built blocks, not active ones
BLOCK_LIMITS = {"SELtdNanobotBuildAndRepairSystem": 1}

# (Workshop id, name), MES and its encounter mods come from mes-mods.xml
MODS = [
    (3415912276, "Skybox: SE1 Alpha Release"),
    (1359618037, "Relative Top Speed"),
    (3816175430, "Trade Lanes MP"),
    (3099489876, "Nanobot Build and Repair System (Maintained) - Original Resources"),
    (3412657089, "OmniCorp The OmniArc Project: Adv Tools"),
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


def voxel_map(ids, storage_name, center, size, rng):
    """A VoxelMap sector object; its position is the storage's min corner."""
    forward, up = random_basis(rng)
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
    grid = copy.deepcopy(grid)
    old_ids = {
        node.text
        for node in grid.iter()
        if node.tag == "EntityId" and node.text and node.text != "0"
    }
    mapping = {old: str(ids.new()) for old in sorted(old_ids)}
    for node in grid.iter():
        if len(node) == 0 and node.text in mapping:
            node.text = mapping[node.text]
    return grid


def grid_center(grid):
    """Local center of the grid's block bounds, in meters."""
    mins = [
        tuple(int(b.find("Min").get(k)) for k in "xyz")
        for b in grid.find("CubeBlocks")
        if b.find("Min") is not None
    ]
    lo = [min(m[i] for m in mins) for i in range(3)]
    hi = [max(m[i] for m in mins) for i in range(3)]
    return tuple((lo[i] + hi[i]) / 2 * 2.5 for i in range(3))


def place_grid(grid, center, forward, up):
    """Moves the grid so its block bounds' center lands on center."""
    right = cross(forward, up)
    local = grid_center(grid)
    # Grid local axes: X = Right, Y = Up, Z = Backward
    offset = add(add(mul(right, local[0]), mul(up, local[1])), mul(forward, -local[2]))
    set_position(grid, sub(center, offset), forward, up)


def as_sector_object(grid):
    grid.tag = "MyObjectBuilder_EntityBase"
    grid.set(XSI_TYPE, "MyObjectBuilder_CubeGrid")
    return grid


def station(template, ids, name, center, forward, up, owner):
    grid = clone_grid(template, ids)
    place_grid(grid, center, forward, up)
    set_child(grid, "DisplayName", name)
    set_child(grid, "IsStatic", "true")
    # Blueprints save false here; a grid loaded without physics can't respawn anyone
    set_child(grid, "CreatePhysics", "true")
    set_child(grid, "DestructibleBlocks", "false")
    set_child(grid, "Editable", "false")
    for block in grid.find("CubeBlocks"):
        if block.find("Owner") is None:
            continue
        block.find("Owner").text = str(owner)
        kind = block.get(XSI_TYPE).replace("MyObjectBuilder_", "")
        block.find("ShareMode").text = "All" if kind in PUBLIC_BLOCKS else "Faction"
        if (
            kind == "MedicalRoom"
            and block.findtext("SubtypeName") == "LargeMedicalRoom"
        ):
            set_child(block, "CustomName", f"Medical Room - {name}")
            set_child(block, "SpawnName", name)
    return as_sector_object(grid)


def lane_computer(ids, position, forward, up, lane_id, home, target):
    data = f"TLID:{lane_id}\nFrom:{home}\nTo:{target}\nSpeed:{LANE_SPEED}\nRingInterval:{LANE_RING_INTERVAL}"
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
    """An admin safe zone: no damage or shooting, players can still build and weld."""
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
        "Drilling Welding Grinding Building LandingGearLock BuildingProjections",
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


def facing(planet, center):
    """(forward, up) for a station: up is the planet's north, forward faces the planet's axis if it can."""
    inward = sub(planet.center, center)
    level = sub(inward, mul(planet.up, dot(inward, planet.up)))
    forward = norm(level) if dot(level, level) > 1 else planet.forward
    return forward, planet.up


def layout(earth, moon):
    """Station name -> (center, forward, up), and the lane edges. Directions are in EarthLike's frame."""
    north, front = earth.up, earth.forward
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
        places[f"{name} Station"] = (center, *facing(earth, center))
    nearest = min(places, key=lambda name: dist(places[name][0], moon.center))
    center = add(
        moon.center,
        mul(norm(sub(places[nearest][0], moon.center)), MOON_STATION_DISTANCE),
    )
    places["Moon Station"] = (center, *facing(moon, center))
    names = list(places)[:6]
    edges = [
        (a, b)
        for i, a in enumerate(names)
        for b in names[i + 1 :]
        if dist(places[a][0], places[b][0]) < 1.9 * STATION_DISTANCE
    ]
    assert len(edges) == 12, edges
    edges.append((nearest, "Moon Station"))
    return places, edges


def lanes(places, edges):
    """(lane id, home, target, home endpoint, target endpoint), two parallel lanes per edge."""
    result = []
    for a, b in edges:
        pa, pb = places[a][0], places[b][0]
        direction = norm(sub(pb, pa))
        side = norm(
            cross(
                direction,
                (
                    norm(add(pa, pb))
                    if dist(add(pa, pb), (0, 0, 0)) > 1
                    else perpendicular(direction)
                ),
            )
        )
        for offset in (-LANE_SPACING / 2, LANE_SPACING / 2):
            shift = mul(side, offset)
            start = add(add(pa, mul(direction, LANE_ENDPOINT_DISTANCE)), shift)
            end = add(sub(pb, mul(direction, LANE_ENDPOINT_DISTANCE)), shift)
            result.append((f"{len(result) + 1:02d}", a, b, start, end))
    return result


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


def asteroid_ring(rng, earth, axis, places, lane_list):
    """(storage name, center, size, vx2 bytes) for the ring around axis and its 4 ice asteroids."""
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
        asteroids.append(
            (
                f"Octahedron-Ice-{i + 1}",
                center,
                size,
                predefined_asteroid(ICE_SHAPE, ICE_MATERIAL, size),
            )
        )
        taken.append((center, size / 2))

    sizes, weights = zip(*ASTEROID_SIZES)
    while len(asteroids) < RING_ASTEROIDS + len(equator):
        size = rng.choices(sizes, weights)[0]
        angle = rng.uniform(0, 2 * math.pi)
        radius = RING_RADIUS + rng.uniform(-RING_WIDTH / 2, RING_WIDTH / 2)
        height = max(
            -3 * RING_HEIGHT_SIGMA,
            min(3 * RING_HEIGHT_SIGMA, rng.gauss(0, RING_HEIGHT_SIGMA)),
        )
        u = perpendicular(axis)
        v = cross(axis, u)
        radial = add(mul(u, math.cos(angle)), mul(v, math.sin(angle)))
        center = add(earth.center, add(mul(radial, radius), mul(axis, height)))
        if any(dist(center, c) < r + size / 2 + 200 for c, r in taken):
            continue
        if any(
            segment_distance(center, s, e) < size / 2 + LANE_CLEARANCE
            for _, _, _, s, e in lane_list
        ):
            continue
        seed = rng.getrandbits(31)
        storage = f"Octahedron-Asteroid-{len(asteroids) - len(equator) + 1:03d}"
        asteroids.append(
            (
                storage,
                center,
                size,
                procedural_asteroid(seed, rng.getrandbits(31), size),
            )
        )
        taken.append((center, size / 2))
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
    el(faction, "FactionType", "None")
    item = el(factions.find("Players/dictionary"), "item")
    el(item, "Key", identity_id, **{XSI_TYPE: "xsd:long"})
    el(item, "Value", faction_id, **{XSI_TYPE: "xsd:long"})
    return identity_id


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

    # The one random generator of the world; every random choice below draws from it in order
    rng = random.Random(args.seed)
    ids = Ids(rng)
    owner = add_npc(checkpoint, ids)
    places, edges = layout(earth, moon)
    template = ET.parse(STATION_BLUEPRINT).getroot().find(".//CubeGrid")
    for name, (center, forward, up) in places.items():
        objects.append(station(template, ids, name, center, forward, up, owner))
        objects.append(safe_zone(ids, center, STATION_SAFE_ZONE_RADIUS, name))

    lane_list = lanes(places, edges)
    for lane_id, home, target, start, end in lane_list:
        direction = norm(sub(end, start))
        up = perpendicular(direction)
        for position, here, there, facing in (
            (start, home, target, direction),
            (end, target, home, mul(direction, -1)),
        ):
            objects.append(
                lane_computer(ids, position, facing, up, lane_id, here, there)
            )
            objects.append(
                safe_zone(ids, position, LANE_SAFE_ZONE_RADIUS, f"TL {lane_id} {here}")
            )

    axis = magnetic_axis(earth, sun_direction(sector))
    for storage, center, size, data in asteroid_ring(
        rng, earth, axis, places, lane_list
    ):
        (out / f"{storage}.vx2").write_bytes(data)
        objects.append(voxel_map(ids, storage, center, size, rng))

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
