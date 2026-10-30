"""Checks the station market on the test server with the headless client, through the
kiosk client mod's requests as test_kiosks.py sends them (the mod's admin command
/kiosk, the same mod messages the touch screens send), at Alpha Station.

Checked: OCT's daily stock and its prices (140 % and 60 % of the reference), the
game's generated store items gone from OCT's stations (and back with the market
off), buying from OCT and the price drifting up, selling Alpha's specialty at 80 %
and past the day's quota at 60 %, a faction offer bought by a player with the 5 %
fee to OCT, a player's order filled by the faction and canceled, the trading rank
for the Faction switch, the listing limit, expiry, and after a restart the same
listings, faction balance and OCT stock. Prints one line per check and a pass/fail summary;
exit code 0 = all passed.

    OCT_TASK=SE-684 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_market.py

Not covered: the touch screens themselves (tests/test_kiosk_screen.py), and a cluster.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-684")
import rig  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
from test_client import log, with_config  # noqa: E402

PLATE = "Component/SteelPlate"
IRON = "Ingot/Iron"
PLATINUM = "Ingot/Platinum"
check = kiosks.check


class Market:
    """What /kiosk printed for one answer of the server, with the market records"""

    def __init__(self, lines: list[str]):
        self.state = kiosks.State(lines)
        self.ok = self.state.ok
        self.message = self.state.message
        self.balance = self.faction_balance = -1
        self.rows: dict[str, dict[str, str]] = {}
        self.listings: list[dict[str, str]] = []
        for line in lines:
            words = line.split(" ")
            fields = dict(w.split("=", 1) for w in words if "=" in w)
            if words[0] == "balance":
                self.balance = int(fields["mine"])
                self.faction_balance = int(fields["faction"])
            elif words[0] == "market":
                self.rows[words[1]] = fields
            elif words[0] == "listing":
                self.listings.append(
                    {"id": words[1], "kind": words[2], "item": words[3], **fields}
                )

    def price(self, item: str, side: str) -> int:
        """OCT's price in milli-SC: buys or sells"""
        return int(self.rows.get(item, {}).get(side, "0"))

    def stock(self, item: str) -> float:
        return float(self.rows.get(item, {}).get("stock", "0"))

    def mine(self, item: str) -> float:
        return self.state.amount(item, "mine")

    def __str__(self) -> str:
        return str(self.state)


def market(remote, args: str = "market", wait: float = 4.0) -> Market:
    time.sleep(0.6)  # the plugin's cooldown between requests
    return Market(rig.chat(remote, f"/kiosk {args}", wait))


def expect(remote, args: str, what: str, ok: bool = True, message: str = "") -> Market:
    m = market(remote, args)
    check(m.ok == ok and message in m.message, f"{what}: {m.message} | {m}")
    return m


def admin(remote, text: str, wait: float = 3.0) -> str:
    reply = " ".join(rig.chat(remote, text, wait))
    log(f"{text} -> {reply}")
    return reply


def stores(remote, tick: bool = False) -> dict[str, int]:
    reply = admin(remote, "!oct market stores" + (" tick" if tick else ""), 4)
    return {s: int(n) for s, n in re.findall(r"STORE (\S+) (\d+)", reply)}


def stand(remote) -> None:
    reply = kiosks.probe(remote, "kiosk Alpha")
    match = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    if match:
        remote.character_teleport(*(float(v) for v in match.groups()))
        time.sleep(3)


