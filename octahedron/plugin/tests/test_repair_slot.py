"""A repair slot at a real station shipyard (SE-850), with two players on one server. A press
without a blueprint books the slot; the shipyard's containers, welders and connectors are the
player's and shared with their faction, its reactors stay OCT's; a ship docked to a connector
gives the welders the components to weld a ship in the shipyard from its repair projector; the
slot ends after the real 5 minutes without building or welding, or at the player's next press,
and everything is back as before. Also: a slot waits for a running build, which its owner's press
cancels, with the paid part of that; a restart in the middle of a slot; and a build after a slot.
On a world built by build_world.py. The presses are !oct order: see test_table_rules.py.

    OCT_TASK=SE-850r OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_repair_slot.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 30 minutes.
Not covered: a cluster, and a player docking and using the terminal by hand: the probe plugin
builds the ships and connects them.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-850r")
import rig  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from se_remote import CallOp  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (
    Second,
    check,
    checks,
    load,
    press,
    queue,
    settled,
)  # noqa: E402

STATION = "Alpha"
CONFIG = tr.CONFIG
IDLE_SECONDS = 300
OCT = "Octahedron_Authority"
PLATES = 2000
# Armor only, so a paid build needs nothing but the plates put in the ledger
PAID = rig.grid(
    "Paid Cancel",
    777000850000100,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(4)
        for z in range(8)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PAID_PLATES = 32 * 25


def probe(remote, args: str) -> str:
    return kiosks.probe(remote, args)


def yard(remote, player: str | None = None) -> dict[str, str]:
    reply = probe(remote, f"yard {STATION}" + (f" {player}" if player else ""))
    return dict(re.findall(r"(\w+)=(\S+)", reply))


def info(remote, grid: str) -> dict[str, str]:
    return dict(re.findall(r"(\w+)=(\S+)", probe(remote, f"info {grid}")))


def shipyard_id(remote) -> int:
    return next(
        g["entityId"]
        for g in remote.list_grids()
        if g.get("name", "").startswith(STATION) and g["name"].endswith(" Shipyard")
    )


def client_owner(remote, grid: int, cell: str) -> tuple[str, str]:
    """A block's owner and share mode, as this client sees them"""
    x, y, z = (int(v) for v in cell.split(","))
    block = remote.get_block(grid, (x, y, z))
    xml = remote.get_object_builder(block["entityId"])
    owner = re.search(r"<Owner>(\d+)</Owner>", xml)
    share = re.search(r"<ShareMode>(\w+)</ShareMode>", xml)
    return owner[1] if owner else "0", share[1] if share else "None"


def theirs(state: dict, name: str) -> bool:
    return all(
        state[k] == f"{name}/Faction" for k in ("cargo", "welders", "connectors")
    )


def octs(state: dict) -> bool:
    return all(state[k] == f"{OCT}/All" for k in ("cargo", "welders", "connectors"))


def power_stays(state: dict, what: str) -> None:
    check(
        state["reactors"].startswith(OCT + "/")
        and state["batteries"].startswith(OCT + "/"),
        f"{what}: reactors and batteries stay OCT's: {state['reactors']}, {state['batteries']}",
    )
    check(
        state["bigowner"] == OCT,
        f"{what}: OCT stays the big owner: {state['bigowner']}",
    )
    check(state["elsewhere"] == "0", f"{what}: no uranium out of the reactors: {state}")


def slot_runs(remote, name: str = "OctTester") -> float | None:
    """Seconds since the running slot's last work, None without a slot"""
    reply = " ".join(rig.chat(remote, "!oct shipyards", 3))
    found = re.search(rf"repair slot of {name}, (\d+) s", reply)
    return float(found[1]) if found else None


def wait_for(what, timeout: float, every: float = 3.0):
    deadline = time.monotonic() + timeout
    while True:
        value = what()
        if value or time.monotonic() > deadline:
            return value
        time.sleep(every)


