"""The shipyard queue panel's layout at a real station (SE-935): an entry wraps to two lines
with the second indented, a build shows its minutes left with the UTC time (SE-962), no line
is wider than the 46 columns, and the font is the size those columns give (0.57) or more
instead of shrinking to fit an entry on one line. With the built ship left in the build space
and an order behind it, the panel says the ship has to leave with ClearWay's deadline, and it is
moved out at that time (SE-1113). tests/panel-text has the layout of a full queue without the
game. With OCT_SHOTS=<folder> the client renders and saves a shot of the panel then.

    OCT_TASK=SE-935 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_queue_panel.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 10 minutes.
"""

from __future__ import annotations

import os
import re
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-935")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from se_remote import CallOp  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (  # noqa: E402
    CONFIG,
    STATION,
    check,
    checks,
    load,
    press,
    remove,
    settled,
)

COLUMNS = 46
QUEUE_PANEL = "Text Panel - Station ShipyardQueue"
CLEAR = 3
SHOTS = os.environ.get("OCT_SHOTS")
# A block's forward side in its grid's cells
SIDES = {
    "Forward": (0, 0, -1),
    "Backward": (0, 0, 1),
    "Left": (-1, 0, 0),
    "Right": (1, 0, 0),
    "Up": (0, 1, 0),
    "Down": (0, -1, 0),
}


def queue_panel(remote) -> dict:
    panels = remote.find_nearby_blocks(
        subtype="LargeTextPanel", max_distance=100, limit=50
    )
    return next(p for p in panels if p.get("customName") == QUEUE_PANEL)


def utc(minutes: float) -> set[str]:
    """The HH:MM of now plus minutes, a minute either way"""
    at = datetime.now(timezone.utc) + timedelta(minutes=minutes)
    return {(at + timedelta(minutes=d)).strftime("%H:%M") for d in (-1, 0, 1)}


def shoot(remote, name: str) -> None:
    """The queue panel from 2.5 m in front of its screen"""
    panel = queue_panel(remote)
    grid, at = panel["gridId"], tuple(panel["min"])
    cube = next(
        c
        for offset in range(0, 5000, 200)
        for c in remote.list_cubes(grid, offset)["cubes"]
        if tuple(c["min"]) == at
    )
    side = cube["forward"]
    side = SIDES[side] if isinstance(side, str) else side
    world = [
        remote.call([CallOp.grid_to_world(grid, c)]).call(0)["world"]
        for c in (at, tuple(a + s for a, s in zip(at, side)))
    ]
    center = panel["worldPosition"]
    # One large cell out, 2.5 m
    eye = [c + b - a for c, a, b in zip(center, *world)]
    remote.character_teleport(eye[0], eye[1] - 1.6, eye[2])
    time.sleep(3)
    remote.character_look_at(*center, tolerance=0.5)
    time.sleep(3)
    path = Path(SHOTS) / f"{name}.png"
    remote.screenshot_save(str(path), 1280, 720)
    time.sleep(2)
    log(f"screenshot {path}")


def leave(a, mark) -> None:
    """SE-1113: the built ship stays in the build space and an order comes behind it"""
    ready = tp.wait_chat(a, "is ready", 400, mark)
    check(ready is not None, f"the ship is built: {ready}")
    told = tp.wait_chat(a, "is still in", 60, mark)
    told_at = time.monotonic()
    check(
        told is not None and f"within {CLEAR} min" in told,
        f"the orderer is told the deadline: {told}",
    )
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "an order behind the ship")
    text = settled(a, "queue", "to leave")
    log(repr(text))
    lines = text.rstrip().split("\n")
    found = re.search(
        r"Waiting for '(.+?)' to leave the shipyard within (\d+) min \(by (\d\d:\d\d) UTC\)\.",
        " ".join(text.split()),
    )
    since = (time.monotonic() - told_at) / 60
    left = int(found.group(2)) if found else -1
    check(
        found is not None and found.group(1) == "Order Test",
        f"the panel says which ship has to leave: {found and found.group(0)}",
    )
    check(
        found is not None
        and abs(left - (CLEAR - since)) <= 1
        and found.group(3) in utc(CLEAR - since),
        f"it shows the time ClearWay acts at: {found and found.group(0)}, "
        f"{CLEAR - since:.1f} min left",
    )
    check(
        all(len(l) <= COLUMNS for l in lines),
        f"no line is wider than {COLUMNS}: {max(len(l) for l in lines)}",
    )
    if SHOTS:
        shoot(a, "queue-panel-leave")
    moved = tp.wait_chat(a, "was moved out", CLEAR * 60 + 60, mark)
    late = (time.monotonic() - told_at) / 60 - CLEAR
    check(
        moved is not None and -0.25 <= late <= 0.75,
        f"the ship is moved out at the deadline, {late * 60:+.0f} s: {moved}",
    )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client(render=bool(SHOTS))
        rig.ensure_character(a)
        a.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        with_config(a, **CONFIG, ShipyardClearMinutes=CLEAR)
        log(remove(a))
        settled(a, "table", "then press")
        load(a)
        settled(a, "table", "Press the button")
        mark = tp.latest(a)
        check("is in the queue" in press(a), "the order is in the queue")
        text = settled(a, "queue", "for OctTester")
        log(repr(text))
        lines = text.rstrip().split("\n")
        first = next((i for i, l in enumerate(lines) if l.startswith("1. ")), None)
        check(first is not None, "the queue panel shows the order")
        check(
            first is not None
            and first + 1 < len(lines)
            and lines[first + 1].startswith("   ")
            and "for OctTester" in " ".join(lines[first : first + 2]),
            "the entry wraps to a second, indented line",
        )
        # SE-962: a bare UTC time read like a local one, so the deadline shows the minutes left
        text = settled(a, "queue", "done within")
        lines = text.rstrip().split("\n")
        found = re.search(
            r"done within (\d+) min \(by (\d\d:\d\d) UTC\)", " ".join(text.split())
        )
        left = int(found.group(1)) if found else -1
        by = datetime.now(timezone.utc) + timedelta(minutes=left)
        check(
            found is not None
            and 58 <= left <= 60
            and found.group(2)
            in {(by + timedelta(minutes=d)).strftime("%H:%M") for d in (-1, 0, 1)},
            f"the build shows the minutes left and its UTC time: {found and found.group(0)}",
        )
        check(
            all(len(l) <= COLUMNS for l in lines),
            f"no line is wider than {COLUMNS}: {max(len(l) for l in lines)}",
        )
        panel = queue_panel(a)
        grid, at = panel["gridId"], tuple(panel["min"])
        size = a.get_property(grid, at, "FontSize")
        log(f"FontSize {size}")
        value = float(size.get("value") if isinstance(size, dict) else size)
        # The fit on the 512x307 screen: shorter lines than 46 columns get a bigger font
        fit = min(
            0.7,
            512 * 0.96 / (max(len(l) for l in lines) * 18.7),
            307.2 * 0.96 / (len(lines) * 28.8),
        )
        check(
            value >= 0.57 and abs(value - fit) < 0.002,
            f"the font size is {value:.3f}, the fit {fit:.3f}, at least the 46 columns' 0.57",
        )
        leave(a, mark)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} queue panel checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
