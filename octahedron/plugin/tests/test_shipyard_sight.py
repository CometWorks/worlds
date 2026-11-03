"""Can people in a station's lobby see into its shipyard (SE-934) and its Trade Lanes rings
(SE-945)? Loads a world built by build_world.py on the test server with the sight probe
(tests/sight-probe), which casts rays through the game's physics from the lobby spots of
check_sun_clearance.py to the shipyard's free build space: its center, where long ships go,
the 1/3 points of short ones, and a lattice through all of it ("volume": how much of it the
spots see, "corners": how many spots see each of its corners). Then to the ring at this end
of each of the station's lanes, both tracks: its center and 8 points of its rim. Windows
don't block. No client is needed.

    OCT_TASK=SE-934 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_sight.py

Prints a line per station and per gate, and with -v what each lobby spot sees and what
blocks each ray. Passes when the center of each
equatorial station's shipyard, straight behind it, is in sight of at least half of its lobby
spots; exit code 0 = passed. The pole stations and Moon Station place their shipyards
another way (build_world.py), and are only printed.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-934")
import rig  # noqa: E402

EQUATOR = {"Alpha Station", "Beta Station", "Gamma Station", "Delta Station"}
PROBE = (
    "sight-probe",
    Path(__file__).resolve().parent / "sight-probe",
    "SightProbe.xml",
    "5C1A9E07-3D84-4B6F-A2E9-0E6C1D1554AB",
)


def main() -> int:
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[PROBE])
    rig.start_server()
    try:
        deadline = time.monotonic() + 600
        while "SIGHT DONE" not in (log := rig.server_game_log()):
            if time.monotonic() > deadline or rig.server_pid() is None:
                print("FAIL the sight probe didn't report, see", rig.SERVER_DATA)
                return 1
            time.sleep(2)
    finally:
        rig.stop_server()
    if "SIGHT ERROR" in log or "SIGHT no ShipyardOrders" in log:
        print(*re.findall(r"SIGHT (?:ERROR|no ).*", log), sep="\n")
        return 1
    if "-v" in sys.argv:
        print(*re.findall(r"SIGHT BLOCKED (.*)", log), sep="\n")
        print(*re.findall(r"SIGHT SPOT (.*)", log), sep="\n")
    for line in re.findall(r"SIGHT GATE (.*)", log):
        name, gate, *rest = line.split("|")
        print(f"     {name:14s} gate {gate:22s} {'  '.join(rest)}")
    passed = True
    stations = re.findall(r"SIGHT STATION (.*)", log)
    for line in stations:
        name, *counts = line.split("|")
        clear, spots = map(int, counts[0].split()[1].split("/"))
        ok = clear * 2 >= spots
        if name in EQUATOR:
            passed &= ok
            state = "PASS" if ok else "FAIL"
        else:
            state = "    "
        print(f"{state} {name:14s} {'  '.join(counts)}")
    passed &= len(stations) == 7
    print("PASS" if passed else "FAIL")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
