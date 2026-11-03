#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy"]
# ///
"""Sweeps the Sun over a full day and tells if it can strobe on a station.

    uv run check_sun_clearance.py <world folder> [--steps 7200]

Light strobes when the Sun passes behind something with gaps: a Trade Lanes gate or the
station's own shipyard (SE-809, SE-720). For each station and Sun position this takes the ray
from the Sun through every such object and measures how far it passes from the station, with
the object as its bounding sphere too: gates and, for the shipyard, each of its blocks. A
clearance above 0 m means no ray that touches the object can reach the station. For the
shipyard that is the station's bounding sphere; for gates only its decks, the hangar deck and
the lobby above it (build_world.STATION_DECKS_BOTTOM), block by block: a gate may shade the
engineering rooms below (SE-945). The angle is how far the Sun stays from the object's edge
seen from the station's lobby and from its hangar deck. "box" is how far the shipyard's block
bounds stay out of the slab of the Sun's rays through the station's bounding sphere, along
the Sun's axis: above 0 m the Sun never goes behind them. "zone" is the farthest block from
the safe zone's center, of its radius; "gravity" how far the zone stays out of the nearest
planet's gravity.

Prints a line per station and exits with 1 if any clearance is 0 or less. At the stations with
the double helix shipyard (build_world.HELIX_STATIONS), which Viktor laid out as they are, a
clearance of 0 or less shows as "laid" and doesn't fail.
"""

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

import build_world as world

CELL = 2.5
# Viewpoints in the station blueprint's cells (x, y, z): the lobby floor under the glass dome,
# and the deck with the connectors
VIEWPOINTS = {"lobby": (0, 8, 0), "hangar": (0, 3, 0)}
# Where people stand in the lobby, the glass dome's back half, eyes above the floor at y 6
LOBBY_SPOTS = [
    (x, y, z) for x in (-6, -3, 0, 3, 6) for z in (1, 4, 7) for y in (7.2, 8.6)
]


def sun_path(world_folder, steps):
    """(sun directions, one per step of a day, unit axis) as MySectorWeatherComponent has them."""
    sector = ET.parse(world_folder / "SANDBOX_0_0_0_.sbs").getroot()
    checkpoint = ET.parse(world_folder / "Sandbox.sbc").getroot()
    assert checkpoint.findtext(".//EnableSunRotation") == "true"
    # MySector: the axis comes from the environment's direction ...
    axis = np.array(world.sun_axis(sector))
    environment = np.array(world.sun_direction(sector))
    # ... and the start from the weather component's saved one, when it has one
    saved = checkpoint.find(".//BaseSunDirection")
    base = environment
    if saved is not None:
        base = np.array([float(saved.get(k)) for k in "xyz"])
    if np.abs(base).sum() < 0.001:
        base = environment
    base = base / np.linalg.norm(base)
    angles = 2 * np.pi * np.arange(steps) / steps
    # Rodrigues: dot(axis, base) stays as it is
    in_plane = base - axis * base.dot(axis)
    return (
        np.outer(np.cos(angles), in_plane)
        + np.outer(np.sin(angles), np.cross(axis, base))
        + axis * base.dot(axis)
    ), axis


def grid_frame(grid):
    """(position, right, up, backward) of a grid: its local x, y and z axes."""
    position, forward, up = map(np.array, world.frame(grid))
    return position, np.cross(forward, up), up, -forward


def world_position(entity):
    return np.array(world.frame(entity)[0])


def block_cells(grid):
    return np.array(
        [
            [int(m.get(k)) for k in "xyz"]
            for m in (b.find("Min") for b in grid.find("CubeBlocks"))
            if m is not None
        ]
    )


def to_world(grid, cells):
    position, right, up, backward = grid_frame(grid)
    meters = np.asarray(cells) * CELL
    return (
        position
        + np.outer(meters[..., 0], right)
        + np.outer(meters[..., 1], up)
        + np.outer(meters[..., 2], backward)
    )


def bounding_sphere(grid):
    """(center, radius) around the block cells."""
    cells = block_cells(grid)
    center = (cells.min(axis=0) + cells.max(axis=0)) / 2
    radius = np.linalg.norm((cells - center) * CELL, axis=1).max() + world.BLOCK_RADIUS
    return to_world(grid, center[None])[0], radius


