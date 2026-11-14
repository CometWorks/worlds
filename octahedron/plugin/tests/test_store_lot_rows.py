"""The rows of lots in OCT's Stores read as lots on the vanilla Store screen (SE-1497).

Every ore, ingot and component also comes in lots of 1,000 (MarketLots). With OctahedronMod in the
world, the plugin shows each row of lots to the clients as the mod's item for it, "Silicon Ore
(1 t)" or "Steel Plate (x1000)", counted in pieces and priced per lot, while the row of single
units keeps the item's own name. The trades still move the real item.

"after" (the default): the world has OctahedronMod, built from this working copy and served as
Workshop item 3833786664. Alpha gets 5 lots of each ore, ingot and component of ITEMS. Checked:
each that Alpha trades has a row of lots named as the mod names it, as many lots as its row of
single units has thousands, under the item's own name (ice: lots only); a lot never costs more than 1,000 single units and never buys for less;
no tool or other item has a row of lots; buying 2 lots of silicon ore and 1 lot of steel plates
puts 2,000 kg and 1,000 plates into the inventory for the lot prices, and selling them back takes
them; the Store screen words the rows "Silicon Ore (1 t) 5 pcs" and "Silicon Ore 5,000 kg", with the stock there.
"before": the world has no OctahedronMod, so the rows are the old ones; only the screen's
wording is reported. Both modes save screenshots of the Store screen narrowed to silicon ore
(offers and OCT's orders) and to steel plates (offers) into OUT (default
~/.se-test/<task>-lot-rows-<mode>). At the start it logs which rows of lots Alpha's own stock
fills (STOCK lines). Prints one line per check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-1497 uv run --no-project --with httpx python tests/test_store_lot_rows.py [after|before] [OUT]
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
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
BUILT = rig.TEST_ROOT / f"{rig.TASK}-octahedron-mod"
OCTAHEDRON_MOD = 3833786664
LOT = 1000
STOCK = 5  # lots of each item at the station
SILICON = "Ore/Silicon"
PLATE = "Component/SteelPlate"
ORES = "Stone Ice Iron Nickel Silicon Cobalt Magnesium Silver Gold Platinum Uranium"
INGOTS = "Stone Iron Nickel Silicon Cobalt Magnesium Silver Gold Platinum Uranium"
COMPONENTS = (
    "SteelPlate Construction InteriorPlate MetalGrid SmallTube LargeTube Motor Computer "
    "Display BulletproofGlass Girder Superconductor Thrust Reactor RadioCommunication "
    "Detector SolarCell PowerCell Explosives Medical GravityGenerator"
)
ITEMS = (
    [f"Ore/{s}" for s in ORES.split()]
    + [f"Ingot/{s}" for s in INGOTS.split()]
    + [f"Component/{s}" for s in COMPONENTS.split()]
)
LOTS_ONLY = {"Ore/Ice"}


def prepare() -> None:
    rig.prepare_world()
    if MODE == "after":
        mods = rig.mod_items({OCTAHEDRON_MOD: "OctahedronMod"})
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = rig.WORLD / name
            path.write_text(
                path.read_text(encoding="utf-8").replace("<Mods>", "<Mods>" + mods, 1),
                encoding="utf-8",
            )
    shutil.rmtree(BUILT, ignore_errors=True)
    subprocess.run(
        [sys.executable, str(rig.PLUGIN.parent / "make_octahedron_mod.py"), str(BUILT)],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        target = folder / f"{OCTAHEDRON_MOD}.sbm"
        if target.is_symlink():
            target.unlink()
        shutil.rmtree(target, ignore_errors=True)
        if MODE == "after":
            shutil.copytree(BUILT, target)
    rig.write_plugin_config()


def lot_names() -> dict[str, str]:
    """The mod's name of each item's row of lots, by item"""
    root = ET.parse(BUILT / "Data/PhysicalItems_OctahedronLots.sbc").getroot()
    names = {}
    for d in root.iter("Component"):
        m = re.fullmatch(r"OctahedronLot_(\w+?)_(\w+)", d.findtext("Id/SubtypeId"))
        names[f"{m[1]}/{m[2]}"] = d.findtext("DisplayName")
    return names


def listed(remote, item: str) -> list[tuple[str, str, int, int, str]]:
    """The Store's rows of the item (or of all): kind, item, amount, price and the name the screen gives it"""
    found = []
    for line in store(remote, f"list {item}"):
        f = line.split(" ", 4)
        if f[0] in ("OFFER", "ORDER") and (item == "all" or f[1] == item):
            found.append((f[0], f[1], int(f[2]), int(f[3]), f[4] if len(f) > 4 else ""))
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


