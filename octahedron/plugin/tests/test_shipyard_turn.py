"""A player's turn at a real station shipyard (SE-840), with two players on one server. A free build
has no turn. A paid build gets its turn at the queue's front: the containers, welders and connectors
are the player's, shared with nobody in OCT (SE-975) and with their faction in one of their own,
connectors on and not trading, reactors OCT's. The press buys what isn't loaded, from the player's
own account. A window nobody uses drops the order;
after the build the deadline, the chat command or empty containers end the turn, what is left is sold
to the station, and a docked ship floats free. Also: a ship in the way, a build running
short, a press that hands the ship over unfinished, a blueprint loaded in the window, hydrogen from
ice in the containers, a towed grid's breakdown with the shipyard free and busy, overflow, a grid
just given by a tow, and a restart in a turn and in a build. On a world built by build_world.py. The
presses are !oct order: see test_table_rules.py. The windows are set to a few minutes, the default
of 10 is in BuildTests.

    OCT_TASK=SE-840 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_turn.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About an hour.
Not covered: a cluster (tests/shipyard_turn_cluster.py), and a player moving items by hand: the
probe plugin puts them in and takes them out.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-840")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_repair_slot as rs  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (
    Second,
    check,
    checks,
    load,
    press,
    settled,
)  # noqa: E402

STATION = "Alpha"
PLATE = "Component/SteelPlate"
GOLD = "Ingot/Gold"
STONE = "Ore/Stone"
DEFAULT = "Ingot/Gold=100,Component/Motor=10"
OCT = rs.OCT
# No hourly payouts: they would change the balances the checks compare
MONEY = {
    "MoneySubsidyPerActivePlayer": 0,
    "MoneyOctCap": 1_000_000_000_000,
    "MoneyPlayerTaxThreshold": 1_000_000_000,
}
PAY = {
    **tr.CONFIG,
    **MONEY,
    "FreeBuildPcuLimit": 0,
    "ShipyardClearMinutes": 1,
    "ShipyardLaborFeePer100Pcu": 10,
}
WINDOW = 2
TOPUP = 1
ICE = 5000


def armor(name: str, blocks: int) -> str:
    return rig.grid(
        name,
        777000840000100,
        [
            rig.block(
                "MyObjectBuilder_CubeBlock",
                "LargeBlockArmorBlock",
                (x, 0, z),
            )
            for x in range(2)
            for z in range(blocks // 2)
        ],
        tag="MyObjectBuilder_CubeGrid",
    )


SHIP = "Turn Test"
PAID = armor(SHIP, 16)
PAID_PLATES = 16 * 25
# For what must happen while it builds: the welders do the small one in seconds
BIG = rig.grid(
    SHIP,
    777000840000400,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(8)
        for z in range(16)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
BIG_PLATES = 128 * 25
OTHER = "Turn Other"
NEXT = armor(OTHER, 4)


TANK_SHIP = "TankTest"
TANKED = rig.grid(
    TANK_SHIP,
    777000840000300,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)


def probe(remote, args: str) -> str:
    return st.probe(remote, args)


def yard(remote, player: str | None = None) -> dict[str, str]:
    return rs.yard(remote, player)


def liters(remote, grid: str) -> int:
    return int(re.search(r"GAS \d+ (\d+)", probe(remote, f"gas {grid}"))[1])


def money(remote) -> int:
    return int(re.search(r"BALANCE (-?\d+)", probe(remote, "balance"))[1])


def stock(remote) -> dict[str, float]:
    """What the shipyard's containers, welders and connectors hold"""
    reply = probe(remote, f"yardstock {STATION}")
    return {k: float(v) for k, v in re.findall(r"(\w+/\w+)=([\d.]+)", reply)}


def market(remote, item: str) -> tuple[float, float, float]:
    """OCT's bid, ask and stock of the item at the station"""
    have, ask, bid, _ = st.stock(remote, STATION, item)
    assert have >= 0, f"{item} is not in the Store"
    return bid, ask, have


def wait_for(what, timeout: float, every: float = 3.0):
    return rs.wait_for(what, timeout, every)


