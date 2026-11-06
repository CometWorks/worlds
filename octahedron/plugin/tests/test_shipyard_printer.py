"""While an order runs at a shipyard, its welders weld only its ship (SE-1122). A player's printer
stands by the Moon Shipyard where Viktor's missile printer stood on the cluster: outside the build
space and in the welders' reach, a projector with large batteries to build. The player orders a
ship free at Moon Station. The order must end with the ship complete and the printer's projection
unbuilt. Before SE-1122 the station's welders built the printer's batteries with the free build's
power cells, and a ship that needed them stalled and was removed.

The ship is the 10 block "Order Test" of test_shipyard_order.py, or with OCT_BLUEPRINT the bp.sbc
of a real ship like "IMDC Europa Carrier" (Workshop 816832795), for at most OCT_BLUEPRINT_SECONDS
(2400).

    OCT_TASK=SE-1122 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_printer.py

OCT_PRINTER_BATTERIES sets the printer's projected batteries (2). With OCT_SHOTS=<folder> the
client renders and saves screenshots of the build and of the ship at its end. Prints one line per
check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-1122")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_client as tc  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402

check = st.check
log = so.log

STATION = "Moon"
# Where the missile printer stood: meters from the Moon Station Shipyard grid along its right, up
# and back, read from the cluster's partition save of 2026-11-05 22:30Z
PRINTER = (0.1, 134.6, 131.2)
BATTERIES = int(os.environ.get("OCT_PRINTER_BATTERIES", 2))


def blueprint_xml() -> tuple[str, int]:
    """The blueprint's top grids as the world's table blueprint, and the time it may take"""
    path = os.environ.get("OCT_BLUEPRINT")
    if not path:
        return so.SMALL, 600
    ET.register_namespace("xsi", "http://www.w3.org/2001/XMLSchema-instance")
    top = ET.parse(path).find(".//ShipBlueprint/CubeGrids")
    for grid in top:
        grid.tag = "MyObjectBuilder_CubeGrid"
    seconds = int(os.environ.get("OCT_BLUEPRINT_SECONDS", 2400))
    return "".join(ET.tostring(g, encoding="unicode") for g in top), seconds


def printer(remote) -> str:
    reply = st.probe(
        remote, f"yardprinter {STATION} {' '.join(map(str, PRINTER))} {BATTERIES}"
    )
    found = re.search(r"TARGET (\d+)", reply)
    check(found is not None, f"a printer by the shipyard: {reply}")
    return found[1] if found else "0"


def info(remote, grid: str) -> dict[str, str]:
    return dict(re.findall(r"(\w+)=(\S+)", st.probe(remote, f"info {grid}")))


def order(remote) -> str:
    """A free order at Moon Station; the ship's name"""
    sa.at_table(remote, STATION)
    quote = tc.ship_reply(remote, "!oct order")
    check("Your quote for" in quote, f"quote: {quote}")
    name = re.search(r"Your quote for '(.*?)' is", quote)[1]
    reply = tc.ship_reply(remote, "!oct order")
    check(not reply or "is in the queue" in reply, f"ordered: {reply}")
    pattern = rf"Shipyard order \S+ '{re.escape(name)}' \(.*free\) for "
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline and not re.search(pattern, rig.server_game_log()):
        time.sleep(1)
    check(re.search(pattern, rig.server_game_log()), "the free build started")
    return name


def main() -> int:
    xml, seconds = blueprint_xml()
    cubes = sum(
        len(g.find("CubeBlocks"))
        for g in ET.fromstring(
            f"<r xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>{xml}</r>"
        )
    )
    rig.stop_client()
    rig.stop_server()
    so.prepare(xml, max(60, seconds // 60))
    shots = bool(so.SHOTS)
    name = None
    try:
        rig.start_server()
        remote = rig.start_client(render=shots)
        rig.ensure_character(remote)
        remote.set_admin_flag("invulnerable", True)
        tc.with_config(
            remote,
            ShipyardOrderMinutes=max(60, seconds // 60),
            FreeBuildPcuLimit=1_000_000,
            GroupPcuLimit=1_000_000,
            FreeBuildBlockedBlocks="",
            ShipyardTableMinutes=600,
        )
        sa.at_table(remote, STATION)
        target = printer(remote)
        before = info(remote, target)
        log(f"printer {before}")
        name = order(remote)
        yard = next(
            g["entityId"]
            for g in remote.list_grids()
            if g.get("name") == f"{STATION} Station Shipyard"
        )
        shot = shots
        ended = None
        started = time.monotonic()
        deadline = started + seconds
        while time.monotonic() < deadline and ended is None:
            lines, _ = so.chat_lines(remote, 0)
            ended = next(
                (l for l in lines if "stopped building" in l or " is ready in " in l),
                None,
            )
            if shot and time.monotonic() - started > 120:
                so.screenshots(remote, yard, "building")
                shot = False
            time.sleep(5)
        log(f"the build took {time.monotonic() - started:.0f} s")
        check(ended is not None, f"the build ended: {ended}")
        check(" is ready in " in str(ended), f"the ship is complete: {ended}")
        if shots:
            so.screenshots(remote, yard, "end")
        after = info(remote, target)
        log(f"printer {after}")
        check(
            after.get("blocks") == before.get("blocks"),
            f"no printer block built: {before.get('blocks')} -> {after.get('blocks')} blocks",
        )
    finally:
        rig.stop_client()
        rig.stop_server()
    lines = rig.server_game_log().splitlines()
    for line in lines:
        if "Shipyard order" in line and (" stalled" in line or " ended:" in line):
            log(line)
    try:
        so.check_ship(name, cubes, complete=True)
        check(True, f"the saved ship has all {cubes} blocks, complete")
    except AssertionError as e:
        check(False, f"the saved ship is complete: {e}")
    errors = [
        line
        for line in lines
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("shipyard printer")


if __name__ == "__main__":
    sys.exit(main())
