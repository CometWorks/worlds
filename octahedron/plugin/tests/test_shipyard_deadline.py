"""A ship an order left in a shipyard's build space past ShipyardClearMinutes (SE-929), on a world
built by build_world.py with Hangar on the server (OCT_HANGAR_REPO, 1.2.0 or later), set to
AdminSafeZonesOnly and one slot per player. The ship goes into its orderer's Hangar, recorded at this
station and with no cool-down started, and is counted once in the player's PCU. With the slot taken a
paid ship is removed and what it cost comes back, and a free one is just removed. A ship that left the
build space and came back, and one Hangar can't take, are moved out as any other grid. A restart in
the wait keeps the ship, and one after a refund doesn't pay again.

    OCT_TASK=SE-929 OCT_WORLD_SOURCE=<world from build_world.py> OCT_HANGAR_REPO=<hangar> \\
        uv run --no-project --with httpx python tests/test_shipyard_deadline.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 30 minutes. On a
cluster: tests/shipyard_deadline_cluster.py.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-929")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as tu  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import check, checks  # noqa: E402

STATION = tu.STATION
SHIP = tu.SHIP
CLEAR = 1
PAY = {**tu.PAY, "ShipyardClearMinutes": CLEAR, "ShipyardTurnMinutes": 3}
HANGAR = (
    '<?xml version="1.0" encoding="utf-8"?><PluginConfig>'
    "<AdminSafeZonesOnly>true</AdminSafeZonesOnly>"
    "<MaxEntriesPerPlayer>1</MaxEntriesPerPlayer></PluginConfig>"
)


def hangar(a, args: str = "list") -> str:
    reply = " ".join(rig.chat(a, f"!hangar {args}", 8))
    log(f"!hangar {args} -> {reply}")
    return reply


def pcu(a) -> int:
    """The player's PCU as the plugin counts it, ships in Hangar included"""
    reply = tc.ship_reply(a)
    found = re.search(r"You have ([\d,]+) PCU", reply)
    assert found, reply
    return int(found[1].replace(",", ""))


def ship_here(a, name: str = SHIP) -> bool:
    return bool(tu.grid_there(a, name))


def leave_paid(a, blueprint: str = tu.PAID) -> int:
    """A paid ship is built and its turn ended, the ship stays where it is; returns what it took"""
    with_config(a, **PAY)
    tu.probe(a, "air")
    # What the Store's plates cost; a client with no Steam crashes at 1,000,000 SC
    if tu.money(a) < 100_000:
        tu.probe(a, "balance 100000")
    before = tu.money(a)
    tu.turn(a, blueprint)
    tu.start(a)
    check(tu.built(a) is not None, "the paid ship is built")
    paid = before - tu.money(a)
    check(paid > 0, f"it cost {paid} SC")
    rig.chat(a, "!oct shipyard done", 4)
    tu.free_at_end(a, "the paid ship's turn")
    tr.remove(a)
    return paid


def blocked(a, blueprint: str = tu.NEXT) -> tuple[str | None, tuple]:
    """An order behind the ship: returns what the player is told and the chat before it"""
    mark = tp.latest(a)
    reply = tu.order(a, blueprint)
    check("is in the queue" in reply, f"an order behind it: {reply}")
    return tp.wait_chat(a, "blocks the next order", 40, mark), mark


def next_goes_on(a, mark) -> None:
    """The order behind it gets its turn, which ends here"""
    told = tp.wait_chat(a, "It is your turn", 60, mark)
    check(told is not None, f"the waiting order goes on: {told}")
    rig.chat(a, "!oct shipyard done", 4)
    tu.free_at_end(a, "the waiting order")
    tr.remove(a)


