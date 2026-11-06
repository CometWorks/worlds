"""The shipyard queue panel's layout at a real station (SE-935): a build shows how far it is,
the share of its blocks the projector counts built, and its minutes left, then whose ship it
is: "1. 53% (46 mins) OctTester 'Order Test' (99 PCU)" (SE-1117). No line is wider than the 46
columns, and the font is the size those columns give (0.57) or more instead of shrinking to fit
an entry on one line. The build has the station's limit: 240 min at the Moon Shipyard
(ShipyardStationOrderMinutes), else ShipyardOrderMinutes' 60. With the built ship left in the
build space and an order behind it, the panel says the ship has to leave with ClearWay's
deadline, and it is moved out at that time (SE-1113). tests/panel-text has the layout of a full
queue without the game. With OCT_SHOTS=<folder> the client renders and saves shots of the panel
while it builds and while it waits for the ship to leave. OCT_SHIPYARD_STATION=Moon runs it at
the Moon Shipyard.

    OCT_TASK=SE-935 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_queue_panel.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 10 minutes.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
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
    queue,
    remove,
    settled,
)

COLUMNS = 46
# A block's forward side in its grid's cells
SIDES = {
    "Forward": (0, 0, -1),
    "Backward": (0, 0, 1),
    "Left": (-1, 0, 0),
    "Right": (1, 0, 0),
    "Up": (0, 1, 0),
    "Down": (0, -1, 0),
}
# The test ship on a slab of heavy armor, so the welders take a while and the percent has steps
SHIP = so.SMALL.replace(
    "</CubeBlocks>",
    "".join(
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, -1, z))
        for x in range(-3, 4)
        for z in range(9)
    )
    + "</CubeBlocks>",
)
QUEUE_PANEL = "Text Panel - Station ShipyardQueue"
CLEAR = 3
SHOTS = os.environ.get("OCT_SHOTS")


def queue_panel(remote) -> dict:
    panels = remote.find_nearby_blocks(
        subtype="LargeTextPanel", max_distance=100, limit=50
    )
    # The station can have more than one: the one above the table is the nearest
    return min(
        (p for p in panels if p.get("customName") == QUEUE_PANEL),
        key=lambda p: p["distance"],
    )


def aim(remote, center) -> bool:
    """Turns until the crosshair is on the queue panel, trying the directions nearest the
    panel's first: a look at its position alone missed it at the Moon Shipyard"""
    pos = remote.get_character()["position"]
    to = [c - p for c, p in zip(center, pos)]
    n = 200
    sphere = []
    for i in range(n):
        z = 1 - 2 * (i + 0.5) / n
        r = math.sqrt(1 - z * z)
        sphere.append((r * math.cos(i * 2.39996), r * math.sin(i * 2.39996), z))
    sphere.sort(key=lambda d: -sum(a * b for a, b in zip(d, to)))
    for d in sphere:
        remote.character_look_at(*(p + 6 * c for p, c in zip(pos, d)), tolerance=2)
        hit = remote.get_character_target(15).get("block") or {}
        if hit.get("customName") == QUEUE_PANEL:
            return True
    return False


