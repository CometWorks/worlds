"""The table's button and a player's own running build or repair slot (SE-1127), with the
headless client at one station's table, the free-build cool-down at 60 min:

- A press without a blueprint books a repair slot. With a blueprint loaded during the slot,
  the table shows no "cancel it first", and one press ends the slot and orders the
  blueprint, a free build.
- The blueprint loaded again while it builds, as for a retry: the table asks for two presses.
  One press only asks, saying how far the build is, and the build keeps running; a second
  press after the 10 s asks again; two presses within 10 s cancel it. The canceled free
  build's ship is removed.
- The canceled free build doesn't count: the same blueprint quotes free and orders as a free
  build again.

On a world built by build_world.py. The presses are !oct order: see test_table_rules.py.

    OCT_TASK=SE-1127 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_cancel_confirm.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1127")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_repair_slot import building, slot_runs  # noqa: E402
from test_table_rules import check, checks, load, press, settled, wait_for  # noqa: E402

STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
CONFIG = {"FreeBuildCooldownMinutes": 60, "ShipyardTableMinutes": 600}
SHIP = "Order Big"
COOLDOWN = "next free ship is in"


def pressed(remote, *texts: str) -> list[str]:
    """Presses, and returns for each text the reply or chat line with it, "" if none came: a
    press can answer in chat a few seconds later"""
    mark = tp.latest(remote)
    reply = press(remote)
    return [
        reply if text in reply else tp.wait_chat(remote, text, 15, mark) or ""
        for text in texts
    ]


def shipyards(remote) -> str:
    return " ".join(rig.chat(remote, "!oct shipyards", 3))


def running(remote) -> bool:
    """The shipyard runs the player's build"""
    return f"'{SHIP}'" in shipyards(remote)


def ships(remote) -> list[dict]:
    return [g for g in remote.list_grids() if g.get("name") == SHIP]


def repair_then_blueprint(a) -> None:
    reply = press(a)
    check("Repair slot" in reply, f"a press without a blueprint books a slot: {reply}")
    check(wait_for(lambda: slot_runs(a) is not None, 30), "the slot runs")
    load(a, so.BIG)
    table = settled(a, "table", f"'{SHIP}' for")
    check("cancel it first" not in table, f"the table doesn't ask to cancel: {table!r}")
    ended, ordered = pressed(a, "you canceled it", "is in the queue, a free build")
    check(bool(ended), f"the press ends the slot: {ended}")
    check(bool(ordered), f"and the same press orders the blueprint: {ordered}")
    if not wait_for(lambda: running(a), 60, 5):
        # Before SE-1127 the press only ended the slot: the next one orders, to go on
        log(f"no build yet, another press: {press(a)}")
    check(wait_for(lambda: running(a), 60, 5), f"'{SHIP}' is built")


def two_presses(a) -> None:
    check(wait_for(lambda: building(a), 180, 5), "the ship has a complete block")
    load(a, so.BIG)
    # The screen's text wraps
    table = " ".join(settled(a, "table", "twice").split())
    check(
        "Press the button twice" in table, f"the table asks for two presses: {table!r}"
    )
    reply = press(a)
    check(
        "within 10 s to cancel it" in reply and "blocks complete" in reply,
        f"one press asks, with the blocks done: {reply}",
    )
    check("won't count" in reply, f"and says the free build won't count: {reply}")
    time.sleep(3)
    check(running(a), "the build goes on after one press")
    time.sleep(10)
    reply = press(a)
    check("within 10 s" in reply, f"a press after 10 s asks again: {reply}")
    check(running(a), "the build goes on")
    # The table's projection of the blueprint is a grid of that name too
    shown = {g["entityId"] for g in ships(a)}
    (reply,) = pressed(a, f"You canceled '{SHIP}'")
    check(bool(reply), f"two presses within 10 s cancel: {reply}")
    check(
        "the free build doesn't count" in reply,
        f"the canceled free build doesn't count: {reply}",
    )
    left = wait_for(lambda: [g for g in ships(a) if g["entityId"] not in shown], 20, 2)
    heads = [int(n) for n in re.findall(r"head (\d+) blocks", shipyards(a))]
    check(
        not left,
        f"the canceled free build's ship is removed: {left}, arm heads {heads}",
    )


def free_again(a) -> None:
    load(a, so.BIG)
    table = settled(a, "table", "free build")
    check(
        "free build" in table and COOLDOWN not in table,
        f"the blueprint quotes free again: {table!r}",
    )
    reply = press(a)
    check(
        "is in the queue, a free build" in reply,
        f"and orders as a free build: {reply}",
    )
    check(wait_for(lambda: running(a), 60, 5), "it builds")
    press(a)
    (reply,) = pressed(a, f"You canceled '{SHIP}'")
    check(bool(reply), f"canceled at the end of the test: {reply}")


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
        a = rig.start_client()
        rig.ensure_character(a)
        # The run outlasts the suit's oxygen and battery in an unpressurized station
        a.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        with_config(a, **CONFIG)
        log(tr.remove(a))
        settled(a, "table", "then press")
        repair_then_blueprint(a)
        two_presses(a)
        free_again(a)
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
    print(f"{len(checks) - len(failed)}/{len(checks)} cancel checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