def book_and_idle_out(a, b) -> None:
    """The whole of a slot: booked, worked in, and ended by the real 5 idle minutes"""
    home = yard(a)
    power_stays(home, "before")
    check(octs(home), f"before: the blocks are OCT's and shared with all: {home}")
    uranium = float(home["uranium"])
    mark = sa.so_latest(a)

    reply = press(a)
    start = time.monotonic()
    check("Repair slot" in reply, f"a press without a blueprint books a slot: {reply}")
    text = settled(a, "queue", "Repair for OctTester: repairing")
    check(
        "Repair for OctTester: repairing" in text,
        f"the free shipyard starts it, on the queue panel: {text!r}",
    )
    check(
        wait_for(lambda: slot_runs(a) is not None, 20),
        "the shipyard runs the slot",
    )
    yard_id = shipyard_id(a)
    time.sleep(3)
    check(
        sa.shoots(a, yard_id) is True,
        "the welders are in build mode, seen by the client",
    )
    state = yard(a, "OctOther")
    check(theirs(state, "OctTester"), f"the blocks are the player's: {state}")
    power_stays(state, "in the slot")
    reply = " ".join(rig.chat(a, f"!kprobe yard {STATION} OctOther", 3))
    check(
        "access cargo=False welders=False connectors=False reactors=False" in reply,
        f"another player has no access: {reply}",
    )
    seen = client_owner(b, yard_id, state["cell"])
    check(
        seen[1] == "Faction",
        f"the other client sees it shared with the faction: {seen}",
    )
    probe(a, "faction KRS founder")
    with Second():
        probe(b, "faction KRS member")
    reply = " ".join(rig.chat(a, f"!kprobe yard {STATION} OctOther", 3))
    check(
        "access cargo=True welders=True connectors=True reactors=False" in reply,
        f"their faction has access, but to the reactors: {reply}",
    )
    with Second():
        probe(b, "faction KRS none")
    probe(a, "faction KRS none")

    # Nothing to weld for most of the 5 minutes: only work restarts the time, this keeps it
    time.sleep(max(0, start + 200 - time.monotonic()))
    check(slot_runs(a) is not None, "still running after 200 idle s")
    docked = re.search(r"SHIP (\d+)", probe(a, f"yardship {STATION}"))
    check(docked is not None, f"a ship with the components at a connector: {docked}")
    ship = docked[1] if docked else "0"
    check(kiosks.wait_docked(a, ship), "it docks")
    state = yard(a)
    check(
        state["connected"] == "1" and state["trading"] == "0", f"not trading: {state}"
    )
    target = re.search(r"TARGET (\d+)", probe(a, f"yardtarget {STATION}"))
    check(target is not None, f"a damaged ship with a repair projector: {target}")
    damaged = target[1] if target else "0"

    def repaired():
        got = info(a, damaged)
        return got.get("full") == "5" and got.get("blocks") == "5"

    done = wait_for(repaired, 240, 5)
    check(
        done,
        f"the shipyard's welders weld it and build the projection: {info(a, damaged)}",
    )
    left = float(info(a, ship).get("plates", -1))
    check(
        PLATES - 100 <= left < PLATES,
        f"the components come from the docked ship: {PLATES} -> {left}",
    )
    check(
        time.monotonic() - start > IDLE_SECONDS or slot_runs(a) is not None,
        "the slot still runs",
    )
    # Past 5 minutes since it started, which the work restarted
    time.sleep(max(0, start + IDLE_SECONDS + 20 - time.monotonic()))
    since = slot_runs(a)
    check(
        since is not None and since < IDLE_SECONDS,
        f"welding restarted the 5 minutes: {since} s since the last work",
    )
    last = time.monotonic() - (since or 0)
    time.sleep(max(0, last + IDLE_SECONDS - 20 - time.monotonic()))
    check(slot_runs(a) is not None, "20 s before the 5 idle minutes it still runs")
    ended = sa.wait_chat(a, "no building or welding for 5 min", 60, mark)
    took = time.monotonic() - last
    check(ended is not None, f"the slot ends: {ended}")
    check(
        IDLE_SECONDS - 20 <= took <= IDLE_SECONDS + 30,
        f"after 5 idle minutes: {took:.0f} s",
    )
    state = yard(a)
    check(octs(state), f"the blocks are OCT's again: {state}")
    power_stays(state, "after")
    check(
        float(state["uranium"]) >= uranium,
        f"the reactors kept their uranium: {state['uranium']}",
    )
    check(state["connected"] == "0", f"the connectors unlocked: {state['connected']}")
    check(
        state["items"] == "0", f"the shipyard's inventories are empty: {state['items']}"
    )
    back = float(info(a, ship).get("plates", -1))
    check(back > left, f"what the welders held went back to the ship: {left} -> {back}")
    check(
        PLATES - back <= 100,
        f"no components were lost: {PLATES} -> {back}, the repair took about 68",
    )
    time.sleep(3)
    check(sa.shoots(a, yard_id) is False, "the welders are off")
    check(
        client_owner(a, yard_id, state["cell"])[1] == "All",
        "the client sees the blocks shared with all again",
    )
    text = settled(a, "queue", "No ships in the queue")
    check("Repair" not in text, f"the slot is off the queue panel: {text!r}")

    # A load into a player's own projector is no table: it stays shared as it was
    at = a.call([CallOp.grid_to_world(int(damaged), (2, 2, 0))])
    a.character_teleport(*at.call(0)["world"])
    time.sleep(3)
    block = a.get_block(int(damaged), (0, 0, 0))
    log(str(a.load_projection(block["entityId"], xml=tr.blueprint_xml(so.SMALL))))
    time.sleep(8)
    seen = client_owner(a, int(damaged), "0,0,0")
    check(seen[1] == "Faction", f"a player's own projector stays as it was: {seen}")
    sa.at_table(a, STATION)
    probe(a, "undock")


