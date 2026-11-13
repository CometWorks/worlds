"""OCT never restocks what players must mine (SE-982): the ores and ingots of MarketPlayerMinedMetals
(gold, silver, platinum, uranium) and the components whose blueprint needs those ingots, whatever
MarketMinStock names. Iron, in the same list, comes back as before. A station's own
MarketStationStock entry does restock one there: Moon Station tops up to 10 uranium ingots
(SE-1429), and what players sold it on top stays. OCT's acquisition contracts ask players for the
ContractAcquisitionStock items it lacks, player-mined ones included, where a station's own amount
wins.

    OCT_TASK=SE-1429 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_player_mined.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 5 minutes.
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1429")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, stock  # noqa: E402
from test_client import log  # noqa: E402
from test_contracts import contracts  # noqa: E402

IRON = "Ingot/Iron"
MINED = [
    ("Alpha", "Ingot/Gold"),
    ("Alpha", "Ore/Platinum"),
    ("Alpha", "Component/Thrust"),
    ("Alpha", "Component/Superconductor"),
]
# Moon Station's own entries, uranium as in the world's config
OWN = [("Moon", "Ingot/Uranium", 10), ("Moon", "Ingot/Silver", 5)]
URANIUM = "Ingot/Uranium"
# Short lists, so the contracts posted at the start fit into one chat reply; an hour's restock in one
# tick
OPTIONS = dict(
    MarketMinStock=f"{IRON}=100,Ingot/Gold=50,Ore/Platinum=20,Component/Thrust=10,"
    "Component/Superconductor=5",
    MarketStationStock="Moon Station=Ingot/Uranium=10,Ingot/Silver=5",
    MarketRestockMinutes=60,
    ContractAcquisitionStock="Ingot/Gold=50,Component/Thrust=10,Ingot/Uranium=1000",
)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config(rig.config_text(**OPTIONS))
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()
    try:
        for station, item in (
            [("Alpha", IRON), ("Moon", IRON)] + MINED + [(s, i) for s, i, _ in OWN]
        ):
            admin(remote, f"!oct market set {station} {item} 0")
        log(admin(remote, "!oct market tick force"))
        have = stock(remote, "Alpha", IRON)[0]
        check(have == 100, f"Alpha: iron comes back to its minimum: {have}")
        have = stock(remote, "Moon", IRON)[0]
        check(have == 0, f"Moon: iron isn't its own, so it is used, not made: {have}")
        for station, item in MINED:
            have = stock(remote, station, item)[0]
            check(have == 0, f"{station}: no {item} restocked: {have}")
        for station, item, amount in OWN:
            have = stock(remote, station, item)[0]
            check(
                have == amount,
                f"{station}: its own {item} entry restocks {amount}: {have}",
            )

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
            got is not None and got[0] == 10,
            f"Moon asks for its own 10 uranium ingots: {got}",
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
