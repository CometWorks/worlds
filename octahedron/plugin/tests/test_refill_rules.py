"""The refill rules of SE-1387: a new ship comes out of the shipyard with full batteries and H2 tanks
and empty jump drives, and a ship docked at a station connector gets its H2 tanks filled for the
ice it takes at the station's Store price, paid by its owner to OCT.

- New ship: a free build of a ship with an H2 tank, a battery and a jump drive. The jump drive
  is left to the ship: its own battery starts charging it, so it is checked under 10 %.
- Paid: a ship with an empty H2 tank docks; the tank fills and the player pays
  ceil(1,000,000 L / 20 L per kg x the Store's ice price), OCT gets it.
- Oxygen (SE-1536): O2 tanks fill too, from the same ice. Empty H2 and O2 tanks both fill for
  one bill, the ice of the gas needing more (the H2: 1,000,000 L at 20 L per kg is 50,000 kg,
  100,000 L of O2 at 10 L per kg only 10,000 kg); a full H2 tank and an empty O2 tank fill the O2
  for its 10,000 kg; a 90 % full H2 tank (5,000 kg) and an empty O2 tank fill both for the O2's
  10,000 kg.
- Partial: with a few SC the tank gets what they buy and the balance ends at 0.
- Off: a tank that is turned off gets nothing, nor does a tank under 1 % short of full.
- Police: a ship named Police* of an officer on duty (a bot that said !oct police duty) fills
  free; Police (SE-1384) fills it, the dock refill doesn't bill it.
- Shipyard: a ship at the shipyard's connector gets nothing (no turn, so it may not even lock;
  the log says which).

    OCT_TASK=SE-1387 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_refill_rules.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 10 minutes.
Not covered: a cluster, where each node fills at the stations it simulates.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1387")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as tt  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import check, checks, load, press, settled  # noqa: E402

# The modules above pin the old rules for their own tests; this one checks the world's
rig.CONFIG_PINS.clear()

STATION = tt.STATION
ICE = "Ore/Ice"
TANK = 1_000_000  # LargeHydrogenTankSmall
O2_TANK = 100_000  # the large grid's OxygenTank
LITERS_PER_ICE = 20  # H2, the large O2/H2 generator's ratio
O2_LITERS_PER_ICE = 10
SHIP = "RefillTest"
REFILL = rig.grid(
    SHIP,
    777000138700001,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (-1, 0, 0)),
        rig.block("MyObjectBuilder_JumpDrive", "LargeJumpDrive", (1, 0, -1)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)
RIM = ("25;0;0", "-25;0;0")


def probe(a, args: str) -> str:
    return st.probe(a, args)


def energy(a, grid: str) -> tuple[float, float, float, float]:
    m = re.search(
        r"batteries=(\S+)/(\S+) jumps=(\S+)/(\S+)", probe(a, f"energy {grid}")
    )
    return tuple(float(v) for v in m.groups()) if m else (-1.0, -1.0, -1.0, -1.0)


def balance(a) -> int:
    return tt.money(a)


def set_balance(a, sc: int) -> None:
    log(probe(a, f"balance {sc - balance(a)}"))


def oct_money(a) -> int:
    return st.money(a).get("OCT", -1)


def ice_price(a) -> float:
    """What OCT sells a kg of ice for: the lot's price over its size where ice sells in lots"""
    reply = st.admin(a, f"!oct market show {STATION} {ICE}")
    lot = re.search(r"lots of ([\d,]+): buys [\d,.]+ SC, sells ([\d,.]+) SC", reply)
    if lot:
        return float(lot[2].replace(",", "")) / float(lot[1].replace(",", ""))
    return tt.market(a, ICE)[1]


def bill(ice: float, price: float) -> int:
    """The whole SC for the kg of ice, as DockGasMath rounds it"""
    return max(1, math.ceil(ice * price - 1e-6))


def dock(a, items: str) -> str:
    found = re.search(r"SHIP (\d+)", probe(a, f"dock {STATION} mine {items}"))
    check(found is not None, f"a ship docks at {STATION}: {items}")
    ship = found[1] if found else "0"
    check(st.wait_docked(a, ship), f"ship {ship} docks")
    return ship


