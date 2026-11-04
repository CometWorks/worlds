#!/usr/bin/env python3
"""Builds the "Octahedron Trader Ships" showcase save from the game's Empty World.

    build_trader_ships.py <output save folder> [--blueprints DIR]... [--steam-id ID]

Puts every ship of trader_ships.txt (the SE-948 list) in space on a flat grid
of equal cells, each as large as the largest ship plus 20 m of padding. Every
ship is turned to face -Z with its main grid's up along +Y. The grids are
static with dampeners on, so the layout stays as it is on load; convert one to
a ship in game to fly it. Blocks are owned by the world's player, whose Steam
id is --steam-id (default Viktor's), so the save loads with that player in
control of the character.

Blueprints are read from <dir>/<workshop id>/bp.sbc in the --blueprints
folders (default: the local Steam Workshop folder of SE1). Download missing
ones with anonymous SteamCMD:
    steamcmd +force_install_dir DIR +login anonymous \
        +workshop_download_item 244850 <id> ... +quit
"""

import argparse
import copy
import random
import shutil
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
GAME = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers"
WORKSHOP = Path.home() / ".steam/steam/steamapps/workshop/content/244850"
NAME = "Octahedron Trader Ships"
VIKTOR = "76561198223054696"
PADDING = 20.0
GRID_SIZE = {"Large": 2.5, "Small": 0.5}
DIRECTIONS = {
    "Forward": (0, 0, -1),
    "Backward": (0, 0, 1),
    "Left": (-1, 0, 0),
    "Right": (1, 0, 0),
    "Up": (0, 1, 0),
    "Down": (0, -1, 0),
}
FIRST_CELL_Z = -100.0  # The character stands near the origin

XSI = "http://www.w3.org/2001/XMLSchema-instance"
XSI_TYPE = f"{{{XSI}}}type"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def frame(grid):
    po = grid.find("PositionAndOrientation")
    return [
        tuple(float(po.find(tag).get(k)) for k in "xyz")
        for tag in ("Position", "Forward", "Up")
    ]


def set_frame(grid, position, forward, up):
    """Writes PositionAndOrientation without the quaternion, which would override Forward and Up."""
    old = grid.find("PositionAndOrientation")
    po = ET.Element("PositionAndOrientation")
    for tag, v in (("Position", position), ("Forward", forward), ("Up", up)):
        ET.SubElement(po, tag, {k: repr(float(x)) for k, x in zip("xyz", v)})
    grid.insert(list(grid).index(old), po)
    grid.remove(old)


def set_child(parent, tag, value):
    child = parent.find(tag)
    if child is None:
        child = ET.SubElement(parent, tag)
    child.text = str(value)


def load_ships(path):
    for line in path.read_text().splitlines():
        if line.strip() and not line.startswith("#"):
            workshop_id, name = line.split(None, 1)
            yield workshop_id, name.strip()


def find_blueprint(workshop_id, folders):
    for folder in folders:
        path = folder / workshop_id / "bp.sbc"
        if path.exists():
            return path
    raise SystemExit(
        f"blueprint {workshop_id} not found in {[str(f) for f in folders]}"
    )


def remap_ids(grids, rng, used):
    """Gives every entity id a new value, references between the grids included."""
    old = {
        n.text
        for g in grids
        for n in g.iter()
        if n.tag == "EntityId" and n.text and n.text != "0"
    }
    mapping = {}
    for value in sorted(old):
        new = (1 << 56) | rng.getrandbits(56)
        while new in used:
            new = (1 << 56) | rng.getrandbits(56)
        used.add(new)
        mapping[value] = str(new)
    for g in grids:
        for n in g.iter():
            if len(n) == 0 and n.text in mapping:
                n.text = mapping[n.text]


def to_world(v, forward, up):
    """A grid-local vector in world space; grid local axes: X = Right, Y = Up, Z = Backward."""
    local = (cross(forward, up), up, tuple(-x for x in forward))
    return tuple(sum(v[j] * local[j][i] for j in range(3)) for i in range(3))


def cockpit_frame(grid):
    """(forward, up) of the grid's main cockpit, or its first cockpit, else of the grid."""
    _, forward, up = frame(grid)
    cockpits = [
        b
        for b in grid.find("CubeBlocks")
        if b.get(XSI_TYPE) == "MyObjectBuilder_Cockpit"
    ]
    cockpits.sort(key=lambda b: b.findtext("IsMainCockpit") != "true")
    if not cockpits or cockpits[0].find("BlockOrientation") is None:
        return forward, up
    o = cockpits[0].find("BlockOrientation")
    return tuple(to_world(DIRECTIONS[o.get(k)], forward, up) for k in ("Forward", "Up"))


