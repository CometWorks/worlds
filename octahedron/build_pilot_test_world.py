#!/usr/bin/env python3
"""Builds "Octahedron Pilot Test", the Octahedron world with test ships, a walking platform
and a list of test sites for an autopilot of ships and characters (SE-1425).

    build_pilot_test_world.py <saves folder> [--seed N] [--steam-id ID] [--blueprints DIR]...
    build_pilot_test_world.py <saves folder> --check

Writes the offline save <saves folder>/Octahedron Pilot Test and the site list
<saves folder>/pilot-test-sites.json, with a copy in the save. The save folder carries the
session name: mods read their configs from Saves/<SessionName>/Storage.

The world is build_world.py's, seed --seed (default 42), with the live cluster's
OctahedronOres and OctahedronMod Workshop items, made offline. On top of it:
- 13 ships PT-01 to PT-13 of the Workshop cargo ships in trader_ships.txt, parked at rest
  around Gamma Station and Moon Station outside their safe zones: dynamic grids with physics,
  dampeners on, reactors with uranium, hydrogen tanks and batteries full, owned by the
  world's player (--steam-id) and shared with everyone.
- A static walking platform near Gamma Station: a light armor floor of 30 x 30 blocks with a
  walled corridor of two right-angle corners that opens into a hall, and a gravity generator
  and batteries under the floor.
- The sites in the JSON, in world coordinates (meters): cruise legs, docking routes,
  landings on the Moon and on EarthLike, a crossing of the asteroid ring, two traffic
  sites, a jetpack route around Gamma Station and the walking course.

Blueprints are read from <dir>/<workshop id>/bp.sbc in the --blueprints folders, as
build_trader_ships.py reads them. --check reads a written save back, prints PASS and FAIL
lines and exits with 1 on a FAIL; a build runs it at the end.
"""

import argparse
import copy
import json
import math
import random
import shutil
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

import build_trader_ships as bts
import build_world as bw
from build_world import add, cross, dist, dot, mul, norm, sub

Vec = tuple[float, float, float]

NAME = "Octahedron Pilot Test"
SITES = "pilot-test-sites.json"
# The live cluster's Workshop items (deploy.py of the workspace's deploy-octahedron)
ORE_MOD = 3828072455
OCTAHEDRON_MOD = 3833786664
RELATIVE_TOP_SPEED = "Storage/1359618037.sbm_RelativeTopSpeed/RelativeTopSpeed.cfg"
CONTENT = bw.STEAM / "common/SpaceEngineers/Content/Data"

# Single grid ships without DLC thrusters. The Challenger's only seat and the HM-302M's
# cockpit are the Industrial Cockpit of the Decorative Pack DLC; the HM-302M and the
# ST01 have a vanilla remote control.
AREION = "2899884415"
TT15 = "1864247090"
ST01 = "1754053440"
CHALLENGER = "2915053031"
HM302M = "2250129021"
SHIP_MODELS = {
    "PT-01": AREION,
    "PT-02": HM302M,
    "PT-03": CHALLENGER,
    "PT-04": CHALLENGER,
    "PT-05": TT15,
    "PT-06": ST01,
    "PT-07": AREION,
    "PT-08": TT15,
    "PT-09": ST01,
    "PT-10": AREION,
    "PT-11": CHALLENGER,
    "PT-12": TT15,
    "PT-13": CHALLENGER,
}
URANIUM = 100
ENABLED = {"Thrust", "Gyro", "Reactor", "BatteryBlock", "OxygenTank", "RemoteControl"}

PLATFORM = "Walk Platform"
PLATFORM_SIZE = 30
# Open floor cells (x, z) of the walking course: a corridor north, east, south into a hall
CORRIDOR = (
    [(x, z) for x in range(1, 4) for z in range(1, 26)]
    + [(x, z) for x in range(1, 16) for z in range(23, 26)]
    + [(x, z) for x in range(13, 16) for z in range(13, 26)]
)
HALL = [(x, z) for x in range(8, 29) for z in range(1, 13)]
WALL_HEIGHT = 2
# Start, the two corners, the hall's mouth, the hall's middle
WALK_CELLS = [(2, 2), (2, 24), (14, 24), (14, 12), (18, 6)]
GRAVITY_FIELD = (80.0, 30.0, 80.0)
PLATFORM_BATTERIES = 4

SHIP_GATE_TOLERANCE = 50.0
DOCK_GATE_TOLERANCE = 10.0
JETPACK_TOLERANCE = 5.0
WALK_TOLERANCE = 2.0
CRUISE_LENGTH = 22_000.0
# The last dock gate, on the connector's axis, this far from the zone's center
DOCK_APPROACH = 280.0
DOCK_LEG = 500.0
# Turns at the dock gates, last gate first, degrees about the route's plane normal
DOCK_TURNS = (60.0, -45.0, 70.0)
JETPACK_RADIUS = 330.0
# Ship, Gamma Station's connector by cell, and the side the route comes from, in the
# station's frame (out from EarthLike, the ring's axis, their cross product): an arm's
# connector and one under a Station Extender, 86 m apart, both facing open space
DOCKS = (("PT-03", (13, 0, 0), (1, 0, 0)), ("PT-04", (-20, -8, 3), (0, -1, 0)))

# Clearances --check holds the world to, meters
SHIP_CLEARANCE = 150.0
SITE_START_CLEARANCE = 100.0
LEG_CLEARANCE = 100.0
# Between the last legs of two dock sites, which meet at the station
DOCKED_CLEARANCE = 50.0
LANE_CLEARANCE = 700.0
RING_MARGIN = 1_000.0
LEG_STEP = 50.0


# Game content


@dataclass
class BlockDefinition:
    mass: float
    force: float
    effectiveness_in_atmosphere: float
    max_stored_power: float