def round_trip(remote, item: str, lots: int, ask: int, bid: int) -> None:
    """Buys lots of the item and sells them back, by the lot prices"""
    name = item.split("/")[1]
    start = st.money(remote)
    answer = trade(remote, "buy", item + "@lot", lots)
    check("BUY Success" in answer, f"{name}: buy {lots} lots: {answer}")
    check(have(remote, item) == lots * LOT, f"{name}: {lots * LOT:,} in the inventory")
    now = st.money(remote)
    check(
        now["You"] == start["You"] - lots * ask,
        f"{name}: paid {lots} x {ask} SC: {start} -> {now}",
    )
    check(bid > 0, f"{name}: OCT buys lots at {bid} SC")
    if bid:
        answer = trade(remote, "sell", item + "@lot", lots)
        check("SELL Success" in answer, f"{name}: sell the lots back: {answer}")
        check(have(remote, item) == 0, f"{name}: they left the inventory")
        sold = st.money(remote)
        check(
            sold["You"] == now["You"] + lots * bid,
            f"{name}: got {lots} x {bid} SC: {now} -> {sold}",
        )


def run(remote) -> None:
    stand(remote, "Alpha")
    names = lot_names()
    own = listed(remote, "all")
    for kind, item, amount, price, shown in own:
        if shown in names.values():
            log(f"STOCK Alpha {kind} {shown} {amount} lots at {price} SC")
    probe(remote, "pay me 2000000")
    # Flat prices: no drift with the volume, none with the stock against its target
    with_config(
        remote,
        MarketPriceDriftTonnes=0,
        MarketScarcityPercent=100,
        MarketSurplusPercent=100,
    )
    for item in ITEMS:
        admin(remote, f"!oct market set Alpha {item} {STOCK * LOT}")
    st.wait_until(lambda: any(r[2] == STOCK for r in listed(remote, PLATE)), 30)

    if MODE == "after":
        # One item at a time: a chat answer of the whole Store loses lines
        rows = [r for item in ITEMS for r in listed(remote, item)]
        for item in ITEMS:
            mine = [r for r in rows if r[1] == item]
            if not mine:
                log(f"SKIP {item}: not in Alpha's catalog")
                continue
            # The stock moves with the market's use and restock: the rows must agree, not match STOCK
            lot = {r[0]: r for r in mine if r[4] == names[item]}
            single = {r[0]: r for r in mine if r[4] != names[item]}
            check(
                "OFFER" in lot and lot["OFFER"][2] > 0,
                f"{item}: offers lots as '{names[item]}': {mine}",
            )
            if item in LOTS_ONLY:
                check(not single, f"{item}: lots only: {mine}")
                continue
            if "OFFER" not in lot or "OFFER" not in single:
                check(False, f"{item}: a row of lots and one of single units: {mine}")
                continue
            check(
                lot["OFFER"][2] == single["OFFER"][2] // LOT
                and not single["OFFER"][4].endswith(")"),
                f"{item}: and {single['OFFER'][2]:,} single units under its own name, "
                f"{lot['OFFER'][2]} lots",
            )
            ask, unit_ask = lot["OFFER"][3], single["OFFER"][3]
            bid = lot["ORDER"][3] if "ORDER" in lot else 0
            unit_bid = single["ORDER"][3] if "ORDER" in single else 0
            check(
                ask <= unit_ask * LOT and (bid >= unit_bid * LOT or "ORDER" not in lot),
                f"{item}: the lot is the better deal: sells for {ask} <= {unit_ask} x {LOT}, "
                f"buys at {bid} >= {unit_bid} x {LOT}",
            )
        every = listed(remote, "all")
        lotted = {r[1] for r in every if r[4] in names.values()}
        others = {
            r[1]
            for r in every
            if r[1].split("/")[0] not in ("Ore", "Ingot", "Component")
        }
        check(
            all(i.split("/")[0] in ("Ore", "Ingot", "Component") for i in lotted),
            f"rows of lots only for ores, ingots and components ({len(lotted)}; "
            f"{len(others)} other items sold here): "
            f"{sorted(i for i in lotted if i.split('/')[0] not in ('Ore', 'Ingot', 'Component'))}",
        )
        prices = {(r[0], r[1]): r[3] for r in rows if r[4] in names.values()}
        for item, lots in ((SILICON, 2), (PLATE, 1)):
            round_trip(
                remote,
                item,
                lots,
                prices[("OFFER", item)],
                prices.get(("ORDER", item), 0),
            )

    OUT.mkdir(parents=True, exist_ok=True)
    offers = shot(remote, SILICON, "offers")
    orders = shot(remote, SILICON, "orders")
    plates = shot(remote, PLATE, "offers")
    for screen in (offers, orders, plates):
        log(f"SCREEN {MODE} {screen}")
    if MODE == "after":
        check(
            re.search(r"OFFER Silicon Ore \(1 t\) \d+ pcs", offers)
            and re.search(r"OFFER Silicon Ore [\d,]+ kg", offers),
            f"the screen offers 'Silicon Ore (1 t)' in pcs and 'Silicon Ore' in kg: {offers}",
        )
        check(
            "ORDER Silicon Ore (1 t)" in orders,
            f"OCT's orders list 'Silicon Ore (1 t)': {orders}",
        )
        check(
            re.search(r"OFFER Steel Plate \(x1000\) \d+ pcs", plates)
            and re.search(r"OFFER Steel Plate [\d,]+ pcs", plates),
            f"the screen offers 'Steel Plate (x1000)' and 'Steel Plate': {plates}",
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
