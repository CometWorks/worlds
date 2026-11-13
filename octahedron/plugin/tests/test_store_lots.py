"""Checks the items OCT's Stores trade in 1,000 kg lots (MarketLots: ice, SE-1469, and stone,
SE-1471) on the test server with the headless client, through the game's own Store block
requests, like tests/test_store.py.

Checked for each: the Store shows the stock in lots at the book's price per kg times 1,000, in
whole SC, and buys lots back below that; buying 10 lots puts 10,000 kg into the inventory for 10
times the lot price; selling more lots than the inventory holds whole is refused and takes
nothing; selling the 10 lots takes the 10,000 kg for 10 times the buy-back price. Ice costs
0.01 SC per kg: 10 SC a lot, bought back at 9; stone its reference 1 SC per kg: 1,020 SC a
lot, bought back at 980. Prints one line per check and a pass/fail summary;
exit code 0 = all passed.

    OCT_TASK=SE-1471 uv run --no-project --with httpx python tests/test_store_lots.py
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1471")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand, stock  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_store import have, row, trade  # noqa: E402

ICE = "Ore/Ice"
STONE = "Ore/Stone"
LOT = 1000
LOTS = 10
STOCK = 50  # lots at the station


def lots(remote, item: str) -> tuple[int, int]:
    """Buys and sells 10 lots of the item; its lot prices"""
    name = item.split("/")[1].lower()
    admin(remote, f"!oct market set Alpha {item} {STOCK * LOT}")
    _, ask_kg, bid_kg, _ = stock(remote, "Alpha", item)
    ask, bid = round(ask_kg * LOT), round(bid_kg * LOT)
    check(0 < bid < ask, f"{name}: the book sells a lot for {ask} SC, buys at {bid}")
    offer = st.wait_until(lambda: (r := row(remote, item)) and r[0] == STOCK and r, 30)
    check(
        offer == (STOCK, ask),
        f"{name}: the Store offers {STOCK} lots at {ask} SC: {offer}",
    )
    order = row(remote, item, "ORDER")
    check(
        order is not None and order[1] == bid,
        f"{name}: and buys lots at {bid} SC: {order}",
    )

    start = st.money(remote)
    answer = trade(remote, "buy", item, LOTS)
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
    after = st.wait_until(
        lambda: (r := row(remote, item)) and r[0] == STOCK - LOTS and r, 30
    )
    check(
        after and after[0] == STOCK - LOTS,
        f"{name}: the Store has {STOCK - LOTS} lots left: {after}",
    )

    answer = trade(remote, "sell", item, LOTS + 1)
    check(
        "Success" not in answer,
        f"{name}: selling more lots than held is refused: {answer}",
    )
    check(have(remote, item) == LOTS * LOT, f"{name}: and takes nothing")
    answer = trade(remote, "sell", item, LOTS)
    check("SELL Success" in answer, f"{name}: sell {LOTS} lots: {answer}")
    check(have(remote, item) == 0, f"{name}: the 10,000 kg left the inventory")
    sold = st.money(remote)
    check(
        sold["You"] == now["You"] + LOTS * bid,
        f"{name}: paid {LOTS * bid} SC: {now} -> {sold}",
    )
    back = stock(remote, "Alpha", item)[0]
    check(
        back == STOCK * LOT,
        f"{name}: the book's stock is back at {STOCK * LOT:,} kg: {back}",
    )
    return ask, bid


def run(remote) -> None:
    stand(remote, "Alpha")
    probe(remote, "pay me 100000")
    # Flat prices: no drift with the volume, none with the stock against its target
    with_config(
        remote,
        MarketPriceDriftTonnes=0,
        MarketScarcityPercent=100,
        MarketSurplusPercent=100,
    )
    check(lots(remote, ICE) == (10, 9), "ice: 0.01 SC per kg, 10 SC a lot, bought at 9")
    check(
        lots(remote, STONE) == (1020, 980),
        "stone: its reference, 1 SC per kg, 1,020 SC a lot, bought at 980",
    )


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