def block_definitions() -> dict[tuple[str, str], BlockDefinition]:
    """(type, subtype) -> definition, the type without MyObjectBuilder_, from the game's content."""
    component_mass = {}
    for path in CONTENT.glob("Components*.sbc"):
        for component in ET.parse(path).getroot().iter("Component"):
            if component.findtext("Id/SubtypeId") and component.findtext("Mass"):
                component_mass[component.findtext("Id/SubtypeId")] = float(
                    component.findtext("Mass")
                )
    definitions = {}
    for path in (CONTENT / "CubeBlocks").glob("*.sbc"):
        for d in ET.parse(path).getroot().iter("Definition"):
            if d.findtext("Id/TypeId") is None:
                continue
            key = (
                d.findtext("Id/TypeId").removeprefix("MyObjectBuilder_"),
                d.findtext("Id/SubtypeId") or "",
            )
            definitions[key] = BlockDefinition(
                mass=sum(
                    float(c.get("Count")) * component_mass.get(c.get("Subtype"), 0.0)
                    for c in d.iterfind("Components/Component")
                ),
                force=float(d.findtext("ForceMagnitude") or 0),
                effectiveness_in_atmosphere=float(
                    d.findtext("EffectivenessAtMaxInfluence") or 1
                ),
                max_stored_power=float(d.findtext("MaxStoredPower") or 0),
            )
    return definitions


def kind(block: ET.Element) -> str:
    return block.get(bw.XSI_TYPE, "").removeprefix("MyObjectBuilder_")


# Ships


@dataclass
class ShipModel:
    workshop_id: str
    name: str
    grids: list[ET.Element]
    main: int
    radius: float
    grid_size: str
    mass: float
    lift_g: float


@dataclass
class Ship:
    name: str
    model: ShipModel
    center: Vec
    forward: Vec
    up: Vec

    def to_json(self) -> dict:
        return {
            "name": self.name,
            "model": self.model.name,
            "workshop_id": self.model.workshop_id,
            "grid_size": self.model.grid_size,
            "position": rounded(self.center),
            "forward": rounded(self.forward, 6),
            "up": rounded(self.up, 6),
            "radius": round(self.model.radius, 1),
            "mass_kg": round(self.model.mass),
            "lift_g_at_sea_level": round(self.model.lift_g, 2),
        }


def ship_model(
    workshop_id: str,
    name: str,
    folders: list[Path],
    owner: int,
    definitions: dict[tuple[str, str], BlockDefinition],
) -> ShipModel:
    """The blueprint's grids turned to face -Z with up +Y, block bounds centered on the origin,
    fueled, powered, owned by owner and shared with everyone."""
    bp = ET.parse(bts.find_blueprint(workshop_id, folders)).getroot()
    grids = [copy.deepcopy(g) for g in bp.find(".//ShipBlueprint/CubeGrids")]
    main, lo, hi = bts.align(grids)
    center = [(lo[i] + hi[i]) / 2 for i in range(3)]
    for g in grids:
        position, forward, up = bts.frame(g)
        bts.set_frame(g, [p - c for p, c in zip(position, center)], forward, up)
    bts.prepare(grids, str(owner))
    mass, lift = 0.0, 0.0
    for g in grids:
        bw.set_child(g, "IsStatic", "false")
        bw.set_child(g, "CreatePhysics", "true")
        _, forward, up = bts.frame(g)
        for block in g.find("CubeBlocks"):
            definition = definitions[(kind(block), block.findtext("SubtypeName") or "")]
            mass += definition.mass
            pushes_up = dot(thrust_direction(block, forward, up), (0, 1, 0)) > 0.99
            if kind(block) == "Thrust" and pushes_up:
                lift += definition.force * definition.effectiveness_in_atmosphere
            fuel_and_power(block, definition)
    return ShipModel(
        workshop_id=workshop_id,
        name=name,
        grids=grids,
        main=grids.index(main),
        radius=dist(lo, hi) / 2,
        grid_size=main.findtext("GridSizeEnum"),
        mass=mass,
        lift_g=lift / (mass * 9.81),
    )


def thrust_direction(block: ET.Element, forward: Vec, up: Vec) -> Vec:
    """A thruster pushes its grid toward its block's back (MyThrust.ThrustForce)."""
    facing = bts.DIRECTIONS[bw.block_facing(block)[0]]
    return mul(bts.to_world(facing, forward, up), -1)


def fuel_and_power(block: ET.Element, definition: BlockDefinition):
    if block.find("Owner") is not None:
        bw.set_child(block, "ShareMode", "All")
    if kind(block) in ENABLED:
        update(block, "Enabled", "true")
    update(block, "ThrustOverride", 0)
    update(block, "GyroOverride", "false")
    if kind(block) == "BatteryBlock":
        update(block, "CurrentStoredPower", definition.max_stored_power)
        update(block, "ChargeMode", 0)
        update(block, "OnlyDischargeEnabled", "false")
    if kind(block) == "OxygenTank":
        update(block, "FilledRatio", 1)
        update(block, "IsStockpiling", "false")
    if kind(block) == "Reactor":
        for inventory in block.iter():
            if inventory.get(bw.XSI_TYPE) == "MyObjectBuilder_Inventory":
                bw.set_items(inventory, [("MyObjectBuilder_Ingot", "Uranium", URANIUM)])


def update(parent: ET.Element, tag: str, value):
    """Sets a child's text only where the blueprint has the child."""
    if parent.find(tag) is not None:
        parent.find(tag).text = str(value)


def ship_grids(ship: Ship, ids: bw.Ids) -> list[ET.Element]:
    grids = bw.clone_grids(ship.model.grids, ids)
    origin = ((0.0, 0.0, 0.0), (0.0, 0.0, -1.0), (0.0, 1.0, 0.0))
    for g in grids:
        bw.follow(g, origin, (ship.center, ship.forward, ship.up))
    grids[ship.model.main].find("DisplayName").text = ship.name
    return grids


# The walking platform


@dataclass
class Platform:
    position: Vec
    forward: Vec
    up: Vec

    def cell_top(self, x: int, z: int) -> Vec:
        """World position of the floor's top face over the floor cell (x, z)."""
        return add(
            self.position, bts.to_world((x * 2.5, 1.25, z * 2.5), self.forward, self.up)
        )

    @classmethod
    def centered(cls, center: Vec, forward: Vec, up: Vec) -> "Platform":
        middle = (PLATFORM_SIZE - 1) / 2 * 2.5
        return cls(
            sub(center, bts.to_world((middle, 0, middle), forward, up)), forward, up
        )


