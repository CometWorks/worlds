"""Checks where the shipyard builds a ship along its center line (SE-824), on the test server
with the mods of tests/test_support_arm.py, on a world built by build_world.py. The checks read
the delivered ship from the saved world.

A ship shorter than 1/4 of the shipyard (282.5 m: 70.6 m) goes 1/6 of the length from the middle,
to the 1/3 point nearer the station:

- "north", "south", "alpha": a 25 m large grid ship at a pole station and an equatorial one
- "under" at Gamma Station: 70 m long, just short enough
- "small" at Delta Station: a small grid ship on a small arm rotor

These stay in the middle:

- "quarter" at Beta Station: 72.5 m long, at least 1/4
- "wide" at Moon Station: a thin small grid cross, 70 m wide and high, whose support arm to the
  1/3 point outgrows the planner's search box

    OCT_TASK=SE-824 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_placement.py [case ...]

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import sys
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-824")
import rig  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_shipyard_order as so  # noqa: E402
from test_client import log  # noqa: E402

LENGTH = 282.5
CUBE = {"Large": 2.5, "Small": 0.5}


def hull(name: str, entity: int, size: tuple[int, int, int]) -> tuple[str, int]:
    blocks = sa.hull({}, size, "LargeBlockArmorBlock")
    return rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid"), len(blocks)


def small_grid(xml: str) -> str:
    return xml.replace(
        "<GridSizeEnum>Large</GridSizeEnum>", "<GridSizeEnum>Small</GridSizeEnum>"
    )


def small_hull() -> tuple[str, int]:
    blocks = sa.hull({}, (4, 3, 8), "SmallBlockArmorBlock")
    grid = rig.grid(
        "PlaceSmall", 777000824000600, blocks, tag="MyObjectBuilder_CubeGrid"
    )
    return small_grid(grid), len(blocks)


def wide_cross() -> tuple[str, int]:
    cells = {(x, 70, 0) for x in range(140)} | {(70, y, 0) for y in range(140)}
    blocks = [
        rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", c)
        for c in sorted(cells)
    ]
    grid = rig.grid(
        "PlaceWide", 777000824000700, blocks, tag="MyObjectBuilder_CubeGrid"
    )
    return small_grid(grid), len(blocks)


# The tables in the world's order: North, South, Alpha, Beta, Gamma, Delta, Moon
SHIPS = {
    "north": ("North", "PlaceNorth", hull("PlaceNorth", 777000824000100, (3, 3, 10))),
    "south": ("South", "PlaceSouth", hull("PlaceSouth", 777000824000200, (3, 3, 10))),
    "alpha": ("Alpha", "PlaceAlpha", hull("PlaceAlpha", 777000824000300, (3, 3, 10))),
    "quarter": ("Beta", "PlaceQuarter", hull("PlaceQuarter", 777000824000400, (2, 2, 29))),
    "under": ("Gamma", "PlaceUnder", hull("PlaceUnder", 777000824000500, (2, 2, 28))),
    "small": ("Delta", "PlaceSmall", small_hull()),
    "wide": ("Moon", "PlaceWide", wide_cross()),
}  # fmt: skip
SIZES = {"small": "Small", "wide": "Small"}
CENTERED = ("quarter", "wide")


def frame(grid: ET.Element):
    po = grid.find("PositionAndOrientation")
    pos, forward, up = (
        tuple(float(po.find(t).get(a)) for a in "xyz")
        for t in ("Position", "Forward", "Up")
    )
    right = (
        forward[1] * up[2] - forward[2] * up[1],
        forward[2] * up[0] - forward[0] * up[2],
        forward[0] * up[1] - forward[1] * up[0],
    )
    return pos, right, up, tuple(-v for v in forward)


def cells(grid: ET.Element) -> list[tuple[int, int, int]]:
    return [
        tuple(int(b.find("Min").get(a, 0)) for a in "xyz")
        for b in grid.find("CubeBlocks")
        if b.find("Min") is not None
    ]


def dot(a, b) -> float:
    return sum(x * y for x, y in zip(a, b))


def sub(a, b) -> tuple:
    return tuple(x - y for x, y in zip(a, b))


def check_place(root: ET.Element, case: str) -> None:
    station, ship_name, _ = SHIPS[case]
    grids = {
        e.findtext("DisplayName"): e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.find("CubeBlocks") is not None
    }
    ship, yard = grids.get(ship_name), grids[f"{station} Station Shipyard"]
    sa.check(ship is not None, f"{case}: {ship_name} is in the saved world")
    if ship is None:
        return
    cube = CUBE[SIZES.get(case, "Large")]
    pos, right, up, back = frame(ship)
    all_cells = cells(ship)
    middle = [(min(c[i] for c in all_cells) + max(c[i] for c in all_cells)) / 2 for i in range(3)]  # fmt: skip
    center = tuple(
        pos[i] + cube * (middle[0] * right[i] + middle[1] * up[i] + middle[2] * back[i])
        for i in range(3)
    )
    pos, right, up, back = frame(yard)
    z = [c[2] for c in cells(yard)]
    # The free space spans the grid along its Z, its other axes from these cells: x 0, y -16.5
    mid = tuple(
        pos[i] + 2.5 * (-16.5 * up[i] + (min(z) + max(z)) / 2 * back[i])
        for i in range(3)
    )
    away = sub(center, mid)
    along, across, above = dot(away, back), dot(away, right), dot(away, up)
    pos, right, up, back = frame(grids[f"{station} Station"])
    all_cells = cells(grids[f"{station} Station"])
    bounds = [(min(c[i] for c in all_cells) + max(c[i] for c in all_cells)) / 2 for i in range(3)]  # fmt: skip
    near = sub(
        tuple(
            pos[i]
            + 2.5 * (bounds[0] * right[i] + bounds[1] * up[i] + bounds[2] * back[i])
            for i in range(3)
        ),
        mid,
    )
    pos, right, up, back = frame(yard)
    log(
        f"{case}: {along:.1f} m along the shipyard from its middle, {across:.1f} across, "
        f"{above:.1f} up; the station is on the {dot(near, back):+.0f} side"
    )
    sa.check(
        abs(across) <= 2.5 and abs(above) <= 2.5,
        f"{case}: on the center line: {across:.1f}, {above:.1f}",
    )
    if case in CENTERED:
        sa.check(abs(along) <= 2.5, f"{case}: in the middle: {along:.1f}")
    else:
        sa.check(
            abs(abs(along) - LENGTH / 6) <= 2.5,
            f"{case}: at the 1/3 point, {LENGTH / 6:.1f} m from the middle: {along:.1f}",
        )
        sa.check(
            along * dot(near, back) > 0, f"{case}: nearer the station: {along:.1f}"
        )


def main() -> int:
    cases = sys.argv[1:] or list(SHIPS)
    tables = [SHIPS[c][2][0] for c in SHIPS]
    for case, (station, ship, (_, blocks)) in SHIPS.items():
        sa.CASES[case] = (station, ship, blocks, False, False, SIZES.get(case, "Large"))
    rig.stop_client()
    rig.stop_server()
    # One free order after the other
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "FreeBuildCooldownMinutes" not in config
    patched = rig.WORLD.parent / "placement-octahedron.cfg"
    patched.parent.mkdir(parents=True, exist_ok=True)
    patched.write_text(
        config.replace(
            "</OctahedronConfig>",
            "<FreeBuildCooldownMinutes>0</FreeBuildCooldownMinutes></OctahedronConfig>",
        ),
        encoding="utf-8",
    )
    rig.CONFIG_SOURCE = patched
    so.prepare(tables, 60)
    rig.start_server()
    try:
        remote = rig.start_client()
        log("joined")
        for case in cases:
            rig.ensure_character(remote)
            mark = sa.order(remote, case)
            if mark is not None:
                sa.finish(remote, case, mark)
    finally:
        rig.stop_client()
        rig.stop_server()
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    for case in cases:
        check_place(root, case)
    for line in rig.server_game_log().splitlines():
        if "Shipyard arm" in line or "xception" in line:
            log("server: " + line.strip())
    failed = [what for ok, what in sa.checks if not ok]
    print(
        f"{len(sa.checks) - len(failed)}/{len(sa.checks)} placement checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
