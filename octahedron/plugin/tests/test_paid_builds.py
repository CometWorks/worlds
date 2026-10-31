"""Checks paid shipyard builds and the shipyard queue on the test server with the
headless client, at Alpha Station, with OmniArc, ToolCore and Multigrid Projector
as on the Octahedron servers, and the station probe.

The tables hold "PaidTest": 45 armor blocks and a small H2 tank (1,000,000 L).
With FreeBuildPcuLimit 0 every build is paid, with a labor fee of OCT_LABOR_FEE
(default 10) SC per 100 PCU; 0 checks that nothing mentions the fee. The client
uses !oct order, which presses the hologram table's button for an admin (the
button itself is pressed once at the end).

- Quote, then order: the first press shows the quote, the second orders it. The
  player's account pays the quoted total, OCT's stock at Alpha goes down by the
  components, the hydrogen comes from the H2 tank of the player's ship docked at
  Alpha (half of it drained), and the build starts at once.
- Pressing again while it builds doesn't cancel it. When the ship is ready its
  H2 tank holds 500,000 L.
- The finished ship stays in the shipyard, so the next order waits in the queue,
  bought too. After a server restart the queue entry is still there, and pressing
  again cancels it: all the player paid and OCT's stock are back.
- A free build (FreeBuildPcuLimit back at 10,000) queues behind the ship too and is
  canceled by pressing again. The confirm button has the toolbar item the plugin
  gives it (checked through the probe).

    OCT_TASK=SE-685 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_paid_builds.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a cluster, and pressing the button panel itself: the headless
client's use-object detection doesn't hit it (character_use and F answered
nothing_to_use, rendering or not), so the test uses !oct order, which calls
the same press handler as the server's patch of the button.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-685")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import station_tests as st  # noqa: E402
import test_shipyard_order as so  # noqa: E402
from test_client import log, with_config  # noqa: E402

PLATE = "Component/SteelPlate"
SHIP = "PaidTest"
BLUEPRINT = rig.grid(
    SHIP,
    777000685000100,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
    ]
    + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
        for z in range(2, 6)
    ]
    # Enough blocks to keep the welders busy for a while
    + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(1, 5)
        for z in range(0, 10)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
# OCT's stock at Alpha: more than the build takes
STOCK = {
    "Component/LargeTube": 100,
    "Component/SmallTube": 100,
    "Component/Computer": 100,
    "Component/Construction": 100,
    PLATE: 2000,
}
# Enough to pay it, with a low balance otherwise
FUNDS = 300_000
HYDROGEN = 500_000
LABOR_FEE = int(os.environ.get("OCT_LABOR_FEE", "10"))
check = st.check


def admin(remote, text: str, wait: float = 3.0) -> str:
    return st.admin(remote, text, wait)


def press(remote, wait: float = 5.0) -> str:
    return admin(remote, "!oct order", wait)


def balance(remote) -> int:
    return st.money(remote)["You"]


def plates(remote) -> float:
    return st.stock(remote, "Alpha", PLATE)[0]


def gas(remote, grid: str) -> int:
    match = re.search(r"GAS \d+ (\d+)", st.probe(remote, f"gas {grid}"))
    return int(match[1]) if match else -1


def at_table(remote) -> None:
    st.stand(remote, "Alpha")
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    tc.stand_by(remote, table)


def latest(remote) -> tuple[int, set]:
    """A point in the chat: the last message and the HUD lines shown so far"""
    got = remote.get_chat_messages()
    hud = {
        (h.get("sender"), h.get("text"), h.get("timestamp")) for h in got.get("hud", [])
    }
    return got.get("latest", 0), hud


def wait_chat(remote, text: str, timeout: float, after=(0, set())) -> str | None:
    """The first chat line with text since the point, None if none comes"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        got = remote.get_chat_messages(after=after[0])
        lines = [m.get("Text", "") for m in got.get("messages", [])]
        lines += [
            h.get("text", "")
            for h in got.get("hud", [])
            if (h.get("sender"), h.get("text"), h.get("timestamp")) not in after[1]
        ]
        for line in lines:
            if text in line:
                return line
        time.sleep(3)
    return None


def total(reply: str) -> int:
    match = re.search(r"([\d,]+) SC from your account", reply)
    return int(match[1].replace(",", "")) if match else -1


def paid(reply: str) -> tuple[int, int]:
    """What an order paid: the components and the labor fee, which it leaves out at 0"""
    match = re.search(
        r"([\d,]+) SC for the components(?: and ([\d,]+) SC labor)? paid", reply
    )
    if not match:
        return -1, -1
    return int(match[1].replace(",", "")), int((match[2] or "0").replace(",", ""))


def order(remote) -> tuple[str, str]:
    """Presses until the order is placed: the quote's reply (empty when the screen
    showed the player's quote already) and the order's"""
    reply = press(remote, 8)
    if "Your quote for" not in reply:
        return "", reply
    return reply, press(remote, 8)


