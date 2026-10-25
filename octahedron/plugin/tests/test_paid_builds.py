"""Checks paid shipyard builds and the shipyard queue on the test server with the
headless client, at Alpha Station, with OmniArc, ToolCore and Multigrid Projector
as on the Octahedron servers, and the kiosk mods and probe of tests/test_kiosks.py.

The tables hold "PaidTest": 45 armor blocks and a small H2 tank (1,000,000 L).
With FreeBuildPcuLimit 0 every build is paid. The client uses !oct order, which
presses the hologram table's button for an admin (the button itself is pressed
once at the end).

- Quote, then order: the first press shows the quote (the components in the
  player's ledger first, the 55 plates missing from OCT, the labor fee), the second
  orders it. The
  player pays the quoted total, the hydrogen comes from the H2 tank of the
  player's ship docked at Alpha (half of it drained), and the build starts at
  once with the components loaded from the build ledger.
- Pressing again while it builds doesn't cancel it. When the ship is ready its
  H2 tank holds 500,000 L and the build ledger is gone.
- The finished ship stays in the shipyard, so the next order waits in the queue:
  the Faction switch on at the kiosk (the player a leader of faction KME) makes the
  faction pay, from its ledger first. After a server restart the queue entry is
  still there, and pressing again cancels it: the components are in the faction's
  ledger and the fee is back.
- A free build (FreeBuildPcuLimit back at 10,000) queues behind the ship too and is
  canceled with the button panel itself, by the character's use key.

    OCT_TASK=SE-685 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_paid_builds.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a cluster.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-685")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
import test_projection_limits as pl  # noqa: E402
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
# What the player brings: the tank's components but its plates, and 1,150 of the 1,205 plates
DEPOSIT = {
    "Component/LargeTube": 40,
    "Component/SmallTube": 60,
    "Component/Computer": 8,
    "Component/Construction": 40,
    PLATE: 1150,
}
HYDROGEN = 500_000
check = kiosks.check


def prepare() -> None:
    so.prepare(BLUEPRINT, 60)
    # The kiosk mods and probe on top of the shipyard's
    items = "".join(
        f'<ModItem FriendlyName="{name}"><Name>{id_}.sbm</Name><PublishedFileId>{id_}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
        for id_, (name, _) in kiosks.MODS.items()
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("</Mods>", f"{items}</Mods>", 1), "utf-8")
    for mods in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        for id_, (_, folder) in kiosks.MODS.items():
            link = mods / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(folder)
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, pl.MGP, kiosks.PROBE])


def admin(remote, text: str, wait: float = 3.0) -> str:
    reply = " ".join(rig.chat(remote, text, wait))
    log(f"{text} -> {reply}")
    return reply


def press(remote, wait: float = 5.0) -> str:
    return admin(remote, "!oct order", wait)


def ledger(remote, owner: str) -> str:
    return admin(remote, f"!oct ledger show Alpha {owner}", 4)


def amount(shown: str, item: str) -> float:
    """The free amount of the item in a ledger show reply, 0 if none"""
    match = re.search(
        rf"(?<!: ){re.escape(item)} ([\d,.]+)", shown.split("reserved")[0]
    )
    return float(match[1].replace(",", "")) if match else 0.0


def balances(remote, faction: bool = False) -> tuple[int, int]:
    """The player's and their faction's balance, from the kiosk; the request sets the
    kiosk's Faction switch too"""
    time.sleep(0.6)
    lines = rig.chat(remote, "/kiosk market" + (" faction" if faction else ""), 4)
    for line in lines:
        if line.startswith("balance "):
            fields = dict(w.split("=", 1) for w in line.split(" ") if "=" in w)
            return int(fields["mine"]), int(fields["faction"])
    raise AssertionError(f"no balance: {lines}")


def gas(remote, grid: str) -> int:
    match = re.search(r"GAS \d+ (\d+)", kiosks.probe(remote, f"gas {grid}"))
    return int(match[1]) if match else -1


