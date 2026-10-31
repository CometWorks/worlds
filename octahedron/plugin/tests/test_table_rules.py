"""Who can do what at a station's hologram table (SE-832, SE-850), with two players on one
server: a player who loads a blueprint holds the table, nobody else can clear or replace it, 3
minutes after the last load it is cleared and open again; a press means what the presser has,
their blueprint or their build, whoever holds the table; the queue limit, and what the billboard
and the table's screen show. On a world built by build_world.py, for the billboard. The presses
are !oct order, the same handler as the button's patch: see test_paid_builds.py.
tests/test_repair_slot.py has what happens in a repair slot.

    OCT_TASK=SE-850 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_table_rules.py

With OCT_SHOTS=<folder> the first client renders and saves screenshots of the two screens.
Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a cluster, and the terminal's own Remove button: a headless client can't open
the terminal, so !oct table remove asks the server what the button asks it, and the game's own
Access and Ownership validation of the two events is only seen from the client's side, where
Remote refuses the load as the terminal would.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-850")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from se_remote import CallOp  # noqa: E402
from test_client import log, ship_reply, tables, with_config  # noqa: E402

SHOTS = os.environ.get("OCT_SHOTS")
FIRST_LINE = "Press the button to confirm build!"
STATION = "Alpha"
CONFIG = {"FreeBuildCooldownMinutes": 0, "ShipyardTableMinutes": 600}
OTHER = so.SMALL.replace("Order Test", "Order Other")
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


def load(remote, grid: str = so.SMALL) -> None:
    """Loads a blueprint the way the blueprint screen does, as this client"""
    t = table(remote)
    block = remote.get_block(t["gridId"], tuple(t["min"]))
    log(str(remote.load_projection(block["entityId"], xml=blueprint_xml(grid))))
    time.sleep(6)


def owner(remote) -> tuple[str, str]:
    """The table's owner and share mode, as this client sees them"""
    t = table(remote)
    block = remote.get_block(t["gridId"], tuple(t["min"]))
    xml = remote.get_object_builder(block["entityId"])
    found = re.search(r"<Owner>(\d+)</Owner>", xml)
    share = re.search(r"<ShareMode>(\w+)</ShareMode>", xml)
    return found[1] if found else "0", share[1] if share else "None"


def settled_owner(remote, want, timeout: float = 15.0) -> tuple[str, str]:
    """The client's view once want says it's right, or at the timeout: the event takes a moment"""
    deadline = time.monotonic() + timeout
    while True:
        seen = owner(remote)
        if want(seen) or time.monotonic() > deadline:
            return seen
        time.sleep(1)


def screens(remote) -> dict[str, str]:
    """The server's text of the table's screen, the billboard's two and the queue panel"""
    reply = "\n".join(rig.chat(remote, "!oct table show", 2))
    found = re.search(
        r"BLUEPRINT (.*?) HOLDER (.*?)\nTABLE\n(.*)\nDETAILS\n(.*)\nHOW\n(.*)\nQUEUE\n(.*)",
        reply,
        re.S,
    )
    assert found, reply
    return dict(
        zip(("blueprint", "holder", "table", "details", "how", "queue"), found.groups())
    )


def settled(remote, screen: str, want: str, timeout: float = 25.0) -> str:
    """The screen's text once want is in it, or at the timeout"""
    deadline = time.monotonic() + timeout
    while True:
        text = screens(remote)[screen]
        if want in text or time.monotonic() > deadline:
            return text
        time.sleep(2)


def queue(remote) -> str:
    return screens(remote)["queue"]


def remove(remote) -> str:
    """Anyone's way to the table: the terminal's Remove"""
    return ship_reply(remote, "!oct table remove")


def press(remote) -> str:
    return ship_reply(remote, "!oct order")


