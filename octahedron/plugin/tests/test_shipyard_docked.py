"""What counts as in a shipyard's way (SE-923), at a real station shipyard, which lies at an angle
to the world's axes. The build space goes up to the walls and leaves out the connectors and the
conveyors leading to them, so a ship docked there with the components for a paid build doesn't
refuse the press. A block of a ship in the space does, by one block at the space's open end, and
a ship turned across the shipyard too. A ship just past that end doesn't, and the sweep before an
order leaves a pod without power parked beside the walls.

    OCT_TASK=SE-923 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_docked.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 7 minutes.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-923")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as tt  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import check, checks  # noqa: E402

STATION = tt.STATION
SUPPLY = "Supply"
TURN = {**tt.PAY, "ShipyardTurnMinutes": 10}
# Cells of the shipyard's grid: the space's open end is z -57
MIDDLE = "0,-16,0"
LAST_BLOCK_IN = "0,-16,-58"
AT_THE_END = "0,-16,-57"
PAST_THE_END = "0,-16,-60"
PAST_AND_TURNED = "0,-16,-62"
BESIDE_THE_WALLS = "31,-16,0"


def probe(a, args: str) -> str:
    return st.probe(a, args)


def docked(a, name: str = SUPPLY, kind: str = "") -> str:
    reply = probe(a, f"yarddock {STATION} {name} {kind}".strip())
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"'{name}' is at a connector: {reply}")
    ship = ship[1] if ship else "0"
    check(st.wait_docked(a, ship), f"'{name}' is docked")
    return ship


def at(a, name: str, cell: str, kind: str = "") -> None:
    log(probe(a, f"yardat {STATION} {name} {cell} {kind}".strip()))


def refuses(a, name: str, what: str) -> None:
    mark = tp.latest(a)
    sa.at_table(a, STATION)
    tt.press(a)
    told = tp.wait_chat(a, f"Fly '{name}' out of", 30, mark)
    check(told is not None, f"{what}: the press is refused: {told}")
    log(probe(a, f"removegrid {name}"))


def starts(a, ship: str, what: str) -> None:
    sa.at_table(a, STATION)
    reply = tt.start(a)
    check("Fly '" not in reply, f"{what}: no ship to fly out: {reply}")
    told = tp.wait_chat(a, f"builds '{tt.SHIP}' for you now", 60, tt.BUILD_MARK[0])
    check(told is not None, f"{what}: the press starts the build: {told}")
    check(f"DOCKED {ship} yes" in probe(a, f"docked {ship}"), f"{what}: still docked")
    check(tt.built(a) is not None, f"{what}: it is built")
    tt.placed(a)


def finish(a, what: str) -> None:
    # Containers left empty have ended the turn by now
    st.admin(a, "!oct shipyard done")
    tt.free_at_end(a, what)
    tt.ship_gone(a)
    tr.remove(a)


def build_space(a) -> None:
    """Up to the walls, with the connectors and their conveyors outside"""
    reply = probe(a, f"yardspace {STATION}")
    space = re.search(
        r"SPACE (\S+) (\S+) (\S+) connectors=(\d+) in=(\d+) conveyors=(\d+) in=(\d+)",
        reply,
    )
    check(space is not None, f"the build space: {reply}")
    if space:
        size = tuple(float(v) for v in space.group(1, 2, 3))
        check(size == (72.5, 75.0, 287.5), f"it goes up to the walls: {size}")
        check(space[4] == "8" and space[5] == "0", f"no connector is in it: {reply}")
        check(int(space[6]) > 0 and space[7] == "0", f"no conveyor is in it: {reply}")


def ship_docked(a) -> None:
    """The ticket's case: a long powered ship docked, the press starts the build"""
    with_config(a, **TURN)
    tt.turn(a)
    ship = docked(a)
    starts(a, ship, "docked")
    finish(a, "docked")
    check(
        tp.wait_chat(a, "floats free", 5, tt.BUILD_MARK[0]) is not None,
        "its owner is told it floats free at the end",
    )
    log(probe(a, f"removegrid {SUPPLY}"))


def pod_docked(a) -> None:
    """Without power or a cockpit, and a next order's sweep after it floats free"""
    with_config(a, **TURN)
    tt.turn(a)
    ship = docked(a, "Pod", "pod")
    starts(a, ship, "pod")
    finish(a, "pod")
    check("gone" not in probe(a, f"docked {ship}"), "the pod is still there")
    log(probe(a, "removegrid Pod"))


def obstructions(a) -> None:
    """A block in the space refuses the press with a ship docked too, one outside it doesn't"""
    with_config(a, **TURN)
    tt.turn(a)
    ship = docked(a)
    at(a, "Middle", MIDDLE)
    refuses(a, "Middle", "a ship in the middle")
    at(a, "Reaching", LAST_BLOCK_IN)
    refuses(a, "Reaching", "a ship with its last block in the space")
    at(a, "Across", AT_THE_END, "turned")
    refuses(a, "Across", "a ship across the shipyard")
    at(a, "Past", PAST_THE_END)
    at(a, "PastAcross", PAST_AND_TURNED, "turned")
    starts(a, ship, "ships past the space's end")
    finish(a, "obstructions")
    for name in (SUPPLY, "Past", "PastAcross"):
        log(probe(a, f"removegrid {name}"))


def sweep(a) -> None:
    """A pod parked beside the walls stays through the sweep before an order"""
    with_config(a, **TURN)
    at(a, "Parked", BESIDE_THE_WALLS, "pod")
    tt.turn(a)
    check(tt.grid_there(a, "Parked"), "the sweep leaves it alone")
    sa.at_table(a, STATION)
    st.admin(a, "!oct shipyard done")
    tt.free_at_end(a, "sweep")
    tr.remove(a)
    log(probe(a, "removegrid Parked"))


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        with_config(a, **tr.CONFIG)
        log(tr.remove(a))
        sa.admin(a, f"!oct market set {STATION} {tt.PLATE} 10000")
        build_space(a)
        for scenario in (ship_docked, pod_docked, obstructions, sweep):
            probe(a, "air")
            probe(a, f"balance {300_000 - tt.money(a)}")
            scenario(a)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(f"{len(checks) - len(failed)}/{len(checks)} docked checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