def new_ship(a) -> None:
    """Full batteries and H2 tank, empty jump drive"""
    with_config(a, **tr.CONFIG, **tt.MONEY)
    mark = tp.latest(a)
    sa.at_table(a, STATION)
    load(a, REFILL)
    settled(a, "table", "Press the button")
    reply = press(a)
    check("is in the queue" in reply, f"new ship: the free order: {reply}")
    ready = tp.wait_chat(a, "is ready", 600, mark)
    check(ready is not None, f"new ship: it is ready: {ready}")
    got = tt.liters(a, SHIP)
    check(got == TANK, f"new ship: its H2 tank is full: {got:,} of {TANK:,} L")
    stored, most, jump, jump_most = energy(a, SHIP)
    check(
        # Full at the hand-over; by now it has given the jump drive a little
        most > 0 and stored >= most * 0.95,
        f"new ship: its battery is full: {stored} of {most} MWh",
    )
    # The plugin leaves it empty; the ship's own full battery starts charging it at once
    check(
        jump_most > 0 and jump < jump_most * 0.1,
        f"new ship: its jump drive isn't filled: {jump} of {jump_most} MWh",
    )
    charged = st.wait_until(
        lambda: re.search(
            rf"Shipyard order .*'{SHIP}'.* [1-9]\d* batteries charged",
            rig.server_game_log(),
        ),
        30,
    )
    check(bool(charged), "new ship: the server log has the charge")
    log(probe(a, "undock"))
    tt.ship_gone(a, SHIP)
    tr.remove(a)
    probe(a, "air")


def paid(a) -> None:
    """An empty tank fills, the owner pays the ice's price to OCT"""
    set_balance(a, 600_000)
    price = ice_price(a)
    cost = bill(TANK / LITERS_PER_ICE, price)
    log(f"ice at {price} SC per kg: {cost:,} SC for a full tank")
    you, octs = balance(a), oct_money(a)
    mark = tp.latest(a)
    ship = dock(a, "H2=0,AT=" + RIM[0])
    full = st.wait_until(lambda: tt.liters(a, ship) == TANK, 60, 2)
    check(bool(full), f"paid: the tank fills: {tt.liters(a, ship):,} L")
    now = balance(a)
    check(
        you - now == cost, f"paid: the player paid {you - now:,} SC, expected {cost:,}"
    )
    gained = oct_money(a) - octs
    check(gained == cost, f"paid: OCT got {gained:,} SC")
    told = tp.wait_chat(a, "of hydrogen for", 15, mark)
    check(
        told is not None and f"{cost:,} SC" in told,
        f"paid: the player hears the price: {told}",
    )
    time.sleep(5)
    check(balance(a) == now, "paid: a full tank isn't billed again")
    log(probe(a, "undock"))


def o2_liters(a, grid: str) -> int:
    return int(re.search(r"GAS \d+ (\d+)", probe(a, f"gas {grid} Oxygen"))[1])


def oxygen(a) -> None:
    """O2 fills along with H2, billed once for the gas needing more ice"""
    price = ice_price(a)
    cases = (
        # what, the probe ship's tanks and their fill, the kg of ice billed
        ("both empty", "H2=0,O2=0", TANK / LITERS_PER_ICE),
        ("only O2", "H2=1,O2=0", O2_TANK / O2_LITERS_PER_ICE),
        ("O2 needs more ice", "H2=0.9,O2=0", O2_TANK / O2_LITERS_PER_ICE),
    )
    for i, (what, items, ice) in enumerate(cases):
        set_balance(a, 600_000)
        cost = bill(ice, price)
        you, octs = balance(a), oct_money(a)
        mark = tp.latest(a)
        ship = dock(a, f"{items},AT={RIM[i % 2]}")
        full = st.wait_until(
            lambda: tt.liters(a, ship) == TANK and o2_liters(a, ship) == O2_TANK, 60, 2
        )
        check(
            bool(full),
            f"oxygen, {what}: H2 and O2 full: {tt.liters(a, ship):,} L H2, {o2_liters(a, ship):,} L O2",
        )
        time.sleep(5)
        paid_sc = you - balance(a)
        check(
            paid_sc == cost,
            f"oxygen, {what}: billed once, {paid_sc:,} SC, expected {cost:,} for {ice:,.0f} kg of ice",
        )
        check(
            oct_money(a) - octs == cost,
            f"oxygen, {what}: OCT got {oct_money(a) - octs:,} SC",
        )
        told = tp.wait_chat(a, "of oxygen for", 15, mark)
        check(
            told is not None and f"{cost:,} SC" in told,
            f"oxygen, {what}: the player hears the price: {told}",
        )
        log(probe(a, "undock"))


