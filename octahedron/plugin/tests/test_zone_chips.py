"""Checks the zone chips OCT sells on the test server with the headless client,
through the kiosk client mod's requests as test_market.py sends them.

Checked: the game's safe zone block takes 1 chip per 60 minutes; OCT sells no chips
at Alpha Station; at Moon Station it sells them at 1,000 SC from a stock of 100 that
the market's tick tops up; it buys none; one purchase takes at most the stock; a
bought chip goes from the ledger into the character's inventory; ZoneChipPrice and
ZoneChipStations change the price and the stations. Prints one line per check and a
pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-687 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_zone_chips.py

Not covered: the touch screens themselves, a cluster, and a safe zone running on
the bought chips for an hour.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-687")
import rig  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_market import admin, expect, market  # noqa: E402

CHIP = "Component/ZoneChip"
check = kiosks.check


def stand(remote, station: str) -> None:
    reply = kiosks.probe(remote, f"kiosk {station}")
    match = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    check(match is not None, f"{station} Station has a kiosk: {reply}")
    if match:
        remote.character_teleport(*(float(v) for v in match.groups()))
        time.sleep(3)


def run(remote) -> None:
    upkeep = re.findall(r"SAFEZONE (\S+) (\d+) (\d+)", kiosks.probe(remote, "safezone"))
    check(
        bool(upkeep) and all(u[1:] == ("1", "60") for u in upkeep),
        f"a safe zone block takes 1 chip per 60 minutes: {upkeep}",
    )

    stand(remote, "Alpha")
    m = expect(remote, "market", "the market at Alpha")
    check(CHIP not in m.rows, f"OCT sells no chips at Alpha: {m.rows.get(CHIP)}")
    expect(
        remote,
        f"buy item={CHIP} amount=1",
        "no chip to buy at Alpha",
        ok=False,
        message="Nobody sells",
    )

    stand(remote, "Moon")
    m = expect(remote, "market", "the market at Moon")
    check(
        m.price(CHIP, "sells") == 1_000_000,
        f"a chip costs 1,000 SC: {m.rows.get(CHIP)}",
    )
    check(m.price(CHIP, "buys") == 0, "OCT doesn't buy chips")
    check(m.stock(CHIP) == 100, f"OCT has 100 chips in stock: {m.stock(CHIP)}")
    balance = m.balance
    m = expect(remote, f"buy item={CHIP} amount=2", "buy 2 chips at Moon")
    check(m.balance == balance - 2000, f"paid 2,000 SC: {balance} -> {m.balance}")
    check(
        m.mine(CHIP) == 2 and m.stock(CHIP) == 98,
        f"2 chips moved: mine {m.mine(CHIP)}, stock {m.stock(CHIP)}",
    )
    check(m.price(CHIP, "sells") == 1_000_000, "the price doesn't drift")
    reply = admin(remote, "!oct market tick")
    check("topped up" in reply, f"the tick tops the stock up: {reply}")
    m = market(remote)
    check(m.stock(CHIP) == 100, f"100 chips again: {m.stock(CHIP)}")
    expect(
        remote,
        f"sell item={CHIP} amount=1",
        "OCT buys no chips",
        ok=False,
        message="Nobody buys",
    )
    expect(
        remote,
        f"buy item={CHIP} amount=101",
        "one purchase takes at most the stock",
        ok=False,
        message="Only 100",
    )

    expect(
        remote,
        f"withdraw hand item={CHIP} amount=2",
        "withdraw the chips into the character's inventory",
        amounts={(CHIP, "mine"): 0},
    )
    hand = expect(remote, "state hand", "From hand shows the inventory")
    check(
        hand.amount(CHIP, "source") >= 2,
        f"the character carries the chips: {hand.amount(CHIP, 'source')}",
    )

    with_config(remote, ZoneChipPrice="1500")
    m = market(remote)
    check(
        m.price(CHIP, "sells") == 1_500_000,
        f"ZoneChipPrice sets the price: {m.rows.get(CHIP)}",
    )
    with_config(remote, ZoneChipStations="Alpha Station")
    expect(
        remote,
        f"buy item={CHIP} amount=1",
        "ZoneChipStations without Moon: none to buy there",
        ok=False,
        message="Nobody sells",
    )
    with_config(remote)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    kiosks.prepare()
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Market" in line):
            log("server: " + line.strip())
    failed = [what for passed, what in kiosks.checks if not passed]
    print(
        f"{len(kiosks.checks) - len(failed)}/{len(kiosks.checks)} zone chip checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