def first_order(a, b, oct_owner: str) -> None:
    """The screens and the presses of a build that starts at once; its ship stays in the
    shipyard, so the later orders wait"""
    load(a)
    main = settled(a, "table", "Press the button")
    check("OctTester" in main and "free build" in main, f"quote on the table: {main!r}")
    check("Press the button" in main, "the table says to press the button")
    check("Steel Plate" not in main, "no parts list on the table")
    shown = screens(a)
    check(shown["holder"] == "OctTester", f"the loader holds it: {shown['holder']}")
    parts = settled(a, "details", "Steel Plate")
    check(
        parts.startswith(FIRST_LINE + "\n"),
        f"billboard keeps its first line: {parts!r}",
    )
    check("Need" in parts and "Steel Plate" in parts, "billboard lists the parts")
    check("Order Test" in parts, "billboard names the ship")
    check(
        "a repair time slot" in shown["how"] and "most 6 entries" in shown["how"],
        f"the how-to screen shows the blueprint's text: {shown['how']!r}",
    )
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    check(
        sector.count("=== Octahedron Credits ===") == 7,
        "the credits panel's text is on all 7 stations",
    )
    seen = settled_owner(b, lambda s: s != (oct_owner, "All"))
    check(
        seen[0] != oct_owner and seen[1] == "None",
        f"the other client sees the table the loader's, not shared: {seen}",
    )

    try:
        log(str(load(b, OTHER)))
    except Exception as e:  # noqa: BLE001 -- Remote refuses it as the terminal would
        log(f"the other player's load: {e}")
    time.sleep(3)
    shown = screens(a)
    check(
        shown["holder"] == "OctTester" and "Order Test" in shown["details"],
        f"another player can't load over it: {shown['holder']}, {shown['details']!r}",
    )
    reply = remove(b)
    check(
        "holds this hologram table" in reply, f"another player can't clear it: {reply}"
    )
    check(screens(a)["blueprint"] == "True", "the blueprint stays on the table")

    reply = press(b)
    check("Repair slot" in reply, f"their press books a repair slot, no order: {reply}")
    text = settled(a, "queue", "Repair for OctOther")
    check("Repair for OctOther" in text, f"the slot is on the queue panel: {text!r}")
    shown = screens(a)
    check(
        shown["holder"] == "OctTester"
        and "Order Test" in shown["details"]
        and "Press the button" in shown["table"],
        f"the slot leaves the table alone: {shown['holder']}, {shown['table']!r}",
    )
    reply = press(b)
    check("you canceled it" in reply, f"their next press cancels the slot: {reply}")
    text = settled(a, "queue", "No ships in the queue")
    check("Repair" not in text, f"the slot is off the panel: {text!r}")

    reply = press(a)
    check("is in the queue" in reply, f"one press queues the ship: {reply}")
    parts = settled(a, "details", "Ordered ship details")
    check(
        "Steel Plate" not in parts and parts.startswith(FIRST_LINE),
        f"the parts list is gone: {parts!r}",
    )
    shown = screens(a)
    check(shown["blueprint"] == "False", "the blueprint is off the table")
    check(shown["holder"] == "none", f"the table is open again: {shown['holder']}")
    seen = settled_owner(b, lambda s: s == (oct_owner, "All"))
    check(seen == (oct_owner, "All"), f"OCT's and shared with all again: {seen}")

    ready = so.wait_chat(a, "is ready", 400)
    log(ready)
    main = settled(a, "table", "then press")
    check("then press" in main, f"the table is idle again: {main!r}")


