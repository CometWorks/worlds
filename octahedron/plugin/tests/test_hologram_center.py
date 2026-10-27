"""Checks that the plugin centers blueprints over the station hologram tables with the
starter ship switched off, the way a cluster without an Octahedron.cfg runs (SE-729).

Loads blueprints into the station tables, fits them with !oct holograms, saves the world
and works out from each saved table's offset, rotation and scale where the game draws the
blueprint: its bounding box must be centered over the table and fit the free space above
it. The built-in blueprint has its origin at a corner; blueprint files (bp.sbc) given on
the command line are loaded too, each into its own table.

    uv run --no-project --with httpx python tests/test_hologram_center.py [bp.sbc ...]
"""

from __future__ import annotations

import math
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import rig

# Origin at a corner: a small grid cockpit at (0, 0, 0), the hull out to +X, +Y and +Z
CORNER = rig.grid(
    "Hologram Corner",
    777000729000001,
    [rig.block("MyObjectBuilder_Cockpit", "SmallBlockCockpit", (0, 0, 0))]
    + [
        rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (x, y, z))
        for x in range(0, 12, 3)
        for y in range(0, 6, 3)
        for z in range(1, 30, 3)
    ],
    tag="MyObjectBuilder_CubeGrid",
).replace("<GridSizeEnum>Large</GridSizeEnum>", "<GridSizeEnum>Small</GridSizeEnum>")

# The plugin's defaults (octahedron.cfg): table top and margin, in the table's block
CELL, TOP, MARGIN = 2.5, -0.33, 0.05
CONTENT = rig.DS64.parent / "Content" / "Data" / "CubeBlocks"
DIRECTIONS = {
    "Forward": (0, 0, -1),
    "Backward": (0, 0, 1),
    "Left": (-1, 0, 0),
    "Right": (1, 0, 0),
    "Up": (0, 1, 0),
    "Down": (0, -1, 0),
}


def blueprint_grids(path: Path) -> str:
    """The grids of a bp.sbc as ProjectedGrids entries"""
    text = path.read_text(encoding="utf-8-sig")
    grids = re.findall(r"<CubeGrid>(.*?)</CubeGrid>", text, flags=re.S)
    assert grids, path
    return "".join(
        f"<MyObjectBuilder_CubeGrid>{g}</MyObjectBuilder_CubeGrid>" for g in grids
    )


def block_sizes() -> dict[str, tuple[int, int, int]]:
    sizes = {}
    for path in CONTENT.glob("*.sbc"):
        for d in ET.parse(path).getroot().iter("Definition"):
            size = d.find("Size")
            subtype = d.findtext("Id/SubtypeId") or ""
            if size is not None:
                sizes[subtype] = tuple(int(size.get(a, 1)) for a in "xyz")
    return sizes


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def mul(v, m):
    """Row vector v times the 3x3 matrix m (rows), as VRage transforms"""
    return tuple(sum(v[i] * m[i][j] for i in range(3)) for j in range(3))


def matmul(a, b):
    return [mul(row, b) for row in a]


def vec(element, default=0.0):
    """A vector saved as x, y, z attributes or as X, Y, Z elements"""
    return tuple(
        float(element.get(a, element.findtext(a.upper()) or default)) for a in "xyz"
    )


def grid_frame(grid):
    """Rotation rows (Right, Up, Backward) and position of a grid builder"""
    po = grid.find("PositionAndOrientation")
    if po is None:
        return [(1, 0, 0), (0, 1, 0), (0, 0, 1)], (0, 0, 0)
    f = vec(po.find("Forward"))
    u = vec(po.find("Up"))
    return [cross(f, u), u, tuple(-x for x in f)], vec(po.find("Position"))


def corners(grid, sizes):
    """The corners of every known block's box, in the grid's own frame (m)"""
    cs = 0.5 if grid.findtext("GridSizeEnum") == "Small" else 2.5
    for b in grid.find("CubeBlocks"):
        size = sizes.get(b.findtext("SubtypeName") or "")
        if size is None:
            continue
        o = b.find("BlockOrientation")
        f = DIRECTIONS[o.get("Forward", "Forward")] if o is not None else (0, 0, -1)
        u = DIRECTIONS[o.get("Up", "Up")] if o is not None else (0, 1, 0)
        turned = mul([s * cs for s in size], [cross(f, u), u, tuple(-x for x in f)])
        m = b.find("Min")
        low = [c * cs - cs / 2 for c in (vec(m) if m is not None else (0, 0, 0))]
        high = [lo + abs(t) for lo, t in zip(low, turned)]
        for x in (low[0], high[0]):
            for y in (low[1], high[1]):
                for z in (low[2], high[2]):
                    yield (x, y, z)