def into_hangar(a) -> None:
    paid = leave_paid(a)
    owned = pcu(a)
    balance = tu.money(a)
    told, mark = blocked(a)
    check(
        told is not None and "goes into your Hangar" in told and f"{paid:,} SC" in told,
        f"the warning says what will happen: {told}",
    )
    check(ship_here(a), "the bare ship isn't swept away as a wreck")
    stored = tp.wait_chat(a, "in your Hangar now", CLEAR * 60 + 40, mark)
    check(stored is not None, f"the player is told it is stored: {stored}")
    check(
        tu.wait_for(lambda: not ship_here(a), 15, 1), "the ship is gone from the world"
    )
    check(SHIP in hangar(a), "it is in the Hangar")
    info = hangar(a, f"info {SHIP}")
    check(f"zone: {STATION}" in info, f"it counts as stored at the station: {info}")
    check(tu.money(a) == balance, "nothing is paid")
    check(pcu(a) == owned, f"it is counted once: {owned} -> {pcu(a)}")
    next_goes_on(a, mark)

    balance = tu.money(a)
    reply = hangar(a, f"load {SHIP}")
    check(f"Loaded {SHIP}" in reply, f"it comes back: {reply}")
    check(tu.money(a) == balance, "free at this station")
    check(pcu(a) == owned, f"still counted once: {pcu(a)}")
    reply = hangar(a, f"save {SHIP}")
    check("Saved" in reply, f"no cool-down was started: {reply}")


def no_slot_paid(a) -> None:
    check(SHIP in hangar(a), "the slot is taken")
    paid = leave_paid(a)
    owned = pcu(a)
    balance = tu.money(a)
    oct_balance = oct_money(a)
    told, mark = blocked(a)
    check(told is not None and "removed" in told, f"the warning says so too: {told}")
    gone = tp.wait_chat(a, "was removed", CLEAR * 60 + 40, mark)
    check(gone is not None and f"{paid:,} SC" in gone, f"told the amount: {gone}")
    check(tu.wait_for(lambda: not ship_here(a), 15, 1), "the ship is gone")
    check(tu.money(a) == balance + paid, f"refunded: {balance} -> {tu.money(a)}")
    check(oct_money(a) == oct_balance - paid, "by OCT")
    check(hangar(a).count(SHIP) == 1, "the Hangar is as it was")
    check(pcu(a) < owned, f"its PCU is free: {owned} -> {pcu(a)}")
    next_goes_on(a, mark)


def oct_money(a) -> int:
    lines = " ".join(rig.chat(a, "!oct money", 10))
    return int(re.search(r"OCT: ([\d,]+) SC", lines)[1].replace(",", ""))


def no_slot_free(a) -> None:
    with_config(a, **tr.CONFIG, ShipyardClearMinutes=CLEAR)
    tu.probe(a, "air")
    balance = tu.money(a)
    mark = tp.latest(a)
    tr.load(a)
    tr.settled(a, "table", "Press the button")
    check("is in the queue" in tr.press(a), "a free order")
    check(tp.wait_chat(a, "is ready", 400, mark) is not None, "the free ship is built")
    tr.load(a, tr.OTHER)
    tr.settled(a, "table", "Press the button")
    mark = tp.latest(a)
    check("is in the queue" in tr.press(a), "a free order behind it")
    told = tp.wait_chat(a, "blocks the next order", 40, mark)
    check(told is not None and " SC" not in told, f"no money in the warning: {told}")
    gone = tp.wait_chat(a, "was removed", CLEAR * 60 + 40, mark)
    check(gone is not None and " SC" not in gone, f"just removed: {gone}")
    check(tu.money(a) == balance, "nothing is paid")
    check(tp.wait_chat(a, "is ready", 400, mark) is not None, "the next one is built")
    tu.ship_gone(a, "Order Other")
    tr.remove(a)


def ship_outs() -> int:
    return rig.server_game_log().count("Shipyard ShipOut at")