def paid_build(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=0, ShipyardLaborFeePer100Pcu=LABOR_FEE)
    for item, count in STOCK.items():
        admin(remote, f"!oct market set Alpha {item} {count}")
    st.probe(remote, f"pay me {FUNDS - balance(remote)}")
    docked = re.search(r"SHIP (\d+)", st.probe(remote, "dock Alpha mine H2=1"))
    check(docked is not None, "a ship with a full H2 tank docked at Alpha")
    ship = docked[1] if docked else "0"
    check(st.wait_docked(remote, ship), "the ship is docked")
    check(gas(remote, ship) == 1_000_000, "its tank holds 1,000,000 L")
    at_table(remote)
    mine = balance(remote)

    quote = press(remote)
    cost = total(quote)
    check(
        "a paid build" in quote and "free builds are for players under 0" in quote,
        f"the first press shows the quote: {quote}",
    )
    check(cost > 0, f"the quote's total: {cost}")
    mark = latest(remote)
    started = press(remote, 8)
    check("is in the queue" in started, f"the second press orders: {started}")
    check(
        (paid(started)[1] > 0) == (LABOR_FEE > 0)
        and ("labor" in started) == (LABOR_FEE > 0),
        f"the order's message and the labor fee: {started}",
    )
    builds = wait_chat(remote, f"builds '{SHIP}' for you now", 60, mark)
    check(builds is not None, f"the build starts at once: {builds}")
    busy = press(remote)
    check("can't be canceled now" in busy, f"a press while it builds: {busy}")
    now = balance(remote)
    check(now == mine - cost, f"the player paid the quote: {mine} -> {now}, {cost}")
    check(plates(remote) == STOCK[PLATE] - 1205, "the plates left OCT's stock")
    check(
        gas(remote, ship) == 500_000,
        "half the docked ship's hydrogen went into the build",
    )

    ready = wait_chat(remote, "is ready", 900, mark)
    check(
        ready is not None and "500,000 L" in ready,
        f"the ship is ready, its H2 filled: {ready}",
    )
    check(gas(remote, SHIP) == HYDROGEN, f"the new ship's H2 tank holds {HYDROGEN:,} L")


def queued_build(remote) -> tuple[int, float]:
    """Queues a paid order behind the finished ship; returns the balance and OCT's plates
    before it"""
    for item, count in STOCK.items():
        admin(remote, f"!oct market set Alpha {item} {count}")
    st.probe(remote, f"pay me {FUNDS - balance(remote)}")
    at_table(remote)
    before = balance(remote)
    mark = latest(remote)
    quote, queued = order(remote)
    log(f"quote: {quote or 'on the screen already'}")
    check("is in the queue" in queued, f"the second order: {queued}")
    components, fee = paid(queued)
    check(
        wait_chat(remote, f"builds '{SHIP}' for you now", 25, mark) is None,
        "it waits: the finished ship is still in the shipyard",
    )
    after = balance(remote)
    check(
        components > 0 and after == before - components - fee,
        f"the player paid at the order: {before} -> {after}, {components} + {fee}",
    )
    check(plates(remote) == STOCK[PLATE] - 1205, "bought from OCT's stock")
    return before, STOCK[PLATE]


def cancel_after_restart(remote, before: int, stock: float) -> None:
    at_table(remote)
    canceled = press(remote)
    check(
        "left the queue" in canceled, f"after the restart the press cancels: {canceled}"
    )
    check("is paid back" in canceled, f"the cancel pays back: {canceled}")
    after = balance(remote)
    check(after == before, f"all of it is back: {before} -> {after}")
    check(plates(remote) == stock, "and OCT's plates")


def free_build(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=10000)
    at_table(remote)
    quote, queued = order(remote)
    check(not quote or "a free build" in quote, f"a free build: {quote}")
    check("is in the queue, a free build" in queued, f"the free order queues: {queued}")

    armed = st.probe(remote, "buttons Alpha")
    check(
        re.search(
            r"'Button Panel - Station ShipyardConfirm' MyToolbarItemTerminalBlock",
            armed,
        )
        is not None,
        f"the confirm button has a toolbar item, so clients offer it: {armed}",
    )
    canceled = press(remote)
    check(
        "left the queue" in canceled,
        f"pressing again cancels the free order: {canceled}",
    )


def saved_screens() -> None:
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    check(
        "then press" in sector or "for OctTester" in sector,
        "the hologram table's screen is idle or shows a quote (saved world)",
    )
    check(
        "Station Shipyard queue" in sector,
        "the queue panel shows the queue (saved world)",
    )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(BLUEPRINT, 60)
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    before = stock = None
    try:
        paid_build(remote)
        before, stock = queued_build(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check("octahedron-builds 2" in sandbox, "the saved Sandbox.sbc holds the queues")
    saved_screens()
    rig.start_server()
    remote = rig.start_client()
    try:
        if before is not None:
            cancel_after_restart(remote, before, stock)
        free_build(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Shipyard" in line):
            log("server: " + line.strip())
    return st.summary("paid build")


if __name__ == "__main__":
    sys.exit(main())
