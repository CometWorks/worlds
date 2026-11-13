"""The shipyard's modes (SE-1386) at a real station shipyard, with two players on one server:
build, repair, grind and hack, booked as the mode panel's buttons do (!oct shipyard book), each
working only in the shipyard's build volume, the cylinder around its center line.

- The billboard screen at the panel shows the instructions, a booking's confirmation for 5 s, and
  the instructions again; Build without a blueprint asks for one.
- Switching: another mode's button swaps a waiting or running slot, the same button cancels it.
- Repair: the welders weld the player's damaged ship and build its projection with components the
  station sells them, as none are brought; a ship out of the volume stays damaged.
- Grind: the player's ship in the volume is ground and its parts go into the player's ship docked
  at the connectors; a ship of theirs out of the volume and another player's in it stay. A second
  grind with nothing docked sells what was ground for the player.
- Hack: an NPC ship in the volume is the player's after ShipyardHackSeconds (10 s here); another
  player's ship while they are online, and a ship sticking out of the volume, are not.
- Contracts (SE-1408): a wreck whose contract the player took, ground in a grind slot, pays the
  contract's reward and none of its parts are sold; one hacked in a hack slot is theirs and pays
  the reward as a tow-in. A tiny ContractSalvageRadius keeps the contracts' own tow-in check out.

On a world built by build_world.py:

    OCT_TASK=SE-1386 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_modes.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 15 minutes.
Not covered: a cluster (online on another node), and pressing the panel's buttons by hand.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1386")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_repair_slot as rs  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, ship_reply, with_config  # noqa: E402
from test_table_rules import (
    Second,
    check,
    checks,
    screens,
    settled,
    wait_for,
)  # noqa: E402

STATION = rs.STATION


def slot_end(kind: str) -> str:
    """The end of a slot of this kind, not its start: "Your grind slot at Alpha Station Shipyard ended: ..." """
    return f"Your {kind} slot at {STATION} Station Shipyard ended:"


CONFIG = {
    **tr.CONFIG,
    "ShipyardHackSeconds": 10,
    "ShipyardRepairIdleMinutes": 2,
}


def probe(remote, args: str) -> str:
    return st.probe(remote, args)


def book(remote, mode: str) -> str:
    return ship_reply(remote, f"!oct shipyard book {mode}")


def slot(remote, kind: str, name: str = "OctTester") -> float | None:
    """Seconds since the running slot's last work, None without such a slot"""
    reply = " ".join(rig.chat(remote, "!oct shipyards", 3))
    found = re.search(rf"{kind} slot of {name}, (\d+) s", reply)
    return float(found[1]) if found else None


def place(
    remote, name: str, who: str = "me", where: str = "in", items: str = ""
) -> str:
    reply = probe(
        remote,
        f"yardvol {STATION} {name.replace(' ', '_')} {who} {where}"
        + (f" {items}" if items else ""),
    )
    found = re.search(r"SHIP (\d+)", reply)
    check(found is not None, f"'{name}' placed {where} the volume: {reply}")
    return found[1] if found else "0"


def info(remote, grid: str) -> dict[str, str]:
    return rs.info(remote, grid)


def gone(remote, grid: str) -> bool:
    return "gone" in probe(remote, f"info {grid}")


def balance(remote) -> int:
    return int(re.search(r"BALANCE (-?\d+)", probe(remote, "balance"))[1])


def me(remote) -> str:
    """The client's own identity id"""
    return re.search(r"\bME (\d+)", probe(remote, f"yardvol {STATION}"))[1]


def remove(remote, *names: str) -> None:
    for name in names:
        probe(remote, f"removegrid {name.replace(' ', '_')}")


def billboard(a) -> None:
    """Instructions between bookings, a booking's confirmation for 5 s, then the instructions"""
    details = screens(a)["details"]
    check(
        "Book the shipyard here." in details,
        f"the instructions between bookings: {details!r}",
    )
    check("4. Grind" in details and "Max 6 entries" in details, "all of them")
    reply = book(a, "build")
    check(
        "Load a blueprint" in reply, f"Build without a blueprint asks for one: {reply}"
    )
    check("Book the shipyard here." in screens(a)["details"], "and books nothing")
    reply = book(a, "repair")
    check("booked a Repair slot" in reply, f"Repair books a slot: {reply}")
    details = " ".join(screens(a)["details"].split())
    check(
        "Repair slot booked for OctTester" in details
        and "above the holo table" in details,
        f"the confirmation, pointing at the queue: {details!r}",
    )
    shown = settled(a, "details", "Book the shipyard here.", 15)
    check(
        "Book the shipyard here." in shown,
        f"the instructions again after 5 s: {shown!r}",
    )