def yaw_pitch_roll(degrees):
    """Matrix.CreateFromYawPitchRoll: roll about Z, then pitch about X, then yaw about Y"""
    y, p, r = (math.radians(d) for d in degrees)
    rz = [(math.cos(r), math.sin(r), 0), (-math.sin(r), math.cos(r), 0), (0, 0, 1)]
    rx = [(1, 0, 0), (0, math.cos(p), math.sin(p)), (0, -math.sin(p), math.cos(p))]
    ry = [(math.cos(y), 0, -math.sin(y)), (0, 1, 0), (math.sin(y), 0, math.cos(y))]
    return matmul(matmul(rz, rx), ry)


def projected_box(table, sizes):
    """Where the game draws the table's blueprint, in the table's frame: the box of every
    grid's blocks, each grid placed relative to the first"""
    grids = list(table.find("ProjectedGrids"))
    offset = vec(table.find("ProjectionOffset"))
    turn = yaw_pitch_roll(vec(table.find("ProjectionRotation")))
    scale = float(table.findtext("Scale") or 1)
    cs = 0.5 if grids[0].findtext("GridSizeEnum") == "Small" else 2.5
    main_rot, main_pos = grid_frame(grids[0])
    # Rows are orthonormal, so the inverse is the transpose
    to_main = [tuple(main_rot[j][i] for j in range(3)) for i in range(3)]
    low, high = [math.inf] * 3, [-math.inf] * 3
    for grid in grids:
        rot, pos = grid_frame(grid)
        for p in corners(grid, sizes):
            world = [a + b for a, b in zip(mul(p, rot), pos)]
            local = mul([a - b for a, b in zip(world, main_pos)], to_main)
            q = [t * scale - o * cs * scale for t, o in zip(mul(local, turn), offset)]
            low = [min(a, b) for a, b in zip(low, q)]
            high = [max(a, b) for a, b in zip(high, q)]
    return low, high, scale, cs


def check_save(world: Path) -> list[str]:
    sizes = block_sizes()
    failures = []
    sector = ET.parse(world / "SANDBOX_0_0_0_.sbs").getroot()
    tables = [
        b
        for b in sector.iter("MyObjectBuilder_CubeBlock")
        if b.findtext("SubtypeName") == "LargeBlockConsole"
        and b.find("ProjectedGrids") is not None
        and len(b.find("ProjectedGrids"))
    ]
    assert tables, "no table kept its blueprint"
    want = (0.0, (TOP + CELL / 2) / 2, 0.0)
    free = (CELL - 2 * MARGIN, CELL / 2 - TOP - 2 * MARGIN, CELL - 2 * MARGIN)
    seen = set()
    for table in tables:
        name = table.find("ProjectedGrids")[0].findtext("DisplayName")
        if name in seen:
            continue
        seen.add(name)
        low, high, scale, cs = projected_box(table, sizes)
        center = [(a + b) / 2 for a, b in zip(low, high)]
        size = [b - a for a, b in zip(low, high)]
        # Whole-cube offsets: up to half a scaled cube off on each axis
        slack = cs * scale / 2 + 0.01
        off = [c - w for c, w in zip(center, want)]
        ok = all(abs(o) <= slack for o in off) and all(
            s <= f + 0.01 for s, f in zip(size, free)
        )
        print(
            f"{'ok  ' if ok else 'FAIL'} {name}: scale {scale:.3f}, center off by "
            f"{[round(o, 3) for o in off]} m (slack {slack:.3f}), size "
            f"{[round(s, 2) for s in size]} m in {[round(f, 2) for f in free]}",
            flush=True,
        )
        if not ok:
            failures.append(name)
    return failures


def main() -> None:
    blueprints = [CORNER] + [blueprint_grids(Path(p)) for p in sys.argv[1:]]
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    config, count = re.subn(
        r"<StarterShipEnabled>true</StarterShipEnabled>",
        "<StarterShipEnabled>false</StarterShipEnabled>",
        config,
    )
    assert count == 1
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(table_blueprint=blueprints)
    rig.write_plugin_config(config)
    rig.prepare_server()
    rig.start_server()
    try:
        remote = rig.start_client()
        reply = " ".join(rig.chat(remote, "!oct holograms", 5))
        print(reply, flush=True)
        fitted = int(re.search(r"Fitted (\d+)", reply)[1])
    finally:
        rig.stop_client()
        rig.stop_server()
    failures = check_save(rig.WORLD)
    if fitted == 0 or failures:
        sys.exit(f"FAIL: fitted {fitted} tables, off: {failures}")
    print(f"PASS: fitted {fitted} tables, every blueprint centered")


if __name__ == "__main__":
    if sys.argv[1:2] == ["--check"]:
        sys.exit(1 if check_save(rig.WORLD) else 0)
    main()