def left_and_back(a) -> None:
    """A ship with a cockpit: without an entry in the book a bare one would be swept as a wreck"""
    hangar(a, f"remove {SHIP}")
    name = "Order_Test"
    paid = leave_paid(a, so.SMALL)
    before = ship_outs()
    tu.probe(a, f"movegrid {name} 2000")
    check(
        tu.wait_for(lambda: ship_outs() > before, 90, 3),
        "the book forgets a ship that left",
    )
    tu.probe(a, f"movegrid {name} -2000")
    balance = tu.money(a)
    told, mark = blocked(a)
    check(told is not None and "moved out" in told, f"it counts as parked: {told}")
    moved = tp.wait_chat(a, "was moved out", CLEAR * 60 + 40, mark)
    check(moved is not None, f"and is moved out: {moved}")
    check(ship_here(a, "Order Test"), "it is still there")
    check("empty" in hangar(a), "not in the Hangar")
    check(tu.money(a) == balance, f"nothing of the {paid} SC comes back")
    next_goes_on(a, mark)
    tu.ship_gone(a, "Order Test")


def hangar_cant(a) -> None:
    hangar(a, "disable")
    leave_paid(a)
    balance = tu.money(a)
    told, mark = blocked(a)
    check(told is not None, f"told: {told}")
    moved = tp.wait_chat(a, "was moved out", CLEAR * 60 + 40, mark)
    check(moved is not None, f"with Hangar off it is moved out: {moved}")
    check(ship_here(a), "and kept")
    check(tu.money(a) == balance, "nothing is paid")
    next_goes_on(a, mark)
    tu.ship_gone(a)
    hangar(a, "enable")


def restart_in_wait(a):
    """Stored after the restart, once, and with no money moved; then a refund and a restart"""
    paid = leave_paid(a)
    balance = tu.money(a)
    told, _ = blocked(a)
    check(told is not None, f"the wait is on: {told}")
    a = sa.restart(a)
    sa.at_table(a, STATION)
    check(tu.wait_for(lambda: SHIP in hangar(a), 240, 5), "stored after the restart")
    check(tu.wait_for(lambda: not ship_here(a), 15, 1), "the ship is gone")
    check(hangar(a).count(SHIP) == 1, "one entry")
    check(tu.money(a) == balance, f"nothing of the {paid} SC is paid")
    rig.chat(a, "!oct shipyard done", 4)
    tu.free_at_end(a, "after the restart")
    tr.remove(a)

    paid = leave_paid(a)
    balance = tu.money(a)
    told, _ = blocked(a)
    a = sa.restart(a)
    sa.at_table(a, STATION)
    check(
        tu.wait_for(lambda: tu.money(a) == balance + paid, 240, 5),
        f"refunded after a restart in the wait: {balance} + {paid} = {tu.money(a)}",
    )
    check(tu.wait_for(lambda: not ship_here(a), 15, 1), "the ship is gone")
    check(hangar(a).count(SHIP) == 1, "the Hangar is as it was")
    rig.chat(a, "!oct shipyard done", 4)
    tu.free_at_end(a, "after the refund")
    tr.remove(a)
    a = sa.restart(a)
    sa.at_table(a, STATION)
    check(
        tu.money(a) == balance + paid, f"not again at the next restart: {tu.money(a)}"
    )
    check(not ship_here(a), "and the ship stays gone")
    return a


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60, plugins=[tc.HANGAR])
    tr.both_admins()
    (rig.SERVER_DATA / "Hangar.cfg").write_text(HANGAR, encoding="utf-8")
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
        sa.at_table(a, STATION)
        with_config(a, **tr.CONFIG)
        log(tr.remove(a))
        sa.admin(a, f"!oct market set {STATION} {tu.PLATE} 10000")
        for scenario in (
            into_hangar,
            no_slot_paid,
            no_slot_free,
            left_and_back,
            hangar_cant,
        ):
            scenario(a)
        a = restart_in_wait(a)
        tu.blocker(a)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if ("Octahedron" in line or "Hangar" in line)
        and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} shipyard deadline checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