def switching(a) -> None:
    """The slot booked in billboard() runs; Grind swaps it, Grind again cancels"""
    check(wait_for(lambda: slot(a, "repair") is not None, 30), "the repair slot runs")
    reply = book(a, "grind")
    check(
        "booked a Grind slot" in reply, f"Grind swaps the running repair slot: {reply}"
    )
    check(wait_for(lambda: slot(a, "grind") is not None, 30), "the grind slot runs")
    text = settled(a, "queue", "grinding OctTester")
    check("grinding OctTester (grind)" in text, f"on the queue panel: {text!r}")
    reply = book(a, "grind")
    check("you canceled it" in reply, f"Grind again cancels it: {reply}")
    check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "the shipyard is OCT's again")


def repair(a) -> None:
    """The welders weld the player's ship with components the station sells"""
    # The test world's market holds no components until the rig stocks them
    for item, count in sa.STOCK.items():
        if item.startswith("Component/"):
            sa.admin(a, f"!oct market set {STATION} {item} {count}", 3)
    before = balance(a)
    target = re.search(r"TARGET (\d+)", probe(a, f"yardtarget {STATION} 4"))
    check(
        target is not None,
        f"a damaged ship with a repair projector in the middle: {target}",
    )
    damaged = target[1] if target else "0"
    outside = place(a, "Out Of Reach", "me", "outdamaged")
    out_before = info(a, outside)
    reply = book(a, "repair")
    check("booked a Repair slot" in reply, f"booked: {reply}")
    check(wait_for(lambda: slot(a, "repair") is not None, 30), "it runs")
    mark = sa.so_latest(a)
    sold = sa.wait_chat(a, "The station sold your repair", 60, mark)
    check(sold is not None, f"the station sells what the welders lack: {sold}")

    def repaired():
        got = info(a, damaged)
        return got.get("full") == "7" and got.get("blocks") == "7"

    check(
        wait_for(repaired, 240, 5),
        f"welded and built from the projection: {info(a, damaged)}",
    )
    after = balance(a)
    check(after < before, f"paid from the account: {before} -> {after}")
    check(
        info(a, outside).get("integrity") == out_before.get("integrity"),
        f"a ship out of the volume isn't welded: {out_before} -> {info(a, outside)}",
    )
    reply = book(a, "repair")
    check("you canceled it" in reply, f"Repair again ends it: {reply}")
    rig.chat(a, "!oct shipyard done", 4)
    check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "OCT's again")
    remove(a, "Repair Target", "Out Of Reach")


