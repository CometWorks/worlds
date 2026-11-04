#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Puts the Moon Shipyard (SE-986) into a copy of an Octahedron world.

    uv run place_moon_shipyard.py <world> <shipyard folder> <new world> [--owner NAME]

The shipyard folder is make_moon_shipyard.py's output with the asteroid's
MoonShipyard.vx2 added; the game writes that file when it saves a world in
which the MoonShipyardAsteroid mod built the asteroid.

The shipyard replaces the Omni Arc Beam Shipyard in the Moon Station's safe
zone, on the Moon's side: seen from the station's lounge, it lies across the
view just above the top of the Moon, under the lounge's glass dome,
DISTANCE from it. The station and the shipyard move together so that they are
centered in the safe zone, which stays where it is; they keep SAFE_ZONE_MARGIN
inside it and CLEARANCE from each other and every other grid. The station's
economy entry moves with it, and players in the safe zone wait in the lounge,
facing the shipyard. The shipyard's blocks belong to the owner (default: the
world's first human player), shared with the owner's faction, and its reactors
get uranium. The safe zone is changed
to allow welding and grinding, which the shipyard's tools need; that lets
players weld and grind anywhere in the zone.
"""

import argparse
import json
import math
import random
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

import build_world as bw
import make_moon_shipyard as m

STATION = "Moon Station"
# The shipyard this one replaces in the station's safe zone
REPLACES = ("Omni Arc Beam Shipyard",)
LOUNGE = "Station Lounge"
# A spot on the lounge's floor by the couches, where the windows show the whole
# shipyard (the station's cells)
LOUNGE_SPOT = (6, 6.6, -1)
# Seen from the lounge: how far past the top of the Moon the shipyard's
# center is (degrees)
ABOVE_MOON = 10.0
# How far the shipyard's center is from the lounge (m)
DISTANCE = 520.0
SAFE_ZONE_MARGIN = 20.0
CLEARANCE = 40.0
URANIUM = 100
# What the safe zone has to allow for the shipyard's tools to work
TOOL_ACTIONS = ("Welding", "Grinding")
# MySafeZoneAction flags in their order
ACTIONS = (
    "Damage Shooting Drilling Welding Grinding VoxelHand Building LandingGearLock"
    " ConvertToStation BuildingProjections"
).split()
# The shipyard's extent in its grid's frame (m): the rock's radius around the
# axis, and from below the junctions to past the rock's far end
RADIUS = 88.0
LENGTH = (-22.0, 290.0)


def vec(e):
    return np.array([float(e.get(k)) for k in "xyz"])


def frame(entity):
    po = entity.find("PositionAndOrientation")
    return vec(po.find("Position")), vec(po.find("Forward")), vec(po.find("Up"))


def axes(forward, up):
    """Columns Right, Up, Backward: a grid's local axes in the world."""
    return np.stack([np.cross(forward, up), up, -forward], 1)


def block_points(grid):
    position, forward, up = frame(grid)
    turn = axes(forward, up)
    cells = np.array(
        [
            [
                int(b.find("Min").get(k, 0)) if b.find("Min") is not None else 0
                for k in "xyz"
            ]
            for b in grid.find("CubeBlocks")
        ]
    )
    return position + cells * m.GRID @ turn.T


def lounge_eye(station):
    """A point over the middle of the station's lounge, and the station's up."""
    position, forward, up = frame(station)
    turn = axes(forward, up)
    lounge = [
        [
            int(b.find("Min").get(k, 0)) if b.find("Min") is not None else 0
            for k in "xyz"
        ]
        for b in station.find("CubeBlocks")
        if LOUNGE in (b.findtext("CustomName") or "")
    ]
    return position + np.mean(lounge, 0) * m.GRID @ turn.T + up * m.GRID, up


def station_zone(sector):
    return next(
        e
        for e in sector.find("SectorObjects")
        if e.get(bw.XSI_TYPE) == "MyObjectBuilder_SafeZone"
        and e.findtext("DisplayName") == STATION
    )


def allow_tools(zone):
    """Lets the shipyard's welders and deconstructors work in the zone."""
    allowed = zone.find("AllowedActions")
    actions = set((allowed.text or "").split()) | set(TOOL_ACTIONS)
    allowed.text = " ".join(a for a in ACTIONS if a in actions)
    return allowed.text


def view_of(eye, up, planets):
    """From eye: the direction just past the top of the Moon (station up is
    toward the top), and the way it tilts up."""
    moon = np.array(planets["Moon"].center)
    to_moon = moon - eye
    distance = np.linalg.norm(to_moon)
    to_moon /= distance
    lift = up - to_moon * (up @ to_moon)
    lift /= np.linalg.norm(lift)
    angle = math.asin(planets["Moon"].radius / distance) + math.radians(ABOVE_MOON)
    return to_moon * math.cos(angle) + lift * math.sin(angle), lift, angle


def enclosing(points):
    """The center and radius of the smallest sphere around points."""
    from scipy.optimize import minimize

    far = lambda c: np.linalg.norm(points - c, axis=1).max()  # noqa: E731
    center = minimize(far, points.mean(0), method="Nelder-Mead", tol=1e-3).x
    return center, far(center)