def order(a, blueprint: str = PAID) -> str:
    """The blueprint loaded and ordered: returns the answer to the order"""
    # A character waiting in space runs out of oxygen in about 15 minutes
    rig.ensure_character(a)
    sa.at_table(a, STATION)
    load(a, blueprint)
    settled(a, "table", "paid")
    reply = press(a)
    if "Your quote for" in reply:
        reply = press(a)
    return reply


def turn(a, blueprint: str = PAID) -> float:
    """The blueprint ordered, up to the player's turn; returns when it started"""
    mark = tp.latest(a)
    reply = order(a, blueprint)
    check("is in the queue" in reply, f"the paid order: {reply}")
    told = tp.wait_chat(a, "It is your turn", 40, mark)
    check(told is not None, f"the player is told it is their turn: {told}")
    return time.monotonic()


def built(a, timeout: float = 400) -> str | None:
    return tp.wait_chat(a, "Take what is in", timeout, BUILD_MARK[0])


BUILD_MARK = [(0, set())]


def start(a) -> str:
    """The button in the player's turn; returns the answer"""
    BUILD_MARK[0] = tp.latest(a)
    return press(a)


def grid_there(a, name: str = SHIP) -> list:
    return [g for g in a.list_grids() if g.get("name") == name]


def placed(a, name: str = SHIP) -> None:
    check(wait_for(lambda: grid_there(a, name), 30, 1), f"'{name}' is placed")


def ship_gone(a, name: str = SHIP) -> None:
    log(probe(a, "removegrid " + name.replace(" ", "_")))


def free_at_end(a, what: str) -> None:
    wait_for(lambda: rs.octs(yard(a)) and yard(a)["items"] == "0", 30, 1)
    state = yard(a)
    check(rs.octs(state), f"{what}: the blocks are OCT's again: {state}")
    check(state["items"] == "0", f"{what}: nothing is left in them: {state['items']}")
    check(state["connected"] == "0", f"{what}: every connector is unlocked")
    text = settled(a, "queue", "No ships in the queue")
    check("No ships in the queue" in text, f"{what}: the shipyard is free: {text!r}")


def free_build(a) -> None:
    """A free build has no turn: OCT's blocks all along, no leftovers, the shipyard frees at once"""
    with_config(a, **tr.CONFIG)
    mark = tp.latest(a)
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "a free order")
    check(
        tp.wait_chat(a, "It is your turn", 25, mark) is None,
        "no turn for a free build",
    )
    state = yard(a)
    check(rs.octs(state), f"its blocks stay OCT's while it builds: {state}")
    check(tp.wait_chat(a, "is ready", 400, mark), "it is built")
    # The leftovers go at the next check
    wait_for(lambda: yard(a)["items"] == "0", 30, 1)
    state = yard(a)
    check(rs.octs(state), f"they are OCT's after it: {state}")
    check(state["items"] == "0", f"the leftovers are gone: {state['items']}")
    reply = " ".join(rig.chat(a, "!oct shipyard done", 3))
    check("You have no turn" in reply, f"nothing to end: {reply}")
    log(probe(a, "removegrid Order_Test"))


def blocker(a) -> None:
    """A powered ship with a cockpit in the build space: its owner is told, then it is moved out"""
    with_config(a, **tr.CONFIG, ShipyardClearMinutes=1)
    grid = re.search(r"BLOCKER (\d+)", probe(a, f"yardblocker {STATION}"))
    check(grid is not None, f"a ship in the build space: {grid}")
    mark = tp.latest(a)
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "an order behind it")
    told = tp.wait_chat(a, "blocks the next order", 40, mark)
    check(told is not None, f"its owner is told to fly it out: {told}")
    moved = tp.wait_chat(a, "was moved out", 130, mark)
    check(moved is not None, f"it was moved out in time: {moved}")
    check(
        [g for g in a.list_grids() if g.get("name") == "Blocker"],
        "the ship is still there",
    )
    check(tp.wait_chat(a, "is ready", 400, mark), "the waiting order was built")
    log(probe(a, "removegrid Blocker"))
    log(probe(a, "removegrid Order_Test"))


