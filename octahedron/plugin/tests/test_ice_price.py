"""Checks the price of ice at the stations' Stores (SE-1469) on the test server with the headless
client, through the game's own Store block requests, like tests/test_store.py.

Checked: MarketPrices puts ice at 0.01 SC per kg, and the Store shows it at 1 SC per kg, the
lowest whole SC price it can show, and buys no ice back (a bid has to stay under the ask); buying
10,000 kg costs 10,000 SC. Prints one line per check and a pass/fail summary; exit code 0 = all
passed.

    OCT_TASK=SE-1469 uv run --no-project --with httpx python tests/test_ice_price.py
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1469")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand  # noqa: E402
from test_client import log  # noqa: E402
from test_store import have, row, trade  # noqa: E402

ICE = "Ore/Ice"
KG = 10_000


def run(remote) -> None:
    stand(remote, "Alpha")
    probe(remote, "pay me 100000")
    admin(remote, f"!oct market set Alpha {ICE} 50000")
    shown = admin(remote, f"!oct market show Alpha {ICE}")
    check("sells 1 SC" in shown, f"the book sells ice at 1 SC per kg: {shown}")
    offer = st.wait_until(lambda: row(remote, ICE), 20)
    check(
        offer is not None and offer[1] == 1,
        f"the Store offers ice at 1 SC per kg: {offer}",
    )
    check(row(remote, ICE, "ORDER") is None, "the Store doesn't buy ice")

    start = st.money(remote)
    answer = trade(remote, "buy", ICE, KG)
    check("BUY Success" in answer, f"buy: {answer}")
    check(have(remote, ICE) == KG, f"{KG:,} kg of ice in the inventory")
    now = st.money(remote)
    check(now["You"] == start["You"] - KG, f"paid {KG:,} SC: {start} -> {now}")
    check(now["OCT"] == start["OCT"] + KG, "OCT got it")
    answer = trade(remote, "sell", ICE, KG)
    check("Success" not in answer, f"selling ice back is refused: {answer}")


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