def placement(sector, planets, gone):
    """(origin, forward, up) of the shipyard's main grid, and how far the
    station moves: the station and the shipyard together centered in the
    safe zone."""
    grids = [
        e
        for e in sector.find("SectorObjects")
        if e.get(bw.XSI_TYPE) == "MyObjectBuilder_CubeGrid"
    ]
    station = next(g for g in grids if g.findtext("DisplayName") == STATION)
    zone = station_zone(sector)
    zone_center, zone_radius = frame(zone)[0], float(zone.findtext("Radius"))
    own = block_points(station)
    others = np.concatenate(
        [block_points(g)[::10] for g in grids if g is not station and g not in gone]
    )
    ring = np.array(
        [
            [RADIUS * math.cos(t), RADIUS * math.sin(t), z]
            for t in np.linspace(0, 2 * math.pi, 24, endpoint=False)
            for z in np.linspace(*LENGTH, 16)
        ]
    )
    middle = sum(LENGTH) / 2
    eye, up = lounge_eye(station)
    move = np.zeros(3)
    # The view depends a little on where the station ends up: settle it
    for _ in range(3):
        view, lift, angle = view_of(eye + move, up, planets)
        # The shipyard's axis across the view, level with the lounge's floor
        axis = np.cross(view, lift)
        axis /= np.linalg.norm(axis)
        side = np.cross(axis, view)
        d = DISTANCE
        center = eye + view * d
        points = (
            center
            + np.outer(ring[:, 2] - middle, axis)
            + np.outer(ring[:, 0], view)
            + np.outer(ring[:, 1], side)
        )
        apart = np.linalg.norm(points[:, None] - own[None, ::5], axis=2).min()
        both, reach = enclosing(np.concatenate([points, own[::5]]))
        move = zone_center - both
        inside = zone_radius - reach
        clear = np.linalg.norm(
            np.concatenate([points, own])[::3, None] + move - others[None], axis=2
        ).min()
    assert apart >= CLEARANCE, "the shipyard is too close to the station"
    assert inside >= SAFE_ZONE_MARGIN, "the shipyard doesn't fit into the safe zone"
    assert clear >= CLEARANCE, "the shipyard is too close to another grid"
    print(
        f"shipyard {d:.0f} m from the lounge, {math.degrees(angle):.1f} degrees past the"
        f" Moon's center, {apart:.0f} m from the station; the two {inside:.0f} m inside"
        f" the safe zone, {clear:.0f} m from any other grid, the station moved"
        f" {np.linalg.norm(move):.0f} m; the shipyard spans"
        f" {2 * math.degrees(math.atan((LENGTH[1] - LENGTH[0]) / 2 / (d - RADIUS))):.0f}"
        f" x {2 * math.degrees(math.atan(RADIUS / (d - RADIUS))):.0f} degrees"
    )
    # Grid frame: Backward (local Z) along the axis, Up away from the lounge
    grid_forward, grid_up = -axis, view
    origin = center + move - axes(grid_forward, grid_up) @ np.array([0, 0, middle])
    return origin, grid_forward, grid_up, move


def attached(grids, roots):
    """roots and the grids on their rotors, hinges and pistons, and so on."""
    owner = {
        b.findtext("EntityId"): g
        for g in grids
        for b in g.find("CubeBlocks")
        if b.findtext("EntityId")
    }
    found = list(roots)
    for g in found:
        for other in grids:
            if other in found:
                continue
            parents = {b.findtext("ParentEntityId") for b in other.find("CubeBlocks")}
            if any(owner.get(p) is g for p in parents):
                found.append(other)
    return found


def owner_id(root, name):
    for identity in root.iter("MyObjectBuilder_Identity"):
        if name is None and identity.findtext("CharacterEntityId") not in (None, "0"):
            return identity.findtext("IdentityId")
        # player names may start with an icon character
        if name and (identity.findtext("DisplayName") or "").endswith(name):
            return identity.findtext("IdentityId")
    raise SystemExit(f"no identity {name}")