def turn_starts_and_expires(a, b) -> None:
    """No money at the queue. The turn: whose blocks and how. Nobody presses: dropped and sold"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    log(probe(a, "balance 300000"))
    log(probe(a, f"yardoff {STATION}"))
    plate = market(a, PLATE)
    before = money(a)
    home = yard(a)
    t0 = turn(a)
    check(money(a) == before, f"no money changes hands at the queue: {money(a)}")
    state = yard(a, "OctOther")
    check(rs.theirs(state, "OctTester"), f"the blocks are the player's: {state}")
    rs.power_stays(state, "in the turn")
    check(state["trading"] == "0", f"the connectors don't trade: {state['trading']}")
    check(state["disabled"] == "0", f"the connectors are on: {state['disabled']}")
    yard_id = rs.shipyard_id(a)
    wait_for(lambda: rs.client_owner(b, yard_id, state["cell"])[1] == "None", 30, 1)
    seen = rs.client_owner(b, yard_id, state["cell"])
    check(
        seen[1] == "None",
        f"the other client sees it shared with nobody, the players being in OCT: {seen}",
    )
    reply = " ".join(rig.chat(a, f"!sprobe yard {STATION} OctOther", 3))
    check(
        "access cargo=False welders=False connectors=False reactors=False" in reply,
        f"another player, an ally in OCT, has no access: {reply}",
    )
    probe(a, "faction KRS founder")
    with Second():
        probe(b, "faction KRS member")
    # Shared with nobody when the turn began in OCT, so a faction made later doesn't open it
    reply = " ".join(rig.chat(a, f"!sprobe yard {STATION} OctOther", 3))
    check(
        "access cargo=False welders=False connectors=False reactors=False" in reply,
        f"not even their new faction has access: {reply}",
    )
    # Back to OCT, where every player starts, or later turns are shared with no faction
    with Second():
        probe(b, "faction OCT member")
    probe(a, "faction OCT member")
    text = settled(a, "details", "Missing from the containers")
    check("Missing from the containers" in text, f"the screen says it: {text!r}")
    check("Steel Plate" in text, f"the plates are missing: {text!r}")

    mark = tp.latest(a)
    log(probe(a, f"yardload {STATION} {PLATE} 10"))
    log(probe(a, f"yardload {STATION} PhysicalGunObject/Welder2Item 1"))
    told = tp.wait_chat(a, "did not start within", WINDOW * 60 + 40, mark)
    took = time.monotonic() - t0
    check(told is not None, f"the window ended: {told}")
    check(
        WINDOW * 60 - 15 <= took <= WINDOW * 60 + 30,
        f"at its time: {took:.0f} s of {WINDOW * 60}",
    )
    free_at_end(a, "window over")
    gained = money(a) - before
    check(
        gained > 10 * plate[0],
        f"plates at the bid, tax in it, and a tool without a market price at its base price: {gained}",
    )
    now = market(a, PLATE)
    check(
        now[2] == plate[2] + 10,
        f"the plates are in OCT's stock: {plate[2]} -> {now[2]}",
    )
    uranium = float(yard(a)["uranium"])
    check(
        uranium >= float(home["uranium"]) - 20,
        f"the reactors kept their uranium, but what they burned: {uranium}",
    )
    tr.remove(a)


def build_and_deadline(a) -> None:
    """The load counts, the press buys the rest; the build; the deadline sells what is left"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    plate = market(a, PLATE)
    gold = market(a, GOLD)
    before = yard(a)
    money0 = money(a)
    turn(a)
    log(probe(a, f"yardload {STATION} {PLATE} 100"))
    log(probe(a, f"yardload {STATION} {GOLD} 50"))
    check(money(a) == money0, "nothing is paid for the turn or the load")
    reply = start(a)
    check("builds" in reply or "bought" in reply, f"the press starts it: {reply}")
    money1 = money(a)
    missing = PAID_PLATES - 100
    check(
        money0 - money1 >= missing * plate[1],
        f"the press paid for what is missing, {missing} plates at {plate[1]}: {money0 - money1}",
    )
    now = market(a, PLATE)
    check(
        now[2] == plate[2] - missing,
        f"they came out of OCT's stock: {plate[2]} -> {now[2]}",
    )
    state = yard(a)
    check(rs.theirs(state, "OctTester"), f"the player's while it builds: {state}")
    done = built(a)
    t0 = time.monotonic()
    check(
        done is not None,
        f"built with welders of another owner than the reactors: {done}",
    )
    grid = st.wait_until(lambda: grid_there(a), 30)
    check(grid and grid[0].get("blocks", 16) == 16, f"the whole ship: {grid}")
    state = yard(a)
    check(rs.theirs(state, "OctTester"), f"still the player's for the taking: {state}")
    check(
        float(state["uranium"]) >= float(before["uranium"]) - 20,
        f"no conveyor took the reactors' uranium: {state['uranium']}",
    )
    check(state["elsewhere"] == "0", f"no uranium elsewhere: {state['elsewhere']}")
    left = stock(a)
    check(left.get(GOLD) == 50, f"what they loaded on top is there: {left}")
    docked = re.search(r"SHIP (\d+)", probe(a, f"yardship {STATION}"))
    check(docked is not None, f"a ship docks to take it: {docked}")
    ship = docked[1] if docked else "0"
    check(st.wait_docked(a, ship), "it docks")
    state = yard(a)
    check(state["trading"] == "0", f"not trading: {state['trading']}")

    mark = tp.latest(a)
    money2 = money(a)
    told = tp.wait_chat(a, "are over", WINDOW * 60 + 40, mark)
    took = time.monotonic() - t0
    check(told is not None, f"the deadline: {told}")
    check(took <= WINDOW * 60 + 30, f"at its time: {took:.0f} s of {WINDOW * 60}")
    check("sold" in (told or "") or "SC" in (told or ""), f"the sale in chat: {told}")
    free_at_end(a, "deadline")
    want = 50 * gold[0]
    got = money(a) - money2
    check(got == want, f"sold at OCT's bid, tax in it: {got}, want {want:.0f}")
    now = market(a, GOLD)
    check(now[2] == gold[2] + 50, f"in OCT's stock: {gold[2]} -> {now[2]}")
    floats = tp.wait_chat(a, "floats free", 5, mark)
    check(floats is not None, f"the docked ship's owner is told: {floats}")
    check(
        "no" in probe(a, f"docked {ship}").split(),
        "the docked ship floats free",
    )
    log(probe(a, "undock"))
    ship_gone(a)
    tr.remove(a)


