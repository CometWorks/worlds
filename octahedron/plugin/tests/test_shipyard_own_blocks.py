"""A player's turn at a station shipyard welds only their blocks and their faction's (SE-939),
OCT's not counting (SE-975), with two players on one server, both in OCT. In OctTester's repair
slot, each player has a ship to repair in the shipyard: a damaged armor block and a projection to
build. The shipyard's welders repair OctTester's ship and leave OctOther's alone, its block and
its projection. Once both join a faction of their own, the welders repair OctOther's too; then
they go back to OCT. On a world built by build_world.py.

    OCT_TASK=SE-939 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_own_blocks.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 15 minutes.
Not covered: a paid build's ship (test_paid_builds.py), a cluster, and how a client draws the
beams: its ToolCore doesn't have the filter.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-939")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_repair_slot as rs  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (
    Second,
    check,
    checks,
    press,
    settled,
    wait_for,
)  # noqa: E402

STATION = rs.STATION
# Each ship this far from the shipyard's middle, along it, on either side
SHIFT = 12
# How long the welders get on the other player's ship after they repaired the own one
GRACE = 60


def target(remote, shift: int) -> str:
    """A ship to repair, the caller's"""
    found = re.search(
        r"TARGET (\d+)", rs.probe(remote, f"yardtarget {STATION} 2 {shift}")
    )
    check(found is not None, f"a damaged ship with a projection at {shift} m: {found}")
    return found[1] if found else "0"


def repaired(remote, grid: str) -> bool:
    got = rs.info(remote, grid)
    return got.get("full") == "5" and got.get("blocks") == "5"


def turn(a, b) -> None:
    me = rs.probe(a, "whoami")
    with Second():
        other = rs.probe(b, "whoami")
    check(
        me.startswith("ME OCT ") and other.startswith("ME OCT "),
        f"both players are in OCT's faction: {me} / {other}",
    )
    reply = press(a)
    check("Repair slot" in reply, f"a press without a blueprint books a slot: {reply}")
    check(wait_for(lambda: rs.slot_runs(a) is not None, 20), "the slot runs")
    yard_id = rs.shipyard_id(a)
    check(
        wait_for(lambda: sa.shoots(a, yard_id) is True, 20),
        "the welders are in build mode",
    )
    check(rs.theirs(rs.yard(a), "OctTester"), "the welders are OctTester's")
    docked = re.search(r"SHIP (\d+)", rs.probe(a, f"yardship {STATION}"))
    check(docked is not None, f"a ship with the components at a connector: {docked}")
    check(st.wait_docked(a, docked[1] if docked else "0"), "it docks")

    other = target(b, SHIFT)
    before = rs.info(a, other)
    own = target(a, -SHIFT)
    done = wait_for(lambda: repaired(a, own), 240, 5)
    check(done, f"the welders repair the player's ship: {rs.info(a, own)}")
    time.sleep(GRACE)
    after = rs.info(a, other)
    check(
        after.get("blocks") == "3" and after.get("full") == before.get("full"),
        f"not the other player's: no projected block built, none completed: {before} -> {after}",
    )
    check(
        after.get("integrity") == before.get("integrity"),
        f"nor welded: integrity {before.get('integrity')} -> {after.get('integrity')}",
    )

    rs.probe(a, "faction KRS founder")
    with Second():
        rs.probe(b, "faction KRS member")
    done = wait_for(lambda: repaired(a, other), 240, 5)
    check(
        done,
        f"in the faction, the other player's ship is repaired: {rs.info(a, other)}",
    )
    with Second():
        rs.probe(b, "faction OCT member")
    rs.probe(a, "faction OCT member")

    reply = press(a)
    check("you canceled it" in reply, f"the press ends the slot: {reply}")
    # What is left in the containers would keep the turn for its window
    rig.chat(a, "!oct shipyard done", 4)
    check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "the welders are OCT's again")
    # Removes both ships and the docked one
    rs.probe(a, "undock")


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    tr.both_admins()
    with Second():
        folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client()
        with Second():
            b = rig.start_client()
        rig.ensure_character(a)
        with Second():
            rig.ensure_character(b)
        a.set_admin_flag("invulnerable", True)
        b.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        with_config(a, **rs.CONFIG)
        log(tr.remove(a))
        settled(a, "table", "then press")
        turn(a, b)
    finally:
        rig.stop_client()
        with Second():
            rig.stop_client()
        rig.stop_server()
    lines = rig.server_game_log().splitlines()
    check(
        any("a player's turn welds only their blocks" in line for line in lines),
        "the plugin patched ToolCore",
    )
    errors = [
        line
        for line in lines
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} shipyard own blocks checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