def grind(a, b) -> None:
    """The player's ship in the volume is ground into their docked ship; nothing else is"""
    keep = place(a, "Keep Me", "me", "out")
    kept = info(a, keep)
    with Second():
        theirs = place(b, "Not Yours", "me", "in:25")
    mark = sa.so_latest(a)
    reply = book(a, "grind")
    check("booked a Grind slot" in reply, f"booked: {reply}")
    check(wait_for(lambda: slot(a, "grind") is not None, 30), "it runs")
    # The shipyard's connectors are on only during a slot
    docked = re.search(
        r"SHIP (\d+)", probe(a, f"yardship {STATION} Component/SteelPlate=1")
    )
    check(docked is not None, f"the player's cargo ship at a connector: {docked}")
    hauler = docked[1] if docked else "0"
    check(st.wait_docked(a, hauler), "it docks")
    plates = float(info(a, hauler).get("plates", 0))
    # The scrap comes in once the hauler is docked: 24 deconstructors grind it in seconds
    scrap = place(a, "Scrap Me", "me", "in", "Component/SteelPlate=30")
    yard_id = rs.shipyard_id(a)
    check(
        wait_for(
            lambda: rs.yard(a).get("destroshoot", "0") != "0" or gone(a, scrap), 30
        ),
        f"the deconstructors work: {rs.yard(a).get('destroshoot')}",
    )
    check(
        wait_for(lambda: gone(a, scrap), 300, 5),
        f"the ship in the volume is ground: {info(a, scrap)}",
    )
    ended = sa.wait_chat(a, slot_end("grind") + " nothing is left", 90, mark)
    check(
        ended is not None and "nothing is left" in ended,
        f"the slot ends by itself: {ended}",
    )
    check(
        ended is not None and "went into 'Dock Hauler'" in ended,
        f"into the docked ship: {ended}",
    )
    got = float(info(a, hauler).get("plates", 0))
    check(
        got >= plates + 30,
        f"its cargo and parts are in the docked ship: {plates} -> {got}",
    )
    check(not gone(a, keep), "a ship of theirs out of the volume stays")
    check(info(a, keep) == kept, f"untouched: {kept} -> {info(a, keep)}")
    check(not gone(a, theirs), "another player's ship in the volume stays")
    check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "OCT's again")
    check(wait_for(lambda: sa.shoots(a, yard_id) is False, 20), "the tools are off")
    probe(a, "undock")
    remove(a, "Dock Hauler", "Keep Me", "Not Yours")

    # Nothing docked: what was ground is sold for the player
    scrap = place(a, "Scrap Me Too", "me", "in", "Component/SteelPlate=30")
    before = balance(a)
    mark = sa.so_latest(a)
    check("booked a Grind slot" in book(a, "grind"), "a second grind")
    check(wait_for(lambda: gone(a, scrap), 300, 5), "ground")
    ended = sa.wait_chat(a, "was sold for", 90, mark)
    check(ended is not None, f"the parts are sold for the player: {ended}")
    after = balance(a)
    check(after > before, f"paid: {before} -> {after}")
    check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "OCT's again")


def hack(a, b) -> None:
    """An NPC ship in the volume becomes the player's; an online player's and one sticking out don't"""
    # Booked before the ships come in: an idle shipyard's sweep (SE-1383) clears NPC ships
    mark = sa.so_latest(a)
    reply = book(a, "hack")
    check("booked a Hack slot" in reply, f"booked: {reply}")
    check(wait_for(lambda: slot(a, "hack") is not None, 30), "it runs")
    npc = place(a, "Pirate", "npc", "in:25")
    across = place(a, "Too Long", "npc", "across:-25")
    told = sa.wait_chat(a, "sticks out of", 30, mark)
    check(told is not None, f"a ship sticking out is named: {told}")
    hacked = sa.wait_chat(a, slot_end("hack") + " 'Pirate' is yours", 60, mark)
    check(
        hacked is not None and "'Pirate' is yours now" in hacked,
        f"the NPC ship is hacked: {hacked}",
    )
    mine = me(a)
    owners = info(a, npc).get("owners", "")
    check(owners == mine, f"it is the player's: {owners} ({mine})")
    check(
        info(a, across).get("owners") != mine,
        f"the one sticking out isn't: {info(a, across)}",
    )
    remove(a, "Pirate", "Too Long")

    # Another player's ship, its owner online: not hacked
    mark = sa.so_latest(a)
    check("booked a Hack slot" in book(a, "hack"), "another hack slot")
    with Second():
        online = place(b, "Online Owner", "me", "in")
        theirs = me(b)
    told = sa.wait_chat(a, "while its owner OctOther is online", 40, mark)
    check(told is not None, f"an online player's ship isn't hacked: {told}")
    time.sleep(CONFIG["ShipyardHackSeconds"] + 12)
    check(
        info(a, online).get("owners") == theirs, f"it stays theirs: {info(a, online)}"
    )
    check("you canceled it" in book(a, "hack"), "Hack again ends it")
    remove(a, "Online Owner")


AWAY = 6000