def at_table(remote) -> None:
    kiosks_stand(remote)
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    tc.stand_by(remote, table)


def kiosks_stand(remote) -> None:
    reply = kiosks.probe(remote, "kiosk Alpha")
    match = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    if match:
        remote.character_teleport(*(float(v) for v in match.groups()))
        time.sleep(3)


def latest(remote) -> int:
    return remote.get_chat_messages().get("latest", 0)


def wait_chat(remote, text: str, timeout: float, after: int = 0) -> str | None:
    """The first chat line with text after the given point, None if none comes"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        got = remote.get_chat_messages(after=after)
        lines = [m.get("Text", "") for m in got.get("messages", [])]
        lines += [h.get("text", "") for h in got.get("hud", [])]
        for line in lines:
            if text in line:
                return line
        time.sleep(3)
    return None


def total(reply: str) -> int:
    match = re.search(r"([\d,]+) SC paid by", reply)
    return int(match[1].replace(",", "")) if match else -1


def paid(reply: str) -> tuple[int, int]:
    """What an order paid: the components and the labor fee"""
    match = re.search(r"([\d,]+) SC for the components and ([\d,]+) SC labor", reply)
    if not match:
        return -1, -1
    return int(match[1].replace(",", "")), int(match[2].replace(",", ""))


def order(remote) -> tuple[str, str]:
    """Presses until the order is placed: the quote's reply (empty when the screen
    showed the player's quote already) and the order's"""
    reply = press(remote, 8)
    if "Your quote for" not in reply:
        return "", reply
    return reply, press(remote, 8)


def paid_build(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=0)
    me = re.search(r"\(P(\d+)\)", ledger(remote, "me"))
    for item, count in DEPOSIT.items():
        admin(remote, f"!oct ledger deposit Alpha me {item} {count}")
    docked = re.search(r"SHIP (\d+)", kiosks.probe(remote, "dock Alpha mine H2=1"))
    check(docked is not None, "a ship with a full H2 tank docked at Alpha")
    ship = docked[1] if docked else "0"
    check(kiosks.wait_docked(remote, ship), "the ship is docked")
    check(gas(remote, ship) == 1_000_000, "its tank holds 1,000,000 L")
    at_table(remote)
    mine, _ = balances(remote)

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
    builds = wait_chat(remote, f"builds '{SHIP}' for you now", 60, mark)
    check(builds is not None, f"the build starts at once: {builds}")
    busy = press(remote)
    check("can't be canceled now" in busy, f"a press while it builds: {busy}")
    now, _ = balances(remote)
    check(now == mine - cost, f"the player paid the quote: {mine} -> {now}, {cost}")
    check(
        gas(remote, ship) == 500_000,
        "half the docked ship's hydrogen went into the build",
    )
    shown = ledger(remote, "me")
    check(
        amount(shown, PLATE) == 0, f"the ledger's plates went into the build: {shown}"
    )
    check(
        "reserved build" not in shown or "Ore/Ice" in shown,
        f"only the ice stays reserved: {shown}",
    )

    ready = wait_chat(remote, "is ready", 900, mark)
    check(
        ready is not None and "500,000 L" in ready,
        f"the ship is ready, its H2 filled: {ready}",
    )
    check(gas(remote, SHIP) == HYDROGEN, f"the new ship's H2 tank holds {HYDROGEN:,} L")
    shown = ledger(remote, "me")
    check("reserved build" not in shown, f"the build ledger is gone: {shown}")
    if me:
        log(f"player P{me[1]}")


def faction_build(remote) -> tuple[int, int]:
    """Queues a faction-paid order behind the finished ship; returns the faction's
    balance before it and what the components cost"""
    kiosks.probe(remote, "faction KME leader")
    kiosks.probe(remote, "pay KME 100000")
    for item, count in DEPOSIT.items():
        admin(remote, f"!oct ledger deposit Alpha faction:KME {item} {count}")
    kiosks_stand(remote)
    _, before = balances(remote, faction=True)
    at_table(remote)
    mark = latest(remote)
    quote, queued = order(remote)
    log(f"faction quote: {quote or 'on the screen already'}")
    check(
        not quote or "paid by your faction KME" in quote,
        f"the Faction switch: the faction pays: {quote}",
    )
    check("is in the queue" in queued, f"the faction's order: {queued}")
    components, fee = paid(queued)
    check(
        wait_chat(remote, f"builds '{SHIP}' for you now", 25, mark) is None,
        "it waits: the finished ship is still in the shipyard",
    )
    kiosks_stand(remote)
    _, after = balances(remote, faction=True)
    check(
        components > 0 and after == before - components - fee,
        f"the faction paid: {before} -> {after}, {components} + {fee}",
    )
    shown = ledger(remote, "faction:KME")
    check(
        amount(shown, PLATE) == 0 and "reserved build" in shown,
        f"the faction's plates are in its build ledger: {shown}",
    )
    return before, components


def cancel_after_restart(remote, before: int, components: int) -> None:
    kiosks_stand(remote)
    balances(remote, faction=True)
    at_table(remote)
    canceled = press(remote)
    check(
        "left the queue" in canceled, f"after the restart the press cancels: {canceled}"
    )
    kiosks_stand(remote)
    _, after = balances(remote, faction=True)
    shown = ledger(remote, "faction:KME")
    check("reserved build" not in shown, f"the build ledger is gone: {shown}")
    check(
        amount(shown, PLATE) == 1205, f"the plates are back, bought ones too: {shown}"
    )
    check(
        after == before - components,
        f"the fee is back, the bought components stay bought: {before} -> {after}",
    )


def free_build_by_button(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=10000)
    kiosks_stand(remote)
    balances(remote)  # the Faction switch off
    at_table(remote)
    quote, queued = order(remote)
    check(not quote or "a free build" in quote, f"a free build: {quote}")
    check("is in the queue, a free build" in queued, f"the free order queues: {queued}")

    buttons = remote.find_nearby_blocks(
        subtype="LargeButtonPanelPedestal", max_distance=50, limit=5
    )
    check(len(buttons) > 0, f"the confirm button is near the table: {len(buttons)}")
    if not buttons:
        return
    button = min(buttons, key=lambda b: b["distance"])
    position = button["worldPosition"]
    character = remote.get_character()["position"]
    # 1.5 m from the button on the character's side, looking at its top
    away = [c - p for c, p in zip(character, position)]
    length = max(1e-6, sum(a * a for a in away) ** 0.5)
    remote.character_teleport(*(p + 1.5 * a / length for p, a in zip(position, away)))
    time.sleep(3)
    canceled = None
    for lift in (0.4, 0.2, 0.6, 0.0):
        mark = latest(remote)
        remote.character_look_at(position[0], position[1] + lift, position[2])
        time.sleep(1)
        remote.character_use()
        canceled = wait_chat(remote, "left the queue", 8, mark)
        if canceled:
            break
    check(canceled is not None, f"the button itself cancels the free order: {canceled}")


def saved_screens() -> None:
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    check(
        "Shipyard quote for" in sector,
        "the hologram table's screen shows quotes (saved world)",
    )
    check(
        "Station Shipyard queue" in sector,
        "the queue panel shows the queue (saved world)",
    )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    before = components = None
    try:
        paid_build(remote)
        before, components = faction_build(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check("octahedron-builds 1" in sandbox, "the saved Sandbox.sbc holds the queues")
    saved_screens()
    rig.start_server()
    remote = rig.start_client()
    try:
        if before is not None:
            cancel_after_restart(remote, before, components)
        free_build_by_button(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Shipyard" in line):
            log("server: " + line.strip())
    failed = [what for passed, what in kiosks.checks if not passed]
    print(
        f"{len(kiosks.checks) - len(failed)}/{len(kiosks.checks)} paid build checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
