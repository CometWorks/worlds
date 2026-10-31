"""Checks the station markets on the test server with the headless client, through the game's
own Store block requests: the store probe (tests/store-probe) sends what the Store screen sends,
with the character's inventory on the other side.

Checked: the Store shows OCT's real stock and prices, as a station store of an NPC, with no
reputation bonus even for a friend of OCT; buying and selling move the items and the money and
change the stock, the 2 % tax on both sides; refusals (too much, not enough money, not having
the items, an item not offered); the price drifting with the volume; Alpha's specialty at its price
up to the day's quota, then the normal one; OCT stops buying at its limit; the hourly restock
brings back what is out, only that; the zone chips at Moon Station only, at a fixed price with
the stock topped up, none bought back; the settings as config; and after a restart the same
stock. Prints one line per check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-833 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_store.py

Not covered: the screen itself and a cluster (tests/world_config_cluster.py buys and sells there).
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-833")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import admin, check, probe, stand, stock  # noqa: E402
from test_client import log, with_config  # noqa: E402

PLATE = "Component/SteelPlate"
IRON = "Ingot/Iron"
PLATINUM = "Ingot/Platinum"
GOLD = "Ingot/Gold"
CHIP = "Component/ZoneChip"
# The stores refresh this often and after each trade
REFRESH = 20


def store(remote, args: str) -> list[str]:
    lines = st.chat(remote, f"/store {args}")
    log(f"/store {args} -> {lines}")
    return lines


def row(remote, item: str, kind: str = "OFFER") -> tuple[int, int] | None:
    """What the Store shows the client of the item: amount and price"""
    for line in store(remote, f"list {item}"):
        f = line.split(" ")
        if f[0] == kind:
            return int(f[2]), int(f[3])
    return None


def shown(remote, item: str, kind: str, want) -> tuple[int, int] | None:
    """The row once want(row) says the Store has refreshed, or the last one"""
    last = [None]

    def look():
        last[0] = row(remote, item, kind)
        return want(last[0])

    st.wait_until(look, REFRESH)
    return last[0]


def have(remote, item: str) -> float:
    for line in store(remote, f"have {item}"):
        if line.startswith("HAVE "):
            return float(line.split(" ")[1])
    return -1.0


def trade(remote, verb: str, item: str, amount: int) -> str:
    lines = store(remote, f"{verb} {item} {amount}")
    return " ".join(lines)


def run(remote) -> None:
    stand(remote, "Alpha")

    # What it shows
    lines = store(remote, f"list {PLATE}")
    head = next((l for l in lines if l.startswith("STORE ")), "")
    check("NPC True" in head, f"the Store is a station's: {head}")
    check("BONUS 1 1" in head, f"no reputation bonus: {head}")
    have_plates, ask, bid, _ = stock(remote, "Alpha", PLATE)
    check(
        have_plates > 0 and ask > 0 and bid > 0 and bid < ask,
        f"the book: {have_plates} {ask} {bid}",
    )
    offer = row(remote, PLATE)
    order = row(remote, PLATE, "ORDER")
    check(
        offer == (int(have_plates), int(ask)),
        f"the offer is the real stock and price: {offer}",
    )
    check(
        order is not None and order[1] == int(bid), f"the order is OCT's price: {order}"
    )
    check(row(remote, "Component/NoSuch") is None, "an item not sold isn't listed")

    # Buying
    start = st.money(remote)
    check(have(remote, PLATE) == 0, "no plates yet")
    answer = trade(remote, "buy", PLATE, 10)
    check("BUY Success" in answer, f"buy: {answer}")
    check(have(remote, PLATE) == 10, "10 plates in the inventory")
    now = st.money(remote)
    cost = 10 * int(ask)
    check(now["You"] == start["You"] - cost, f"paid {cost}: {start} -> {now}")
    check(now["OCT"] == start["OCT"] + cost, "OCT got it")
    after, *_ = stock(remote, "Alpha", PLATE)
    check(after == have_plates - 10, f"the book's stock went down: {after}")
    check(row(remote, PLATE)[0] == int(after), "and the Store's")

    # Refused
    too_many = int(after) + 1
    answer = trade(remote, "buy", PLATE, too_many)
    check("BUY WrongAmount" in answer, f"more than the stock: {answer}")
    answer = trade(remote, "sell", PLATE, 11)
    check("SELL NotEnoughAmount" in answer, f"more than the character has: {answer}")
    check(
        "NOITEM" in trade(remote, "buy", "Component/ZoneChip", 1),
        "chips aren't sold here",
    )
    check(st.money(remote)["You"] == now["You"], "refusals cost nothing")
    check(have(remote, PLATE) == 10, "refusals keep the plates")

    # Selling
    _, _, bid_now, _ = stock(remote, "Alpha", PLATE)
    answer = trade(remote, "sell", PLATE, 10)
    check("SELL Success" in answer, f"sell: {answer}")
    check(have(remote, PLATE) == 0, "the plates left the inventory")
    sold = st.money(remote)
    check(sold["You"] == now["You"] + 10 * int(bid_now), f"paid {bid_now} each: {sold}")
    check(sold["OCT"] == now["OCT"] - 10 * int(bid_now), "by OCT")
    back, *_ = stock(remote, "Alpha", PLATE)
    check(back == have_plates, f"the stock is back: {back}")
    spread = (start["You"] - sold["You"]) / cost
    check(
        0 < spread <= 0.1,
        f"a round trip costs the taxes, rounding to SC aside: {spread:.3f}",
    )

    # The price drifts with the volume: 100 t at 1 SC or more
    with_config(remote, MarketPriceDriftTonnes=0.1)
    _, ask_before, *_ = stock(remote, "Alpha", PLATE)
    trade(remote, "buy", PLATE, 5)
    _, ask_after, *_ = stock(remote, "Alpha", PLATE)
    check(
        ask_after > ask_before,
        f"100 kg bought raises the price: {ask_before} -> {ask_after}",
    )
    check(
        shown(remote, PLATE, "OFFER", lambda r: r and r[1] == int(ask_after))[1]
        == int(ask_after),
        "the Store shows the new price",
    )
    with_config(remote, MarketPriceDriftTonnes=0)
    _, _, bid_flat, _ = stock(remote, "Alpha", PLATE)
    shown(remote, PLATE, "ORDER", lambda r: r and r[1] == int(bid_flat))
    trade(remote, "sell", PLATE, 5)

    # The specialty: its own price up to the quota, the normal one after
    probe(remote, "pay me 100000")
    admin(remote, f"!oct market set Alpha {PLATINUM} 100")
    with_config(remote, MarketPriceDriftTonnes=0, MarketSpecialtyDailyTonnes=0.005)
    admin(remote, "!oct market tick force")
    _, plat_ask, plat_bid, wanted = stock(remote, "Alpha", PLATINUM)
    shown(remote, PLATINUM, "ORDER", lambda r: r and r[1] == int(plat_bid))
    check(wanted == 5, f"the specialty up to the day's 5 kg: {wanted}")
    check(
        plat_bid < plat_ask,
        f"it sells for more than the station pays: {plat_bid} {plat_ask}",
    )
    _, beta_ask, beta_bid, _ = stock(remote, "Beta", PLATINUM)
    check(
        plat_bid > beta_bid, f"Alpha pays more for it than Beta: {plat_bid} {beta_bid}"
    )
    check("BUY Success" in trade(remote, "buy", PLATINUM, 8), "platinum bought")
    before = st.money(remote)["You"]
    check(
        "SELL WrongAmount" in trade(remote, "sell", PLATINUM, 6),
        "past the quota at once: refused",
    )
    check("SELL Success" in trade(remote, "sell", PLATINUM, 5), "the quota sold")
    check(
        st.money(remote)["You"] == before + 5 * int(plat_bid),
        "at the specialty's price",
    )
    _, _, normal, wanted = stock(remote, "Alpha", PLATINUM)
    check(normal == beta_bid, f"past the quota the normal price: {normal}")
    shown(remote, PLATINUM, "ORDER", lambda r: r and r[1] == int(normal))
    before = st.money(remote)["You"]
    check("SELL Success" in trade(remote, "sell", PLATINUM, 3), "the rest sold")
    check(st.money(remote)["You"] == before + 3 * int(normal), "at the normal price")
    with_config(remote, MarketPriceDriftTonnes=0)

    # OCT's limit
    admin(remote, f"!oct market set Alpha {GOLD} 1000000")
    check(
        shown(remote, GOLD, "ORDER", lambda r: r is None) is None,
        f"200 t of gold: OCT stops buying: {stock(remote, 'Alpha', GOLD)}",
    )
    check(row(remote, GOLD) is not None, "and still sells")

    # The hourly restock brings back what is completely out
    admin(remote, f"!oct market set Alpha {IRON} 0")
    admin(remote, f"!oct market set Alpha {PLATE} 7")
    check(stock(remote, "Alpha", IRON)[0] == 0, "no iron")
    reply = admin(remote, "!oct market tick")
    check("restocked" not in reply, f"not a new hour: {reply}")
    check(stock(remote, "Alpha", IRON)[0] == 0, "still none")
    reply = admin(remote, "!oct market tick force")
    check("restocked" in reply, f"a new hour: {reply}")
    check(stock(remote, "Alpha", IRON)[0] == 2000, "back to the minimum")
    check(stock(remote, "Alpha", PLATE)[0] == 7, "7 plates aren't out: left alone")
    with_config(remote, MarketStationStock=f"Alpha Station={IRON}=11")
    admin(remote, f"!oct market set Alpha {IRON} 0")
    admin(remote, "!oct market tick force")
    check(stock(remote, "Alpha", IRON)[0] == 11, "the station's profile")
    with_config(remote)

    # The tax is a setting: nothing else moves the prices
    with_config(remote, MarketTaxPercent=10, MarketPriceDriftTonnes=0)
    _, ask10, bid10, _ = stock(remote, "Alpha", PLATE)
    with_config(remote, MarketTaxPercent=2, MarketPriceDriftTonnes=0)
    _, ask2, bid2, _ = stock(remote, "Alpha", PLATE)
    check(
        ask10 > ask2 and bid10 < bid2,
        f"the tax: {ask2}/{bid2} at 2 %, {ask10}/{bid10} at 10 %",
    )

    # A friend of OCT pays the same: reputation changes no prices
    probe(remote, "rep OCT 1500")
    _, ask2, *_ = stock(remote, "Alpha", PLATE)
    shown(remote, PLATE, "OFFER", lambda r: r and r[1] == int(ask2))
    lines = store(remote, f"list {PLATE}")
    head = next((l for l in lines if l.startswith("STORE ")), "")
    check("BONUS 1 1" in head, f"a friend: no bonus: {head}")
    before = st.money(remote)["You"]
    check("BUY Success" in trade(remote, "buy", PLATE, 1), "a friend buys")
    check(st.money(remote)["You"] == before - int(ask2), "at the same price")
    trade(remote, "sell", PLATE, 1)
    probe(remote, "rep OCT 0")

    # The game's economy tick neither wipes nor generates
    counted = admin(remote, "!oct market stores tick")
    check(row(remote, PLATE) is not None, f"after an economy tick: {counted}")

    # Zone chips: Moon only, a fixed price, topped up
    stand(remote, "Moon")
    chip = row(remote, CHIP)
    check(chip == (100, 1000), f"Moon sells chips: {chip}")
    check(row(remote, CHIP, "ORDER") is None, "and doesn't buy them")
    check("BUY Success" in trade(remote, "buy", CHIP, 2), "chips bought")
    check(have(remote, CHIP) == 2, "2 chips")
    stock_now, ask_now, *_ = stock(remote, "Moon", CHIP)
    check(
        stock_now == 98 and ask_now == 1000,
        f"the stock goes down, the price stays: {stock_now} {ask_now}",
    )
    admin(remote, "!oct market tick")
    check(stock(remote, "Moon", CHIP)[0] == 100, "topped up at the next tick")
    stand(remote, "Alpha")
    admin(remote, f"!oct market set Alpha {PLATE} 321")


def after_restart(remote) -> None:
    stand(remote, "Alpha")
    check(stock(remote, "Alpha", PLATE)[0] == 321, "the stock survives a restart")
    check(
        shown(remote, PLATE, "OFFER", lambda r: r and r[0] == 321)[0] == 321,
        "and the Store shows it",
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
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check("octahedron-market 2" in sandbox, "the saved Sandbox.sbc holds the market")
    rig.start_server()
    remote = rig.start_client(extra_dev_folders=[st.STORE_PROBE])
    try:
        after_restart(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Market" in line):
            log("server: " + line.strip())
    return st.summary("store")


if __name__ == "__main__":
    sys.exit(main())