def wall_cells() -> set[tuple[int, int]]:
    open_cells = set(CORRIDOR) | set(HALL)
    walls = set()
    for x, z in CORRIDOR:
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                walls.add((x + dx, z + dz))
    return {
        (x, z)
        for x, z in walls - open_cells
        if 0 <= x < PLATFORM_SIZE and 0 <= z < PLATFORM_SIZE
    }


def platform_grid(platform: Platform, ids: bw.Ids, owner: int) -> ET.Element:
    """A static light armor platform with gravity, powered by batteries under its floor."""
    grid = ET.Element(
        "MyObjectBuilder_EntityBase", {bw.XSI_TYPE: "MyObjectBuilder_CubeGrid"}
    )
    bw.el(grid, "SubtypeName")
    bw.el(grid, "EntityId", ids.new())
    bw.el(grid, "PersistentFlags", "CastShadows InScene")
    # In its place after the flags, where the game writes it
    bw.el(grid, "PositionAndOrientation")
    bw.set_position(grid, platform.position, platform.forward, platform.up)
    bw.el(grid, "GridSizeEnum", "Large")
    blocks = bw.el(grid, "CubeBlocks")
    for x in range(PLATFORM_SIZE):
        for z in range(PLATFORM_SIZE):
            armor(blocks, (x, 0, z))
    for x, z in sorted(wall_cells()):
        for y in range(1, WALL_HEIGHT + 1):
            armor(blocks, (x, y, z))
    middle = PLATFORM_SIZE // 2
    gravity = functional(
        blocks, "GravityGenerator", "", (middle, -1, middle), ids, owner
    )
    bw.set_child(gravity, "CustomName", f"{PLATFORM} Gravity")
    bw.set_child(gravity, "Enabled", "true")
    bw.set_child(gravity, "GravityAcceleration", 9.81)
    bw.vector(gravity, "FieldSize", GRAVITY_FIELD)
    for n in range(PLATFORM_BATTERIES):
        cell = (middle - PLATFORM_BATTERIES // 2 + n, -1, middle + 1)
        battery = functional(
            blocks, "BatteryBlock", "LargeBlockBatteryBlock", cell, ids, owner
        )
        for tag, value in (
            ("CustomName", f"{PLATFORM} Battery"),
            ("Enabled", "true"),
            ("CurrentStoredPower", 3),
            ("ProducerEnabled", "true"),
            ("MaxStoredPower", 3),
            ("SemiautoEnabled", "false"),
            ("OnlyDischargeEnabled", "false"),
            ("ChargeMode", 0),
        ):
            bw.set_child(battery, tag, value)
    bw.el(grid, "DisplayName", PLATFORM)
    bw.el(grid, "DestructibleBlocks", "true")
    bw.el(grid, "IsStatic", "true")
    # Grids written without it load without physics
    bw.el(grid, "CreatePhysics", "true")
    return grid


def armor(blocks: ET.Element, cell: tuple[int, int, int]):
    block = bw.el(
        blocks,
        "MyObjectBuilder_CubeBlock",
        **{bw.XSI_TYPE: "MyObjectBuilder_CubeBlock"},
    )
    bw.el(block, "SubtypeName", "LargeBlockArmorBlock")
    bw.el(block, "Min", **dict(zip("xyz", cell)))
    bw.vector(block, "ColorMaskHSV", (0.0, -0.8, 0.0))


def functional(
    blocks: ET.Element, block_type: str, subtype: str, cell, ids: bw.Ids, owner: int
) -> ET.Element:
    block = bw.el(
        blocks,
        "MyObjectBuilder_CubeBlock",
        **{bw.XSI_TYPE: f"MyObjectBuilder_{block_type}"},
    )
    bw.el(block, "SubtypeName", subtype)
    bw.el(block, "EntityId", ids.new())
    bw.el(block, "Min", **dict(zip("xyz", cell)))
    bw.el(block, "BlockOrientation", Forward="Forward", Up="Up")
    bw.vector(block, "ColorMaskHSV", (0.0, -0.8, 0.0))
    bw.el(block, "Owner", owner)
    bw.el(block, "BuiltBy", owner)
    bw.el(block, "ShareMode", "All")
    return block


# The world as the sites see it


@dataclass
class Sphere:
    name: str
    center: Vec
    radius: float


@dataclass
class World:
    planets: dict[str, ET.Element]
    zones: list[Sphere]
    grids: list[ET.Element]
    voxels: list[Sphere]
    lanes: list[tuple[str, Vec, Vec]]
    ring: dict

    @classmethod
    def load(cls, save: Path, sector: ET.Element) -> "World":
        zones, grids, voxels, computers = [], [], [], {}
        for entity in sector.find("SectorObjects"):
            entity_type = kind(entity)
            if entity_type == "SafeZone":
                zones.append(
                    Sphere(
                        entity.findtext("DisplayName"),
                        bw.frame(entity)[0],
                        float(entity.findtext("Radius")),
                    )
                )
            if entity_type == "CubeGrid":
                grids.append(entity)
                lane = entity.findtext("DisplayName").split(" ")
                if lane[0] == "TL":
                    computers.setdefault(lane[1], []).append(bw.frame(entity)[0])
            if entity_type == "VoxelMap":
                voxels.append(voxel_sphere(entity, save))
        planets = bw.load_planets(sector)
        earth, moon = planets["EarthLike"], planets["Moon"]
        axis = bw.rotate(
            bw.magnetic_axis(earth, bw.sun_direction(sector)),
            earth.up,
            bw.moon_turn(earth, moon),
        )
        ring = {
            "center": rounded(earth.center),
            "axis": rounded(axis, 6),
            "inner_radius": bw.RING_INNER,
            "outer_radius": bw.RING_OUTER,
            "thickness": bw.RING_THICKNESS,
        }
        lanes = [(lane_id, *ends) for lane_id, ends in sorted(computers.items())]
        return cls(planets, zones, grids, voxels, lanes, ring)

    def zone(self, name: str) -> Sphere:
        return next(z for z in self.zones if z.name == name)

    def grid(self, name: str) -> ET.Element:
        return next(g for g in self.grids if g.findtext("DisplayName") == name)


def voxel_sphere(entity: ET.Element, save: Path) -> Sphere:
    """An asteroid's storage box as the sphere it fills at most."""
    name = entity.findtext("StorageName")
    position, forward, up = bw.frame(entity)
    half = bw.storage_size(save / f"{name}.vx2") / 2
    right = cross(forward, up)
    center = add(
        position, add(add(mul(right, half), mul(up, half)), mul(forward, -half))
    )
    return Sphere(name, center, half)


def grid_sphere(grid: ET.Element) -> Sphere:
    """The sphere around a grid's block cells."""
    size = bts.GRID_SIZE[grid.findtext("GridSizeEnum")]
    cells = [bw.block_cell(b) for b in grid.find("CubeBlocks")]
    lo = [min(c[i] for c in cells) for i in range(3)]
    hi = [max(c[i] for c in cells) for i in range(3)]
    position, forward, up = bw.frame(grid)
    middle = [(lo[i] + hi[i]) / 2 * size for i in range(3)]
    center = add(position, bts.to_world(middle, forward, up))
    radius = dist(lo, hi) / 2 * size + size
    return Sphere(grid.findtext("DisplayName"), center, radius)


def block_center(grid: ET.Element, block: ET.Element) -> Vec:
    size = bts.GRID_SIZE[grid.findtext("GridSizeEnum")]
    position, forward, up = bw.frame(grid)
    return add(
        position, bts.to_world([c * size for c in bw.block_cell(block)], forward, up)
    )


def connectors(world: World, station: str) -> list[dict]:
    """The station grid's one-block connectors: block center, outward axis and up."""
    grid = world.grid(station)
    _, forward, up = bw.frame(grid)
    result = []
    for block in grid.find("CubeBlocks"):
        if kind(block) != "ShipConnector" or "Platform" in block.findtext(
            "SubtypeName"
        ):
            continue
        block_forward, block_up = bw.block_facing(block)
        result.append(
            {
                "name": block.findtext("CustomName"),
                "subtype": block.findtext("SubtypeName"),
                "cell": list(bw.block_cell(block)),
                "position": rounded(block_center(grid, block)),
                "out": rounded(
                    bts.to_world(bts.DIRECTIONS[block_forward], forward, up), 6
                ),
                "up": rounded(bts.to_world(bts.DIRECTIONS[block_up], forward, up), 6),
                "grid_size": "Large",
            }
        )
    return result


def free_connector(world: World, station: str, connector: dict, width: float) -> bool:
    """Whether a ship width wide can come straight in along the connector's axis from outside
    the safe zone, past every block of the station and its shipyard."""
    zone = world.zone(station)
    start = tuple(connector["position"])
    out = tuple(connector["out"])
    end = add(start, mul(out, zone.radius * 2))
    for grid in world.grids:
        if dist(bw.frame(grid)[0], zone.center) > zone.radius:
            continue
        for block in grid.find("CubeBlocks"):
            p = block_center(grid, block)
            if dist(p, start) > 3 and dot(sub(p, start), out) > 0:
                if bw.segment_distance(p, start, end) < width / 2 + 2.5:
                    return False
    return True


# Site design


def rounded(v, digits: int = 2) -> list[float]:
    return [round(x, digits) for x in v]


def gate(position: Vec, tolerance: float = SHIP_GATE_TOLERANCE) -> dict:
    return {"position": rounded(position), "tolerance": tolerance}


@dataclass
class Frame:
    """Local axes around a station's safe zone."""

    origin: Vec
    e1: Vec
    e2: Vec
    e3: Vec

    def at(self, x: float, y: float, z: float) -> Vec:
        return add(
            self.origin, add(add(mul(self.e1, x), mul(self.e2, y)), mul(self.e3, z))
        )

    def direction(self, x: float, y: float, z: float) -> Vec:
        return norm(sub(self.at(x, y, z), self.origin))

    @classmethod
    def around(cls, origin: Vec, away: Vec, up: Vec) -> "Frame":
        e1 = norm(away)
        e2 = norm(bw.perpendicular_part(up, e1))
        return cls(origin, e1, e2, cross(e1, e2))


def facing_up(forward: Vec, up: Vec) -> Vec:
    return norm(bw.perpendicular_part(up, forward))


class Design:
    """Places the ships and the sites."""

    def __init__(self, world: World, models: dict[str, ShipModel]):
        self.world = world
        self.models = models
        self.ships: list[Ship] = []
        self.sites: list[dict] = []
        earth, moon = world.planets["EarthLike"], world.planets["Moon"]
        self.earth, self.moon = earth, moon
        self.axis = tuple(world.ring["axis"])
        gamma = world.zone("Gamma Station").center
        self.gamma = Frame.around(gamma, sub(gamma, earth.center), self.axis)
        moon_station = world.zone("Moon Station").center
        self.lunar = Frame.around(
            moon_station, sub(moon_station, moon.center), earth.up
        )
        self.platform = Platform.centered(
            self.gamma.at(1500, -1500, 1500), self.gamma.e3, self.gamma.e2
        )

    def ship(self, name: str, center: Vec, toward: Vec) -> str:
        forward = norm(sub(toward, center))
        self.ships.append(
            Ship(
                name,
                self.models[SHIP_MODELS[name]],
                center,
                forward,
                facing_up(forward, self.gamma.e2),
            )
        )
        return name

    def route(
        self,
        site_kind: str,
        site_id: str,
        ship: str,
        start: Vec,
        points: list[Vec],
        **extra,
    ):
        self.ship(ship, start, points[0])
        site = {
            "id": site_id,
            "kind": site_kind,
            "ship": ship,
            "gates": [gate(p) for p in points],
        }
        site.update(extra)
        self.sites.append(site)

    def build(self):
        g = self.gamma
        start = g.at(1000, -500, -600)
        self.route(
            "cruise",
            "cruise-1",
            "PT-01",
            start,
            [add(start, mul(g.e1, 1500))],
            arrival=gate(add(start, mul(g.e1, CRUISE_LENGTH))),
        )
        start = g.at(700, 900, -500)
        heading = g.direction(0.45, 0.89, 0)
        self.route(
            "cruise",
            "cruise-2",
            "PT-02",
            start,
            [add(start, mul(heading, 1500))],
            arrival=gate(add(start, mul(heading, CRUISE_LENGTH))),
        )
        self.docks()
        self.landings()
        self.asteroids()
        self.traffic()
        self.jetpack()
        self.walk()

    def docks(self):
        station = "Gamma Station"
        width = 2 * self.models[CHALLENGER].radius
        zone = self.world.zone(station)
        by_cell = {tuple(c["cell"]): c for c in connectors(self.world, station)}
        for n, (ship, cell, side) in enumerate(DOCKS, 1):
            connector = by_cell[cell]
            if not free_connector(self.world, station, connector, width):
                raise SystemExit(f"{station}'s connector {cell} has no free approach")
            position, out = tuple(connector["position"]), tuple(connector["out"])
            # The approach gate: where the connector's axis is DOCK_APPROACH from the zone's center
            offset = sub(position, zone.center)
            b = dot(offset, out)
            t = -b + math.sqrt(b * b - dot(offset, offset) + DOCK_APPROACH**2)
            points = [add(position, mul(out, t))]
            # Back from there, each leg turned toward side
            normal = norm(cross(out, self.gamma.direction(*side)))
            leg = out
            for turn in DOCK_TURNS:
                leg = bw.rotate(leg, normal, math.radians(turn))
                points.insert(0, add(points[0], mul(leg, DOCK_LEG)))
            start = points.pop(0)
            gates = [gate(p) for p in points[:-1]] + [
                gate(points[-1], DOCK_GATE_TOLERANCE)
            ]
            self.ship(ship, start, points[0])
            self.sites.append(
                {
                    "id": f"dock-{n}",
                    "kind": "dock",
                    "ship": ship,
                    "gates": gates,
                    "connector": {"station": station, **connector},
                }
            )

    def pad(self, planet, direction: Vec, body: str) -> dict:
        radius = float(planet.entity.findtext("Radius"))
        return {
            "position": rounded(add(planet.center, mul(direction, radius))),
            "up": rounded(direction, 6),
            "body": body,
            "height": "nominal",
            "note": "at the planet's nominal radius; the ground is up to "
            f"{float(planet.entity.findtext('MaximumHillRadius')) - radius:.0f} m higher "
            f"or {radius - float(planet.entity.findtext('MinimumSurfaceRadius')):.0f} m "
            "lower, the host finds the real surface along up",
        }

    def landings(self):
        moon, lunar = self.moon, self.lunar
        for site_id, ship, side in (
            ("land-moon-1", "PT-05", 1),
            ("land-moon-2", "PT-13", -1),
        ):
            down = lunar.direction(
                math.cos(math.radians(25)), 0, side * math.sin(math.radians(25))
            )
            start = lunar.at(-700, -side * 400, side * 600)
            self.route(
                "land",
                site_id,
                ship,
                start,
                [add(moon.center, mul(down, 11_500))],
                pad=self.pad(moon, down, "Moon"),
            )
        # From Gamma Station out of the ring's plane, then down at 20 degrees north of it
        earth, g = self.earth, self.gamma
        down = norm(
            add(
                mul(g.e1, math.cos(math.radians(20))),
                mul(g.e2, math.sin(math.radians(20))),
            )
        )
        start = g.at(800, 400, 1200)
        self.route(
            "land",
            "land-earth-1",
            "PT-06",
            start,
            [g.at(0, 12_000, 3000), add(earth.center, mul(down, 70_000))],
            pad=self.pad(earth, down, "EarthLike"),
        )

    def asteroids(self):
        """Through the ring, straight through its largest asteroid near Gamma Station."""
        g = self.gamma
        rock = min(
            (
                v
                for v in self.world.voxels
                if v.name.startswith("Octahedron-Asteroid") and v.radius >= 256
            ),
            key=lambda v: dist(v.center, g.origin),
        )
        start = g.at(600, -900, 600)
        self.route(
            "asteroids",
            "asteroids-1",
            "PT-07",
            start,
            [
                add(rock.center, mul(self.axis, -3000)),
                add(rock.center, mul(self.axis, 3000)),
            ],
            arrival=gate(add(rock.center, mul(self.axis, 5000))),
            obstacle={
                "asteroid": rock.name,
                "center": rounded(rock.center),
                "radius": rock.radius,
                "note": "the line between the two gates runs through its center",
            },
        )

    def traffic(self):
        g = self.gamma
        a, b = g.at(2500, -2000, -2000), g.at(2500, -2000, 0)
        self.sites.append(
            {
                "id": "traffic-1",
                "kind": "traffic",
                "note": "two ships swap places head-on",
                "ships": [self.ship("PT-08", a, b), self.ship("PT-09", b, a)],
                "arrivals": [gate(b), gate(a)],
            }
        )
        middle = g.at(3500, 1500, 2500)
        starts = [
            add(middle, mul(add(mul(g.e2, math.cos(t)), mul(g.e3, math.sin(t))), 1200))
            for t in (math.radians(a) for a in (90, 210, 330))
        ]
        names = [
            self.ship(n, s, middle) for n, s in zip(("PT-10", "PT-11", "PT-12"), starts)
        ]
        self.sites.append(
            {
                "id": "traffic-2",
                "kind": "traffic",
                "note": "three ships cross one point, each to the far side of it",
                "crossing": rounded(middle),
                "ships": names,
                "arrivals": [gate(sub(mul(middle, 2), s)) for s in starts],
            }
        )

    def jetpack(self):
        """Half a circle over Gamma Station, outside its safe zone."""
        g = self.gamma
        points = [
            g.at(-100, JETPACK_RADIUS * math.sin(t), JETPACK_RADIUS * math.cos(t))
            for t in (math.radians(a) for a in (30, 60, 90, 120, 150))
        ]
        self.sites.append(
            {
                "id": "jetpack-1",
                "kind": "jetpack",
                "start": rounded(points[0]),
                "gates": [gate(p, JETPACK_TOLERANCE) for p in points[1:-1]],
                "arrival": gate(points[-1], JETPACK_TOLERANCE),
            }
        )

    def walk(self):
        points = [self.platform.cell_top(x, z) for x, z in WALK_CELLS]
        self.sites.append(
            {
                "id": "walk-1",
                "kind": "walk",
                "platform": PLATFORM,
                "note": "the floor's top face; the gravity generator pulls along -up",
                "start": rounded(points[0]),
                "gates": [gate(p, WALK_TOLERANCE) for p in points[1:-1]],
                "arrival": gate(points[-1], WALK_TOLERANCE),
                "up": rounded(self.platform.up, 6),
            }
        )


def site_list(world: World, design: Design, seed: int) -> dict:
    stations = []
    for zone in world.zones:
        if zone.name.endswith(" Station"):
            stations.append(
                {
                    "name": zone.name,
                    "center": rounded(zone.center),
                    "safe_zone_radius": zone.radius,
                    "connectors": connectors(world, zone.name),
                }
            )
    planets = []
    for name, planet in world.planets.items():
        e = planet.entity
        planets.append(
            {
                "name": name,
                "center": rounded(planet.center),
                "radius": float(e.findtext("Radius")),
                "min_surface_radius": float(e.findtext("MinimumSurfaceRadius")),
                "max_hill_radius": float(e.findtext("MaximumHillRadius")),
                "gravity_g": float(e.findtext("SurfaceGravity")),
                "atmosphere": e.findtext("HasAtmosphere") == "true",
            }
        )
    return {
        "world": NAME,
        "seed": seed,
        "notes": [
            "Positions are world coordinates in meters, tolerances are radii in meters.",
            "A ship's position is the center of its block bounds, radius half their diagonal.",
            "A connector's position is its block's center, out its outward axis.",
            "A pad's height is nominal: the host finds the ground along its up.",
            "lift_g_at_sea_level: the thrust along the cockpit's up in atmosphere per weight at 1 g.",
        ],
        "ships": [s.to_json() for s in sorted(design.ships, key=lambda s: s.name)],
        "stations": stations,
        "planets": planets,
        "ring": world.ring,
        "lanes": [
            {"id": lane_id, "ends": [rounded(a), rounded(b)]}
            for lane_id, a, b in world.lanes
        ],
        "platform": {
            "name": PLATFORM,
            "position": rounded(design.platform.position),
            "forward": rounded(design.platform.forward, 6),
            "up": rounded(design.platform.up, 6),
        },
        "sites": design.sites,
    }


# The save


def add_player(checkpoint: ET.Element, ids: bw.Ids, steam_id: str) -> int:
    identity_id = ids.new(2)
    identity = bw.el(checkpoint.find("Identities"), "MyObjectBuilder_Identity")
    bw.el(identity, "IdentityId", identity_id)
    bw.el(identity, "DisplayName", "Pilot")
    bw.el(identity, "CharacterEntityId", 0)
    item = bw.el(checkpoint.find("AllPlayersData/dictionary"), "item")
    key = bw.el(item, "Key", **{bw.XSI_TYPE: "PlayerId"})
    bw.el(key, "ClientId", steam_id)
    bw.el(key, "SerialId", 0)
    value = bw.el(item, "Value", **{bw.XSI_TYPE: "MyObjectBuilder_Player"})
    bw.el(value, "DisplayName", "Pilot")
    bw.el(value, "IdentityId", identity_id)
    bw.el(value, "Connected", "false")
    return identity_id


def used_ids(*roots: ET.Element) -> set[int]:
    return {
        int(node.text)
        for root in roots
        for node in root.iter()
        if node.tag in ("EntityId", "IdentityId") and node.text and node.text.isdigit()
    }


def build(saves: Path, seed: int, steam_id: str, folders: list[Path]):
    save = saves / NAME
    bw.NAME = NAME
    bw.DESCRIPTION = (
        "Octahedron with test ships and sites for autopilot tests (SE-1425)."
    )
    # A world with a local mod has to be offline anyway, and the tests need no server
    bw.SETTINGS["OnlineMode"] = "OFFLINE"
    saves.mkdir(parents=True, exist_ok=True)
    bw.main(
        [
            str(save),
            "--seed",
            str(seed),
            "--ore-mod",
            str(ORE_MOD),
            "--octahedron-mod",
            str(OCTAHEDRON_MOD),
        ]
    )

    sector_tree = ET.parse(save / "SANDBOX_0_0_0_.sbs")
    checkpoint_tree = ET.parse(save / "Sandbox.sbc")
    sector, checkpoint = sector_tree.getroot(), checkpoint_tree.getroot()
    ids = bw.Ids(random.Random(f"{seed} pilot test"))
    ids.used = used_ids(sector, checkpoint)
    owner = add_player(checkpoint, ids, steam_id)

    definitions = block_definitions()
    names = dict(bts.load_ships(bts.HERE / "trader_ships.txt"))
    models = {
        workshop_id: ship_model(
            workshop_id, names[workshop_id], folders, owner, definitions
        )
        for workshop_id in sorted(set(SHIP_MODELS.values()))
    }
    world = World.load(save, sector)
    design = Design(world, models)
    design.build()
    objects = sector.find("SectorObjects")
    for ship in design.ships:
        objects.extend(ship_grids(ship, ids))
    objects.append(platform_grid(design.platform, ids, owner))

    for tree, file in (
        (sector_tree, "SANDBOX_0_0_0_.sbs"),
        (checkpoint_tree, "Sandbox.sbc"),
    ):
        ET.indent(tree, "  ")
        bw.write_xml(tree, save / file)
    text = json.dumps(site_list(world, design, seed), indent=2) + "\n"
    (saves / SITES).write_text(text)
    shutil.copy(saves / SITES, save / SITES)
    print(f"{save}: {len(design.ships)} ships, {len(design.sites)} sites")


# The check


class Report:
    def __init__(self):
        self.failed = False

    def line(self, ok: bool, text: str):
        self.failed |= not ok
        print(f"{'PASS' if ok else 'FAIL'} {text}")


def leg_samples(a: Vec, b: Vec) -> list[Vec]:
    count = max(1, math.ceil(dist(a, b) / LEG_STEP))
    return [add(a, mul(sub(b, a), i / count)) for i in range(count + 1)]


def in_ring(p: Vec, ring: dict) -> bool:
    relative = sub(p, tuple(ring["center"]))
    height = dot(relative, tuple(ring["axis"]))
    rho = math.sqrt(max(0.0, dot(relative, relative) - height * height))
    return (
        ring["inner_radius"] - RING_MARGIN <= rho <= ring["outer_radius"] + RING_MARGIN
        and abs(height) <= ring["thickness"] / 2 + RING_MARGIN
    )


@dataclass
class Leg:
    site: str
    a: Vec
    b: Vec
    into_zone: bool = False
    into_planet: bool = False
    through_ring: bool = False


def site_starts(site: dict, ships: dict[str, dict]) -> list[Vec]:
    if "start" in site:
        return [tuple(site["start"])]
    names = site.get("ships") or [site["ship"]]
    return [tuple(ships[n]["position"]) for n in names]


def site_legs(site: dict, ships: dict[str, dict]) -> list[Leg]:
    starts = site_starts(site, ships)
    if site["kind"] == "traffic":
        return [
            Leg(site["id"], s, tuple(arrival["position"]))
            for s, arrival in zip(starts, site["arrivals"])
        ]
    points = starts + [tuple(g["position"]) for g in site["gates"]]
    if "arrival" in site:
        points.append(tuple(site["arrival"]["position"]))
    legs = [Leg(site["id"], a, b) for a, b in zip(points, points[1:])]
    if site["kind"] == "asteroids":
        legs[1].through_ring = True
    if site["kind"] == "dock":
        legs.append(
            Leg(
                site["id"],
                points[-1],
                tuple(site["connector"]["position"]),
                into_zone=True,
            )
        )
    if site["kind"] == "land":
        legs.append(
            Leg(
                site["id"], points[-1], tuple(site["pad"]["position"]), into_planet=True
            )
        )
    return legs


def site_points(site: dict, ships: dict[str, dict]) -> list[Vec]:
    points = site_starts(site, ships) + [
        tuple(g["position"]) for g in site.get("gates", [])
    ]
    if "arrival" in site:
        points.append(tuple(site["arrival"]["position"]))
    points += [tuple(a["position"]) for a in site.get("arrivals", [])]
    return points


def turn_degrees(a: Vec, b: Vec, c: Vec) -> float:
    u, v = norm(sub(b, a)), norm(sub(c, b))
    return math.degrees(math.acos(max(-1.0, min(1.0, dot(u, v)))))


def check(saves: Path) -> bool:
    report = Report()
    save = saves / NAME
    checkpoint = ET.parse(save / "Sandbox.sbc").getroot()
    config = ET.parse(save / "Sandbox_config.sbc").getroot()
    sector = ET.parse(save / "SANDBOX_0_0_0_.sbs").getroot()
    sites = json.loads((saves / SITES).read_text())
    report.line(
        (save / SITES).read_text() == (saves / SITES).read_text(),
        f"{SITES} next to the save and in it are the same",
    )

    for name, root in (("Sandbox.sbc", checkpoint), ("Sandbox_config.sbc", config)):
        settings = (
            root.findtext("SessionName"),
            root.findtext("Settings/OnlineMode"),
            root.findtext("Settings/WorldSizeKm"),
        )
        report.line(
            settings == (save.name, "OFFLINE", "0"),
            f"{name}: SessionName {settings[0]!r} = folder name, OnlineMode {settings[1]}, WorldSizeKm {settings[2]}",
        )
    rts = save / RELATIVE_TOP_SPEED
    report.line(
        rts.exists() and rts.read_bytes() == bw.RELATIVE_TOP_SPEED.read_bytes(),
        f"{RELATIVE_TOP_SPEED} equals config/{bw.RELATIVE_TOP_SPEED.name}",
    )
    mods = [
        (m.get("FriendlyName"), m.findtext("PublishedFileId"))
        for m in checkpoint.iterfind("Mods/ModItem")
    ]
    local = [m for m in mods if m[1] == "0"]
    report.line(not local, f"{len(mods)} mods, {len(local)} local (id 0)")
    missing = [m for m in mods if not (bts.WORKSHOP / m[1]).is_dir()]
    print(f"INFO mods: {', '.join(f'{n} ({i})' for n, i in mods)}")
    print(
        f"INFO {len(mods) - len(missing)} of {len(mods)} mods are in {bts.WORKSHOP}; missing: {missing or 'none'}"
    )

    world = World.load(save, sector)
    ships = {s["name"]: s for s in sites["ships"]}
    check_ships(report, world, ships)
    check_sites(report, world, sites, ships)
    return not report.failed


def check_ships(report: Report, world: World, ships: dict[str, dict]):
    grids = {g.findtext("DisplayName"): g for g in world.grids}
    names = sorted(n for n in grids if n.startswith("PT-"))
    expected = [f"PT-{n:02d}" for n in range(1, len(names) + 1)]
    report.line(
        len(names) >= 12 and names == expected and set(names) == set(ships),
        f"{len(names)} ships: {', '.join(names)}",
    )
    models = {(s["model"], s["grid_size"]) for s in ships.values()}
    sizes = {size for _, size in models}
    report.line(
        len(models) >= 3 and sizes == {"Small", "Large"},
        f"{len(models)} models, grid sizes {sorted(sizes)}",
    )
    for name in names:
        g = grids[name]
        flags = (
            g.findtext("IsStatic"),
            g.findtext("CreatePhysics"),
            g.findtext("DampenersEnabled"),
        )
        report.line(
            flags == ("false", "true", "true"),
            f"{name} {ships[name]['model']}: dynamic, physics, dampeners {flags}",
        )
    spheres = {name: grid_sphere(grids[name]) for name in names}
    obstacles = [
        grid_sphere(g) for g in world.grids if g.findtext("DisplayName") not in spheres
    ]
    obstacles += world.voxels + world.zones
    for name, ship in spheres.items():
        others = obstacles + [s for n, s in spheres.items() if n != name]
        nearest = min(others, key=lambda o: dist(o.center, ship.center) - o.radius)
        gap = dist(nearest.center, ship.center) - nearest.radius - ship.radius
        for planet in world.planets.values():
            hill = float(planet.entity.findtext("MaximumHillRadius"))
            gap = min(gap, dist(planet.center, ship.center) - hill - ship.radius)
        report.line(
            gap >= SHIP_CLEARANCE,
            f"{name} is {gap:.0f} m from the nearest thing ({nearest.name})",
        )


def check_sites(report: Report, world: World, sites: dict, ships: dict[str, dict]):
    counts: dict[str, int] = {}
    for site in sites["sites"]:
        counts[site["kind"]] = counts.get(site["kind"], 0) + 1
    needed = {
        "cruise": 2,
        "dock": 2,
        "land": 2,
        "asteroids": 1,
        "traffic": 2,
        "jetpack": 1,
        "walk": 1,
    }
    report.line(
        all(counts.get(k, 0) >= n for k, n in needed.items()),
        f"sites per kind: {counts}",
    )
    lands = {s["pad"]["body"] for s in sites["sites"] if s["kind"] == "land"}
    report.line(lands == {"Moon", "EarthLike"}, f"landings on {sorted(lands)}")

    starts = {s["id"]: site_starts(s, ships) for s in sites["sites"]}
    legs = [leg for s in sites["sites"] for leg in site_legs(s, ships)]
    for site in sites["sites"]:
        problems = point_problems(world, site, ships, starts)
        problems += leg_problems(
            world, sites["ring"], [l for l in legs if l.site == site["id"]], legs
        )
        problems += shape_problems(site, ships)
        report.line(
            not problems, f"site {site['id']}: {'; '.join(problems) or 'clear'}"
        )


def point_problems(
    world: World, site: dict, ships: dict[str, dict], starts: dict[str, list[Vec]]
) -> list[str]:
    problems = []
    for p in site_points(site, ships):
        problems += [
            f"point in {z.name}'s zone"
            for z in world.zones
            if dist(p, z.center) < z.radius
        ]
        for name, planet in world.planets.items():
            if dist(p, planet.center) < float(
                planet.entity.findtext("MaximumHillRadius")
            ):
                problems.append(f"point in {name}")
        for other, other_starts in starts.items():
            if other == site["id"]:
                continue
            near = min(dist(p, s) for s in other_starts)
            if near < SITE_START_CLEARANCE:
                problems.append(f"point {near:.0f} m from {other}'s start")
    return problems


def leg_problems(
    world: World, ring: dict, own: list[Leg], legs: list[Leg]
) -> list[str]:
    problems = []
    for leg in own:
        samples = leg_samples(leg.a, leg.b)
        if not leg.into_zone:
            zones = {
                z.name
                for z in world.zones
                for p in samples
                if dist(p, z.center) < z.radius
            }
            problems += [f"leg through {z}'s zone" for z in sorted(zones)]
        if not leg.into_planet:
            for name, planet in world.planets.items():
                hill = float(planet.entity.findtext("MaximumHillRadius"))
                if any(dist(p, planet.center) < hill for p in samples):
                    problems.append(f"leg through {name}")
        if not leg.through_ring and any(in_ring(p, ring) for p in samples):
            problems.append("leg through the asteroid ring")
        for lane_id, a, b in world.lanes:
            near = min(bw.segment_distance(p, a, b) for p in samples)
            if near < LANE_CLEARANCE:
                problems.append(f"leg {near:.0f} m from lane TL {lane_id}")
        for other in legs:
            if other.site == leg.site:
                continue
            near = min(bw.segment_distance(p, other.a, other.b) for p in samples)
            docked = leg.into_zone and other.into_zone
            if near < (DOCKED_CLEARANCE if docked else LEG_CLEARANCE):
                problems.append(f"leg {near:.0f} m from a leg of {other.site}")
    return sorted(set(problems))


def shape_problems(site: dict, ships: dict[str, dict]) -> list[str]:
    problems = []
    if site["kind"] == "cruise":
        length = dist(site_starts(site, ships)[0], tuple(site["arrival"]["position"]))
        if length < 20_000:
            problems.append(f"cruise of {length:.0f} m")
    if site["kind"] == "dock":
        points = site_starts(site, ships) + [
            tuple(g["position"]) for g in site["gates"]
        ]
        points.append(tuple(site["connector"]["position"]))
        turns = [
            turn_degrees(*points[i - 1 : i + 2]) for i in range(1, len(points) - 1)
        ]
        if not 3 <= len(site["gates"]) <= 5 or not all(30 <= t <= 90 for t in turns):
            problems.append(
                f"{len(site['gates'])} gates, turns {[round(t) for t in turns]}"
            )
    return problems


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "saves", type=Path, help="the folder the save and the site list go into"
    )
    parser.add_argument("--seed", type=int, default=42, help="build_world.py's seed")
    parser.add_argument("--steam-id", default=bts.VIKTOR, help="the world's player")
    parser.add_argument(
        "--blueprints", type=Path, action="append", help="blueprint folders"
    )
    parser.add_argument(
        "--check", action="store_true", help="only check a written save"
    )
    args = parser.parse_args()
    if not args.check:
        build(args.saves, args.seed, args.steam_id, args.blueprints or [bts.WORKSHOP])
    sys.exit(0 if check(args.saves) else 1)


if __name__ == "__main__":
    main()