def second_press(a) -> None:
    reply = press(a)
    check("Repair slot" in reply, f"another slot: {reply}")
    check(wait_for(lambda: slot_runs(a) is not None, 20), "it runs")
    check(theirs(yard(a), "OctTester"), "the blocks are the player's")
    docked = re.search(r"SHIP (\d+)", probe(a, f"yardship {STATION}"))
    ship = docked[1] if docked else "0"
    check(kiosks.wait_docked(a, ship), "a ship docks")
    reply = press(a)
    check("you canceled it" in reply, f"the second press ends it: {reply}")
    state = yard(a)
    check(octs(state), f"at once the blocks are OCT's: {state}")
    check(state["connected"] == "0", f"and the ship floats free: {state['connected']}")
    check("DOCKED" in probe(a, f"docked {ship}"), "the ship is still there")
    check(slot_runs(a) is None, "the shipyard runs no slot")
    probe(a, "undock")


def waits_for_a_build(a, b) -> None:
    """A slot behind a running build; the build's owner cancels it and the slot starts"""
    load(a, so.BIG)
    settled(a, "table", "Press the button")
    mark = sa.so_latest(a)
    check("is in the queue" in press(a), "a big ship is ordered")
    check(sa.wait_chat(a, "builds 'Order Big' for you now", 60, mark), "its build runs")
    time.sleep(15)
    check(octs(yard(a)), "a build leaves the blocks OCT's")
    reply = press(b)
    check("Repair slot" in reply, f"the other player books a slot: {reply}")
    text = settled(a, "queue", "Repair for OctOther: waiting")
    check(
        "Order Big" in text and "Repair for OctOther: waiting" in text,
        f"it waits behind the build: {text!r}",
    )
    check(slot_runs(a, "OctOther") is None, "and doesn't run")
    reply = press(b)
    check("left the queue" in reply, f"a waiting slot is canceled by a press: {reply}")
    check("Order Big" in queue(a), "the build isn't touched")
    check("Repair slot" in press(b), "booked again")
    settled(a, "queue", "Repair for OctOther: waiting")
    reply = press(a)
    check(
        "is canceled and removed" in reply,
        f"the owner's press cancels the build: {reply}",
    )
    check(
        wait_for(lambda: slot_runs(a, "OctOther") is not None, 30),
        "the slot starts once the build is gone",
    )
    state = yard(a)
    check(theirs(state, "OctOther"), f"the shipyard is the other player's now: {state}")
    check(state["items"] == "0", f"the build's components are gone: {state['items']}")
    check(
        not [g for g in a.list_grids() if g.get("name") == "Order Big"],
        "the unfinished ship is gone",
    )
    reply = press(b)
    check("you canceled it" in reply, f"their press ends it: {reply}")
    check(octs(yard(a)), "OCT's again")