def running_short(a) -> None:
    """Taken out while it builds: it waits and says so, a top-up goes on; without one, after its
    minutes the stall rules remove a ship that misses this much, and the parts and fee come back
    """
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW, ShipyardTopUpMinutes=TOPUP)
    turn(a, BIG)
    log(probe(a, f"yardload {STATION} {PLATE} {BIG_PLATES}"))
    start(a)
    log(probe(a, f"yardtake {STATION}"))
    mark = BUILD_MARK[0]
    told = tp.wait_chat(a, "waits for", 120, mark)
    check(told is not None, f"it waits for the parts and says so: {told}")
    check("Steel Plate" in (told or ""), f"naming them: {told}")
    log(probe(a, f"yardload {STATION} {PLATE} {BIG_PLATES + 100}"))
    done = built(a)
    check(done is not None, f"a top-up finishes it: {done}")
    check("is ready" in (done or ""), f"a whole ship: {done}")
    check(stock(a).get(PLATE, 0) > 0, f"the rest is there: {stock(a)}")

    before = money(a)
    mark = tp.latest(a)
    reply = " ".join(rig.chat(a, "!oct shipyard done", 4))
    check("Your turn ends now" in reply, f"the chat command in the last part: {reply}")
    told = tp.wait_chat(a, "You ended your turn", 15, mark)
    check(told is not None, f"the sale in chat: {told}")
    free_at_end(a, "chat command")
    check(money(a) > before, f"they got the money: {before} -> {money(a)}")
    ship_gone(a)
    tr.remove(a)

    turn(a, BIG)
    log(probe(a, f"yardload {STATION} {PLATE} {BIG_PLATES}"))
    start(a)
    paid = money(a)
    log(probe(a, f"yardtake {STATION}"))
    # No use to the build, and it keeps the window open after it
    log(probe(a, f"yardload {STATION} {GOLD} 5"))
    t0 = time.monotonic()
    told = tp.wait_chat(a, "waits for", 120, BUILD_MARK[0])
    check(told is not None, f"it waits again: {told}")
    done = built(a, TOPUP * 60 + 300)
    took = time.monotonic() - t0
    check(
        done is not None and "The ship was removed" in done,
        f"without a top-up the stall rules end it: {done}",
    )
    check(
        took >= TOPUP * 60,
        f"after its {TOPUP} min: {took:.0f} s",
    )
    check(wait_for(lambda: not grid_there(a), 30, 1), "the ship is gone")
    check(money(a) > paid, f"the fee is back: {paid} -> {money(a)}")
    check(rs.theirs(yard(a), "OctTester"), "the window goes on")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "after a removed ship")
    tr.remove(a)


