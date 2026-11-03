"""OCT never restocks what players must mine (SE-982): the ores and ingots of MarketPlayerMinedMetals
(gold, silver, platinum, uranium) and the components whose blueprint needs those ingots, whatever
MarketMinStock and MarketStationStock name. Iron, in the same lists, comes back as before.

    OCT_TASK=SE-982 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_player_mined.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 5 minutes.
Not covered: the acquisition contracts, which ask for what the same filtered profile lacks.
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-982")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, stock  # noqa: E402
from test_client import log, with_config  # noqa: E402

IRON = "Ingot/Iron"
MINED = [
    ("Alpha", "Ingot/Gold"),
    ("Alpha", "Ore/Platinum"),
    ("Alpha", "Component/Thrust"),
    ("Alpha", "Component/Superconductor"),
    ("Moon", "Ingot/Uranium"),
    ("Moon", "Ingot/Silver"),
]


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()
    try:
        with_config(
            remote,
            MarketMinStock=f"{IRON}=100,Ingot/Gold=50,Ore/Platinum=20,Component/Thrust=10,"
            "Component/Superconductor=5",
            MarketStationStock="Moon Station=Ingot/Uranium=30,Ingot/Silver=5",
        )
        for station, item in [("Alpha", IRON), ("Moon", IRON)] + MINED:
            admin(remote, f"!oct market set {station} {item} 0")
        log(admin(remote, "!oct market tick force"))
        for station in ("Alpha", "Moon"):
            check(
                stock(remote, station, IRON)[0] == 100,
                f"{station}: iron comes back to its minimum",
            )
        for station, item in MINED:
            have = stock(remote, station, item)[0]
            check(have == 0, f"{station}: no {item} restocked: {have}")
        with_config(remote)
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
