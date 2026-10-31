"""Who can do what at a station's hologram table (SE-832), with two players on one server: a
blueprint on the table is only its loader's, one press queues the ship and the next cancels it,
anyone can remove someone else's blueprint, the queue limit, and what the billboard and the
table's screen show. On a world built by build_world.py, for the billboard. The presses are
!oct order, the same handler as the button's patch: see test_paid_builds.py.

    OCT_TASK=SE-832 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_table_rules.py

With OCT_SHOTS=<folder> the first client renders and saves screenshots of the two screens.
Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a cluster, and the Remove button's own click when the terminal can't be opened
headless: then !oct table remove does what its request does on the server.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-832")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log, ship_reply, tables, with_config  # noqa: E402

SHOTS = os.environ.get("OCT_SHOTS")
BILLBOARD = "Billboard LCD Ship Build Details"
FIRST_LINE = "Press the button to confirm build!"
STATION = "Alpha"
SWAPPED = (
    "PULSAR", "LAUNCHER", "APPDATA", "CLIENT_PORT", "CLIENT_ID",
    "CLIENT_NAME", "CLIENT_PID", "CLIENT_LOG",
)  # fmt: skip

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


class Second:
    """rig's client globals point at the second client inside the with"""

    values = {
        "PULSAR": rig.TEST_ROOT / f"{rig.TASK}-c1",
        "LAUNCHER": rig.TEST_ROOT / f"{rig.TASK}-c1" / "Interim.bin",
        "APPDATA": rig.TEST_ROOT / f"{rig.TASK}-c1-data",
        "CLIENT_PORT": rig.CLIENT_PORT + 1,
        "CLIENT_ID": rig.CLIENT_ID + 1,
        "CLIENT_NAME": "OctOther",
        "CLIENT_PID": rig.TEST_ROOT / f"{rig.TASK}-c1-data" / "game.pid",
        "CLIENT_LOG": rig.TEST_ROOT / f"{rig.TASK}-c1-data" / "launch.log",
    }

    def __enter__(self):
        self.saved = {k: getattr(rig, k) for k in SWAPPED}
        for k, v in self.values.items():
            setattr(rig, k, v)

    def __exit__(self, *_):
        for k, v in self.saved.items():
            setattr(rig, k, v)


def both_admins() -> None:
    cfg = rig.SERVER_DATA / "SpaceEngineers-Dedicated.cfg"
    cfg.write_text(
        cfg.read_text(encoding="utf-8").replace(
            f"<unsignedLong>{rig.CLIENT_ID}</unsignedLong>",
            f"<unsignedLong>{rig.CLIENT_ID}</unsignedLong>"
            f"<unsignedLong>{rig.CLIENT_ID + 1}</unsignedLong>",
        ),
        encoding="utf-8",
    )


def blueprint_xml(grid: str) -> str:
    return (
        '<?xml version="1.0"?><Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
        '<ShipBlueprints><ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Rules" /><CubeGrids>'
        + grid.replace("MyObjectBuilder_CubeGrid", "CubeGrid")
        + "</CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>"
    )


def table(remote) -> dict:
    return min(tables(remote), key=lambda b: b["distance"])


def load(remote) -> None:
    """Loads a blueprint the way the blueprint screen does, as this client"""
    t = table(remote)
    block = remote.get_block(t["gridId"], tuple(t["min"]))
    log(str(remote.load_projection(block["entityId"], xml=blueprint_xml(so.SMALL))))
    time.sleep(6)


def console(remote) -> str:
    t = table(remote)
    return remote.get_surface(t["gridId"], tuple(t["min"]), 1).get("text", "")


def billboard(remote, surface: int = 0) -> dict:
    t = table(remote)
    found = remote.find_nearby_blocks(
        subtype="LargeBlockBillboard", max_distance=20, limit=20
    )
    b = next(b for b in found if b["customName"] == BILLBOARD)
    assert b["gridId"] == t["gridId"]
    return remote.get_surface(b["gridId"], tuple(b["min"]), surface)


def queue_panel(remote) -> str:
    found = remote.find_nearby_blocks(
        subtype="LargeTextPanel", max_distance=20, limit=20
    )
    b = next(b for b in found if "ShipyardQueue" in b["customName"])
    return remote.get_surface(b["gridId"], tuple(b["min"]), 0).get("text", "")


def has_blueprint(remote) -> bool:
    t = table(remote)
    return bool(
        remote.get_block(t["gridId"], tuple(t["min"]))["projector"].get("isProjecting")
    )


def remove(remote) -> str:
    """Anyone's way to the table: the terminal's Remove"""
    return ship_reply(remote, "!oct table remove")


def press(remote) -> str:
    return ship_reply(remote, "!oct order")


def settled(read, want: str, timeout: float = 25.0) -> str:
    """What read() returns once want is in it, or at the timeout"""
    deadline = time.monotonic() + timeout
    while True:
        text = read()
        if want in text or time.monotonic() > deadline:
            return text
        time.sleep(2)