def align(grids):
    """Turns and moves the grids together so the main grid (most blocks) sits at the origin
    with its cockpit facing -Z and up +Y. Returns the main grid and the block bounds (lo, hi)
    in meters."""
    main = max(grids, key=lambda g: len(g.find("CubeBlocks")))
    origin = frame(main)[0]
    forward, up = cockpit_frame(main)
    axes = (cross(forward, up), up, tuple(-x for x in forward))  # -> world X, Y, Z

    def turn(v):
        return tuple(dot(v, a) for a in axes)

    lo, hi = [float("inf")] * 3, [float("-inf")] * 3
    for g in grids:
        position, f, u = frame(g)
        position = turn(tuple(p - o for p, o in zip(position, origin)))
        f, u = turn(f), turn(u)
        set_frame(g, position, f, u)
        size = GRID_SIZE[g.find("GridSizeEnum").text]
        for b in g.find("CubeBlocks"):
            m = b.find("Min")
            cell = [int(m.get(k)) * size for k in "xyz"] if m is not None else [0, 0, 0]
            center = [p + c for p, c in zip(position, to_world(cell, f, u))]
            for i in range(3):
                lo[i] = min(lo[i], center[i] - size / 2)
                hi[i] = max(hi[i], center[i] + size / 2)
    return main, lo, hi


def prepare(grids, owner):
    for g in grids:
        g.tag = "MyObjectBuilder_EntityBase"
        g.set(XSI_TYPE, "MyObjectBuilder_CubeGrid")
        for tag in ("LinearVelocity", "AngularVelocity"):
            node = g.find(tag)
            if node is not None:
                g.remove(node)
        set_child(g, "IsStatic", "true")
        set_child(g, "DampenersEnabled", "true")
        for b in g.find("CubeBlocks"):
            if b.find("Owner") is not None:
                set_child(b, "Owner", owner)
                set_child(b, "BuiltBy", owner)


def build(out, blueprint_folders, steam_id, seed=948):
    template = GAME / "Content/CustomWorlds/Empty World"
    if out.exists():
        raise SystemExit(f"{out} exists; remove it first")
    shutil.copytree(template, out, ignore=shutil.ignore_patterns("*.sbsB5", "*.sbcB5"))

    sandbox = ET.parse(out / "Sandbox.sbc")
    root = sandbox.getroot()
    player = root.find("AllPlayersData/dictionary/item")
    old_steam_id = player.findtext("Key/ClientId")
    owner = player.findtext("Value/IdentityId")
    set_child(root, "SessionName", NAME)
    set_child(
        root,
        "Description",
        "The SE-948 Workshop cargo ships, built by se1/worlds octahedron/build_trader_ships.py",
    )
    set_child(
        root, "LastSaveTime", datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S")
    )
    config = ET.parse(out / "Sandbox_config.sbc")
    set_child(config.getroot(), "SessionName", NAME)

    sector = ET.parse(out / "SANDBOX_0_0_0_.sbs")
    objects = sector.getroot().find("SectorObjects")
    used = {int(n.text) for n in sector.iter() if n.tag == "EntityId" and n.text}
    # The player's Steam id is in the players, the controlled entities and the character
    for tree in (sandbox, sector):
        for node in tree.iter():
            if node.text == old_steam_id:
                node.text = steam_id
    rng = random.Random(seed)

    ships = []
    for workshop_id, name in load_ships(HERE / "trader_ships.txt"):
        bp = ET.parse(find_blueprint(workshop_id, blueprint_folders)).getroot()
        grids = [copy.deepcopy(g) for g in bp.find(".//ShipBlueprint/CubeGrids")]
        main, lo, hi = align(grids)
        main.find("DisplayName").text = name
        remap_ids(grids, rng, used)
        prepare(grids, owner)
        ships.append((workshop_id, name, grids, lo, hi))

    cell = [max(s[4][i] - s[3][i] for s in ships) + PADDING for i in range(3)]
    columns = round(len(ships) ** 0.5)
    for n, (workshop_id, name, grids, lo, hi) in enumerate(ships):
        target = (
            (n % columns - (columns - 1) / 2) * cell[0],
            0.0,
            FIRST_CELL_Z - (n // columns) * cell[2],
        )
        shift = [target[i] - (lo[i] + hi[i]) / 2 for i in range(3)]
        for g in grids:
            position, f, u = frame(g)
            set_frame(g, [p + s for p, s in zip(position, shift)], f, u)
            objects.append(g)
        size = " x ".join(f"{hi[i] - lo[i]:.1f}" for i in (0, 1, 2))
        print(
            f"{n + 1:2} {workshop_id} {len(grids)} grid(s) W x H x L {size} m  {name}"
        )

    for tree, file in (
        (sandbox, "Sandbox.sbc"),
        (config, "Sandbox_config.sbc"),
        (sector, "SANDBOX_0_0_0_.sbs"),
    ):
        tree.write(out / file, encoding="utf-8", xml_declaration=True)
    print(
        f"cell {cell[0]:.1f} x {cell[1]:.1f} x {cell[2]:.1f} m, {columns} columns, "
        f"{len(ships)} ships -> {out}"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("out", type=Path)
    parser.add_argument("--blueprints", type=Path, action="append")
    parser.add_argument("--steam-id", default=VIKTOR)
    args = parser.parse_args()
    build(args.out, args.blueprints or [WORKSHOP], args.steam_id)


if __name__ == "__main__":
    main()