def shipyard_grids(folder, ids, origin, forward, up, owner):
    bp = ET.parse(folder / "bp.sbc").getroot()
    grids = bw.clone_grids(bp.findall(".//CubeGrid"), ids)
    turn = axes(forward, up)
    for g in grids:
        position, f, u = frame(g)
        bw.set_position(g, origin + turn @ position, turn @ f, turn @ u)
        bw.set_child(g, "IsStatic", "true" if g is grids[0] else "false")
        bw.set_child(g, "CreatePhysics", "true")
        # else the deconstructors grind the shipyard itself
        bw.set_child(g, "DestructibleBlocks", "false")
        for block in g.find("CubeBlocks"):
            kind = block.get(bw.XSI_TYPE).replace("MyObjectBuilder_", "")
            # after the paint (and the skin, where there is one)
            head = block.find("SkinSubtypeId")
            at = (
                list(block).index(
                    head if head is not None else block.find("ColorMaskHSV")
                )
                + 1
            )
            for k, (tag, value) in enumerate(
                (("Owner", owner), ("BuiltBy", owner), ("ShareMode", "Faction"))
            ):
                e = ET.Element(tag)
                e.text = value
                block.insert(at + k, e)
            if kind == "Reactor":
                for inventory in block.iter():
                    if inventory.get(bw.XSI_TYPE) == "MyObjectBuilder_Inventory":
                        bw.set_items(
                            inventory, [("MyObjectBuilder_Ingot", "Uranium", URANIUM)]
                        )
    return [bw.as_sector_object(g) for g in grids]


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("world", type=Path)
    parser.add_argument("shipyard", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--owner", help="identity name, default the first player's")
    parser.add_argument("--name", help="the new world's name")
    args = parser.parse_args()
    meta = json.loads((args.shipyard / "asteroid.json").read_text())
    shutil.copytree(
        args.world, args.out, ignore=shutil.ignore_patterns("Backup", "*.sbsB5")
    )
    sbs = args.out / "SANDBOX_0_0_0_.sbs"
    tree = ET.parse(sbs)
    sector = tree.getroot()
    planets = bw.load_planets(sector)
    for planet in planets.values():
        # The storage's name ends in the planet's diameter: Moon-1353915701d19000
        planet.radius = float(planet.storage.rsplit("d", 1)[1]) / 2
    objects = sector.find("SectorObjects")
    grids = [e for e in objects if e.get(bw.XSI_TYPE) == "MyObjectBuilder_CubeGrid"]
    gone = attached(grids, [g for g in grids if g.findtext("DisplayName") in REPLACES])
    origin, forward, up, move = placement(sector, planets, gone)
    for g in gone:
        objects.remove(g)
    station = next(g for g in grids if g.findtext("DisplayName") == STATION)
    for g in attached(grids, [station]):
        p = g.find("PositionAndOrientation/Position")
        for k, v in zip("xyz", vec(p) + move):
            p.set(k, repr(float(v)))
    # Players in the safe zone wait in the lounge, facing the shipyard
    zone = station_zone(sector)
    position, station_forward, station_up = frame(station)
    spot = position + axes(station_forward, station_up) @ (
        np.array(LOUNGE_SPOT) * m.GRID
    )
    facing = origin + axes(forward, up) @ np.array([0, 0, sum(LENGTH) / 2]) - spot
    facing -= station_up * (facing @ station_up)
    facing /= np.linalg.norm(facing)
    for e in objects:
        if e.get(bw.XSI_TYPE) == "MyObjectBuilder_Character" and np.linalg.norm(
            frame(e)[0] - frame(zone)[0]
        ) < float(zone.findtext("Radius")):
            bw.set_position(e, spot, facing, station_up)
            print(f"{e.findtext('DisplayName')} waits in the lounge")
    print(f"removed {', '.join(g.findtext('DisplayName') for g in gone)}")
    print(f"{STATION} safe zone allows {allow_tools(station_zone(sector))}")
    checkpoint = ET.parse(args.out / "Sandbox.sbc")
    owner = owner_id(checkpoint.getroot(), args.owner)
    ids = bw.Ids(random.Random(m.SEED))
    for e in objects.iter("EntityId"):
        if e.text and e.text.isdigit():
            ids.used.add(int(e.text))
    for g in shipyard_grids(args.shipyard, ids, origin, forward, up, owner):
        objects.append(g)
    # The rock: its storage's center is the grid frame's point over the
    # storage's middle voxel
    half = meta["storage"] / 2
    local = np.array([half, half, half]) - np.array(meta["origin_voxel"], float)
    center = origin + axes(forward, up) @ local
    rock = bw.voxel_map(
        ids, m.ROCK_NAME, tuple(center), meta["storage"], tuple(forward), tuple(up)
    )
    objects.append(rock)
    shutil.copy(args.shipyard / "MoonShipyard.vx2", args.out / f"{m.ROCK_NAME}.vx2")
    ET.indent(sector, "  ")
    tree.write(sbs, encoding="utf-8", xml_declaration=True)
    # The station's economy entry sits where the station is
    path = args.out / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8-sig")
    entry = text.index(f"<StationEntityId>{station.findtext('EntityId')}<")
    start = text.rindex("<Position ", 0, entry)
    end = text.index("/>", start) + 2
    where = vec(ET.fromstring(text[start:end])) + move
    position = (
        "<Position "
        + " ".join(f'{k}="{v!r}"' for k, v in zip("xyz", map(float, where)))
        + " />"
    )
    path.write_text(text[:start] + position + text[end:], encoding="utf-8")
    if args.name:
        for file in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = args.out / file
            text = path.read_text(encoding="utf-8-sig")
            old = text.split("<SessionName>", 1)[1].split("</SessionName>", 1)[0]
            path.write_text(
                text.replace(
                    f"<SessionName>{old}</SessionName>",
                    f"<SessionName>{args.name}</SessionName>",
                    1,
                ),
                encoding="utf-8",
            )
    print(f"wrote {args.out}: the shipyard at {np.round(origin, 1)}, owner {owner}")


if __name__ == "__main__":
    main()
