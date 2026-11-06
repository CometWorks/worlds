"""Checks on the test server, with the real Modular Encounters Systems mod and the headless
client, that no station's Store trades or stocks an item players can't make: MES's proprietary
parts of its NPC-only blocks and every prototech item. With MarketCatalogBuildableOnly off (the
control, so the mod's items are there and the check can fail) the Store sells and buys MES's
Proprietary Circuits and a restock fills them up; with the world's config the rows go from the
running Store, both trades are refused, neither a forced nor a timed restock brings stock back,
and no station lists any of them. The restock runs every minute here instead of every 10.
Also prints the catalog's prices at Alpha and Delta, for the price review. Prints one line per
check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-1159 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_store_unbuildable.py

Not covered: a cluster, and the other mods of the world (the server logs what it leaves out).
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-1159")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_store_excluded as se  # noqa: E402
from station_tests import admin, check, log, stand, stock  # noqa: E402
from test_client import with_config  # noqa: E402
from test_store import REFRESH, row, shown, store, trade  # noqa: E402

CIRCUITS = "Component/ProprietaryTech"
THRUST = "Component/MESThrust"
PLATE = "Component/SteelPlate"
UNBUILDABLE = (CIRCUITS, THRUST)
# Every station tops these up from 0, 10 a minute: the plates show that a restock ran
PLATES = f"{PLATE}=600"
PROFILE = f"{PLATES},{CIRCUITS}=600,Component/PrototechFrame=600"
FAST = dict(MarketRestockMinutes=1, MarketStationStock="", MarketMinStock=PROFILE)
# A timed restock comes on the next minute, the market's tick within another
RESTOCK = 150


def restocked(remote, item: str) -> float:
    """OCT's stock of the item at Alpha once a timed restock brought plates back"""
    st.wait_until(lambda: stock(remote, "Alpha", PLATE)[0] > 0, RESTOCK, 5)
    return stock(remote, "Alpha", item)[0]


def run(remote) -> None:
    stand(remote, "Alpha")

    # The control: with the rule off the Store trades MES's circuits, and a restock fills them
    # (a timed restock may add 10 to the 5 set here)
    with_config(remote, MarketCatalogBuildableOnly="false", **FAST)
    admin(remote, f"!oct market set Alpha {CIRCUITS} 5")
    offer = shown(remote, CIRCUITS, "OFFER", lambda r: r and r[0] >= 5)
    check(
        offer is not None and offer[0] >= 5,
        f"before: the Store sells circuits: {offer}",
    )
    check(row(remote, THRUST, "ORDER") is not None, "before: and buys thrust parts")
    admin(remote, f"!oct market set Alpha {CIRCUITS} 0")
    admin(remote, f"!oct market set Alpha {PLATE} 0")
    have = restocked(remote, CIRCUITS)
    check(have > 0, f"before: a timed restock brings circuits: {have}")

    # The world's rule, with stock in the book like a saved world's
    with_config(remote, **FAST)
    admin(remote, f"!oct market set Alpha {CIRCUITS} 5")
    check(
        shown(remote, CIRCUITS, "OFFER", lambda r: r is None) is None,
        "the running Store stops selling circuits",
    )
    for item in UNBUILDABLE:
        check(row(remote, item, "ORDER") is None, f"and stops buying {item}")
    check("NOITEM" in trade(remote, "buy", CIRCUITS, 1), "a purchase is refused")
    check("NOITEM" in trade(remote, "sell", THRUST, 1), "a sale is refused")
    check(row(remote, PLATE) is not None, "the Store still sells plates")
    log_text = rig.server_game_log()
    check(
        all(i in log_text for i in UNBUILDABLE),
        "the server logs MES's parts as what players can't make",
    )

    # Neither restock fills them: back on the control, the book still holds none
    admin(remote, f"!oct market set Alpha {CIRCUITS} 0")
    admin(remote, "!oct market tick force")
    admin(remote, f"!oct market set Alpha {PLATE} 0")
    restocked(remote, PLATE)
    # The control without circuits in the profile, so no restock adds any before the read
    with_config(
        remote, MarketCatalogBuildableOnly="false", **{**FAST, "MarketMinStock": PLATES}
    )
    have = stock(remote, "Alpha", CIRCUITS)[0]
    check(have == 0, f"no restock brought circuits: {have}")
    with_config(remote)
    # Every catalog item's prices at Alpha and Delta, for the price review
    admin(remote, "!oct market show Alpha all")
    admin(remote, "!oct market show Delta all")

    for station in se.STATIONS:
        stand(remote, station)
        # The client may answer NOSTORE until the Store has streamed in
        rows = st.wait_until(
            lambda: [
                l for l in store(remote, "list all") if l.startswith(("OFFER", "ORDER"))
            ],
            REFRESH,
        )
        bad = [l for l in rows if "rototech" in l or any(i in l for i in UNBUILDABLE)]
        check(
            bool(rows) and not bad,
            f"{station}: {len(rows)} rows, none unbuildable: {bad}",
        )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    se.prepare()
    rig.start_server()
    remote = rig.start_client(extra_dev_folders=[st.STORE_PROBE])
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary("unbuildable store item")


if __name__ == "__main__":
    sys.exit(main())
