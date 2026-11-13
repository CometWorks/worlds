"""OCT never restocks what players must mine (SE-982): the ores and ingots of MarketPlayerMinedMetals
(gold, silver, platinum, uranium) and the components whose blueprint needs those ingots, whatever
MarketMinStock names. Iron, in the same list, comes back as before. A station's own
MarketStationStock entry does restock one there, and the world's config has just one: Moon Station
tops up to 10 uranium ingots (SE-1429), and what players sold it on top stays. No other station
tops up any gold, silver, platinum or uranium ore or ingot. MarketTargetStock sets their target
stock at every station: below it their prices are higher, and acquisitions ask for the rest. It
wins over the top-up amount, so Moon asks for its uranium ingots up to the target, not up to 10.

    OCT_TASK=SE-1429 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_player_mined.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 5 minutes.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-1429")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, stock  # noqa: E402
from test_client import log  # noqa: E402
from test_contracts import contracts  # noqa: E402

IRON = "Ingot/Iron"
STATIONS = ["Alpha", "Beta", "Gamma", "Delta", "North", "South", "Moon"]
RARE = [
    f"{kind}/{metal}"
    for kind in ("Ore", "Ingot")
    for metal in ("Gold", "Silver", "Platinum", "Uranium")
]
COMPONENTS = ["Component/Thrust", "Component/Superconductor"]
URANIUM = "Ingot/Uranium"
# The one top-up of a player-mined item the world's config may have
MOON_URANIUM = ("Moon Station", URANIUM)
# Short lists, so the contracts posted at the start fit into one chat reply; an hour's restock in one
# tick
OPTIONS = dict(
    MarketMinStock=f"{IRON}=100,"
    + ",".join(f"{item}=50" for item in RARE + COMPONENTS),
    # The world's config's player-mined entries, which check_config makes sure of
    MarketStationStock="Moon Station=Ingot/Uranium=10",
    MarketRestockMinutes=60,
    MarketTargetStock="Ingot/Gold=50,Component/Thrust=10,Ingot/Uranium=1000",
)


def check_config() -> None:
    """The world's config tops up no player-mined ore or ingot but Moon's uranium ingots"""
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    entries = []
    for option in ("MarketMinStock", "MarketStationStock"):
        value = re.search(rf"<{option}>([^<]*)</{option}>", text)[1]
        for part in value.split(";"):
            station, _, items = part.rpartition("Station=")
            station = (station + "Station") if station else "every station"
            for entry in items.split(","):
                item, _, amount = entry.strip().partition("=")
                if item in RARE:
                    entries.append(((station.strip(), item), amount))
    log(f"player-mined top-ups in the config: {entries}")
    check(
        [e[0] for e in entries] == [MOON_URANIUM] and entries[0][1] == "10",
        f"the config tops up only Moon's uranium ingots, 10: {entries}",
    )


def main() -> int:
    check_config()
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config(rig.config_text(**OPTIONS))
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()
    try:
        cleared = [("Alpha", IRON), ("Moon", IRON)] + [
            (s, i) for s in STATIONS for i in RARE + COMPONENTS
        ]
        for station, item in cleared:
            admin(remote, f"!oct market set {station} {item} 0")
        log(admin(remote, "!oct market tick force"))
        have = stock(remote, "Alpha", IRON)[0]
        check(have == 100, f"Alpha: iron comes back to its minimum: {have}")
        have = stock(remote, "Moon", IRON)[0]
        check(have == 0, f"Moon: iron isn't its own, so it is used, not made: {have}")
        for station in STATIONS:
            for item in RARE + COMPONENTS:
                have = stock(remote, station, item)[0]
                if (f"{station} Station", item) == MOON_URANIUM:
                    check(
                        have == 10, f"Moon: its uranium ingots come back to 10: {have}"
                    )
                else:
                    check(have == 0, f"{station}: no {item} restocked: {have}")

        # OCT tops up to 10 and stops; what players sold on top stays, and the Store still buys
        admin(remote, f"!oct market set Moon {URANIUM} 4")
        admin(remote, "!oct market tick force")
        have = stock(remote, "Moon", URANIUM)[0]
        check(have == 10, f"Moon's uranium topped up from 4 to 10: {have}")
        admin(remote, f"!oct market set Moon {URANIUM} 1000")
        admin(remote, "!oct market tick force")
        have, _, _, wanted = stock(remote, "Moon", URANIUM)
        check(have == 1000, f"1000 uranium ingots players sold stay at Moon: {have}")
        check(wanted > 0, f"Moon's Store still buys uranium: {wanted}")

        # Below the target an item sells for more, at the target for its normal price
        gold = "Ingot/Gold"
        empty = stock(remote, "Alpha", gold)[1]
        admin(remote, f"!oct market set Alpha {gold} 50")
        full = stock(remote, "Alpha", gold)[1]
        admin(remote, f"!oct market set Alpha {gold} 0")
        check(
            empty > full * 1.2,
            f"Alpha's gold ingots cost more out of stock than at the target: {empty} vs {full}",
        )

        # Acquisitions of what players must mine, most posted at the start with the stock empty;
        # Moon's uranium only up to its own 10
        admin(remote, f"!oct market set Moon {URANIUM} 0")
        found = contracts(remote, post=True)
        found.pop("others")
        asked = {
            station: {
                c[4].rsplit(" ", 1)[0]: (float(c[4].rsplit(" ", 1)[1]), c[3])
                for c in found.get(station, [])
                if c[1] == "acquisition"
            }
            for station in ("Alpha Station", "Moon Station")
        }
        for station, items in asked.items():
            log(f"{station} acquisitions: {items}")
            for item, amount in (("Ingot/Gold", 50), ("Component/Thrust", 10)):
                got = items.get(item)
                check(
                    got is not None and got[0] == amount and got[1] > 0,
                    f"{station}: an acquisition of {amount} {item}: {got}",
                )
        got = asked["Alpha Station"].get(URANIUM)
        check(
            got is not None and got[0] == 1000,
            f"Alpha asks for 1000 uranium ingots: {got}",
        )
        got = asked["Moon Station"].get(URANIUM)
        check(
            got is not None and got[0] >= 990,
            f"Moon asks for its 1000 target uranium ingots, less the 10 top-up it may hold, not just 10: {got}",
        )
    finally:
        rig.stop_client()
        rig.stop_server()
    lines = [l for l in rig.server_game_log().splitlines() if "players must mine" in l]
    listed = lines[-1] if lines else ""
    log(listed.strip())
    for item in (
        "Ingot/Gold",
        "Ore/Uranium",
        "Component/Thrust",
        "Component/Superconductor",
    ):
        check(item in listed, f"the log lists {item}")
    for item in (
        "Component/SteelPlate",
        "Component/Construction",
        "Component/Computer",
    ):
        check(item not in listed, f"but not {item}")
    errors = [
        l
        for l in rig.server_game_log().splitlines()
        if "Octahedron" in l and ("xception" in l or "Critical" in l)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("player-mined")


if __name__ == "__main__":
    sys.exit(main())
