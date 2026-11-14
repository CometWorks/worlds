"""No drilling and no landing gear or magnetic plate locks in a station's admin safe zone,
which allows no action since SE-974; both still work outside it.

- Drilling: a switched on ship drill buried in a small asteroid in Alpha's zone digs
  nothing, the same 100 m out of the zone digs ore.

- Locking: a ship of one landing gear, and one of a magnetic plate, each against a static
  armor block in Alpha's zone and 100 m out of it. All four are ready to lock; locked by
  hand, the two outside lock and the two inside don't.
- Auto lock: the same with auto lock on, the game's other way to lock.
- Bots' gears (SE-1466): with the test client's Steam ID in ShipyardBotSteamIds the gears and
  plates on its ships (a gear has no owner: a cargo container of its makes the ship its) lock in
  the zone, by hand and by auto lock, and outside it too. Taken off the list again, its new ones
  in the zone don't.
- Locked before: with the zone set back to allow locks, a gear and a plate in it lock; the
  zone then forbids locks, as the admin menu would. They stay locked, and after a server
  restart they load locked. Released, they don't lock again.

The world must come from this branch's build_world.py: the test first checks that Alpha's
zone allows no action.

    OCT_TASK=SE-974 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_zone_actions.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-974")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log, with_config  # noqa: E402

STATION = "Alpha"
KINDS = ("LargeBlockLandingGear", "LargeBlockMagneticPlate")
# Long enough for a refused lock to have happened: the server tries every 10 frames
SETTLE = 5


def spawn(a, where: str, kind: str, auto: str = "") -> str:
    reply = probe(a, f"gear {STATION} {where} {kind} {auto}".strip())
    m = re.search(r"GEAR (\d+) inzone=(\w+)", reply)
    check(
        m is not None and m[2] == ("yes" if where == "in" else "no"),
        f"a {kind} ship {where} the zone: {reply}",
    )
    return m[1] if m else "0"


def mode(a, ship: str) -> str:
    m = re.search(r"mode=(\w+)", probe(a, f"gearstate {ship}"))
    return m[1] if m else "?"


def ready(a, ship: str, what: str) -> None:
    check(
        bool(st.wait_until(lambda: mode(a, ship) == "ReadyToLock", 20)),
        f"{what} is ready to lock on its pad",
    )


def locks(a, ship: str) -> bool:
    return bool(st.wait_until(lambda: mode(a, ship) == "Locked", 10))


def zone(a, actions: str = "") -> str:
    m = re.search(r"ZONE (\S+)", probe(a, f"zone {STATION} {actions}".strip()))
    return m[1] if m else "?"


def drilling(a) -> None:
    drills = {}
    for where in ("in", "out"):
        reply = probe(a, f"drill {STATION} {where}")
        m = re.search(r"DRILL (\d+) inzone=(\w+)", reply)
        check(
            m is not None and m[2] == ("yes" if where == "in" else "no"),
            f"a drill in an asteroid {where} the zone: {reply}",
        )
        drills[where] = m[1] if m else "0"

    def ore(where: str) -> float:
        m = re.search(r"ore=(\S+)", probe(a, f"drillstate {drills[where]}"))
        return float(m[1]) if m else -1.0

    check(
        bool(st.wait_until(lambda: ore("out") > 0, 30, 2)), "the drill outside digs ore"
    )
    dug = ore("in")
    check(dug == 0, f"the drill in the zone digs nothing: {dug} kg")


def by_hand(a) -> None:
    for kind in KINDS:
        ships = {where: spawn(a, where, kind) for where in ("in", "out")}
        for where, ship in ships.items():
            ready(a, ship, f"the {kind} {where} the zone")
        for ship in ships.values():
            probe(a, f"gearlock {ship} on")
        check(locks(a, ships["out"]), f"the {kind} outside the zone locks")
        time.sleep(SETTLE)
        state = mode(a, ships["in"])
        check(state == "ReadyToLock", f"the {kind} in the zone doesn't lock: {state}")


def auto_lock(a) -> None:
    for kind in KINDS:
        ships = {where: spawn(a, where, kind, "auto") for where in ("in", "out")}
        check(locks(a, ships["out"]), f"the {kind} outside the zone auto locks")
        time.sleep(SETTLE)
        state = mode(a, ships["in"])
        check(
            state in ("Unlocked", "ReadyToLock"),
            f"the {kind} in the zone doesn't auto lock: {state}",
        )


def bot_gears(a) -> None:
    with_config(a, ShipyardBotSteamIds=rig.CLIENT_ID)
    for kind in KINDS:
        ships = {where: spawn(a, where, kind, "owned") for where in ("in", "out")}
        for where, ship in ships.items():
            ready(a, ship, f"the bot's {kind} {where} the zone")
            probe(a, f"gearlock {ship} on")
        for where, ship in ships.items():
            check(locks(a, ship), f"the bot's {kind} {where} the zone locks")
        ship = spawn(a, "in", kind, "auto,owned")
        check(locks(a, ship), f"the bot's {kind} in the zone auto locks")
    with_config(a)
    for kind in KINDS:
        ship = spawn(a, "in", kind, "owned")
        ready(a, ship, f"the {kind} off the bot list")
        probe(a, f"gearlock {ship} on")
        time.sleep(SETTLE)
        state = mode(a, ship)
        check(
            state == "ReadyToLock",
            f"off the bot list, the {kind} doesn't lock: {state}",
        )


def locked_before(a) -> list[str]:
    check(
        zone(a, "Drilling+LandingGearLock") == "Drilling+LandingGearLock",
        "Alpha's zone allows locks again",
    )
    ships = [spawn(a, "in", kind) for kind in KINDS]
    for kind, ship in zip(KINDS, ships):
        ready(a, ship, f"the {kind}")
        probe(a, f"gearlock {ship} on")
        check(locks(a, ship), f"the {kind} locks in the zone while it allows locks")
    check(zone(a, "None") == "None", "the zone forbids locks again")
    # The server reattaches locked gears every 100 frames
    time.sleep(10)
    for kind, ship in zip(KINDS, ships):
        state = mode(a, ship)
        check(state == "Locked", f"the {kind} stays locked: {state}")
    return ships


def after_restart(a, ships: list[str]) -> None:
    check(zone(a) == "None", "after the restart the zone still forbids locks")
    for kind, ship in zip(KINDS, ships):
        check(locks(a, ship), f"the {kind} loads locked")
    time.sleep(10)
    for kind, ship in zip(KINDS, ships):
        state = mode(a, ship)
        check(state == "Locked", f"the {kind} stays locked after the load: {state}")
    for kind, ship in zip(KINDS, ships):
        probe(a, f"gearlock {ship} off")
        check(
            bool(st.wait_until(lambda: mode(a, ship) == "ReadyToLock", 10)),
            f"the {kind} unlocks",
        )
        probe(a, f"gearlock {ship} on")
        time.sleep(SETTLE)
        state = mode(a, ship)
        check(state == "ReadyToLock", f"and doesn't lock again: {state}")


def start():
    rig.start_server()
    a = rig.start_client()
    rig.ensure_character(a)
    return a


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.write_plugin_config()
    try:
        a = start()
        actions = zone(a)
        check(actions == "None", f"Alpha's zone allows no action: {actions}")
        drilling(a)
        by_hand(a)
        auto_lock(a)
        bot_gears(a)
        ships = locked_before(a)
        log("restarting the server, it saves")
        rig.stop_client()
        rig.stop_server()
        a = start()
        after_restart(a, ships)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("zone actions")


if __name__ == "__main__":
    sys.exit(main())
