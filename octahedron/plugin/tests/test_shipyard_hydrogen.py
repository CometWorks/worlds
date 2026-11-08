"""Where a new ship's hydrogen and oxygen come from (SE-922, SE-1288), so that a ship with only
hydrogen thrusters can fly out of the shipyard and its O2 tanks aren't empty. From the player's
docked ships, each keeping half of every H2 and O2 tank, then the ice in their containers and the
shipyard's, then the Store's, moved by the plugin without conveyors. A free ship takes from the
docked ships too, and OCT gives the rest whatever the Store has. A press refused for lack of money
puts everything back. The ship has an H2 tank and two O2 tanks, one of them Fieldwork's Lab tank
(DIS-0064). On a world built by build_world.py.

    OCT_TASK=SE-922 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_hydrogen.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 20 minutes.
The O2 checks start with "O2".
Not covered: a cluster (tests/shipyard_hydrogen_cluster.py).
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-922")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as tt  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import check, checks, load, press, settled  # noqa: E402

STATION = tt.STATION
ICE = "Ore/Ice"
FILLED = 500_000
O2_FILLED = 100_000
SHIP = tt.TANK_SHIP
# tt.TANKED with two O2 tanks of 100,000 L: the vanilla one and the Lab one the report names
GASSED = rig.grid(
    SHIP,
    777000840000310,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
        rig.block("MyObjectBuilder_OxygenTank", "", (0, 0, -1)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeBlockOxygenTankLab", (0, -1, 0)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)
# 25,000 kg for the H2 at 20 L per kg, 10,000 kg for the O2 at 10 L per kg
ICE_NEEDED = 35_000


def probe(a, args: str) -> str:
    return st.probe(a, args)


def ship_ice(a, ship: str) -> int:
    return int(re.search(r"ITEM \d+ (\d+)", probe(a, f"griditem {ship} {ICE}"))[1])


def oxygen(a, grid: str) -> int:
    return int(re.search(r"GAS \d+ (\d+)", probe(a, f"gas {grid} Oxygen"))[1])


def docked(a, command: str) -> str:
    found = re.search(r"SHIP (\d+)", probe(a, command))
    check(found is not None, f"a ship: {command}: {found}")
    ship = found[1] if found else "0"
    check(st.wait_docked(a, ship), "it docks")
    return ship


def new_ship_filled(a, what: str, ready: str | None) -> None:
    check(
        ready is not None and f"{FILLED:,} L" in ready,
        f"{what}: the message names the hydrogen: {ready}",
    )
    got = tt.liters(a, SHIP)
    check(got == FILLED, f"{what}: its H2 tank holds {FILLED:,} L: {got}")
    check(
        ready is not None and f"O2 tanks have {O2_FILLED:,} L" in ready,
        f"O2 {what}: the message names the oxygen: {ready}",
    )
    got = oxygen(a, SHIP)
    check(got == O2_FILLED, f"O2 {what}: its two O2 tanks hold {O2_FILLED:,} L: {got}")


def free_build(a, what: str) -> str | None:
    with_config(a, **tr.CONFIG, **tt.MONEY)
    mark = tp.latest(a)
    sa.at_table(a, STATION)
    load(a, GASSED)
    settled(a, "table", "Press the button")
    reply = press(a)
    check("is in the queue" in reply, f"{what}: the free order: {reply}")
    return tp.wait_chat(a, "is ready", 400, mark)


def clean_up(a) -> None:
    log(probe(a, "undock"))
    tt.ship_gone(a, SHIP)
    tr.remove(a)
    probe(a, "air")


def free_alone(a) -> None:
    """Nobody docked: OCT gives it all, and an empty Store doesn't matter"""
    sa.admin(a, f"!oct market set {STATION} {ICE} 0")
    new_ship_filled(a, "free, alone", free_build(a, "free, alone"))
    stock = tt.market(a, ICE)[2]
    check(stock == 0, f"free, alone: the Store is untouched: {stock}")
    clean_up(a)


def free_docked(a) -> None:
    """A docked ship gives what is over half its tanks, then its ice; OCT gives the rest"""
    ship = docked(a, f"dock {STATION} mine H2=0.6,O2=0.7,{ICE}=2000")
    sa.admin(a, f"!oct market set {STATION} {ICE} 1000")
    new_ship_filled(a, "free, docked", free_build(a, "free, docked"))
    gas = tt.liters(a, ship)
    check(gas == FILLED, f"free, docked: the tank keeps half: {gas}")
    gas = oxygen(a, ship)
    check(gas == 50_000, f"O2 free, docked: the O2 tank keeps half: {gas}")
    ice = ship_ice(a, ship)
    check(ice == 0, f"free, docked: its ice went too: {ice}")
    stock = tt.market(a, ICE)[2]
    check(stock == 1000, f"free, docked: the Store is untouched: {stock}")
    clean_up(a)


