"""Checks that the shipyard turns a ship that only fits another way before it checks the size
(SE-867), on the test server with the mods of tests/test_support_arm.py, on a world built by
build_world.py. The checks read the delivered ship from the saved world.

- "bed" at North Station: 150 m long, wider than the shipyard if measured along the cryo chamber
  that comes first in the blueprint, facing right. The cockpit after it counts: it points along
  the shipyard.
- "sideways" at Alpha Station: 150 m long, its only cockpit facing right: built turned about its
  up, its length along the shipyard.
- "short" at Beta Station: 25 m long, its only cockpit facing right: fits as it is, so its cockpit
  still points along the shipyard.
- "seats" at South Station: short, a cryo chamber, a passenger seat and a bed facing right come
  first in the blueprint, then the cockpit: its cockpit points along the shipyard (SE-880).
- "flagged" at Delta Station: short, a bed facing right flagged as the main cockpit, then the
  cockpit: its cockpit points along the shipyard (SE-880).
- "nocontrol" at Moon Station: short, only those three facing right, nothing that flies it: the
  grid's forward points along the shipyard, so its cryo chamber points across (SE-880).
- "file" at Gamma Station, with OCT_ALIGN_BP=<bp.sbc>: a real blueprint whose cockpit points along
  its length, ordered with Ignore PCU limits on. Workshop item 3344819653 is the example.
  Its DLC blocks stall the build unless the NPC-owned welders may place them (SE-805).

    OCT_TASK=SE-867 OCT_WORLD_SOURCE=<world from build_world.py> OCT_ALIGN_BP=<bp.sbc> \\
        uv run --no-project --with httpx python tests/test_shipyard_align.py [case ...]

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-867")
import rig  # noqa: E402
import test_hologram_center as hc  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_placement as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log  # noqa: E402


def ship(name: str, entity: int, length: int, bed: bool) -> tuple[str, int]:
    """Armor 1 wide, 2 high, with a cockpit on top: pointing along the ship behind a cryo chamber
    facing right, or facing right itself"""
    cockpit = rig.block(
        "MyObjectBuilder_Cockpit",
        "LargeBlockCockpit",
        (0, 2, 0),
        f"<EntityId>{entity + 1}</EntityId>",
        forward="Forward" if bed else "Right",
    )
    chamber = rig.block(
        "MyObjectBuilder_CryoChamber", "LargeBlockCryoChamber", (0, 2, 2), forward="Right"
    )  # fmt: skip
    blocks = ([chamber] if bed else []) + [cockpit] + sa.hull({}, (1, 2, length), "LargeBlockArmorBlock")  # fmt: skip
    return rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid"), len(blocks)


def seats(name: str, entity: int, cockpit: bool, main: str = "") -> tuple[str, int]:
    """Short armor with ship controllers on top that don't fly it, all facing right, then a cockpit
    pointing along the ship. Not the DLC cryo room or bed: their DLC stalls the build (SE-805).
    """
    sideways = dict(forward="Right")
    blocks = [
        rig.block("MyObjectBuilder_CryoChamber", "LargeBlockCryoChamber", (0, 2, 0), **sideways),
        rig.block("MyObjectBuilder_Cockpit", "PassengerSeatLarge", (0, 2, 2), **sideways),
        rig.block("MyObjectBuilder_CryoChamber", "LargeBlockBedFree", (0, 2, 4), main, **sideways),
    ]  # fmt: skip
    if cockpit:
        blocks.append(rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 2, 6), f"<EntityId>{entity + 1}</EntityId>"))  # fmt: skip
    blocks += sa.hull({}, (1, 2, 10), "LargeBlockArmorBlock")
    return rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid"), len(blocks)


def from_file(path: str) -> tuple[str, int]:
    grids = hc.blueprint_grids(Path(path))
    return grids, grids.count("<MyObjectBuilder_CubeBlock ")


# case: (station, ship, (blueprint, blocks))
SHIPS = {
    "bed": ("North", "AlignBed", ship("AlignBed", 777000867000100, 60, True)),
    "sideways": ("Alpha", "AlignSideways", ship("AlignSideways", 777000867000200, 60, False)),
    "short": ("Beta", "AlignShort", ship("AlignShort", 777000867000300, 10, False)),
    "seats": ("South", "AlignSeats", seats("AlignSeats", 777000880000100, True)),
    "flagged": ("Delta", "AlignFlagged", seats("AlignFlagged", 777000880000200, True, "<IsMainCockpit>true</IsMainCockpit>")),
    "nocontrol": ("Moon", "AlignNoControl", seats("AlignNoControl", 777000880000300, False)),
}  # fmt: skip
BP = os.environ.get("OCT_ALIGN_BP")
if BP:
    name = ET.parse(BP).getroot().findtext(".//CubeGrid/DisplayName")
    SHIPS["file"] = ("Gamma", name, from_file(BP))
# The tables in the world's order. No order is placed at the ones without a case.
STATIONS = ["North", "South", "Alpha", "Beta", "Gamma", "Delta", "Moon"]
# Whether the cockpit ends up pointing along the shipyard; the ones that don't are turned
COCKPIT_ALONG = {"bed": True, "sideways": False, "short": True, "file": True, "seats": True, "flagged": True, "nocontrol": False}  # fmt: skip
CENTERED = ("bed", "sideways", "file")
COCKPITS = ("LargeBlockCockpit", "LargeBlockStandingCockpit")
# The block whose direction is checked where the ship has no cockpit
LOOKS = {"nocontrol": ("LargeBlockCryoChamber",)}


def check_turn(root: ET.Element, case: str) -> None:
    """A built grid's own frame says nothing, the arm's grid has its own: this goes by its blocks"""
    station, name, _ = SHIPS[case]
    grids = {
        e.findtext("DisplayName"): e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.find("CubeBlocks") is not None
    }
    built = grids.get(name)
    if built is None:
        return  # check_place has said so
    _, right, up, back = tp.frame(built)
    _, _, yard_up, yard_back = tp.frame(grids[f"{station} Station Shipyard"])
    neg = lambda v: tuple(-x for x in v)  # noqa: E731
    world = {
        "Forward": neg(back),
        "Backward": back,
        "Left": neg(right),
        "Right": right,
        "Up": up,
        "Down": neg(up),
    }
    looks = LOOKS.get(case, COCKPITS)
    cockpit = next(
        b for b in built.find("CubeBlocks") if b.findtext("SubtypeName") in looks
    )
    orientation = cockpit.find("BlockOrientation")
    attrib = {} if orientation is None else orientation.attrib
    forward = world[attrib.get("Forward", "Forward")]
    cockpit_up = world[attrib.get("Up", "Up")]
    along = -tp.dot(forward, yard_back)
    if COCKPIT_ALONG[case]:
        sa.check(along > 0.99, f"{case}: its cockpit points along the shipyard: {along:+.2f}")  # fmt: skip
    else:
        sa.check(abs(along) < 0.01, f"{case}: its cockpit points across the shipyard: {along:+.2f}")  # fmt: skip
    above = tp.dot(cockpit_up, yard_up)
    sa.check(above > 0.99, f"{case}: its cockpit's up is the shipyard's: {above:+.2f}")
    if case in CENTERED:
        all_cells = tp.cells(built)
        spans = [max(c[i] for c in all_cells) - min(c[i] for c in all_cells) for i in range(3)]  # fmt: skip
        longest = [right, up, back][spans.index(max(spans))]
        length = abs(tp.dot(longest, yard_back))
        sa.check(length > 0.99, f"{case}: its length is along the shipyard: {length:.2f}")  # fmt: skip


def main() -> int:
    cases = sys.argv[1:] or list(SHIPS)
    blueprints = {station: bp[0] for station, _, bp in SHIPS.values()}
    tables = [blueprints.get(s, SHIPS["short"][2][0]) for s in STATIONS]
    for case, (station, name, (_, blocks)) in SHIPS.items():
        # The real ship has connectors for the arm to dock at
        sa.CASES[case] = (station, name, blocks, False, case == "file", "Large")
    tp.SHIPS.update(SHIPS)
    tp.CENTERED = CENTERED
    rig.stop_client()
    rig.stop_server()
    # One free order after the other
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "FreeBuildCooldownMinutes" not in config
    patched = rig.WORLD.parent / "align-octahedron.cfg"
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
            if case == "file":
                sa.ignore_pcu(remote, True)
            mark = sa.order(remote, case)
            if mark is not None:
                sa.finish(remote, case, mark)
            if case == "file":
                sa.ignore_pcu(remote, False)
    finally:
        rig.stop_client()
        rig.stop_server()
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    for case in cases:
        tp.check_place(root, case)
        check_turn(root, case)
    failed = [what for ok, what in sa.checks if not ok]
    print(
        f"{len(sa.checks) - len(failed)}/{len(sa.checks)} align checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