def rules(a, b) -> None:
    load(a)
    main = settled(lambda: console(a), "Press the button")
    check("OctTester" in main and "free build" in main, f"quote on the table: {main!r}")
    check("Press the button" in main, "the table says to press the button")
    check("Steel Plate" not in main, "no parts list on the table")
    parts = billboard(a).get("text", "")
    check(
        parts.startswith(FIRST_LINE + "\n"),
        f"billboard keeps its first line: {parts!r}",
    )
    check("Need" in parts and "Steel Plate" in parts, "billboard lists the parts")

    reply = press(b)
    check(
        "only they can" in reply and "OctTester" in reply,
        f"another player can't order: {reply}",
    )
    check(
        "No ships" in settled(lambda: queue_panel(a), "No ships"),
        "nothing queued by that",
    )

    reply = press(a)
    check("is in the queue" in reply, f"one press queues the ship: {reply}")
    main = settled(lambda: console(a), "can cancel")
    check(
        "OctTester can cancel" in main,
        f"the table offers the loader the cancel: {main!r}",
    )
    parts = settled(lambda: billboard(a).get("text", ""), "Ordered ship details")
    check(
        "Plate" not in parts and parts.startswith(FIRST_LINE),
        f"the parts list is gone: {parts!r}",
    )
    check(not has_blueprint(a), "the blueprint is off the table")

    reply = press(b)
    check(
        "load a blueprint" in reply,
        f"the other player has nothing to press for: {reply}",
    )
    check("OctTester" in queue_panel(a), "their press canceled nothing")

    load(b)
    main = settled(lambda: console(b), "OctOther")
    check(
        "OctOther" in main, f"the other player's blueprint on the free table: {main!r}"
    )
    reply = press(a)
    check(
        "only they can" in reply and "OctOther" in reply,
        f"can't cancel while another's blueprint is on it: {reply}",
    )
    check("OctTester" in queue_panel(a), "their order stays")

    log(remove(a))
    main = settled(lambda: console(a), "OctTester can cancel", 5)
    check(
        "OctTester can cancel" in main,
        f"anyone can remove a blueprint, screens back within seconds: {main!r}",
    )
    check(
        "OctOther" not in billboard(a).get("text", ""),
        "the billboard is cleared with it",
    )
    reply = press(a)
    check(
        "OctOther" not in reply and "canceled" in reply.lower(),
        f"then the same press cancels: {reply}",
    )
    check(
        "No ships" in settled(lambda: queue_panel(a), "No ships"), "the queue is empty"
    )
    main = settled(lambda: console(a), "then press")
    check("then press" in main, f"the table is idle: {main!r}")


def limit(a, b) -> None:
    with_config(a, ShipyardQueueMax=1, ShipyardTableMinutes=600)
    load(a)
    settled(lambda: console(a), "Press the button")
    check("is in the queue" in press(a), "the only place in the queue is taken")
    load(b)
    main = settled(lambda: console(b), "queue is full")
    check(
        "queue is full, 1 ships" in main, f"the table says the queue is full: {main!r}"
    )
    reply = press(b)
    check("queue is full" in reply, f"and a press doesn't queue: {reply}")
    remove(b)


def owner_leaves(a, b):
    """The queued build stays when its owner leaves, and they can cancel it when back"""
    with_config(a, ShipyardQueueMax=6, ShipyardTableMinutes=600)
    time.sleep(3)
    load(b)
    settled(lambda: console(b), "Press the button")
    check("is in the queue" in press(b), "a second place in the queue")
    rig.stop_client()
    time.sleep(20)
    panel = queue_panel(b)
    check("OctTester" in panel, f"the order stays while its owner is away: {panel!r}")
    a = rig.start_client()
    rig.ensure_character(a)
    sa.at_table(a, STATION)
    return a


def shots(a) -> None:
    Path(SHOTS).mkdir(parents=True, exist_ok=True)
    load(a)
    settled(lambda: console(a), "Press the button")
    t = table(a)
    b = next(
        b
        for b in a.find_nearby_blocks(
            subtype="LargeBlockBillboard", max_distance=20, limit=20
        )
        if b["customName"] == BILLBOARD
    )
    for name, block in (("billboard", b), ("table", t)):
        pos = block["worldPosition"]
        for suffix, offset in (("a", 3.0), ("b", -3.0)):
            a.character_teleport(pos[0] + offset, pos[1], pos[2] + offset)
            time.sleep(4)
            a.character_look_at(*pos)
            time.sleep(4)
            path = Path(SHOTS) / f"{name}-{suffix}.png"
            a.screenshot_save(str(path), 1280, 720)
            log(f"screenshot {path}")
    sa.at_table(a, STATION)


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    both_admins()
    with Second():
        folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client(render=bool(SHOTS))
        with Second():
            b = rig.start_client()
        rig.ensure_character(a)
        with Second():
            rig.ensure_character(b)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        log(remove(a))
        time.sleep(3)
        if SHOTS:
            shots(a)
            remove(a)
            time.sleep(3)
        rules(a, b)
        limit(a, b)
        a = owner_leaves(a, b)
    finally:
        rig.stop_client()
        with Second():
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
        f"{len(checks) - len(failed)}/{len(checks)} table rule checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