def paid_store(a) -> None:
    """Nobody docked, nothing loaded: the plugin brings the ice from the Store"""
    with_config(a, **tt.PAY, ShipyardTurnMinutes=tt.WINDOW)
    sa.admin(a, f"!oct market set {STATION} {ICE} 100000")
    stock = tt.market(a, ICE)[2]
    check(stock == 100_000, f"the Store's stock of ice: {stock}")
    tt.turn(a, GASSED)
    log(probe(a, f"yardload {STATION} {tt.PLATE} 200"))
    tt.start(a)
    new_ship_filled(a, "the Store", tt.built(a))
    now = tt.market(a, ICE)[2]
    check(
        now == stock - ICE_NEEDED,
        f"O2 the Store: {ICE_NEEDED:,} kg of ice bought: {now}",
    )
    rig.chat(a, "!oct shipyard done", 4)
    tt.free_at_end(a, "the Store")
    clean_up(a)


def paid_in_order(a) -> None:
    """Refused for lack of money, all back. Then the tank over half, the ship's ice, the shipyard's, the Store's"""
    with_config(a, **tt.PAY, ShipyardTurnMinutes=tt.WINDOW)
    sa.admin(a, f"!oct market set {STATION} {ICE} 30000")
    tt.turn(a, GASSED)
    ship = docked(a, f"yardship {STATION} H2=0.55,O2=0.6,{ICE}=5000")
    log(probe(a, f"yardload {STATION} {ICE} 5000"))
    log(probe(a, f"yardload {STATION} {tt.PLATE} 200"))

    funds = tt.money(a)
    log(probe(a, f"balance {-funds}"))
    reply = tt.start(a)
    check("What is missing costs" in reply, f"no money, the press is refused: {reply}")
    gas = tt.liters(a, ship)
    check(gas == 550_000, f"refused: the tank has its hydrogen back: {gas}")
    gas = oxygen(a, ship)
    check(gas == 60_000, f"O2 refused: the O2 tank has its oxygen back: {gas}")
    ice = ship_ice(a, ship)
    check(ice == 5000, f"refused: the ship has its ice back: {ice}")
    left = tt.stock(a).get(ICE, 0)
    check(left == 5000, f"refused: the shipyard has its ice back: {left}")
    log(probe(a, f"balance {funds}"))

    tt.start(a)
    tt.wait_for(lambda: tt.liters(a, ship) == FILLED, 15, 1)
    gas = tt.liters(a, ship)
    check(gas == FILLED, f"started: the tank gave what is over half: {gas}")
    gas = oxygen(a, ship)
    check(gas == 50_000, f"O2 started: the O2 tank gave what is over half: {gas}")
    ice = ship_ice(a, ship)
    check(ice == 0, f"started: then the ship's ice: {ice}")
    left = tt.stock(a).get(ICE, 0)
    check(left == 0, f"started: then the shipyard's: {left}")
    now = tt.market(a, ICE)[2]
    # A tank's fill is a float: a fraction of a kg off. The tanks gave 2,500 kg of H2 and
    # 1,000 kg of O2 worth, the ships and the shipyard 10,000 kg: the Store 21,500 kg.
    check(abs(now - 8500) < 1, f"O2 started: then the Store's, 21,500 kg: {now}")
    new_ship_filled(a, "in order", tt.built(a))
    rig.chat(a, "!oct shipyard done", 4)
    tt.free_at_end(a, "in order")
    clean_up(a)


def paid_ship_ice(a) -> None:
    """Tanks at or under half give nothing; the ship's ice is enough, and the quote says so"""
    with_config(a, **tt.PAY, ShipyardTurnMinutes=tt.WINDOW)
    sa.admin(a, f"!oct market set {STATION} {ICE} 30000")
    ship = docked(a, f"dock {STATION} mine H2=0.3,O2=0.4,{ICE}=40000")
    sa.at_table(a, STATION)
    load(a, GASSED)
    text = settled(a, "details", "from your ship")
    check("35,000" in text, f"O2 the quote counts the ship's ice: {text!r}")
    tt.turn(a, GASSED)
    log(probe(a, f"yardload {STATION} {tt.PLATE} 200"))
    tt.start(a)
    new_ship_filled(a, "the ship's ice", tt.built(a))
    gas = tt.liters(a, ship)
    check(gas == 300_000, f"the ship's ice: the tank under half gave nothing: {gas}")
    gas = oxygen(a, ship)
    check(
        gas == 40_000, f"O2 the ship's ice: the O2 tank under half gave nothing: {gas}"
    )
    ice = ship_ice(a, ship)
    check(ice == 5000, f"O2 the ship's ice: 35,000 kg of it went: {ice}")
    now = tt.market(a, ICE)[2]
    check(now == 30000, f"the ship's ice: nothing from the Store: {now}")
    rig.chat(a, "!oct shipyard done", 4)
    tt.free_at_end(a, "the ship's ice")
    clean_up(a)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(GASSED, 60)
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        with_config(a, **tr.CONFIG, **tt.MONEY)
        log(tr.remove(a))
        # Under 1,000,000 SC: the game's MillionaireClub achievement disconnects an offline client
        log(probe(a, f"balance {600_000 - tt.money(a)}"))
        # The components for the ships, whatever the default stock has
        for part in tt.TANKED_PARTS:
            sa.admin(a, f"!oct market set {STATION} Component/{part} 1000")
        for scenario in (
            paid_store,
            free_alone,
            free_docked,
            paid_in_order,
            paid_ship_ice,
        ):
            log(f"--- {scenario.__name__}")
            scenario(a)
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
        f"{len(checks) - len(failed)}/{len(checks)} shipyard hydrogen checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