def done_while_loading(a) -> None:
    """The chat command before the build: the order goes, what is loaded is sold"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    turn(a)
    log(probe(a, f"yardload {STATION} {GOLD} 5"))
    before = money(a)
    mark = tp.latest(a)
    reply = " ".join(rig.chat(a, "!oct shipyard done", 4))
    check("Your turn ends now" in reply, f"the chat command: {reply}")
    check(
        tp.wait_chat(a, "You ended your turn", 15, mark),
        "the turn ended",
    )
    free_at_end(a, "chat command while loading")
    check(money(a) > before, f"what was loaded is sold: {before} -> {money(a)}")
    tr.remove(a)


def cancel_running(a) -> None:
    """A press on a running paid build hands the ship over unfinished, what is left stays for the
    window. A free one's is in test_repair_slot.py"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    turn(a, BIG)
    log(probe(a, f"yardload {STATION} {PLATE} {BIG_PLATES + 100}"))
    start(a)
    placed(a)
    paid = money(a)
    mark = tp.latest(a)
    reply = press(a)
    told = tp.wait_chat(a, "You canceled", 20, mark)
    check(told is not None, f"a press cancels: {reply} {told}")
    check("The unfinished ship is yours" in (told or ""), f"handed over: {told}")
    check(wait_for(lambda: grid_there(a), 30, 1), "the unfinished ship is there")
    wait_for(lambda: stock(a).get(PLATE, 0) > 100, 15, 1)
    left = stock(a).get(PLATE, 0)
    check(left > 100, f"what is left stays in the containers: {left}")
    check(rs.theirs(yard(a), "OctTester"), "still the player's turn, for the taking")
    check(money(a) == paid, f"no fee comes back: {paid} -> {money(a)}")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "after a cancel")
    ship_gone(a)
    tr.remove(a)


