"""Checks where the shipyard builds a ship along its center line (SE-824), on the test server
with the mods of tests/test_support_arm.py, on a world built by build_world.py. The checks read
the delivered ship from the saved world.

A ship shorter than 1/4 of the shipyard (287.5 m: 71.9 m) goes 1/6 of the length from the middle,
to the 1/3 point nearer the station:

- "north", "alpha": a 25 m large grid ship at a pole station and an equatorial one
- "under" at Gamma Station: 70 m long, just short enough
- "small" at Delta Station: a small grid ship on a small arm rotor
- "wide" at Moon Station: a thin small grid cross, 70 m wide and high, the largest search there is

"quarter" at Beta Station, 72.5 m long, at least 1/4, stays in the middle.

"limit" at South Station forces the support arm's time limit, ShipyardArmPlanSeconds 0:

- A press gets the refusal and orders nothing. With the limit back, it can order.
- A paid order waits behind a grid in the shipyard. The server restarts with the limit at 0 and
  that grid gone: the order ends with its reason, no money or stock moved, and the rotor head has
  no arm.

    OCT_TASK=SE-824 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_placement.py [case ...]

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-824")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_client as tc  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_shipyard_order as so  # noqa: E402
from test_client import log  # noqa: E402

PLATE = "Component/SteelPlate"
# The free space along the shipyard: its grid's cells -57 to 57 along Z, across x 0, y -16.5
LENGTH = 287.5
# Shipyards of another model: the free space's middle in grid cells and its length in m.
# Moon's double helix: cells 10 to 191 along its axis, the welders' reach (SE-959).
SPACES = {"Moon": ((0, 0, 100.5), 455.0)}
CUBE = {"Large": 2.5, "Small": 0.5}


def hull(name: str, entity: int, size: tuple[int, int, int]) -> tuple[str, int]:
    # A battery: the checks after the build need a block with an owner, which armor hasn't
    battery = rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 0))  # fmt: skip
    blocks = sa.hull({(0, 0, 0): battery}, size, "LargeBlockArmorBlock")
    return rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid"), len(blocks)


def small_grid(xml: str) -> str:
    return xml.replace(
        "<GridSizeEnum>Large</GridSizeEnum>", "<GridSizeEnum>Small</GridSizeEnum>"
    )


def small_battery(cell) -> str:
    return rig.block("MyObjectBuilder_BatteryBlock", "SmallBlockSmallBatteryBlock", cell)  # fmt: skip


def small_hull() -> tuple[str, int]:
    blocks = sa.hull({(0, 0, 0): small_battery((0, 0, 0))}, (4, 3, 8), "SmallBlockArmorBlock")  # fmt: skip
    grid = rig.grid(
        "PlaceSmall", 777000824000600, blocks, tag="MyObjectBuilder_CubeGrid"
    )
    return small_grid(grid), len(blocks)


def wide_cross() -> tuple[str, int]:
    cells = {(x, 70, 0) for x in range(140)} | {(70, y, 0) for y in range(140)}
    blocks = [small_battery((70, 70, 0))] + [
        rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", c)
        for c in sorted(cells - {(70, 70, 0)})
    ]
    grid = rig.grid(
        "PlaceWide", 777000824000700, blocks, tag="MyObjectBuilder_CubeGrid"
    )
    return small_grid(grid), len(blocks)


# The tables in the world's order: North, South, Alpha, Beta, Gamma, Delta, Moon
SHIPS = {
    "north": ("North", "PlaceNorth", hull("PlaceNorth", 777000824000100, (3, 3, 10))),
    "limit": ("South", "PlaceLimit", sa.large("PlaceLimit", 777000824000200, False)),
    "alpha": ("Alpha", "PlaceAlpha", hull("PlaceAlpha", 777000824000300, (3, 3, 10))),
    "quarter": ("Beta", "PlaceQuarter", hull("PlaceQuarter", 777000824000400, (2, 2, 29))),
    "under": ("Gamma", "PlaceUnder", hull("PlaceUnder", 777000824000500, (2, 2, 28))),
    "small": ("Delta", "PlaceSmall", small_hull()),
    "wide": ("Moon", "PlaceWide", wide_cross()),
}  # fmt: skip
SIZES = {"small": "Small", "wide": "Small"}
CENTERED = ("quarter",)
BLOCKER = 777000824000800


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
    (x, y, z), length = SPACES.get(station, ((0, -16.5, 0), LENGTH))
    mid = tuple(
        pos[i] + 2.5 * (x * right[i] + y * up[i] + z * back[i]) for i in range(3)
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
            abs(abs(along) - length / 6) <= 2.5,
            f"{case}: at the 1/3 point, {length / 6:.1f} m from the middle: {along:.1f}",
        )
        sa.check(
            along * dot(near, back) > 0, f"{case}: nearer the station: {along:.1f}"
        )


def blocker(root: ET.Element) -> str:
    """A grid in South Station's shipyard: an order there waits for it to leave. Owned, with a
    cockpit, else the shipyard sweeps it away as garbage"""
    yard = next(
        e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.findtext("DisplayName") == "South Station Shipyard"
    )
    pos, _, up, _ = frame(yard)
    at = tuple(pos[i] - 2.5 * 16.5 * up[i] for i in range(3))
    blocks = rig.small_ship_blocks(owner=BLOCKER)
    grid = rig.grid("PlaceBlocker", BLOCKER, blocks, position=at)
    return grid.replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")


def state(remote) -> tuple[int, float]:
    """The player's money and OCT's plates at the station"""
    return sa.money(remote)["You"], st.stock(remote, "South", PLATE)[0]


def forced_limit(remote):
    """Returns the client, joined again after the restart"""
    tc.with_config(
        remote, ShipyardArmPlanSeconds=0, FreeBuildPcuLimit=0, ShipyardTableMinutes=600
    )
    sa.at_table(remote, "South")
    reply = sa.admin(remote, "!oct order", 8)
    sa.check(
        "could not plan a support arm" in reply, f"limit: the press is refused: {reply}"
    )
    sa.check(
        "'PlaceLimit'" not in " ".join(sa.shipyard(remote, "South")),
        "limit: nothing is ordered",
    )
    tc.with_config(remote, FreeBuildPcuLimit=0, ShipyardTableMinutes=600)
    for item, count in sa.STOCK.items():
        sa.admin(remote, f"!oct market set South {item} {count}", 3)
    sa.fund(remote)
    before = state(remote)
    sa.at_table(remote, "South")
    # The table quotes by itself once the plan is through, then the first press orders
    reply = sa.admin(remote, "!oct order", 8)
    if "Your quote for" in reply:
        reply = sa.admin(remote, "!oct order", 8)
    sa.check("is in the queue" in reply, f"limit: back at 5 s it orders: {reply}")
    sa.check("builds 'PlaceLimit'" not in reply, f"limit: the order waits: {reply}")
    held = state(remote)
    sa.check(held == before, f"limit: nothing is bought yet: {before} -> {held}")
    # The server ends the order when it starts; no player needed
    rig.stop_client()
    rig.stop_server()
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text, count = re.subn(
        rf"<MyObjectBuilder_EntityBase(?:(?!</MyObjectBuilder_EntityBase>).)*?<EntityId>{BLOCKER}</EntityId>.*?</MyObjectBuilder_EntityBase>\s*",
        "",
        sector.read_text(encoding="utf-8"),
        count=1,
        flags=re.S,
    )
    sa.check(count == 1, "limit: the grid in the shipyard is gone")
    sector.write_text(text, encoding="utf-8")
    # The game prefers its binary copy of the sector
    (rig.WORLD / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    rig.write_plugin_config(
        re.sub(r"<FreeBuildPcuLimit>\d+<", "<FreeBuildPcuLimit>0<", config).replace(
            "</OctahedronConfig>",
            "<ShipyardArmPlanSeconds>0</ShipyardArmPlanSeconds></OctahedronConfig>",
        )
    )
    rig.start_server()
    remote = rig.start_client()
    line = sa.wait_log(
        r"'PlaceLimit' can't be built: .*could not plan a support arm", 120
    )
    sa.check(line is not None, f"limit: the order ends with the reason: {line}")
    rig.ensure_character(remote)
    after = state(remote)
    sa.check(after == before, f"limit: everything is back: {before} -> {after}")
    lines = sa.shipyard(remote, "South")
    sa.check(
        not any("'PlaceLimit'" in line for line in lines[1:]),
        f"limit: no build in the shipyard: {lines}",
    )
    return remote


def main() -> int:
    cases = sys.argv[1:] or list(SHIPS)
    tables = [SHIPS[c][2][0] for c in SHIPS]
    for case, (station, ship, (_, blocks)) in SHIPS.items():
        sa.CASES[case] = (station, ship, blocks, False, False, SIZES.get(case, "Large"))
    rig.stop_client()
    rig.stop_server()
    # One free order after the other, each ship left where it was built until the final save
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "FreeBuildCooldownMinutes" not in config
    assert "ShipyardClearMinutes" not in config
    patched = rig.WORLD.parent / "placement-octahedron.cfg"
    patched.parent.mkdir(parents=True, exist_ok=True)
    patched.write_text(
        config.replace(
            "</OctahedronConfig>",
            "<FreeBuildCooldownMinutes>0</FreeBuildCooldownMinutes>"
            "<ShipyardClearMinutes>600</ShipyardClearMinutes></OctahedronConfig>",
        ),
        encoding="utf-8",
    )
    rig.CONFIG_SOURCE = patched
    prepare_world = rig.prepare_world
    source = ET.parse(rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").getroot()
    rig.prepare_world = lambda **kw: prepare_world(extra_grids=[blocker(source)], **kw)
    so.prepare(tables, 60)
    # The blocker's owner: the game takes no ownership from an identity it doesn't know
    sandbox = rig.WORLD / "Sandbox.sbc"
    text = sandbox.read_text(encoding="utf-8")
    assert "</Identities>" in text
    sandbox.write_text(
        text.replace(
            "</Identities>",
            f"<MyObjectBuilder_Identity><IdentityId>{BLOCKER}</IdentityId>"
            "<DisplayName>PlaceBlocker</DisplayName><CharacterEntityId>0</CharacterEntityId>"
            "</MyObjectBuilder_Identity></Identities>",
            1,
        ),
        encoding="utf-8",
    )
    rig.start_server()
    try:
        remote = rig.start_client()
        log("joined")
        for case in cases:
            rig.ensure_character(remote)
            if case == "limit":
                remote = forced_limit(remote)
                continue
            mark = sa.order(remote, case)
            if mark is not None:
                sa.finish(remote, case, mark)
    finally:
        rig.stop_client()
        rig.stop_server()
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    for case in cases:
        if case != "limit":
            check_place(root, case)
    sa.check(
        "<DisplayName>PlaceLimit</DisplayName>"
        not in (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
        or "limit" not in cases,
        "limit: no ship was started",
    )
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