def shoot(remote, name: str) -> None:
    """The queue panel from 3 m in front of its screen, which faces the block's backward side,
    aimed at until the crosshair is on it"""
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
    here, front = (
        remote.call([CallOp.grid_to_world(grid, c)]).call(0)["world"]
        for c in (at, tuple(a + s for a, s in zip(at, side)))
    )
    center = panel["worldPosition"]
    log(f"panel at {center}, facing {side}")
    # The shot is what the character sees, not the view from behind it
    if remote.get_view().get("thirdPerson"):
        remote.key("V")
    # With the jetpack on and dampeners the character stays where it is put
    if not remote.get_character()["jetpack"]:
        remote.key("X")
    remote.character_teleport(
        *(c - 1.2 * (b - a) for c, a, b in zip(center, here, front))
    )
    time.sleep(3)
    log(f"crosshair on the queue panel: {aim(remote, center)}")
    time.sleep(2)
    path = Path(SHOTS) / f"{name}.png"
    remote.screenshot_save(str(path), 1280, 720)
    time.sleep(2)
    log(f"screenshot {path}")
    sa.at_table(remote, STATION)
    if remote.get_character()["jetpack"]:
        remote.key("X")


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
        r"Waiting for '(.+?)' to leave the shipyard \((\d+) mins?\)\.",
        " ".join(text.split()),
    )
    since = (time.monotonic() - told_at) / 60
    left = int(found.group(2)) if found else -1
    check(
        found is not None and found.group(1) == "Order Test",
        f"the panel says which ship has to leave: {found and found.group(0)}",
    )
    check(
        found is not None and abs(left - (CLEAR - since)) <= 1,
        f"it shows the time ClearWay acts at: {found and found.group(0)}, "
        f"{CLEAR - since:.1f} min left",
    )
    check(
        all(len(l) <= COLUMNS for l in lines),
        f"no line is wider than {COLUMNS}: {max(len(l) for l in lines)}",
    )
    font(a, lines)
    if SHOTS:
        shoot(a, "queue-panel-leave")
    moved = tp.wait_chat(a, "was moved out", CLEAR * 60 + 60, mark)
    late = (time.monotonic() - told_at) / 60 - CLEAR
    check(
        moved is not None and -0.25 <= late <= 0.75,
        f"the ship is moved out at the deadline, {late * 60:+.0f} s: {moved}",
    )


def font(a, lines: list[str]) -> None:
    """The font is the fit of the panel's text, at least the 46 columns' 0.57"""
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


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(SHIP, 60)
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
        load(a, SHIP)
        settled(a, "table", "Press the button")
        mark = tp.latest(a)
        check("is in the queue" in press(a), "the order is in the queue")
        minutes = 240 if STATION == "Moon" else 60
        told = tp.wait_chat(a, "for you now, in", 120, mark)
        check(
            told is not None and f"in {minutes} min at most" in told,
            f"{STATION}'s build runs {minutes} min at most: {told}",
        )
        building = re.compile(
            r"1\. (\d+)% \((\d+) mins?\) OctTester 'Order Test' \([\d,]+ PCU\)"
        )
        # The projector counts its blocks a moment after the build starts
        deadline = time.monotonic() + 60
        while True:
            text = queue(a)
            found = building.search(" ".join(text.split()))
            # "done within": the line before SE-1117, for the shot to compare with
            if found or "done within" in text or time.monotonic() > deadline:
                break
            time.sleep(1)
        log(repr(text))
        lines = text.rstrip().split("\n")
        if SHOTS and not found:
            shoot(a, "queue-panel-build")
        left = int(found.group(2)) if found else -1
        check(
            found is not None and minutes - 2 <= left <= minutes,
            f"the build shows its percent and {minutes} min left: {found and found.group(0)}",
        )
        check(
            all(len(l) <= COLUMNS for l in lines),
            f"no line is wider than {COLUMNS}: {max(len(l) for l in lines)}",
        )
        # The percent grows with the welders until the build ends
        seen = []
        shot = False
        while found and len(seen) < 300:
            seen.append(int(found.group(1)))
            if SHOTS and not shot and 0 < seen[-1] < 100:
                shoot(a, "queue-panel-build")
                shot = True
            time.sleep(2)
            found = building.search(" ".join(queue(a).split()))
        log(f"percents seen: {seen}")
        check(
            len(seen) > 0 and all(0 <= p <= 100 for p in seen) and seen == sorted(seen)
            # The shots take 16 s, in which a fast shipyard can finish the ship
            and (len(set(seen)) >= 2 or 0 < seen[0] < 100),
            f"the percent goes up from 0 to 100: {seen}",
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