def next_in_same_turn(a) -> None:
    """A blueprint loaded and pressed in a turn's window starts at once, in both windows"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    turn(a)
    log(probe(a, f"yardload {STATION} {PLATE} {PAID_PLATES + 150}"))
    BUILD_MARK[0] = tp.latest(a)
    reply = order(a, NEXT)
    check(f"{OTHER}" in reply, f"another blueprint in the loading window: {reply}")
    check(
        tp.wait_chat(a, f"builds '{OTHER}' for you now", 40, BUILD_MARK[0]),
        "it starts at once, the queued one gone",
    )
    check(built(a), "that ship")
    ship_gone(a, OTHER)
    tr.remove(a)
    plate = market(a, PLATE)
    mark = tp.latest(a)
    BUILD_MARK[0] = mark
    reply = order(a, NEXT)
    check(f"{OTHER}" in reply, f"a next blueprint in the last part: {reply}")
    check(
        tp.wait_chat(a, f"builds '{OTHER}' for you now", 40, mark),
        "it starts at once",
    )
    check(
        tp.wait_chat(a, "It is your turn", 1, mark) is None,
        "no second turn: the same one goes on",
    )
    check(rs.theirs(yard(a), "OctTester"), "the blocks stay the player's")
    now = market(a, PLATE)
    check(now[2] == plate[2], f"the leftovers built it, nothing was bought: {now[2]}")
    check(built(a), "the second ship")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "second ship")
    ship_gone(a, OTHER)
    tr.remove(a)


def hydrogen(a) -> None:
    """A docked ship's hydrogen over half its tank first, then ice in the containers, then the store's"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    sa.admin(a, f"!oct market set {STATION} Ore/Ice 30000")
    turn(a, TANKED)
    docked = re.search(r"SHIP (\d+)", probe(a, f"yardship {STATION} H2=0.6"))
    check(docked is not None, f"a ship with some hydrogen at the shipyard: {docked}")
    ship = docked[1] if docked else "0"
    check(st.wait_docked(a, ship), "it docks")
    log(probe(a, f"yardload {STATION} Ore/Ice {ICE}"))
    log(probe(a, f"yardload {STATION} {PLATE} 200"))
    ice = market(a, "Ore/Ice")
    start(a)
    wait_for(lambda: liters(a, ship) == 500_000, 15, 1)
    gas = liters(a, ship)
    check(gas == 500_000, f"the docked ship's tank gave what is over half: {gas}")
    left = stock(a).get("Ore/Ice", 0)
    check(left == 0, f"then the ice in the containers: {left}")
    now = market(a, "Ore/Ice")
    check(now[2] < ice[2], f"then the store's: {ice[2]} -> {now[2]}")
    check(built(a), "the ship")
    got = liters(a, TANK_SHIP)
    check(got == 500_000, f"its tank is filled: {got}")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "hydrogen")
    log(probe(a, "undock"))
    ship_gone(a, TANK_SHIP)
    tr.remove(a)


def own_account(a) -> None:
    """A faction member pays from their own account, and sales go to them"""
    with_config(a, **PAY, ShipyardTurnMinutes=WINDOW)
    probe(a, "faction KFP leader")
    mine = money(a)
    turn(a)
    state = yard(a)
    check(
        rs.theirs(state, "OctTester", "Faction"),
        f"in a faction other than OCT, the blocks are shared with it: {state}",
    )
    log(probe(a, f"yardload {STATION} {GOLD} 5"))
    start(a)
    mine1 = money(a)
    check(mine1 < mine, f"the player pays: {mine} -> {mine1}")
    check(built(a), "the ship")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "in a faction")
    mine2 = money(a)
    check(mine2 > mine1, f"the sale goes to the player: {mine1} -> {mine2}")
    probe(a, "faction OCT member")
    ship_gone(a)
    tr.remove(a)


