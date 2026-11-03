"""The stations' connectors trade, and no player can turn that off (SE-936). The world copy starts
with every station connector shared with everyone and not trading, the way players had left them on
Octahedron, and the plugin puts them right: trading and OCT's faction's only. A player's attempt to
turn trading off doesn't hold. A ship docks to one just the same, and its empty battery and its
empty, stockpiling H2 tank stay empty with ice in the station's gas generators.

The control, with StationConnectorsTrade off and the connectors not trading: the same ship charges
and fills, so the readings can tell. Turned back on, the plugin lets that ship go and the connector
trades again. On a world built by build_world.py.

    OCT_TASK=SE-936 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_station_connectors.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 8 minutes.
Not covered: a cluster, where each node does the same for the stations it simulates.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-936")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_support_arm as sa  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log, with_config  # noqa: E402

STATION = "Alpha"
EMPTY = "H2=0,BAT=0,STOCKPILE"
WATCH = 45
# Every station connector of the world copy, as players had turned them
VISITORS = (
    r"(Connector - Station Visitors \[No IIM\]</CustomName>.*?<TradingEnabled>)true"
)


def prepare() -> None:
    rig.prepare_world()
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text, count = re.subn(
        VISITORS, r"\1false", sector.read_text(encoding="utf-8"), flags=re.S
    )
    assert count == 56, count
    sector.write_text(text, encoding="utf-8")
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.write_plugin_config()


def connectors(a) -> list[dict]:
    return [
        {
            "grid": int(m[1]),
            "cell": tuple(int(c) for c in m[2].split(",")),
            "line": m[0],
        }
        | dict(re.findall(r"(\w+)=(\w+)", m[0]))
        for m in re.finditer(
            r"CONNECTOR (\d+) (\S+) trading=\w+ share=\w+ connected=\w+",
            probe(a, f"connectors {STATION}"),
        )
    ]


def put_right(a) -> bool:
    found = connectors(a)
    return len(found) == 8 and all(
        c["trading"] == "yes" and c["share"] == "Faction" for c in found
    )


def readings(a, ship: str) -> tuple[float, float, str]:
    """The ship's hydrogen in liters, its battery's MWh and the link line"""
    link = probe(a, f"link {ship}")
    stored = re.search(r"stored=(\S+)", link)
    gas = re.search(r"GAS \d+ (\d+)", probe(a, f"gas {ship}"))
    return float(gas[1]) if gas else -1, float(stored[1]) if stored else -1, link


def docked(a) -> str:
    reply = probe(a, f"dock {STATION} mine {EMPTY}")
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"a ship at a station connector: {reply}")
    ship = ship[1] if ship else "0"
    check(st.wait_docked(a, ship), "it docks")
    return ship


def kept(a) -> None:
    """The world's connectors put right, and a player can't turn trading off"""
    check(
        bool(st.wait_until(lambda: put_right(a), 30, 3)),
        "every connector of the station trades and is OCT's faction's only",
    )
    first = connectors(a)[0]
    try:
        a.set_property(first["grid"], first["cell"], "Trading", False)
        log("the player turned trading off")
    except Exception as e:  # noqa: BLE001 -- refused is what the check wants
        log(f"the player's change was refused: {e}")
    time.sleep(10)
    check(put_right(a), "the player's change doesn't hold")


def trading(a) -> None:
    """A docked ship stays empty: the game passes nothing through a trading connector. The
    plugin's own charging there (SE-972) is off for this, test_station_charging.py covers it.
    """
    with_config(a, StationDockCharging="false")
    ship = docked(a)
    gas, stored, link = readings(a, ship)
    check("trading=no,yes" in link, f"the station's connector trades: {link}")
    deadline = time.monotonic() + WATCH
    while time.monotonic() < deadline and gas <= 0 and stored <= 0:
        time.sleep(3)
        gas, stored, link = readings(a, ship)
    check(gas == 0, f"no hydrogen from the station in {WATCH} s: {gas:.0f} L")
    check(stored == 0, f"no power from the station in {WATCH} s: {stored:.3f} MWh")
    check(
        "logical=no" in link and "reach=no" in link,
        f"no conveyor link to the station: {link}",
    )
    log(probe(a, "undock"))
    with_config(a)


def control(a) -> None:
    """Without the rule, a connector that doesn't trade fills the same ship; with it, lets it go"""
    with_config(a, StationConnectorsTrade="false")
    log(probe(a, f"trading {STATION} off"))
    ship = docked(a)
    filled = st.wait_until(lambda: (r := readings(a, ship))[0] > 0 and r[1] > 0, 90, 3)
    gas, stored, link = readings(a, ship)
    check(
        bool(filled), f"control, not trading: {gas:.0f} L and {stored:.3f} MWh: {link}"
    )
    with_config(a)
    check(
        bool(
            st.wait_until(
                lambda: f"DOCKED {ship} no" in probe(a, f"docked {ship}"), 30, 3
            )
        ),
        "with the rule back on the ship is let go",
    )
    check(
        bool(st.wait_until(lambda: put_right(a), 30, 3)),
        "and the connectors trade again",
    )
    log(probe(a, "undock"))


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        log(probe(a, f"ice {STATION} 10000"))
        kept(a)
        trading(a)
        control(a)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("station connector")


if __name__ == "__main__":
    sys.exit(main())