def take_contract(a, name: str) -> tuple | None:
    """A contract on an ownerless ship, taken by a, then the ship in the volume. The contract is
    posted with the ship out of the station's safe zone and of the players' sync range: a wreck
    in either gets none."""
    ship = place(a, name, "none", "in")
    grid = name.replace(" ", "_")
    probe(a, f"movegrid {grid} {AWAY}")
    log(" | ".join(rig.chat(a, "!oct contracts post", 4)))
    # The chat list is too long for the client's history: the server log names the contract
    pattern = rf"Contract (\d+) posted at \S+ Station: wreck '{name}' \(\d+\), (\d+) SC"
    # The log is flushed a while after the line is written
    wait_for(lambda: re.search(pattern, rig.server_game_log()), 30)
    posted = re.findall(pattern, rig.server_game_log())
    contract = (
        (int(posted[-1][0]), "wreck", "Inactive", int(posted[-1][1]), name)
        if posted
        else None
    )
    check(
        contract is not None and contract[3] > 0,
        f"a wreck contract on '{name}': {contract}",
    )
    if contract:
        reply = " | ".join(rig.chat(a, f"!oct contract accept {contract[0]}", 4))
        check("Success" in reply, f"taken: {reply}")
    probe(a, f"movegrid {grid} {-AWAY}")
    return (ship, contract) if contract else None


def contracts(a, b) -> None:
    """A contract wreck ground pays the contract instead of its parts; one hacked is kept and paid"""
    with_config(
        a,
        **CONFIG,
        ShipyardSweepIdle="false",
        ContractSalvageMinBlocks=1,
        ContractWreckMinReward=1,
        ContractSalvageRadius=1,
    )
    taken = take_contract(a, "Contract Scrap")
    if taken:
        ship, contract = taken
        before = balance(a)
        mark = sa.so_latest(a)
        check("booked a Grind slot" in book(a, "grind"), "a grind slot for the wreck")
        check(
            wait_for(lambda: float(info(a, ship).get("integrity", 0)) == 0, 300, 5),
            f"the contract wreck is ground: {info(a, ship)}",
        )
        # Not the slot's end alone: the HUD keeps the earlier grind's end, whatever the mark
        ended = sa.wait_chat(a, "Contracts paid you for", 120, mark)
        check(
            ended is not None and "Contracts paid you for 1 of the wrecks" in ended,
            f"the slot says the contract paid: {ended}",
        )
        check(
            ended is not None and "was sold for" not in ended,
            f"nothing was sold for the player: {ended}",
        )
        after = balance(a)
        check(
            after - before == contract[3],
            f"paid the contract's {contract[3]:,} SC and no sale: {before:,} -> {after:,}",
        )
        check(wait_for(lambda: rs.octs(rs.yard(a)), 25), "OCT's again")
        remove(a, "Contract Scrap")

    taken = take_contract(a, "Contract Keep")
    if taken:
        ship, contract = taken
        before = balance(a)
        mark = sa.so_latest(a)
        check("booked a Hack slot" in book(a, "hack"), "a hack slot for the wreck")
        hacked = sa.wait_chat(a, "you kept it, and the contract paid you", 90, mark)
        check(hacked is not None, f"the hacked wreck counts as kept: {hacked}")
        if hacked is None:
            log(f"chat: {[m['Text'] for m in a.get_chat_messages()['messages'][-12:]]}")
        mine = me(a)
        owners = info(a, ship).get("owners", "")
        check(owners == mine, f"it is the player's: {owners} ({mine})")
        after = balance(a)
        check(
            after - before == contract[3],
            f"paid the contract's {contract[3]:,} SC: {before:,} -> {after:,}",
        )
        remove(a, "Contract Keep")
    with_config(a, **CONFIG)


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    tr.both_admins()
    with Second():
        folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    rig.link_mods(folder, rig.STATION_MODS)
    try:
        rig.start_server()
        a = rig.start_client()
        with Second():
            b = rig.start_client()
        rig.ensure_character(a)
        with Second():
            rig.ensure_character(b)
        a.set_admin_flag("invulnerable", True)
        b.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        with_config(a, **CONFIG)
        log(tr.remove(a))
        log(probe(a, f"yardvol {STATION}"))
        probe(
            a, "balance 200000"
        )  # past 1,000,000 SC the game unlocks an achievement, which crashes a client without Steam
        # OCT_SECTIONS=contracts,... runs only those
        only = os.environ.get("OCT_SECTIONS", "").split(",")
        sections = {
            "billboard": lambda: billboard(a),
            "switching": lambda: switching(a),
            "repair": lambda: repair(a),
            "grind": lambda: grind(a, b),
            "hack": lambda: hack(a, b),
            "contracts": lambda: contracts(a, b),
        }
        for name, run in sections.items():
            if only == [""] or name in only:
                run()
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
        f"{len(checks) - len(failed)}/{len(checks)} shipyard mode checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
