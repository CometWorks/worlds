"""The rows of 1,000 kg lots in OCT's Stores read as lots on the vanilla Store screen (SE-1497).

With OctahedronMod in the world, the plugin shows each row of lots (MarketLots) to the clients as
the mod's "(1 t)" item, "Silicon Ore (1 t)", counted in pieces and priced per lot, while the row
of single units keeps the item's own name, counted in kg. The trades still move the real item.

"after" (the default): the world has OctahedronMod, built from this working copy and served as
Workshop item 3833786664. Checked: every lot item's row of lots carries its "(1 t)" item and no
single-unit row does; buying 2 lots of silicon ore puts 2,000 kg of silicon ore into the
inventory for 2 times the lot price, and selling them back takes it; the Store screen words the
rows "Silicon Ore (1 t) 50 pcs" and "Silicon Ore 50,000 kg". "before": the world has no
OctahedronMod, so the rows are the old ones; only the screen's wording is reported. Both modes
save screenshots of the Store screen narrowed to silicon ore, its offers and OCT's orders, into
OUT (default ~/.se-test/<task>-lot-rows-<mode>). Prints one line per check and a pass/fail
summary; exit code 0 = all passed.

    OCT_TASK=SE-1497 uv run --no-project --with httpx python tests/test_store_lot_rows.py [after|before] [OUT]
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1497")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_store import have, store, trade  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else "after"
OUT = (
    Path(sys.argv[2])
    if len(sys.argv) > 2
    else rig.TEST_ROOT / f"{rig.TASK}-lot-rows-{MODE}"
)
OCTAHEDRON_MOD = 3833786664
LOT = 1000
STOCK = 50  # lots of each item at the station
SILICON = "Ore/Silicon"
# MarketLots of octahedron.cfg, and the name of each item's row of lots
LOTS = {
    "Ore/Ice": "Ice (1 t)",
    "Ore/Stone": "Stone (1 t)",
    "Ore/Iron": "Iron Ore (1 t)",
    "Ore/Nickel": "Nickel Ore (1 t)",
    "Ore/Silicon": "Silicon Ore (1 t)",
    "Ore/Cobalt": "Cobalt Ore (1 t)",
    "Ore/Magnesium": "Magnesium Ore (1 t)",
    "Ingot/Stone": "Gravel (1 t)",
    "Ingot/Iron": "Iron Ingot (1 t)",
    "Ingot/Silicon": "Silicon Wafer (1 t)",
    "Ingot/Nickel": "Nickel Ingot (1 t)",
}


def prepare() -> None:
    rig.prepare_world()
    built = rig.TEST_ROOT / f"{rig.TASK}-octahedron-mod"
    if MODE == "after":
        mods = rig.mod_items({OCTAHEDRON_MOD: "OctahedronMod"})
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = rig.WORLD / name
            path.write_text(
                path.read_text(encoding="utf-8").replace("<Mods>", "<Mods>" + mods, 1),
                encoding="utf-8",
            )
        shutil.rmtree(built, ignore_errors=True)
        subprocess.run(
            [
                sys.executable,
                str(rig.PLUGIN.parent / "make_octahedron_mod.py"),
                str(built),
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        target = folder / f"{OCTAHEDRON_MOD}.sbm"
        if target.is_symlink():
            target.unlink()
        shutil.rmtree(target, ignore_errors=True)
        if MODE == "after":
            shutil.copytree(built, target)
    rig.write_plugin_config()


def listed(remote, item: str) -> list[tuple[str, int, int, str]]:
    """The Store's rows of the item: kind, amount, price and the name the screen gives it"""
    found = []
    for line in store(remote, f"list {item}"):
        f = line.split(" ", 4)
        if f[0] in ("OFFER", "ORDER") and f[1] == item:
            found.append((f[0], int(f[2]), int(f[3]), f[4] if len(f) > 4 else ""))
    return found


def shot(remote, item: str, tab: str) -> str:
    """Opens the Store screen narrowed to the item's rows, saves a screenshot, closes it"""
    lines = store(remote, f"show {item}{' orders' if tab == 'orders' else ''}")
    time.sleep(3)
    path = OUT / f"{item.replace('/', '-')}-{tab}.png"
    remote.screenshot_save(str(path))
    time.sleep(3)  # The screenshot is written after the call returns
    remote.key("ESCAPE")
    time.sleep(1)
    log(f"SCREENSHOT {path}")
    return " ".join(line for line in lines if line.startswith("SHOW "))


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
    for item in LOTS:
        admin(remote, f"!oct market set Alpha {item} {STOCK * LOT}")
    st.wait_until(lambda: any(r[1] == STOCK for r in listed(remote, SILICON)), 30)

    if MODE == "after":
        for item, name in LOTS.items():
            rows = listed(remote, item)
            lots = [r for r in rows if r[3] == name]
            single = [r for r in rows if r[3] != name]
            check(
                any(r[0] == "OFFER" and r[1] == STOCK for r in lots),
                f"{item}: the row of lots offers {STOCK} '{name}': {rows}",
            )
            check(
                all(r[1] != STOCK * LOT for r in lots)
                and all(r[1] != STOCK for r in single if r[0] == "OFFER"),
                f"{item}: only the row of lots is named a lot: {rows}",
            )
            if item != "Ore/Ice":
                check(
                    any(r[0] == "OFFER" and r[1] == STOCK * LOT for r in single),
                    f"{item}: the row of single units keeps its name: {single}",
                )

        rows = listed(remote, SILICON)
        ask = next(r[2] for r in rows if r[0] == "OFFER" and r[3] == LOTS[SILICON])
        bid = next((r[2] for r in rows if r[0] == "ORDER" and r[3] == LOTS[SILICON]), 0)
        start = st.money(remote)
        answer = trade(remote, "buy", SILICON + "@lot", 2)
        check("BUY Success" in answer, f"buy 2 lots of silicon ore: {answer}")
        check(
            have(remote, SILICON) == 2 * LOT, "2,000 kg of silicon ore in the inventory"
        )
        now = st.money(remote)
        check(
            now["You"] == start["You"] - 2 * ask,
            f"paid 2 x {ask} SC: {start} -> {now}",
        )
        check(bid > 0, f"OCT buys silicon ore lots at {bid} SC")
        if bid:
            answer = trade(remote, "sell", SILICON + "@lot", 2)
            check("SELL Success" in answer, f"sell the 2 lots back: {answer}")
            check(have(remote, SILICON) == 0, "the 2,000 kg left the inventory")
            sold = st.money(remote)
            check(
                sold["You"] == now["You"] + 2 * bid,
                f"got 2 x {bid} SC: {now} -> {sold}",
            )

    OUT.mkdir(parents=True, exist_ok=True)
    offers = shot(remote, SILICON, "offers")
    orders = shot(remote, SILICON, "orders")
    log(f"SCREEN {MODE} {offers}")
    log(f"SCREEN {MODE} {orders}")
    if MODE == "after":
        check(
            f"OFFER Silicon Ore (1 t) {STOCK} pcs" in offers
            and f"OFFER Silicon Ore {STOCK * LOT:,} kg" in offers,
            f"the screen offers 'Silicon Ore (1 t)' in pcs and 'Silicon Ore' in kg: {offers}",
        )
        check(
            "ORDER Silicon Ore (1 t)" in orders,
            f"OCT's orders list 'Silicon Ore (1 t)': {orders}",
        )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client(render=True, extra_dev_folders=[st.STORE_PROBE])
    remote.key("V")  # First person: the screenshots show the screen, not the character
    log("joined")
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary(f"store lot rows {MODE}")


if __name__ == "__main__":
    sys.exit(main())