def run(remote) -> None:
    stand(remote)
    m = expect(remote, "market", "the market at Alpha's kiosk")
    check(m.balance == 25_000, f"a new player has 25,000 SC: {m.balance}")
    check(m.stock(PLATE) == 300, f"OCT's daily stock: 300 plates: {m.stock(PLATE)}")
    check(m.stock(IRON) == 2000, f"OCT's daily stock: 2,000 iron: {m.stock(IRON)}")
    sells, buys = m.price(PLATE, "sells"), m.price(PLATE, "buys")
    check(
        sells > 0 and abs(sells * 60 - buys * 140) <= 140,
        f"OCT sells at 140 % and buys at 60 %: {sells} {buys}",
    )

    # The game's store items: none while the market runs, generated with it off
    on = stores(remote, tick=True)
    time.sleep(5)
    on = stores(remote)
    check(len(on) == 7 and not any(on.values()), f"no generated store items: {on}")
    with_config(remote, MarketEnabled="false")
    stores(remote, tick=True)
    time.sleep(5)
    off = stores(remote)
    check(any(off.values()), f"with the market off the game generates them: {off}")
    with_config(remote)
    for _ in range(14):
        time.sleep(5)
        again = stores(remote)
        if again and not any(again.values()):
            break
    check(not any(again.values()), f"the market removes them again: {again}")

    # Buying from OCT; the price drifts up
    m = expect(remote, f"buy item={PLATE} amount=10", "buy 10 plates from OCT")
    cost = -(-10 * sells // 1000)
    check(m.balance == 25_000 - cost, f"paid {cost} SC: {m.balance}")
    check(m.mine(PLATE) == 10 and m.stock(PLATE) == 290, "10 plates moved")
    check(
        m.price(PLATE, "sells") > sells, f"the price went up: {m.price(PLATE, 'sells')}"
    )
    balance = m.balance

    # Alpha's specialty: platinum at 80 % up to the day's quota, then 60 %. Small amounts: the
    # game's achievement for a rich account would disconnect this client, which has no Steam
    admin(remote, f"!oct ledger deposit Alpha me {PLATINUM} 1000")
    m = market(remote)
    special = m.price(PLATINUM, "buys")
    check(
        m.rows.get(PLATINUM, {}).get("special") == "1", "platinum is Alpha's specialty"
    )
    m = expect(remote, f"sell item={PLATINUM} amount=10", "sell 10 platinum")
    paid = m.balance - balance
    check(abs(paid - 10 * special / 1000) <= 1, f"at the specialty price: {paid}")
    balance = m.balance
    special = m.price(PLATINUM, "buys")
    with_config(remote, MarketSpecialtyDailyTonnes="0.015")
    m = expect(
        remote, f"sell item={PLATINUM} amount=10", "sell 10 more, past the quota"
    )
    paid = m.balance - balance
    expected = 5 * special / 1000 + 5 * special * 0.75 / 1000
    check(
        abs(paid - expected) <= 2, f"5 at 80 % and 5 at 60 %: {paid} ~ {expected:.0f}"
    )
    check(
        m.price(PLATINUM, "buys") * 4 <= special * 3 + 4,
        f"the quota is used up: {m.price(PLATINUM, 'buys')}",
    )
    with_config(remote)
    balance = m.balance

    # A faction offer bought by the player
    kiosks.probe(remote, "faction KME leader")
    expect(remote, f"give item={PLATE} amount=5", "give 5 plates to the faction")
    m = expect(
        remote,
        f"offer faction item={PLATE} amount=5 price=50%",
        "the faction offers them at half the reference",
    )
    offer = next((l for l in m.listings if l["owner"] == "faction"), None)
    check(
        offer is not None and offer["amount"] == "5",
        f"the faction's offer: {m.listings}",
    )
    faction_before = m.faction_balance
    price = int(offer["price"]) if offer else 0
    m = expect(remote, f"buy item={PLATE} amount=5", "the player buys 5 plates")
    cost = -(-5 * price // 1000)
    check(m.balance == balance - cost, f"the offer's price, not OCT's: {cost}")
    check(
        m.faction_balance == faction_before + cost - cost * 5 // 100,
        f"the faction gets it less the 5 % fee: {faction_before} -> {m.faction_balance}",
    )
    check(not m.listings, f"the offer is sold out: {m.listings}")
    balance = m.balance

    # A player's order filled by the faction, then canceled
    admin(remote, f"!oct ledger deposit Alpha me {IRON} 200")
    expect(remote, f"give item={IRON} amount=100", "give 100 iron to the faction")
    m = expect(
        remote,
        f"order item={IRON} amount=100 price=100%",
        "order 100 iron at the reference",
    )
    order = next((l for l in m.listings if l["kind"] == "order"), None)
    held = balance - m.balance
    check(order is not None and held > 0, f"the order holds {held} SC: {m.listings}")
    faction_before = m.faction_balance
    m = expect(
        remote, f"sell faction item={IRON} amount=40", "the faction sells 40 iron"
    )
    check(m.mine(IRON) == 140, f"to the player's order: {m.mine(IRON)}")
    gained = m.faction_balance - faction_before
    check(
        abs(gained - held * 0.4 * 0.95) <= 2,
        f"paid from the order less the fee: {gained}",
    )
    left = next((l for l in m.listings if order and l["id"] == order["id"]), {})
    check(left.get("amount") == "60", f"the order wants 60 more: {left}")
    before = m.balance
    if order:
        m = expect(remote, f"cancel id={order['id']}", "cancel the order")
        check(
            abs(m.balance - before - held * 0.6) <= 1,
            f"the rest of the money back: {m.balance - before}",
        )

    # The trading rank
    kiosks.probe(remote, "faction KME member")
    expect(
        remote,
        f"buy faction item={PLATE} amount=1",
        "a member may not buy for the faction",
        ok=False,
        message="rank",
    )
    kiosks.probe(remote, "faction KME leader")

    # The listing limit and expiry
    with_config(remote, MarketMaxListings="2")
    expect(remote, f"offer item={IRON} amount=1 price=99", "first listing")
    expect(remote, f"offer item={IRON} amount=1 price=99", "second listing")
    expect(
        remote,
        f"offer item={IRON} amount=1 price=99",
        "the third is over the limit",
        ok=False,
        message="2 listings",
    )
    with_config(remote)
    m = market(remote)
    mine = [l for l in m.listings if l["owner"] == "mine"]
    iron = m.mine(IRON)
    if mine:
        admin(remote, f"!oct market age {mine[0]['id']} 8")
        reply = admin(remote, "!oct market tick")
        check("expired" in reply, f"an 8 days old listing expires: {reply}")
        m = market(remote)
        check(m.mine(IRON) == iron + 1, f"its iron is back: {m.mine(IRON)}")
    m = market(remote)
    # The player's balance may change with the hourly payout; the faction's doesn't
    kept = (
        m.faction_balance,
        m.stock(PLATE),
        sorted(l["id"] for l in m.listings),
    )
    log(f"before the restart: {kept}")
    return kept


def after_restart(remote, kept) -> None:
    stand(remote)
    m = expect(remote, "market", "the market after a restart")
    now = (
        m.faction_balance,
        m.stock(PLATE),
        sorted(l["id"] for l in m.listings),
    )
    check(
        now == kept,
        f"listings, the faction's balance and OCT's stock kept: {now} vs {kept}",
    )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    kiosks.prepare()
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    kept = None
    try:
        kept = run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check("octahedron-market 1" in sandbox, "the saved Sandbox.sbc holds the market")
    if kept:
        rig.start_server()
        remote = rig.start_client()
        try:
            after_restart(remote, kept)
        finally:
            rig.stop_client()
            rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Market" in line):
            log("server: " + line.strip())
    failed = [what for passed, what in kiosks.checks if not passed]
    print(
        f"{len(kiosks.checks) - len(failed)}/{len(kiosks.checks)} market checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
