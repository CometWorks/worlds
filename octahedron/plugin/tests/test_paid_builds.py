"""Checks paid shipyard builds and the shipyard queue on the test server with the
headless client, at Alpha Station, with OmniArc, ToolCore and Multigrid Projector
as on the Octahedron servers, and the station probe.

The tables hold "PaidTest": 45 armor blocks, a small H2 tank (1,000,000 L) and a
remote control, which keeps the finished ship from counting as a wreck.
With FreeBuildPcuLimit 0 every build is paid, with a labor fee of OCT_LABOR_FEE
(default 10) SC per 100 PCU; 0 checks that nothing mentions the fee. The client
uses !oct order, which presses the hologram table's button for an admin (the
button itself is pressed once at the end).

- Quote, then order: the first press shows the quote, the second queues it, and nothing
  is paid yet. At the player's turn the third press pays: the player's account pays what
  is missing and any fee, OCT's stock at Alpha goes down by the components, and the
  hydrogen comes from the H2 tank of the player's ship docked at Alpha (half of it
  drained). When the ship is ready its H2 tank holds 500,000 L, and the empty containers
  end the turn.
- The finished ship stays in the shipyard, so the next order waits in the queue, with
  nothing paid yet. After a server restart the queue entry is still there, and pressing
  again cancels it.
- A free build (FreeBuildPcuLimit back at 10,000) queues behind the ship too and is
  canceled by pressing again. The confirm button has the toolbar item the plugin
  gives it (checked through the probe).

A player's turn itself is in tests/test_shipyard_turn.py.

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
from test_table_rules import blueprint_xml  # noqa: E402

# Written for half full H2 tanks on new ships and no hydrogen for docked ships (SE-1387)
rig.CONFIG_PINS.update(ShipyardHydrogenFillPercent=50, StationDockHydrogen="false")

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
    + [rig.block("MyObjectBuilder_RemoteControl", "LargeBlockRemoteControl", (0, 0, 6))]
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
    "Component/InteriorPlate": 100,
    "Component/Motor": 100,
    PLATE: 2000,
}
# Enough to pay it, with a low balance otherwise
FUNDS = 300_000
HYDROGEN = 500_000
LABOR_FEE = int(os.environ.get("OCT_LABOR_FEE", "10"))
check = st.check


def press(remote) -> str:
    return st.admin(remote, "!oct order")


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


def load(remote) -> None:
    """Loads the blueprint on the table again, which the last order cleared"""
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    block = remote.get_block(table["gridId"], tuple(table["min"]))
    log(str(remote.load_projection(block["entityId"], xml=blueprint_xml(BLUEPRINT))))
    st.wait_until(lambda: "BLUEPRINT True" in st.admin(remote, "!oct table show"), 30)


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
    match = re.search(r"([\d,]+) SC if .* buy everything", reply)
    return int(match[1].replace(",", "")) if match else -1


def order(remote) -> tuple[str, str]:
    """Presses until the order is placed: the quote's reply (empty when the screen
    showed the player's quote already) and the order's"""
    reply = press(remote)
    if "Your quote for" not in reply:
        return "", reply
    return reply, press(remote)


def paid_build(remote) -> None:
    with_config(
        remote,
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=LABOR_FEE,
        ShipyardTableMinutes=600,
    )
    for item, count in STOCK.items():
        st.admin(remote, f"!oct market set Alpha {item} {count}")
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
    started = press(remote)
    check("is in the queue" in started, f"the second press queues it: {started}")
    told = wait_chat(remote, "It is your turn", 40, mark)
    check(told is not None, f"the player's turn comes at once: {told}")
    now = balance(remote)
    check(now == mine, f"nothing is paid at the queue: {mine} -> {now}")
    check(gas(remote, ship) == 1_000_000, "the docked ship's tank is still full")
    check(plates(remote) == STOCK[PLATE], "nothing left OCT's stock yet")
    at_table(remote)
    mark = latest(remote)
    begun = press(remote)
    builds = wait_chat(remote, f"builds '{SHIP}' for you now", 60, mark)
    check(builds is not None, f"the press in the turn starts it: {begun} {builds}")
    check(
        ("labor" in begun) == (LABOR_FEE > 0),
        f"the press's message and the labor fee: {begun}",
    )
    now = balance(remote)
    check(
        mine - cost <= now < mine,
        f"the player paid at most the quote, less the docked hydrogen's ice: {mine} -> {now}, {cost}",
    )
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
    told = wait_chat(remote, "its containers are empty", 30, mark)
    check(told is not None, f"nothing left: the turn ends: {told}")


def waiting_build(remote) -> int:
    """Queues an order behind the finished ship; returns the player's balance"""
    for item, count in STOCK.items():
        st.admin(remote, f"!oct market set Alpha {item} {count}")
    st.probe(remote, f"pay me {FUNDS - balance(remote)}")
    before = balance(remote)
    at_table(remote)
    load(remote)
    before = balance(remote)
    mark = latest(remote)
    quote, queued = order(remote)
    log(f"quote: {quote or 'on the screen already'}")
    check("is in the queue" in queued, f"the order: {queued}")
    check(
        wait_chat(remote, "It is your turn", 25, mark) is None,
        "it waits: the finished ship is still in the shipyard",
    )
    after = balance(remote)
    check(after == before, f"nothing is paid yet: {before} -> {after}")
    check(plates(remote) == STOCK[PLATE], "the plates stay in OCT's stock")
    return before


def cancel_after_restart(remote, before: int) -> None:
    at_table(remote)
    canceled = press(remote)
    check(
        "left the queue" in canceled, f"after the restart the press cancels: {canceled}"
    )
    after = balance(remote)
    check(after == before, f"no money moved: {before} -> {after}")
    check(plates(remote) == STOCK[PLATE], "nor OCT's plates")


def free_build(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=10000, ShipyardTableMinutes=600)
    at_table(remote)
    load(remote)
    quote, queued = order(remote)
    check(not quote or "a free build" in quote, f"a free build: {quote}")
    check("is in the queue, a free build" in queued, f"the free order queues: {queued}")

    armed = st.probe(remote, "buttons Alpha")
    check(
        re.search(
            r"'Button Panel - Book Shipyard' MyToolbarItemTerminalBlock",
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
    before = None
    try:
        paid_build(remote)
        before = waiting_build(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check("octahedron-builds 3" in sandbox, "the saved Sandbox.sbc holds the queues")
    saved_screens()
    rig.start_server()
    remote = rig.start_client()
    try:
        if before is not None:
            cancel_after_restart(remote, before)
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
