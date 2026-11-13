"""Screenshots of a station's shipyard booking corner (SE-1386): the button panel by the hologram
table and the billboard screen facing it, between bookings and right after one. Runs a server
and a rendering client on a world built from this working copy and saves
<station>-<label>-<shot>.png into OCT_SHOTS: wide (table, panel and billboard), panel, billboard,
and booked (the billboard 2 s after OCT_SHOTS_BOOK, by default a repair slot's booking).

    OCT_TASK=SE-1386s OCT_SHOTS=<folder> OCT_SHOTS_LABEL=after \\
        uv run --no-project --with httpx python tests/shipyard_panel_shots.py

For the shots before the change, run it from a checkout of the old commit with
OCT_SHOTS_BOOK='!oct order' (the single button's press without a blueprint).
"""

from __future__ import annotations

import os
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1386s")
import rig  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log  # noqa: E402

SHOTS = Path(os.environ["OCT_SHOTS"])
LABEL = os.environ.get("OCT_SHOTS_LABEL", "after")
BOOK = os.environ.get("OCT_SHOTS_BOOK", "!oct shipyard book repair")
STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
PANELS = ("Button Panel - Book Shipyard", "Button Panel - Station ShipyardConfirm")
BILLBOARD = "Billboard LCD Ship Build Details"


def blocks() -> dict[str, tuple[float, float, float]]:
    """The panel's and the billboard's centers in the world, by their names"""
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    station = next(
        e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.findtext("DisplayName") == f"{STATION} Station"
    )
    po = station.find("PositionAndOrientation")
    pos, fwd, up = (
        [float(po.find(t).get(a)) for a in "xyz"] for t in ("Position", "Forward", "Up")
    )
    right = [
        fwd[1] * up[2] - fwd[2] * up[1],
        fwd[2] * up[0] - fwd[0] * up[2],
        fwd[0] * up[1] - fwd[1] * up[0],
    ]
    found = {}
    for block in station.iter("MyObjectBuilder_CubeBlock"):
        name = block.findtext("CustomName") or ""
        if name in PANELS:
            name = "panel"
        elif name == BILLBOARD:
            name = "billboard"
        else:
            continue
        m = [int(block.find("Min").get(a)) * 2.5 for a in "xyz"]
        found[name] = tuple(
            pos[i] + m[0] * right[i] + m[1] * up[i] - m[2] * fwd[i] for i in range(3)
        )
    return found


def shot(remote, name: str) -> None:
    path = SHOTS / f"{STATION}-{LABEL}-{name}.png"
    remote.screenshot_save(str(path), 1280, 720)
    log(f"screenshot {path}")


def main() -> int:
    SHOTS.mkdir(parents=True, exist_ok=True)
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    where = blocks()
    log(f"blocks {where}")
    rig.prepare_server()
    rig.prepare_client()
    rig.start_server()
    try:
        rig.start_client(render=True)
        remote = rig.api()
        rig.wait_world(remote)
        rig.ensure_character(remote)
        remote.set_admin_flag("invulnerable", True)
        if remote.get_view()["thirdPerson"]:
            remote.key("V")
        sa.at_table(remote, STATION)
        time.sleep(30)
        panel, billboard = where["panel"], where["billboard"]
        middle = tuple((panel[i] + billboard[i]) / 2 for i in range(3))
        remote.character_look_at(*middle)
        time.sleep(5)
        shot(remote, "wide")
        remote.character_look_at(*panel)
        time.sleep(5)
        shot(remote, "panel")
        remote.character_look_at(*billboard)
        time.sleep(5)
        shot(remote, "billboard")
        log(f"{BOOK}: {rig.chat(remote, BOOK, 2)}")
        shot(remote, "booked")
        time.sleep(10)
        shot(remote, "later")
        # The client writes the file after the call returns
        time.sleep(10)
    finally:
        rig.stop_client()
        rig.stop_server()
    return 0


if __name__ == "__main__":
    sys.exit(main())
