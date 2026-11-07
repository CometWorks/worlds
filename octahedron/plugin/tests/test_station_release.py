"""When the stations' connectors let go of a docked ship (SE-1237), with the check every 2 s and a
30 s countdown instead of 1 and 10 minutes:

- The game's own auto unlock is off on every station connector, the world's 600 s put to 0.
- present: the ship stays while its owner is in the station's safe zone.
- absent: the owner leaves the zone and the ship is let go at the countdown's end.
- back: the owner returns halfway, the countdown stops; leaving again starts it over.
- oct: a ship of another OCT member is let go, though the player in the zone is in OCT too.
- team: a ship of a member of the player's own faction stays while the player is there.
- full: with no free connector the ship docked the longest gets the countdown with the owner
  there, and a chat message; a connector freeing up stops it, and full again lets it go.

    OCT_TASK=SE-1237 uv run --no-project --with httpx python tests/test_station_release.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a cluster handover, station_release_cluster.py has that.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1237")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_support_arm as sa  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log, with_config  # noqa: E402

STATION = "Alpha"
CHECK = 2
COUNTDOWN = 30
# The rim connectors; probe ships at the pads get pushed off
RIM = ["13;0;0", "-13;0;0"]
# A trading connector won't lock for 30 s after letting go: when each was last used
used: dict[str, float] = {}
# Ships with how many countdowns they may start, checked in the server log once it has been written out
quiet: list[tuple[str, int, str]] = []


def docked(a, ship: str) -> bool:
    return f"DOCKED {ship} yes" in probe(a, f"docked {ship}")


def dock(a, owner: str, at: str) -> str:
    time.sleep(max(0.0, used.get(at, 0) + 31 - time.monotonic()))
    used[at] = float("inf")
    reply = probe(a, f"dock {STATION} {owner} AT={at}")
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"a {owner} ship at {at}: {reply}")
    ship = ship[1] if ship else "0"
    check(bool(st.wait_docked(a, ship)), f"{ship} docks")
    return ship


def released_after(a, ship: str, limit: float) -> float | None:
    """Seconds until the ship is let go, None if it is still docked after limit"""
    start = time.monotonic()
    while time.monotonic() - start < limit:
        if not docked(a, ship):
            return time.monotonic() - start
        time.sleep(1)
    return None


def stays(a, ship: str, seconds: float) -> bool:
    return released_after(a, ship, seconds) is None


def away(a) -> None:
    x, y, z = sa.station_tables()[STATION]
    a.character_teleport(x + 5000, y, z)
    log("the player left the station's safe zone")


def countdown(a, ship: str) -> float | None:
    """The seconds left the connector holding the ship stored, -1 for none, None while it stores nothing"""
    m = re.search(rf"release={ship}:[^:]+:([^:]+):", probe(a, f"connectors {STATION}"))
    return float(m[1]) if m else None


def started(a, ship: str) -> bool:
    return bool(
        st.wait_until(
            lambda: (c := countdown(a, ship)) is not None and c >= 0, CHECK * 3 + 10
        )
    )


def stopped(a, ship: str) -> bool:
    return bool(st.wait_until(lambda: countdown(a, ship) == -1, CHECK * 3 + 10))


def undock(a) -> None:
    log(probe(a, "undock"))
    for at in used:
        used[at] = min(used[at], time.monotonic())


def log_count(pattern: str) -> int:
    return sum(
        1
        for line in rig.server_game_log().splitlines()
        if "[Octahedron]" in line and re.search(pattern, line)
    )


def vanilla_off(a) -> None:
    found = re.findall(r"unlock=(\d+)", probe(a, f"connectors {STATION}"))
    check(
        len(found) == 8 and set(found) == {"0"},
        f"the game's auto unlock is off on every connector: {found}",
    )


def present(a) -> None:
    ship = dock(a, "mine", RIM[0])
    check(
        stays(a, ship, COUNTDOWN * 2.5), "present: the ship stays with its owner there"
    )
    quiet.append((ship, 1, "present: no countdown before the owner left, one after"))
    away(a)
    left = time.monotonic()
    check(started(a, ship), "absent: the countdown starts once the owner left")
    took = released_after(a, ship, COUNTDOWN + CHECK * 4)
    took = None if took is None else time.monotonic() - left
    check(
        took is not None and COUNTDOWN - CHECK <= took <= COUNTDOWN + CHECK * 3 + 5,
        f"absent: let go at the countdown's end, {took} s after leaving",
    )
    undock(a)


def back(a) -> None:
    sa.at_table(a, STATION)
    ship = dock(a, "mine", RIM[1])
    away(a)
    check(started(a, ship), "back: the countdown starts")
    time.sleep(COUNTDOWN / 3)
    sa.at_table(a, STATION)
    check(stopped(a, ship), "back: the countdown stops with the owner back")
    check(stays(a, ship, COUNTDOWN * 1.5), "back: the ship stays")
    away(a)
    left = time.monotonic()
    took = released_after(a, ship, COUNTDOWN + CHECK * 4)
    took = None if took is None else time.monotonic() - left
    check(
        took is not None and took >= COUNTDOWN - CHECK,
        f"back: leaving again starts the countdown over, let go {took} s after",
    )
    undock(a)


def factions(a) -> None:
    sa.at_table(a, STATION)
    log(probe(a, "faction OCT member"))
    ship = dock(a, "mate", RIM[0])
    took = released_after(a, ship, COUNTDOWN + CHECK * 5)
    check(took is not None, f"oct: another OCT member's ship is let go, after {took} s")
    undock(a)
    log(probe(a, "faction TST founder"))
    ship = dock(a, "mate", RIM[1])
    check(stays(a, ship, COUNTDOWN * 2), "team: a ship of the player's faction stays")
    undock(a)
    log(probe(a, "faction OCT member"))


def full(a) -> None:
    first = dock(a, "mine", RIM[0])
    time.sleep(CHECK * 2)
    second = dock(a, "mine", RIM[1])
    mark = sa.so_latest(a)
    log(probe(a, f"connectorpower {STATION} off"))
    check(started(a, first), "full: the ship docked the longest gets the countdown")
    told = sa.wait_chat(a, "Every connector of", CHECK * 3 + 15, mark)
    check(told is not None, f"full: its owner is told by chat: {told}")
    time.sleep(COUNTDOWN / 3)
    log(probe(a, f"letgo {second}"))
    used[RIM[1]] = time.monotonic()
    check(stopped(a, first), "full: a connector freeing up stops the countdown")
    check(stays(a, first, COUNTDOWN * 1.5), "full: the ship stays then")
    log(probe(a, f"connectorpower {STATION} off"))
    full_again = time.monotonic()
    took = released_after(a, first, COUNTDOWN + CHECK * 4)
    took = None if took is None else time.monotonic() - full_again
    check(
        took is not None and took >= COUNTDOWN - CHECK,
        f"full: full again, the countdown starts over and lets it go {took} s after",
    )
    quiet.append((second, 0, "full: the ship docked later got no countdown"))
    log(probe(a, f"connectorpower {STATION} on"))
    undock(a)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.write_plugin_config()
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        with_config(
            a,
            StationConnectorCheckSeconds=CHECK,
            StationConnectorCountdownSeconds=COUNTDOWN,
            StationDockCharging="false",
        )
        time.sleep(CHECK * 2)
        vanilla_off(a)
        present(a)
        back(a)
        factions(a)
        full(a)
    finally:
        rig.stop_client()
        rig.stop_server()
    for ship, count, what in quiet:
        check(log_count(rf"countdown of .*\({ship}\) .*started") == count, what)
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("station release")


if __name__ == "__main__":
    sys.exit(main())