def rules(a, b) -> None:
    load(a)
    settled(a, "table", "Press the button")
    reply = press(a)
    check("is in the queue" in reply, f"one press queues behind the ship: {reply}")
    main = settled(a, "table", "can cancel")
    check("OctTester can cancel" in main, f"the table offers the cancel: {main!r}")

    reply = press(b)
    check("Repair slot" in reply, f"an empty table's press books a slot: {reply}")
    text = settled(a, "queue", "Repair for OctOther")
    check(
        "Order Test" in text and "Repair for OctOther: waiting" in text,
        f"the slot waits behind the order: {text!r}",
    )
    reply = press(b)
    check("left the queue" in reply, f"their next press cancels the slot: {reply}")
    check("Order Test" in queue(a), "and nothing else")

    load(b)
    main = settled(b, "table", "OctOther")
    check("OctOther" in main, f"the other player's blueprint on the table: {main!r}")
    check("Press the button" in main, f"their build is on offer: {main!r}")
    reply = press(a)
    check(
        "left the queue" in reply,
        f"a build is canceled while someone else uses the table: {reply}",
    )
    check("OctTester" not in queue(a), "their order is gone")
    shown = screens(a)
    check(
        shown["holder"] == "OctOther"
        and shown["blueprint"] == "True"
        and "OctOther" in shown["table"]
        and "Press the button" in shown["table"]
        and "Steel Plate" in shown["details"],
        f"without touching their table: {shown['holder']}, {shown['table']!r}",
    )

    reply = press(b)
    check("is in the queue" in reply, f"the holder's press orders: {reply}")
    check(screens(a)["holder"] == "none", "which opens the table")
    reply = press(b)
    check("left the queue" in reply, f"and their next press cancels it: {reply}")
    main = settled(a, "table", "then press")
    check("then press" in main, f"the table is idle: {main!r}")

    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "a queued order")
    load(a)
    main = settled(a, "table", "cancel it first")
    check("cancel it first" in main, f"a new blueprint warns of it: {main!r}")
    check("left the queue" in press(a), "the press cancels the old order first")
    check(screens(a)["blueprint"] == "True", "the new blueprint stays")
    main = settled(a, "table", "Press the button to confirm")
    check("confirm" in main, f"then the new blueprint's on offer: {main!r}")
    remove(a)

    with_config(a, **CONFIG, GroupPcuLimit=1)
    load(a)
    reply = press(a)
    check(
        "PCU" in reply and "Repair" not in reply,
        f"a refused blueprint's press says why, it books nothing: {reply}",
    )
    check("Repair" not in queue(a), "no slot in the queue")
    with_config(a, **CONFIG)
    remove(a)


def timeout(a, b, oct_owner: str) -> None:
    """The real 3 minutes, the default: another load restarts them, then the table is open"""
    with_config(a, FreeBuildCooldownMinutes=0)
    load(a)
    first = time.monotonic()
    check(screens(a)["holder"] == "OctTester", "the player holds the table")
    time.sleep(max(0, first + 120 - time.monotonic()))
    load(a)
    last = time.monotonic()
    time.sleep(max(0, first + 205 - time.monotonic()))
    check(
        screens(a)["holder"] == "OctTester",
        "another load restarted the time: still held past the first 3 minutes",
    )
    time.sleep(max(0, last + 165 - time.monotonic()))
    check(
        screens(a)["blueprint"] == "True",
        "the blueprint is there shortly before 3 minutes",
    )
    deadline = last + 200
    while time.monotonic() < deadline and screens(a)["blueprint"] == "True":
        time.sleep(2)
    took = time.monotonic() - last
    shown = screens(a)
    check(
        shown["blueprint"] == "False" and shown["holder"] == "none",
        f"cleared and released {took:.0f} s after the last load: {shown['holder']}",
    )
    check(170 <= took <= 200, f"after the 3 minutes: {took:.0f} s")
    seen = settled_owner(b, lambda s: s == (oct_owner, "All"))
    check(seen == (oct_owner, "All"), f"OCT's and shared with all again: {seen}")
    main = settled(a, "table", "then press")
    check("then press" in main, f"the table's screen is idle: {main!r}")
    load(b, OTHER)
    shown = screens(a)
    check(shown["holder"] == "OctOther", f"anyone can load now: {shown['holder']}")
    remove(b)
    with_config(a, **CONFIG)


def limit(a, b) -> None:
    with_config(a, **CONFIG, ShipyardQueueMax=1)
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "the only place in the queue is taken")
    load(b)
    main = " ".join(settled(b, "table", "1 ships").split())
    check("queue is full, 1 ships" in main, f"the table says it is full: {main!r}")
    reply = press(b)
    check("queue is full" in reply, f"and a press doesn't queue: {reply}")
    remove(b)
    reply = press(b)
    check("queue is full" in reply, f"nor book a repair slot: {reply}")
    with_config(a, **CONFIG)


