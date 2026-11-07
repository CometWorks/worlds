"""!oct shipyard clear (SE-1198): an admin empties a station's shipyard queue. A held turn with
another player's order waiting behind it, then a running paid build: each entry ends as its
player's own cancel or !oct shipyard done would, the players are told, and nothing is paid twice
or lost. Without a station name it clears the station of the admin safe zone the admin stands
in, and away from any it asks for the name. Two players on one server, on a world built by build_world.py; the helpers are
test_shipyard_turn.py's.

    OCT_TASK=SE-1198C OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_clear.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 15 minutes.
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1198C")
import rig  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as t  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import Second, check, checks, load, press, settled  # noqa: E402

STATION = t.STATION


def clear(a, station: str = STATION) -> str:
    return " ".join(rig.chat(a, f"!oct shipyard clear {station}".strip(), 5))


def no_station_away(a) -> None:
    """Without a station name, away from any station's safe zone: nothing is cleared"""
    x, y, z = sa.station_tables()[STATION]
    a.character_teleport(x + 5000, y, z)
    reply = clear(a, "")
    check(
        "Name the station" in reply,
        f"away from a station it asks for the name: {reply}",
    )
    sa.at_table(a, STATION)


def turn_and_waiting(a, b) -> None:
    """A held turn with something loaded, another player's order waiting: the turn ends with the
    load sold once for its player, the waiting order leaves with no money moving"""
    with_config(a, **t.PAY, ShipyardTurnMinutes=t.WINDOW)
    gold = t.market(a, t.GOLD)
    t.turn(a)
    log(t.probe(a, f"yardload {STATION} {t.GOLD} 5"))
    with Second():
        b_mark = tp.latest(b)
        load(b, t.NEXT)
        settled(b, "table", "paid")
        reply = press(b)
        if "Your quote for" in reply:
            reply = press(b)
        check("is in the queue" in reply, f"another player's order waits: {reply}")
        b_money = t.money(b)
    before = t.money(a)
    mark = tp.latest(a)
    reply = clear(a)
    check(
        "left the queue" in reply and "the turn ends" in reply,
        f"the reply lists both entries: {reply}",
    )
    told = tp.wait_chat(a, "An admin cleared", 30, mark)
    check(told is not None, f"the turn's player is told: {told}")
    with Second():
        told = tp.wait_chat(b, "an admin cleared it", 15, b_mark)
        check(told is not None, f"the waiting order's player is told: {told}")
        check(
            t.money(b) == b_money, f"no money moves for a waiting order: {t.money(b)}"
        )
    t.free_at_end(a, "cleared turn")
    gained = t.money(a) - before
    check(
        abs(gained - 5 * gold[0]) < 1,
        f"the load is sold once, at OCT's bid: {gained}, want {5 * gold[0]:.0f}",
    )
    now = t.market(a, t.GOLD)
    check(now[2] == gold[2] + 5, f"into OCT's stock once: {gold[2]} -> {now[2]}")
    reply = clear(a)
    check("queue is empty" in reply, f"a second clear finds nothing: {reply}")
    tr.remove(a)
    with Second():
        tr.remove(b)


def running_build(a) -> None:
    """A running paid build: canceled with the unfinished ship handed over, then the turn ends
    with the leftovers sold"""
    with_config(a, **t.PAY, ShipyardTurnMinutes=t.WINDOW)
    t.turn(a, t.BIG)
    log(t.probe(a, f"yardload {STATION} {t.PLATE} {t.BIG_PLATES + 100}"))
    t.start(a)
    t.placed(a)
    paid = t.money(a)
    mark = tp.latest(a)
    # No station named: the admin stands in the station's safe zone, by its table
    reply = clear(a, "")
    check(
        "the unfinished ship handed over" in reply,
        f"without a name, the station of the zone; the reply names the build: {reply}",
    )
    told = tp.wait_chat(a, "An admin canceled", 30, mark)
    check(
        "The unfinished ship is yours" in (told or ""),
        f"the ship is handed over: {told}",
    )
    told = tp.wait_chat(a, "An admin cleared", 40, mark)
    check(told is not None, f"then the turn ends: {told}")
    check(len(t.grid_there(a)) == 1, "one unfinished ship, no copy")
    t.free_at_end(a, "cleared build")
    check(t.money(a) > paid, f"the leftover plates are sold: {paid} -> {t.money(a)}")
    t.ship_gone(a)
    tr.remove(a)


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
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        with_config(a, **tr.CONFIG)
        log(tr.remove(a))
        sa.admin(a, f"!oct market set {STATION} {t.PLATE} 10000")
        t.probe(a, "air")
        turn_and_waiting(a, b)
        t.probe(a, "air")
        no_station_away(a)
        running_build(a)
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
        f"{len(checks) - len(failed)}/{len(checks)} shipyard clear checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
