"""Screenshots of each station's shipyard from the same lobby spot (SE-1369): the middle of the
glass dome's back half, eye height, looking at the middle of the shipyard's build space. Runs a
server and a rendering client on a world built from a commit (OCT_SHOTS_REV, default the
working copy) and saves <station>-<label>.png into OCT_SHOTS.

    OCT_TASK=SE-1369s OCT_SHOTS=<folder> OCT_SHOTS_LABEL=before OCT_SHOTS_REV=70fcefb \\
        uv run --no-project --with httpx python tests/shipyard_view_shots.py

Without OCT_SHOTS_REV it shoots the world of this working copy (label "after"). With
OCT_SHOTS_REV the config's GridMoves are left out, unless OCT_SHOTS_MOVES=1: then the server
moves the kept world's shipyards before the client joins, as a deploy does.
"""

from __future__ import annotations

import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1369s")
REV = os.environ.get("OCT_SHOTS_REV")
if REV:
    os.environ["OCT_OLD_WORLDS"] = REV
import rig  # noqa: E402
from test_client import log  # noqa: E402

SHOTS = Path(os.environ["OCT_SHOTS"])
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
LABEL = os.environ.get("OCT_SHOTS_LABEL", "before" if REV else "after")
STATIONS = ["Alpha", "Beta", "Gamma", "Delta", "North", "South"]
# In the station blueprint's cells: standing on the lobby floor (y 6) in the middle of the dome's
# back half (check_sun_clearance.LOBBY_SPOTS), and the middle of the shipyard blueprint's build
# space (ShipyardOrders.FreeSpace)
LOBBY_FEET = (0, 6.5, 4)
BUILD_SPACE = (0, -16.5, 0)


def frames(world: Path) -> dict[str, tuple]:
    sector = ET.parse(world / "SANDBOX_0_0_0_.sbs").getroot()
    out = {}
    for e in sector.iterfind("SectorObjects/MyObjectBuilder_EntityBase"):
        where = e.find("PositionAndOrientation")
        # A station's safe zone has its name too
        if where is None or e.get(XSI_TYPE) != "MyObjectBuilder_CubeGrid":
            continue
        out[e.findtext("DisplayName")] = tuple(
            tuple(float(where.find(p).get(k)) for k in "xyz")
            for p in ("Position", "Forward", "Up")
        )
    return out


def at(frame: tuple, cell: tuple) -> tuple:
    p, f, u = frame
    r = (
        f[1] * u[2] - f[2] * u[1],
        f[2] * u[0] - f[0] * u[2],
        f[0] * u[1] - f[1] * u[0],
    )
    x, y, z = cell
    return tuple(p[i] + 2.5 * (x * r[i] + y * u[i] - z * f[i]) for i in range(3))


def main() -> int:
    SHOTS.mkdir(parents=True, exist_ok=True)
    if REV:
        from test_world_patch import old_world

        os.environ["OCT_WORLD_SOURCE"] = str(old_world())
        rig._world_source = None
    rig.stop_client()
    rig.stop_server()
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    if REV and not os.environ.get("OCT_SHOTS_MOVES"):
        # The kept world as it is: its shipyards don't move
        config = re.sub(r"\s*<GridMoves>.*</GridMoves>", "", config, flags=re.S)
    rig.write_plugin_config(config)
    rig.prepare_world()
    world = frames(rig.WORLD)
    rig.prepare_server()
    rig.prepare_client()
    rig.start_server()
    try:
        rig.start_client(render=True)
        remote = rig.api()
        rig.wait_world(remote)
        rig.ensure_character(remote)
        if remote.get_view()["thirdPerson"]:
            remote.key("V")
        for name in STATIONS:
            station = world[f"{name} Station"]
            feet = at(station, LOBBY_FEET)
            target = at(world[f"{name} Station Shipyard"], BUILD_SPACE)
            remote.character_teleport(*feet)
            time.sleep(20)
            remote.character_look_at(*target)
            time.sleep(10)
            path = SHOTS / f"{name}-{LABEL}.png"
            remote.screenshot_save(str(path), 1280, 720)
            log(f"screenshot {path}")
    finally:
        rig.stop_client()
        rig.stop_server()
    return 0


if __name__ == "__main__":
    sys.exit(main())