def owner_leaves(a, b, ship: int):
    """a's order from limit() is waiting. It stays when they leave and is built when its
    turn comes."""
    rig.stop_client()
    time.sleep(20)
    check("OctTester" in queue(b), "the order stays while its owner is away")
    b.close_grid(ship)
    log(f"closed the ship in the shipyard, {ship}")
    deadline = time.monotonic() + 420
    built = False
    while time.monotonic() < deadline and not built:
        time.sleep(15)
        text = queue(b)
        built = "OctTester" not in text
    check(built, f"the build ran and ended with its owner away: {text}")
    a = rig.start_client()
    rig.ensure_character(a)
    sa.at_table(a, STATION)
    mine = [
        g
        for g in a.list_grids()
        if g.get("name") == "Order Test" and g["entityId"] != ship
    ]
    log(str(mine))
    check(bool(mine), "the ship is there when they are back")
    return a


def shoot(remote, name: str, grid: int, stand, look) -> None:
    """A first person screenshot of the cell look from the cell stand"""
    at = remote.call(
        [CallOp.grid_to_world(grid, stand), CallOp.grid_to_world(grid, look)]
    )
    world = [c.get("world") or c.get("position") for c in (at.call(0), at.call(1))]
    remote.character_teleport(*world[0])
    time.sleep(3)
    remote.character_look_at(*world[1], tolerance=0.2)
    time.sleep(4)
    path = Path(SHOTS) / f"{name}.png"
    remote.screenshot_save(str(path), 1280, 720)
    log(f"screenshot {path}")


def shots(a, name: str) -> None:
    """The billboard from both sides, the table's screen and the queue panel"""
    Path(SHOTS).mkdir(parents=True, exist_ok=True)
    for _ in range(4):
        if a.get_view().get("firstPerson"):
            break
        a.key("V")
        time.sleep(1)
    t = table(a)
    x, y, z = t["min"]
    grid = t["gridId"]
    # The console is at (3, 7, 0), its button at x + 1, the billboard at x + 2, facing along x
    shoot(a, f"{name}-billboard-1", grid, (x + 3, y, z), (x + 2, y, z))
    shoot(a, f"{name}-billboard-2", grid, (x + 1, y, z + 1), (x + 2, y, z))
    shoot(a, f"{name}-table", grid, (x, y, z + 1), (x, y, z))
    shoot(a, f"{name}-queue", grid, (x + 1, y, z + 2), (x + 1, y + 1, z))
    # The panels' pillar: the credits look toward -z, the leaderboard toward +x
    shoot(a, f"{name}-credits", grid, (-4, y, 2), (-4, y, 4))
    shoot(a, f"{name}-leaderboard", grid, (-3, y, 3), (-4, y, 5))
    sa.at_table(a, STATION)


def take_shots(a) -> None:
    load(a)
    settled(a, "details", "Steel Plate")
    shots(a, "free")
    with_config(a, **CONFIG, FreeBuildPcuLimit=0)
    settled(a, "table", "paid")
    time.sleep(12)
    shots(a, "paid")
    with_config(a, **CONFIG)
    remove(a)
    time.sleep(3)
    settled(a, "table", "then press")
    shots(a, "idle")


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
        with_config(a, **CONFIG)
        log(remove(a))
        time.sleep(3)
        oct_owner = owner(a)[0]
        log(f"OCT is {oct_owner}")
        if SHOTS:
            take_shots(a)
        timeout(a, b, oct_owner)
        first_order(a, b, oct_owner)
        built = [g for g in a.list_grids() if g.get("name") == "Order Test"]
        log(f"the ship in the shipyard: {built}")
        rules(a, b)
        limit(a, b)
        a = owner_leaves(a, b, built[0]["entityId"])
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
