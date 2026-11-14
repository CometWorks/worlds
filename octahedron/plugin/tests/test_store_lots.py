"""Checks the items OCT's Stores also trade in 1,000 kg lots (MarketLots, SE-1469 and SE-1471) on
the test server with the headless client, through the game's own Store block requests, like
tests/test_store.py.

Checked for ice (lots only), stone and iron ingots: the Store's row of lots shows the stock in
lots at the book's lot price, a row of single units next to it for stone and iron at the per-kg
price; OCT buys lots back below the lot price; buying 10 lots puts 10,000 kg into the inventory for
10 times the lot price; selling more lots than the inventory holds whole is refused and takes
nothing; selling the 10 lots takes the 10,000 kg for 10 times the buy-back price; buying 57 kg
on the row of single units charges 57 times its price. Ice costs 41 SC a lot, bought back at 39 (0.04 SC a kg and the tax);
stone 1,000 SC, bought back at 980, and 1 SC a kg (a lot costs no more than its kg, SE-1497). At the end it prints OCT's prices of the lot
items and what refining makes of them (LOTPRICE lines). Prints one line per check and a pass/fail
summary; exit code 0 = all passed.

    OCT_TASK=SE-1471 uv run --no-project --with httpx python tests/test_store_lots.py
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-1471")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_store import have, store, trade  # noqa: E402

ICE = "Ore/Ice"
STONE = "Ore/Stone"
IRON = "Ingot/Iron"
LOT = 1000
LOTS = 10
STOCK = 50  # lots at the station
SINGLE = 57  # kg bought on the row of single units
PRICED = [
    "Ore/Ice",
    "Ore/Stone",
    "Ore/Iron",
    "Ore/Nickel",
    "Ore/Silicon",
    "Ore/Cobalt",
    "Ore/Magnesium",
    "Ingot/Stone",
    "Ingot/Iron",
    "Ingot/Nickel",
    "Ingot/Silicon",
    "Ingot/Cobalt",
    "Ingot/Magnesium",
]


def rows(remote, item: str, kind: str) -> list[tuple[int, int]]:
    """The Store's rows of the item, amount and price, cheapest first"""
    found = []
    for line in store(remote, f"list {item}"):
        f = line.split(" ")
        if f[0] == kind and f[1] == item:
            found.append((int(f[2]), int(f[3])))
    return sorted(found, key=lambda r: r[1])


def book(remote, item: str) -> dict[str, float]:
    """The book's prices of the item at Alpha: per kg and per lot, SC"""
    reply = admin(remote, f"!oct market show Alpha {item}")
    num = r"([\d,.]+)"
    m = re.search(
        rf"OCT buys {num} SC up to {num}, sells {num} SC, stock {num}"
        rf"(?:; lots of {num}: buys {num} SC, sells {num} SC)?",
        reply,
    )
    if not m:
        return {}
    v = [float(x.replace(",", "")) if x else 0.0 for x in m.groups()]
    return {"bid": v[0], "ask": v[2], "lot_bid": v[5], "lot_ask": v[6]}


def lots(remote, item: str, single: bool) -> tuple[int, int, int, int]:
    """Trades the item by the lot, and by the kg when it has that row: lot ask, lot bid, kg ask, kg bid"""
    name = item.split("/")[1].lower()
    admin(remote, f"!oct market set Alpha {item} {STOCK * LOT}")
    b = book(remote, item)
    ask, bid = round(b.get("lot_ask", 0)), round(b.get("lot_bid", 0))
    unit_ask, unit_bid = round(b.get("ask", 0)), round(b.get("bid", 0))
    check(
        0 < bid < ask, f"{name}: the book sells a lot for {ask} SC, buys at {bid}: {b}"
    )
    want = [(STOCK * LOT, unit_ask), (STOCK, ask)] if single else [(STOCK, ask)]
    offers = st.wait_until(
        lambda: (r := rows(remote, item, "OFFER")) == want and r, 30
    ) or rows(remote, item, "OFFER")
    check(offers == want, f"{name}: the Store offers {want}: {offers}")
    orders = rows(remote, item, "ORDER")
    check(
        orders and orders[-1][1] == bid and len(orders) == (2 if unit_bid else 1),
        f"{name}: and buys lots at {bid} SC, single units at {unit_bid or 'none'}: {orders}",
    )
    if single and unit_bid:
        check(
            unit_bid * LOT < ask and bid < unit_ask * LOT,
            f"{name}: no row buys at what the other sells for",
        )

    start = st.money(remote)
    answer = trade(remote, "buy", item + "@lot", LOTS)
    check("BUY Success" in answer, f"{name}: buy {LOTS} lots: {answer}")
    check(
        have(remote, item) == LOTS * LOT, f"{name}: {LOTS * LOT:,} kg in the inventory"
    )
    now = st.money(remote)
    check(
        now["You"] == start["You"] - LOTS * ask,
        f"{name}: paid {LOTS * ask} SC: {start} -> {now}",
    )
    check(now["OCT"] == start["OCT"] + LOTS * ask, f"{name}: OCT got it")

    answer = trade(remote, "sell", item + "@lot", LOTS + 1)
    check(
        "Success" not in answer,
        f"{name}: selling more lots than held is refused: {answer}",
    )
    check(have(remote, item) == LOTS * LOT, f"{name}: and takes nothing")
    answer = trade(remote, "sell", item + "@lot", LOTS)
    check("SELL Success" in answer, f"{name}: sell {LOTS} lots: {answer}")
    check(have(remote, item) == 0, f"{name}: the 10,000 kg left the inventory")
    sold = st.money(remote)
    check(
        sold["You"] == now["You"] + LOTS * bid,
        f"{name}: paid {LOTS * bid} SC: {now} -> {sold}",
    )

    if single:
        answer = trade(remote, "buy", item, SINGLE)
        check("BUY Success" in answer, f"{name}: buy {SINGLE} kg: {answer}")
        check(have(remote, item) == SINGLE, f"{name}: {SINGLE} kg in the inventory")
        paid = st.money(remote)
        check(
            paid["You"] == sold["You"] - SINGLE * unit_ask,
            f"{name}: paid {SINGLE * unit_ask} SC for {SINGLE} kg: {sold} -> {paid}",
        )
        if unit_bid:
            answer = trade(remote, "sell", item, SINGLE)
            check("SELL Success" in answer, f"{name}: sell {SINGLE} kg back: {answer}")
            check(have(remote, item) == 0, f"{name}: the {SINGLE} kg left")
    return ask, bid, unit_ask, unit_bid


def run(remote) -> None:
    stand(remote, "Alpha")
    probe(remote, "pay me 400000")
    # Flat prices: no drift with the volume, none with the stock against its target
    with_config(
        remote,
        MarketPriceDriftTonnes=0,
        MarketScarcityPercent=100,
        MarketSurplusPercent=100,
    )
    check(
        lots(remote, ICE, False)[:2] == (41, 39),
        "ice: lots only, 41 SC a lot, bought at 39",
    )
    check(
        lots(remote, STONE, True) == (1000, 980, 1, 0),
        "stone: 1,000 SC a lot, bought at 980; 1 SC a kg, not bought by the kg",
    )
    ask, bid, unit_ask, unit_bid = lots(remote, IRON, True)
    check(unit_bid > 0, f"iron ingots: bought by the kg too, at {unit_bid}")
    for item in PRICED:
        log(f"LOTPRICE {item} {book(remote, item)}")


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client(extra_dev_folders=[st.STORE_PROBE])
    log("joined")
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary("store lots")


if __name__ == "__main__":
    sys.exit(main())