def paid_cancel(a) -> None:
    """A paid build canceled while it runs: all of it is paid back"""
    with_config(a, **CONFIG, FreeBuildPcuLimit=0)
    log(
        sa.admin(
            a, f"!oct ledger deposit {STATION} me Component/SteelPlate {PAID_PLATES}"
        )
    )

    def plates() -> float:
        shown = " ".join(rig.chat(a, f"!oct ledger show {STATION} me", 3))
        found = re.search(
            r"(?<!: )Component/SteelPlate ([\d,.]+)", shown.split("reserved")[0]
        )
        return float(found[1].replace(",", "")) if found else 0.0

    def money() -> int:
        return int(re.search(r"BALANCE (-?\d+)", probe(a, "balance"))[1])

    probe(a, "balance 100000")
    before = (plates(), money())
    load(a, PAID)
    settled(a, "table", "paid")
    time.sleep(3)
    mark = sa.so_latest(a)
    reply = press(a)
    if "Your quote for" in reply:
        reply = press(a)
    check("is in the queue" in reply, f"the paid order: {reply}")
    check(
        sa.wait_chat(a, "builds 'Paid Cancel' for you now", 60, mark), "its build runs"
    )
    time.sleep(10)
    check(plates() == before[0] - PAID_PLATES, f"the ledger paid: {plates()}")
    check(money() < before[1], f"the labor fee is paid: {before[1]} -> {money()}")
    reply = press(a)
    check(
        "is canceled and removed" in reply and "paid back" in reply,
        f"a press cancels the running build: {reply}",
    )
    time.sleep(3)
    check(plates() == before[0], f"its components are back in the ledger: {plates()}")
    check(money() == before[1], f"the labor fee is back: {money()}")
    check(yard(a)["items"] == "0", "nothing stays in the shipyard")
    text = settled(a, "queue", "No ships in the queue")
    check("Paid Cancel" not in text, f"it left the queue: {text!r}")
    with_config(a, **CONFIG)


def restart(a):
    """A restart in the middle of a slot: it goes on, from the queue and the saved world"""
    check("Repair slot" in press(a), "a slot")
    check(wait_for(lambda: slot_runs(a) is not None, 20), "it runs")
    a = sa.restart(a)
    sa.at_table(a, STATION)
    check(wait_for(lambda: slot_runs(a) is not None, 60), "it runs after the restart")
    state = yard(a)
    check(theirs(state, "OctTester"), f"the blocks are still the player's: {state}")
    reply = press(a)
    check("you canceled it" in reply, f"the press ends it: {reply}")
    check(octs(yard(a)), "OCT's again")
    return a


def build_after(a) -> None:
    """The welders work for OCT again"""
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "an order after the slots")
    so.wait_chat(a, "is ready", 400)
    check(
        [g for g in a.list_grids() if g.get("name") == "Order Test"],
        "the shipyard builds it",
    )


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60, plugins=[kiosks.PROBE])
    tr.both_admins()
    with Second():
        folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client()
        with Second():
            b = rig.start_client()
        rig.ensure_character(a)
        with Second():
            rig.ensure_character(b)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        with_config(a, **CONFIG)
        log(tr.remove(a))
        time.sleep(3)
        book_and_idle_out(a, b)
        second_press(a)
        waits_for_a_build(a, b)
        paid_cancel(a)
        with Second():
            rig.stop_client()
        a = restart(a)
        build_after(a)
    finally:
        rig.stop_client()
        with Second():
            rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} repair slot checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