def partial(a) -> None:
    """A few SC buy what they buy, the balance ends at 0"""
    price = ice_price(a)
    money = 100
    set_balance(a, money)
    mark = tp.latest(a)
    ship = dock(a, "H2=0,AT=" + RIM[1])
    expected = money * LITERS_PER_ICE / price
    got = st.wait_until(lambda: tt.liters(a, ship), 60, 2)
    check(
        abs(got - expected) <= 1,
        f"partial: the tank got what {money} SC buy: {got:,} L, expected {expected:,.0f}",
    )
    check(balance(a) == 0, f"partial: the balance is spent: {balance(a)}")
    told = tp.wait_chat(a, "all you could pay for", 15, mark)
    check(told is not None, f"partial: the player hears it: {told}")
    time.sleep(5)
    check(tt.liters(a, ship) == got, "partial: nothing more without money")
    log(probe(a, "undock"))
    set_balance(a, 600_000)


def off(a) -> None:
    """A tank turned off, or one under 1 % short, gets nothing"""
    you = balance(a)
    ship = dock(a, "H2=0,H2OFF,AT=" + RIM[0])
    time.sleep(10)
    check(tt.liters(a, ship) == 0, f"off: the tank stays empty: {tt.liters(a, ship)}")
    log(probe(a, "undock"))
    ship = dock(a, "H2=0.995,AT=" + RIM[1])
    time.sleep(10)
    got = tt.liters(a, ship)
    check(got == 995_000, f"off: a tank 0.5 % short isn't topped up: {got:,}")
    check(balance(a) == you, f"off: nothing billed: {you - balance(a)} SC")
    log(probe(a, "undock"))


def police(a) -> None:
    """A police ship of an officer on duty fills free (Police), never billed here"""
    with_config(a, **tr.CONFIG, **tt.MONEY, ShipyardBotSteamIds=str(rig.CLIENT_ID))
    reply = " ".join(st.chat(a, f"!oct police duty {STATION}"))
    check("On duty" in reply, f"police: the bot goes on duty: {reply}")
    you, octs = balance(a), oct_money(a)
    ship = dock(a, "H2=0,NAME=PoliceRefill,AT=" + RIM[0])
    full = st.wait_until(lambda: tt.liters(a, ship) == TANK, 60, 2)
    check(bool(full), f"police: the tank fills: {tt.liters(a, ship):,} L")
    time.sleep(5)
    check(balance(a) == you, f"police: the officer paid nothing: {you - balance(a)} SC")
    check(oct_money(a) == octs, "police: OCT got nothing")
    log(probe(a, "undock"))
    log(" ".join(st.chat(a, "!oct police duty off")))
    with_config(a, **tr.CONFIG, **tt.MONEY)


def shipyard(a) -> None:
    """Nothing at the shipyard"""
    you = balance(a)
    found = re.search(r"SHIP (\d+)", probe(a, f"yardship {STATION} H2=0"))
    check(found is not None, "shipyard: a ship at the shipyard's connector")
    ship = found[1] if found else "0"
    time.sleep(15)
    log(probe(a, f"docked {ship}"))
    check(tt.liters(a, ship) == 0, f"shipyard: no hydrogen: {tt.liters(a, ship)}")
    check(balance(a) == you, "shipyard: nothing billed")
    log(probe(a, "undock"))


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(REFILL, 60)
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        with_config(a, **tr.CONFIG, **tt.MONEY)
        log(tr.remove(a))
        # Under 1,000,000 SC: the game's MillionaireClub achievement disconnects an offline client
        set_balance(a, 600_000)
        sa.admin(a, f"!oct market set {STATION} {ICE} 100000")
        for scenario in (new_ship, paid, oxygen, partial, off, police, shipyard):
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
        f"{len(checks) - len(failed)}/{len(checks)} refill rules checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