def breakdown(a, b) -> None:
    """A towed grid breaks down at once: into the free shipyard for what it really has, or all of
    it sold while the shipyard is busy, the build there undisturbed"""
    with_config(a, **tr.CONFIG, **MONEY, ShipyardTurnMinutes=WINDOW)
    wreck = re.search(r"WRECK (\d+)", probe(a, f"yardwreck {STATION}"))
    check(wreck is not None, f"a wreck by the shipyard: {wreck}")
    wreck_id = wreck[1] if wreck else "0"
    money0 = money(a)
    mark = tp.latest(a)
    log(" ".join(rig.chat(a, f"!oct shipyard tow {STATION} {wreck_id}", 3)))
    told = tp.wait_chat(a, "broke it down", 25, mark)
    check(told is not None, f"at once, and the player is told: {told}")
    check(wait_for(lambda: not grid_there(a, "Wreck"), 30, 1), "the wreck is gone")
    state = yard(a)
    check(rs.theirs(state, "OctTester"), f"the player's turn: {state}")
    left = stock(a)
    log(f"the wreck gave {left}")
    check(left.get(GOLD) == 100, f"its cargo came along: {left}")
    check(
        left.get("Component/Motor") == 14,
        f"its cargo's and the container's motors: {left}",
    )
    # 2 whole armor blocks, 1 half built of 25 plates
    plates = left.get(PLATE, 0)
    check(50 < plates < 75, f"the half block yields what it has: {plates}")
    check(money(a) == money0, "all of it fit: nothing sold")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "breakdown")
    check(money(a) > money0, f"the rest is sold: {money0} -> {money(a)}")
    tr.remove(a)

    # No room: what doesn't fit is sold, the cheapest first
    wreck = re.search(r"WRECK (\d+)", probe(a, f"yardwreck {STATION} {DEFAULT} over"))
    wreck_id = wreck[1] if wreck else "0"
    money0 = money(a)
    mark = tp.latest(a)
    rig.chat(a, f"!oct shipyard tow {STATION} {wreck_id}", 3)
    told = tp.wait_chat(a, "broke it down", 25, mark)
    check(told is not None, f"broken down with no room: {told}")
    left = stock(a)
    log(f"with no room it kept {left}")
    check(0 < left.get(STONE, 0), f"as much stone as fits: {left}")
    check(left.get(GOLD) == 100, f"the gold, dearer than the stone, stays: {left}")
    check(left.get("Component/Motor") >= 14, f"so do the motors: {left}")
    check(
        market(a, STONE)[0] < market(a, GOLD)[0],
        "the stone is cheaper than the gold",
    )
    check(money(a) > money0, f"the money is there at once: {money0} -> {money(a)}")
    check("sold" in (told or ""), f"the player is told: {told}")
    # Not this much stone on sale: past 1,000,000 SC a client with no Steam crashes
    log(probe(a, f"yardtake {STATION}"))
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "breakdown with no room")
    tr.remove(a)

    # Busy: someone else's build runs, the wreck is sold whole
    with Second():
        build_mark = tp.latest(b)
        load(b)
        settled(b, "table", "Press the button")
        check("is in the queue" in press(b), "someone else's build")
    # All whole: the welders would finish a half block in their reach with the build's plates
    wreck = re.search(r"WRECK (\d+)", probe(a, f"yardwreck {STATION} {DEFAULT} whole"))
    wreck_id = wreck[1] if wreck else "0"
    money0 = money(a)
    mark = tp.latest(a)
    rig.chat(a, f"!oct shipyard tow {STATION} {wreck_id}", 3)
    told = tp.wait_chat(a, "is busy", 25, mark)
    check(told is not None, f"the shipyard is busy: all sold: {told}")
    check(money(a) > money0, f"the money is there at once: {money0} -> {money(a)}")
    check(wait_for(lambda: not grid_there(a, "Wreck"), 30, 1), "the wreck is gone")
    state = yard(a)
    check(rs.octs(state), f"no turn for it, the build's blocks stay OCT's: {state}")
    with Second():
        check(tp.wait_chat(b, "is ready", 400, build_mark), "the build isn't disturbed")
    log(probe(a, "removegrid Order_Test"))


def tow_given(a) -> None:
    """A grid a tow gave: a warning, then the sweep leaves it alone for its minutes"""
    with_config(a, **tr.CONFIG, ShipyardTowGraceMinutes=1, ShipyardClearMinutes=30)
    wreck = re.search(r"WRECK (\d+)", probe(a, f"yardwreck {STATION} Ingot/Gold=1 in"))
    check(wreck is not None, f"a wreck in the build space: {wreck}")
    wreck_id = wreck[1] if wreck else "0"
    mark = tp.latest(a)
    rig.chat(a, f"!oct shipyard given {wreck_id}", 3)
    told = tp.wait_chat(a, "out of the shipyard within 1 min", 15, mark)
    check(told is not None, f"the warning: {told}")
    t0 = time.monotonic()
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "an order behind it")
    check(
        not wait_for(lambda: not grid_there(a, "Wreck"), 30, 2),
        "the sweep leaves it alone at first",
    )
    done = wait_for(lambda: not grid_there(a, "Wreck"), 200)
    took = time.monotonic() - t0
    check(done, "then it counts as any wreck")
    check(took >= 60 - 5, f"not before its minute: {took:.0f} s")
    check(tp.wait_chat(a, "is ready", 400, mark), "the order goes on")
    log(probe(a, "removegrid Order_Test"))


