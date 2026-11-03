"""A ship docked at a station's trading connector charges from the station, and the station's fuse
cuts the ship that wants the most when the docked ships want more than it has to spare (SE-972).

- Charging: a ship with an empty battery and an empty, stockpiling H2 tank docks at Alpha. The
  battery charges at its 12 MW input and the station's reactors lose uranium. The tank stays empty
  and the grids stay apart: the connector still trades. (The probe's raw conveyor path check finds
  a way through a powered ship's connector; item pulls check access, which refuses a trading
  connector, so it isn't asserted.)
- Fuse: with the station's reactors and all but 6 of its batteries off, it spares a few dozen MW.
  A ship with one battery docks and charges. A second ship with as many batteries as fit into the
  spare docks: together they want more, so the second, which wants the most, doesn't charge while
  the first keeps charging. With the station's power back on the second still doesn't charge,
  until it undocks and docks again.

    OCT_TASK=SE-972 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_station_charging.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 8 minutes.
Not covered: a cluster, where each node charges at the stations it simulates.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-972")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_support_arm as sa  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log  # noqa: E402

STATION = "Alpha"
BATTERY_MW = 12.0
WATCH = 15
BATTERIES_ON = 6
# Two of Alpha's outer connectors, with room for long ships; cells as x;y;z
RIM = ("13;0;0", "-13;0;0")


def power(a, state: str = "") -> tuple[float, float]:
    """The station's spare MW and its reactors' uranium"""
    m = re.search(
        r"spare=(\S+) uranium=(\S+)", probe(a, f"power {STATION} {state}".strip())
    )
    return (float(m[1]), float(m[2])) if m else (-1.0, -1.0)


def stored(a, ship: str) -> float:
    m = re.search(r"stored=(\S+)", probe(a, f"link {ship}"))
    return float(m[1]) if m else -1.0


def gas(a, ship: str) -> float:
    m = re.search(r"GAS \d+ (\d+)", probe(a, f"gas {ship}"))
    return float(m[1]) if m else -1.0


def docked(a, items: str) -> str:
    reply = probe(a, f"dock {STATION} mine {items}")
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"a ship at a station connector: {reply}")
    ship = ship[1] if ship else "0"
    check(st.wait_docked(a, ship), f"ship {ship} docks")
    return ship


def rate(a, ship: str, seconds: float = WATCH) -> float:
    """The ship's charging in MW over the seconds"""
    start, t0 = stored(a, ship), time.monotonic()
    time.sleep(seconds)
    end, t1 = stored(a, ship), time.monotonic()
    return (end - start) * 3600 / (t1 - t0)


def charges(a) -> None:
    """A docked ship charges at its battery's input power, and the station pays in uranium"""
    _, uranium = power(a)
    ship = docked(a, "H2=0,BAT=0,STOCKPILE")
    check(
        bool(st.wait_until(lambda: stored(a, ship) > 0, 15, 2)),
        "the docked ship's empty battery charges",
    )
    mw = rate(a, ship)
    check(
        BATTERY_MW * 0.7 < mw < BATTERY_MW * 1.3,
        f"at about the battery's {BATTERY_MW:.0f} MW input: {mw:.2f} MW",
    )
    _, after = power(a)
    check(
        0 < after < uranium,
        f"the station's reactors lose uranium: {uranium} -> {after}",
    )
    link = probe(a, f"link {ship}")
    check(
        "trading=no,yes" in link and "logical=no" in link,
        f"the connector still trades, the grids stay apart: {link}",
    )
    check(gas(a, ship) == 0, "no hydrogen from the station")
    log(probe(a, "undock"))


def fuse(a) -> None:
    """The ship that wants more than the station has to spare is cut, until it docks again"""
    power(a, f"off {BATTERIES_ON}")
    spare = st.wait_until(lambda: (s := power(a)[0]) < 100 and s, 15, 2) or -1.0
    check(
        2 * BATTERY_MW <= spare < 100,
        f"with most of its power off the station spares {spare:.1f} MW",
    )
    small = docked(a, f"BAT=0,AT={RIM[0]}")
    check(
        bool(st.wait_until(lambda: stored(a, small) > 0, 15, 2)),
        "a ship with one battery charges",
    )
    # Fits alone, but not with the first
    count = max(2, int(spare // BATTERY_MW))
    big = docked(a, f"BAT=0,BATS={count},AT={RIM[1]}")
    log(f"the second ship has {count} batteries, {count * BATTERY_MW:.0f} MW")
    time.sleep(3)
    big_mw, small_mw = rate(a, big), rate(a, small)
    check(big_mw == 0, f"the fuse cuts the ship that wants the most: {big_mw:.2f} MW")
    check(small_mw > BATTERY_MW * 0.7, f"the other keeps charging: {small_mw:.2f} MW")
    check(
        "fuse tripped for ship" in rig.server_game_log(),
        "the server log has the trip",
    )

    spare, _ = power(a, "on")
    st.wait_until(lambda: power(a)[0] > count * BATTERY_MW, 15, 2)
    big_mw = rate(a, big)
    check(
        big_mw == 0, f"with the reactors on it stays cut while docked: {big_mw:.2f} MW"
    )

    log(probe(a, f"redock {big}"))
    time.sleep(2)
    check(
        bool(
            st.wait_until(
                lambda: f"DOCKED {big} yes" in probe(a, f"docked {big}"), 60, 3
            )
        ),
        "and docks again",
    )
    time.sleep(3)
    big_mw = rate(a, big)
    check(
        big_mw > count * BATTERY_MW * 0.7,
        f"docked again it charges: {big_mw:.2f} MW",
    )
    check("fuse of ship" in rig.server_game_log(), "the server log has the reset")
    log(probe(a, "undock"))


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
        log(probe(a, f"ice {STATION} 10000"))
        charges(a)
        fuse(a)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("station charging")


if __name__ == "__main__":
    sys.exit(main())
