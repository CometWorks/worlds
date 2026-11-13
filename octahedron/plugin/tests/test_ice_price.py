"""Checks ice at the stations' Stores (SE-1469) on the test server with the headless client,
through the game's own Store block requests, like tests/test_store.py.

Checked: MarketPrices puts ice at 0.01 SC per kg and MarketLots makes the Store trade it in
1,000 kg lots: a lot sells for 10 SC and is bought back at 9 (the tax, rounded to whole SC). Buying
10 lots puts 10,000 kg into the inventory for 100 SC; selling them back takes the 10,000 kg for
90 SC; selling more lots than the inventory holds whole is refused and takes nothing. Prints one
line per check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-1469 uv run --no-project --with httpx python tests/test_ice_price.py
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1469")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_store import have, row, trade  # noqa: E402

ICE = "Ore/Ice"
LOT = 1000
LOTS = 10


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
    admin(remote, f"!oct market set Alpha {ICE} 50000")
    shown = admin(remote, f"!oct market show Alpha {ICE}")
    check(
        "OCT buys 0.009 SC" in shown and "sells 0.01 SC" in shown,
        f"the book: 0.01 SC per kg, bought at 0.009: {shown}",
    )
    offer = st.wait_until(lambda: (r := row(remote, ICE)) and r[0] == 50 and r, 30)
    check(offer == (50, 10), f"the Store offers 50 lots at 10 SC: {offer}")
    order = row(remote, ICE, "ORDER")
    check(order is not None and order[1] == 9, f"and buys lots at 9 SC: {order}")

    start = st.money(remote)
    answer = trade(remote, "buy", ICE, LOTS)
    check("BUY Success" in answer, f"buy {LOTS} lots: {answer}")
    check(have(remote, ICE) == LOTS * LOT, f"{LOTS * LOT:,} kg of ice in the inventory")
    now = st.money(remote)
    check(
        now["You"] == start["You"] - LOTS * 10, f"paid {LOTS * 10} SC: {start} -> {now}"
    )
    check(now["OCT"] == start["OCT"] + LOTS * 10, "OCT got it")
    after = st.wait_until(
        lambda: (r := row(remote, ICE)) and r[0] == 50 - LOTS and r, 30
    )
    check(
        after and after[0] == 50 - LOTS, f"the Store has {50 - LOTS} lots left: {after}"
    )

    answer = trade(remote, "sell", ICE, LOTS + 1)
    check("Success" not in answer, f"selling more lots than held is refused: {answer}")
    check(have(remote, ICE) == LOTS * LOT, "and takes nothing")
    answer = trade(remote, "sell", ICE, LOTS)
    check("SELL Success" in answer, f"sell {LOTS} lots: {answer}")
    check(have(remote, ICE) == 0, "the 10,000 kg left the inventory")
    sold = st.money(remote)
    check(sold["You"] == now["You"] + LOTS * 9, f"paid {LOTS * 9} SC: {now} -> {sold}")
    back = admin(remote, f"!oct market show Alpha {ICE}")
    check("stock 50,000" in back, f"the book's stock is back at 50,000 kg: {back}")


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
    return st.summary("ice price")


if __name__ == "__main__":
    sys.exit(main())