def towed_in(a) -> None:
    """A tow reaching the shipyard: over the limit it breaks down there and then, within it the
    grid is the player's, with a warning to move it"""
    with_config(a, **tr.CONFIG, **MONEY, ContractSalvageRadius=1000)
    mark = tp.latest(a)
    log(probe(a, f"yardtow {STATION} 1"))
    over = tp.wait_chat(a, "is too big to keep: with its", 120, mark)
    check(over is not None, f"over the limit: told why: {over}")
    told = tp.wait_chat(a, "broke it down at once", 30, mark)
    check(told is not None, f"at the player's shipyard: {told}")
    check(wait_for(lambda: not grid_there(a, "Wreck"), 30, 1), "the grid is gone")
    check(rs.theirs(yard(a), "OctTester"), "the player's turn")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "tow")
    log(probe(a, "removegrid Tug"))

    mark = tp.latest(a)
    log(probe(a, f"yardtow {STATION} 1000000"))
    given = tp.wait_chat(a, "out of the shipyard within", 120, mark)
    check(given is not None, f"within the limit: the warning: {given}")
    check(tp.latest(a) != mark, "and told it's theirs")
    me = a.get_faction()["identityId"]
    check(
        wait_for(lambda: str(me) in rs.info(a, "Wreck").get("owners", ""), 20),
        "the player's",
    )
    log(probe(a, "removegrid Tug"))
    log(probe(a, "removegrid Wreck"))


def restarts(a):
    """A turn's deadline and blocks survive a restart, and so does a running paid build"""
    with_config(a, **PAY, ShipyardTurnMinutes=4)
    t0 = turn(a)
    log(probe(a, f"yardload {STATION} {PLATE} 10"))
    a = sa.restart(a)
    sa.at_table(a, STATION)
    wait_for(lambda: stock(a).get(PLATE) == 10, 30, 1)
    check(rs.theirs(yard(a), "OctTester"), "still the player's after a restart")
    check(stock(a).get(PLATE) == 10, f"the load is still there: {stock(a)}")
    left = 4 * 60 - (time.monotonic() - t0)
    mark = tp.latest(a)
    told = tp.wait_chat(a, "did not start within", max(left, 0) + 40, mark)
    took = time.monotonic() - t0
    check(told is not None, f"the window ended: {told}")
    check(
        took <= 4 * 60 + 30,
        f"the old deadline holds, it didn't start over: {took:.0f} s",
    )
    free_at_end(a, "window after a restart")
    tr.remove(a)

    # The ship may be done before the client is back, and that takes minutes: the window
    # is long enough, and only the server's log has the ending for sure
    with_config(a, **PAY, ShipyardTurnMinutes=10)
    turn(a, BIG)
    log(probe(a, f"yardload {STATION} {PLATE} {BIG_PLATES}"))
    log(probe(a, f"yardload {STATION} {GOLD} 5"))
    start(a)
    placed(a)
    a = sa.restart(a)
    sa.at_table(a, STATION)
    done = sa.wait_log(rf"'{SHIP}' of .* built or broken down", 400)
    check(done is not None, f"the build ends after the restart: {done}")
    state = yard(a)
    check(rs.theirs(state, "OctTester"), f"the last part of the turn goes on: {state}")
    check(stock(a).get(GOLD) == 5, f"with what was loaded on top: {stock(a)}")
    rig.chat(a, "!oct shipyard done", 4)
    free_at_end(a, "build after a restart")
    ship_gone(a)
    tr.remove(a)
    return a


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
        # The default stock is short of what the builds here buy
        sa.admin(a, f"!oct market set {STATION} {PLATE} 10000")
        for scenario in (
            free_build,
            blocker,
            lambda r: turn_starts_and_expires(r, b),
            build_and_deadline,
            running_short,
            done_while_loading,
            cancel_running,
            next_in_same_turn,
            hydrogen,
            own_account,
            lambda r: breakdown(r, b),
            towed_in,
            tow_given,
        ):
            probe(a, "air")
            # A client without Steam crashes on the millionaire achievement
            if money(a) > 400_000:
                probe(a, f"balance {400_000 - money(a)}")
            scenario(a)
        with Second():
            rig.stop_client()
        a = restarts(a)
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
        f"{len(checks) - len(failed)}/{len(checks)} shipyard turn checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
