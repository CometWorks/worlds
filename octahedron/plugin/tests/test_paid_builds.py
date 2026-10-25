"""Checks paid shipyard builds and the shipyard queue on the test server with the
headless client, at Alpha Station, with OmniArc, ToolCore and Multigrid Projector
as on the Octahedron servers, and the kiosk mods and probe of tests/test_kiosks.py.

The tables hold "Paid Test": armor, a control seat and a small H2 tank (1,000,000 L).
With FreeBuildPcuLimit 0 every build is paid. The client uses !oct order, which
presses the hologram table's button for an admin (the button itself is pressed
once at the end).

- Quote, then order: the first press shows the quote (the plates in the player's
  ledger first, the rest from OCT, the labor fee), the second orders it. The
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
SHIP = "Paid Test"
BLUEPRINT = rig.grid(
    SHIP,
    777000685000100,
    [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpitSeat", (0, 1, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
    ]
    + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
        for z in range(2, 6)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
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


def balances(remote) -> tuple[int, int]:
    """The player's and their faction's balance, from the kiosk"""
    time.sleep(0.6)
    lines = rig.chat(remote, "/kiosk market", 4)
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


def wait_chat(remote, text: str, timeout: float) -> str | None:
    try:
        return so.wait_chat(remote, text, timeout)
    except TimeoutError:
        return None


def total(reply: str) -> int:
    match = re.search(r"([\d,]+) SC paid by", reply)
    return int(match[1].replace(",", "")) if match else -1


def paid_build(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=0)
    me = re.search(r"\(P(\d+)\)", ledger(remote, "me"))
    admin(remote, f"!oct ledger deposit Alpha me {PLATE} 50")
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
    started = press(remote, 8)
    check("is in the queue" in started, f"the second press orders: {started}")
    builds = wait_chat(remote, f"builds '{SHIP}' for you now", 60)
    check(builds is not None, f"the build starts at once: {builds}")
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

    busy = press(remote)
    check("can't be canceled now" in busy, f"a press while it builds: {busy}")
    ready = wait_chat(remote, "is ready", 900)
    check(
        ready is not None and "500,000 L" in ready,
        f"the ship is ready, its H2 filled: {ready}",
    )
    check(gas(remote, SHIP) == HYDROGEN, f"the new ship's H2 tank holds {HYDROGEN:,} L")
    shown = ledger(remote, "me")
    check("reserved build" not in shown, f"the build ledger is gone: {shown}")
    if me:
        log(f"player P{me[1]}")


def faction_build(remote) -> tuple[int, float]:
    """Queues a faction-paid order behind the finished ship; returns the faction's
    balance before it and the faction's plates"""
    kiosks.probe(remote, "faction KME leader")
    admin(remote, f"!oct ledger deposit Alpha faction:KME {PLATE} 30")
    kiosks_stand(remote)
    time.sleep(0.6)
    rig.chat(remote, "/kiosk state faction", 3)
    _, before = balances(remote)
    rig.chat(remote, "/kiosk state faction", 3)
    at_table(remote)
    quote = press(remote)
    check(
        "paid by your faction KME" in quote,
        f"the Faction switch: the faction pays: {quote}",
    )
    cost = total(quote)
    queued = press(remote, 8)
    check("is in the queue" in queued, f"the faction's order: {queued}")
    check(
        wait_chat(remote, f"builds '{SHIP}' for you now", 25) is None,
        "it waits: the finished ship is still in the shipyard",
    )
    _, after = balances(remote)
    check(after == before - cost, f"the faction paid: {before} -> {after}, {cost}")
    shown = ledger(remote, "faction:KME")
    check(
        amount(shown, PLATE) == 0 and "reserved build" in shown,
        f"the faction's plates are in its build ledger: {shown}",
    )
    return before, 30.0


def cancel_after_restart(remote, before: int) -> None:
    at_table(remote)
    canceled = press(remote)
    check(
        "left the queue" in canceled, f"after the restart the press cancels: {canceled}"
    )
    _, after = balances(remote)
    shown = ledger(remote, "faction:KME")
    check("reserved build" not in shown, f"the build ledger is gone: {shown}")
    check(amount(shown, PLATE) >= 30, f"the plates are back, bought ones too: {shown}")
    log(
        f"faction balance {before} -> {after} (the purchases stay bought, the fee is back)"
    )
    check(after < before, "the faction kept what it bought, as items")


def free_build_by_button(remote) -> None:
    with_config(remote, FreeBuildPcuLimit=10000)
    rig.chat(remote, "/kiosk state", 3)  # the Faction switch off
    at_table(remote)
    quote = press(remote)
    check("a free build" in quote, f"a free build: {quote}")
    queued = press(remote, 8)
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
        remote.character_look_at(position[0], position[1] + lift, position[2])
        time.sleep(1)
        remote.character_use()
        canceled = wait_chat(remote, "left the queue", 8)
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
    before = None
    try:
        paid_build(remote)
        before, _ = faction_build(remote)
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
            cancel_after_restart(remote, before)
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