def lane_gates(grids):
    """Centers of all gates, as Trade Lanes places them (UpdateRings, AddPointsBetweenGates):
    from each computer, every RingInterval to the other one, both ends included, to its right.
    """
    gates = []
    for grid in grids:
        data = grid.findtext(".//Value")
        if data is None or "TLID:" not in data:
            continue
        keys = dict(line.split(":", 1) for line in data.splitlines())
        target = np.array([float(x) for x in keys["Target"].split(":")[2:5]])
        position, right, _, _ = grid_frame(grid)
        for _, point in world.gate_points(
            tuple(position), tuple(target), float(keys["RingInterval"])
        ):
            gates.append(np.array(point) + right * world.GATE_OFFSET)
    return np.array(gates)


def sweep(sun, center, radius, viewpoints, points, radii):
    """(meters, degrees): the smallest clearance over the day."""
    meters, degrees = np.inf, np.inf
    for chunk in np.array_split(sun, max(1, len(sun) // 256)):
        offset = points - center
        along = offset @ chunk.T
        off_ray = np.sqrt(np.maximum((offset**2).sum(axis=1)[:, None] - along**2, 0))
        # Behind the station, as seen from the Sun, nothing shades it
        off_ray[along <= 0] = np.inf
        meters = min(meters, (off_ray - radii[:, None] - radius).min())
        for viewpoint in viewpoints:
            offset = points - viewpoint
            distance = np.linalg.norm(offset, axis=1)
            edge = np.arcsin(np.minimum(radii / distance, 1.0))
            cosine = (offset @ chunk.T) / distance[:, None]
            angle = np.arccos(np.clip(cosine, -1.0, 1.0)) - edge[:, None]
            degrees = min(degrees, np.degrees(angle.min()))
    return meters, degrees


def clearances(sun, center, radius, points, radii):
    """Per point, the smallest clearance over the day between the Sun's ray through it and the
    sphere (center, radius)."""
    offset = points - center
    result = np.full(len(points), np.inf)
    for chunk in np.array_split(sun, max(1, len(sun) // 256)):
        along = offset @ chunk.T
        off_ray = np.sqrt(np.maximum((offset**2).sum(axis=1)[:, None] - along**2, 0))
        off_ray[along <= 0] = np.inf
        result = np.minimum(result, off_ray.min(axis=1))
    return result - radii - radius


def decks_clearance(sun, grid, gates):
    """The smallest clearance of the gates from the station's decks: first against the decks'
    bounding sphere, then block by block for the gates that come within its radius of it.
    """
    cells = block_cells(grid)
    blocks = to_world(grid, cells[cells[:, 1] >= world.STATION_DECKS_BOTTOM])
    center = (blocks.min(axis=0) + blocks.max(axis=0)) / 2
    radius = np.linalg.norm(blocks - center, axis=1).max() + world.BLOCK_RADIUS
    near = clearances(sun, center, radius, gates, world.GATE_RADIUS)
    close = near <= radius
    meters = near[~close].min(initial=np.inf)
    for gate in gates[close]:
        # Seen from the block: the gate shades it when it lies toward the Sun
        shade = clearances(-sun, gate, world.GATE_RADIUS, blocks, world.BLOCK_RADIUS)
        meters = min(meters, shade.min())
    return meters


def box_clearance(axis, center, radius, yard):
    """How far the shipyard's block bounds stay out of the slab of the Sun's rays through the
    sphere (center, radius), along the Sun's axis; 0 or less when the Sun can go behind them.
    """
    lo, hi = world.grid_bounds(yard)
    corners = [
        [x, y, z]
        for x in (lo[0] - 0.5, hi[0] + 0.5)
        for y in (lo[1] - 0.5, hi[1] + 0.5)
        for z in (lo[2] - 0.5, hi[2] + 0.5)
    ]
    heights = (to_world(yard, np.array(corners)) - center) @ axis
    return max(heights.min(), -heights.max()) - radius


def is_window(block):
    return "Window" in (block.findtext("SubtypeName") or "")


def sight(station, yard):
    """(lobby spots with a clear line to the shipyard's center, lobby spots that see each of the
    shipyard's 8 bounds corners on average, spots): lines blocked by the blocks' cells, but for
    the station's windows; to the center also by the shipyard's own, to the corners not.
    """
    solid = {
        tuple(int(m.get(k)) for k in "xyz")
        for b in station.find("CubeBlocks")
        if not is_window(b) and (m := b.find("Min")) is not None
    }
    yard_solid = {tuple(c) for c in block_cells(yard)}
    spots = [
        to_world(station, np.array([[x, y, z]], dtype=float))[0]
        for x, y, z in LOBBY_SPOTS
        if (x, round(y), z) not in solid
    ]
    lo, hi = world.grid_bounds(yard)
    target = to_world(yard, (np.array(lo) + np.array(hi))[None] / 2)[0]
    corners = to_world(
        yard,
        np.array(
            [
                [x, y, z]
                for x in (lo[0], hi[0])
                for y in (lo[1], hi[1])
                for z in (lo[2], hi[2])
            ],
            dtype=float,
        ),
    )

    def blocked(grid, cells, start, end):
        position, right, up, backward = grid_frame(grid)
        samples = start + np.outer(
            np.linspace(0, 1, int(np.linalg.norm(end - start)) * 2 + 2), end - start
        )
        relative = samples - position
        local = np.round(
            np.stack([relative @ right, relative @ up, relative @ backward], axis=1)
            / CELL
        ).astype(int)
        return any(tuple(c) in cells for c in local[:-2])

    center_clear = sum(
        not blocked(station, solid, s, target)
        and not blocked(yard, yard_solid, s, target)
        for s in spots
    )
    corners_seen = sum(
        not blocked(station, solid, s, c) for s in spots for c in corners
    ) / len(corners)
    return center_clear, corners_seen, len(spots)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("world", type=Path)
    parser.add_argument(
        "--steps", type=int, default=7200, help="Sun positions over the day"
    )
    args = parser.parse_args()

    sector = ET.parse(args.world / "SANDBOX_0_0_0_.sbs").getroot()
    objects = list(sector.find("SectorObjects"))
    grids = [o for o in objects if o.get(world.XSI_TYPE) == "MyObjectBuilder_CubeGrid"]
    by_name = {g.findtext("DisplayName"): g for g in grids}
    sun, axis = sun_path(args.world, args.steps)
    gates = lane_gates(grids)
    planets = list(world.load_planets(sector).values())
    print(
        f"Sun axis {np.round(axis, 3)}, {args.steps} positions a day, {len(gates)} gates"
        " (clearance m / degrees from lobby and hangar)"
    )
    failed = False
    stations = 0
    for name, grid in by_name.items():
        if not name.endswith(" Station") or name.startswith("TL "):
            continue
        center, radius = bounding_sphere(grid)
        views = to_world(grid, np.array(list(VIEWPOINTS.values()), dtype=float))
        yard = by_name[f"{name} Shipyard"]
        yard_blocks = to_world(yard, block_cells(yard))
        cells = []
        # Viktor laid out the stations with the double helix shipyard as they are: they show
        # what the Sun does there, without failing (SE-959)
        laid_out = name in world.HELIX_STATIONS
        for label, points, radii in (
            ("gates", gates, np.full(len(gates), world.GATE_RADIUS)),
            ("shipyard", yard_blocks, np.full(len(yard_blocks), world.BLOCK_RADIUS)),
        ):
            meters, degrees = sweep(sun, center, radius, views, points, radii)
            if label == "gates":
                meters = decks_clearance(sun, grid, gates)
            failed |= meters <= 0 and not laid_out
            state = "ok  " if meters > 0 else "laid" if laid_out else "FAIL"
            cells.append(f"{label} {state} {meters:8.0f} m {degrees:6.2f} deg")
        box = box_clearance(axis, center, radius, yard)
        failed |= box <= 0 and not laid_out
        state = "ok  " if box > 0 else "laid" if laid_out else "FAIL"
        cells.append(f"box {state} {box:5.0f} m")
        center_clear, corners_seen, spots = sight(grid, yard)
        cells.append(
            f"sight center {center_clear}/{spots} corners {corners_seen:.0f}/{spots}"
        )
        zone_entity = next(
            o
            for o in objects
            if o.get(world.XSI_TYPE) == "MyObjectBuilder_SafeZone"
            and o.findtext("DisplayName") == name
        )
        zone = world_position(zone_entity)
        zone_radius = float(zone_entity.findtext("Radius"))
        farthest = max(
            np.linalg.norm(to_world(g, block_cells(g)) - zone, axis=1).max()
            for g in (grid, yard)
        )
        inside = farthest + world.BLOCK_RADIUS <= zone_radius
        failed |= not inside
        cells.append(
            f"zone {'ok' if inside else 'FAIL'} {farthest:.0f}/{zone_radius:.0f} m"
        )
        # How far the zone's surface stays out of the nearest planet's gravity
        gravity = min(
            np.linalg.norm(zone - np.array(p.center)) - zone_radius - p.gravity_limit
            for p in planets
        )
        failed |= gravity <= 0
        cells.append(f"gravity {'ok' if gravity > 0 else 'FAIL'} {gravity:.0f} m")
        print(f"{name:14s} {'  '.join(cells)}")
        stations += 1
    failed |= stations == 0
    print("FAIL: the Sun can strobe on a station" if failed else "PASS")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
