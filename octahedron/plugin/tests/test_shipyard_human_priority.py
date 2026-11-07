"""Human players' shipyard orders before the bots' (SE-1207). Two clients at one station's table:
the first a human player, the second a bot by ShipyardBotSteamIds. The free-build cool-down is at
60 min for the first part, so a bumped free build that counted would show:

- The bot's free build is running when the human player orders: the bot's build is canceled,
  its ship removed, and the bot is told. The human player's order builds. The canceled free
  build doesn't count: the bot's blueprint quotes free again and orders, behind the human's.
- The queue at 1 with the bot's order waiting behind a finished ship that hasn't left: the human
  player's order pushes it past the limit, and the bot's order is canceled and the bot told.
- The queue at 1 with the human player's order: the bot's order is refused, and so is another
  human player's (the second client with ShipyardBotSteamIds empty). Nothing is canceled.

On a world built by build_world.py. The presses are !oct order: see test_table_rules.py.

    OCT_TASK=SE-1207 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_human_priority.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 15 minutes.
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1207")
import rig  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_repair_slot import building  # noqa: E402
from test_table_rules import (  # noqa: E402
    OTHER,
    Second,
    check,
    checks,
    load,
    press,
    settled,
    wait_for,
)

STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
BOT = rig.CLIENT_ID + 1
CONFIG = {
    "FreeBuildCooldownMinutes": 60,
    "ShipyardTableMinutes": 600,
    "ShipyardClearMinutes": 30,
    "ShipyardBotSteamIds": BOT,
}
BIG = "Order Big"
SMALL = "Order Other"
BUMPED = "a player's order took its place"
COOLDOWN = "next free ship is in"


def config(a, **values) -> None:
    # with_config starts from the world's config each time
    with_config(a, **{**CONFIG, **values})


def shipyards(a) -> str:
    return " ".join(rig.chat(a, "!oct shipyards", 3))


def ships(a, name: str) -> list[dict]:
    return [g for g in a.list_grids() if g.get("name") == name]


def order(remote, grid: str, want: str = "is in the queue") -> str:
    load(remote, grid)
    settled(remote, "table", "free build")
    reply = press(remote)
    if "Your quote for" in reply:
        # The first press may only show the quote
        reply = press(remote)
    check(want in reply, f"ordered, '{want}': {reply}")
    return reply


def bot_build_bumped(a, b) -> None:
    """The bot's running free build makes way for the human player's order"""
    with Second():
        order(b, so.BIG)
    check(wait_for(lambda: building(a), 180, 5), "the bot's build has a complete block")
    check(f"'{BIG}'" in shipyards(a), "the bot's build runs")
    with Second():
        mark = tp.latest(b)
    order(a, OTHER)
    with Second():
        told = tp.wait_chat(b, BUMPED, 40, mark)
    check(told is not None, f"the bot is told its build was canceled: {told}")
    check(
        "the free build doesn't count" in (told or ""),
        f"and that the free build doesn't count: {told}",
    )
    gone = wait_for(lambda: not ships(a, BIG), 30, 2)
    check(gone, f"the bot's ship is removed: {ships(a, BIG)}")
    check(
        wait_for(lambda: f"'{SMALL}'" in shipyards(a), 60, 5),
        f"the human player's order builds: {shipyards(a)}",
    )
    # Not counted: the bot's blueprint is free again, and queues behind the human player's
    with Second():
        load(b, so.BIG)
        table = settled(b, "table", "free build")
        check(
            "free build" in table and COOLDOWN not in table,
            f"the bot's blueprint quotes free again: {table!r}",
        )
        reply = press(b)
        if "Your quote for" in reply:
            reply = press(b)
        check("a free build" in reply, f"the bot orders again: {reply}")
    queue = tr.queue(a)
    check(
        -1 < queue.find(SMALL) < queue.find(BIG),
        f"the bot's order is behind the human player's: {queue!r}",
    )
    # The bot's order then waits for the human player's ship to leave the shipyard
    check(
        wait_for(lambda: ships(a, SMALL) and f"'{SMALL}'" not in shipyards(a), 120, 5),
        "the human player's ship is finished",
    )


def bot_pushed_out(a, b) -> None:
    """A queue of 1 holding the bot's waiting order: the human player's order takes its place"""
    config(a, FreeBuildCooldownMinutes=0, ShipyardQueueMax=1)
    check(BIG in tr.queue(a), f"the bot's order waits: {tr.queue(a)!r}")
    with Second():
        mark = tp.latest(b)
    order(a, OTHER)
    with Second():
        told = tp.wait_chat(b, BUMPED, 40, mark)
    check(told is not None, f"the bot is told its order was canceled: {told}")
    queue = wait_for(lambda: BIG not in tr.queue(a) and tr.queue(a), 30, 3) or ""
    check(
        SMALL in queue and BIG not in queue,
        f"the human player's order alone in the queue: {queue!r}",
    )


def humans_full(a, b) -> None:
    """A queue of 1 holding the human player's order refuses the bot's and another human's"""
    with Second():
        order(b, so.BIG, "queue is full")
    config(a, FreeBuildCooldownMinutes=0, ShipyardQueueMax=1, ShipyardBotSteamIds="")
    with Second():
        reply = press(b)
        if "Your quote for" in reply:
            reply = press(b)
    check("queue is full" in reply, f"another human player's order is refused: {reply}")
    queue = tr.queue(a)
    check(
        SMALL in queue and BIG not in queue,
        f"nothing canceled, nothing added: {queue!r}",
    )
    log(press(a))
    with Second():
        log(tr.remove(b))


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    tr.both_admins()
    with Second():
        folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client()
        with Second():
            b = rig.start_client()
        rig.ensure_character(a)
        with Second():
            rig.ensure_character(b)
        # The run outlasts the suit's oxygen and battery in an unpressurized station
        a.set_admin_flag("invulnerable", True)
        b.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        config(a)
        log(tr.remove(a))
        settled(a, "table", "then press")
        bot_build_bumped(a, b)
        bot_pushed_out(a, b)
        humans_full(a, b)
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
        f"{len(checks) - len(failed)}/{len(checks)} human priority checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
