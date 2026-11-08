"""Checks on the test server, with the real Modular Encounters Systems mod and the headless
client, that no station's Store trades the Inhibitor Chip: with the setting from before it, the
Store offers the chip and orders it (the control, so the mod's item is there and the check can
fail); with the world's config, the rows go from the running Store at every station, a sale is
refused as an item not offered, stock in the book doesn't bring the chip back, and the hourly
restock leaves it out. Prints one line per check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-866 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_store_excluded.py

Not covered: a cluster. A paid build is the unit tests': the book plans no purchase of an item
without a price (tests/market) and an order short of an item is refused (tests/builds).
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-866")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
from station_tests import admin, check, stand, stock  # noqa: E402
from test_client import with_config  # noqa: E402
from test_store import REFRESH, row, shown, trade  # noqa: E402

CHIP = "Component/InhibitorChip"
MES = 1521905890
MOD_ITEM = (
    f'<ModItem FriendlyName="Modular Encounters Systems"><Name>{MES}.sbm</Name>'
    f"<PublishedFileId>{MES}</PublishedFileId>"
    "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
)
BEFORE = "*/*Prototech*, */Scrap, Component/ZoneChip, Component/*Plushie*"
STATIONS = ("Alpha", "Beta", "Gamma", "Delta", "Moon", "North", "South")


def prepare() -> None:
    rig.prepare_world()
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        path.write_text(
            path.read_text(encoding="utf-8").replace("<Mods>", f"<Mods>{MOD_ITEM}", 1),
            encoding="utf-8",
        )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        link = folder / f"{MES}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(MES))
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, st.PROBE])


def run(remote) -> None:
    stand(remote, "Alpha")

    # The control: as it was, OCT trades the chip at its minimal price / 10 and the tax
    with_config(remote, MarketCatalogExcluded=BEFORE)
    admin(remote, f"!oct market set Alpha {CHIP} 5")
    offer = shown(remote, CHIP, "OFFER", lambda r: r and r[0] == 5)
    order = row(remote, CHIP, "ORDER")
    check(offer == (5, 1_020_000), f"before: the Store sells the chip: {offer}")
    check(order and order[1] == 980_000, f"before: and buys it: {order}")
    answer = trade(remote, "sell", CHIP, 1)
    check(
        "NotEnoughAmount" in answer,
        f"before: a sale gets as far as the items: {answer}",
    )

    # The world's config. The chip's 5 in stock stay in the book, like a saved world's
    with_config(remote)
    check(
        shown(remote, CHIP, "OFFER", lambda r: r is None) is None,
        "the running Store stops selling it",
    )
    check(row(remote, CHIP, "ORDER") is None, "and stops buying it")
    check("NOITEM" in trade(remote, "buy", CHIP, 1), "a purchase is refused")
    check("NOITEM" in trade(remote, "sell", CHIP, 1), "a sale is refused")
    check(stock(remote, "Alpha", CHIP)[1] == -1, "the market has no row for it")
    check(
        row(remote, "Component/SteelPlate") is not None, "the Store still sells plates"
    )

    # The hourly restock brings back what is out, of what it lists
    admin(remote, f"!oct market set Alpha {CHIP} 0")
    admin(remote, "!oct market tick force")
    check(
        row(remote, CHIP) is None and stock(remote, "Alpha", CHIP)[1] == -1,
        "a restock doesn't bring it",
    )

    for station in STATIONS:
        stand(remote, station)
        st.wait_until(lambda: row(remote, "Component/SteelPlate"), REFRESH)
        check(
            row(remote, CHIP) is None and row(remote, CHIP, "ORDER") is None,
            f"{station}: no chip in the Store",
        )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.start_server()
    remote = rig.start_client(extra_dev_folders=[st.STORE_PROBE])
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary("excluded store item")


if __name__ == "__main__":
    sys.exit(main())
