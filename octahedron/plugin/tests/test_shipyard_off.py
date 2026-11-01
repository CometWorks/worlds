"""A shipyard is off unless someone has a job at it (SE-887), with two players on one server. It starts
off: connectors and welders off, cargo containers and terminals OCT's, and it goes off again if found on,
as in a world from before the rule. A free build turns on only the welders, the blocks stay OCT's. A
paid build's turn gives the slot's player and their faction the blocks, and nobody else. The reactors,
batteries and refill stations stay on and open throughout. A player who leaves doesn't stop the build:
a paid build whose DLC blocks the server no longer knows they own, one with a server restart while
they are away, and a repair slot with the slot's whole end. On a world built by build_world.py. The
presses are !oct order: see test_table_rules.py.

    OCT_TASK=SE-887 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_off.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 30 minutes.
Not covered: a cluster, where the player moves to another node (tests/shipyard_off_cluster.py).
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-887")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_repair_slot as rs  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_shipyard_turn as stt  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import Second, check, checks, load, press, settled  # noqa: E402

STATION = "Alpha"
PLATE = "Component/SteelPlate"
SHIP = "Off Test"
RUNS = 8 * 32
DLC_BLOCKS = 2
WITH_DLC = rig.grid(
    SHIP,
    777000887000200,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(8)
        for z in range(32)
    ]
    + [
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlockWarfare2", (0, 1, 2)),
        rig.block("MyObjectBuilder_Reactor", "LargeBlockSmallGeneratorWarfare2", (0, 1, 4)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)  # fmt: skip
REPAIR_BLOCKS = 60
# What the DLC blocks take and the default stock is short of
STOCKED = (
    "SteelPlate", "Construction", "PowerCell", "Computer", "LargeTube", "MetalGrid",
    "Reactor", "Motor", "SmallTube", "Girder", "InteriorPlate", "Superconductor",
)  # fmt: skip


def yard(remote) -> dict[str, str]:
    return rs.yard(remote)


def access(remote, player: str) -> dict[str, str]:
    reply = st.probe(remote, f"yard {STATION} {player}")
    return dict(re.findall(r"(\w+)=(\w+)", reply.split(" access ")[1]))


def off(state: dict[str, str]) -> bool:
    return (
        rs.octs(state)
        and state["welderson"] == "0"
        and state["connectorson"] == "0"
        and state["connected"] == "0"
    )


def stays_on(remote, what: str) -> None:
    """What is never switched: power, and the refill stations for anyone"""
    state = yard(remote)
    rs.power_stays(state, what)
    for key in ("reactorson", "refill"):
        have, total = state[key].split("/")
        check(have == total and total != "0", f"{what}: {key} {state[key]}")
    for player in ("OctTester", "OctOther"):
        check(
            access(remote, player)["refill"] == "True",
            f"{what}: the refill stations are open to {player}",
        )


def locked(remote, player: str, what: str) -> None:
    seen = access(remote, player)
    check(
        seen["cargo"] == seen["welders"] == seen["connectors"] == "False",
        f"{what}: {player} has no access: {seen}",
    )


def is_off(remote, what: str, wait: int = 30) -> None:
    rs.wait_for(lambda: off(yard(remote)), wait, 1)
    state = yard(remote)
    check(off(state), f"{what}: off and OCT's: {state}")
    locked(remote, "OctTester", what)
    locked(remote, "OctOther", what)
    stays_on(remote, what)


def starts_off(a) -> None:
    is_off(a, "at the start")
    log(st.probe(a, f"yardon {STATION}"))
    check(yard(a)["welderson"] == "24", "the probe turned them on")
    is_off(a, "found on, as in an old world")


def free_build(a) -> None:
    with_config(a, **tr.CONFIG)
    mark = tp.latest(a)
    load(a)
    settled(a, "table", "Press the button")
    check("is in the queue" in press(a), "a free order")
    check(
        rs.wait_for(lambda: yard(a)["welderson"] == "24", 30, 1),
        "a free build turns on the welders",
    )
    state = yard(a)
    check(rs.octs(state), f"the blocks stay OCT's: {state}")
    check(state["connectorson"] == "0", f"the connectors stay off: {state}")
    locked(a, "OctTester", "in a free build")
    stays_on(a, "in a free build")
    check(tp.wait_chat(a, "is ready", 400, mark), "it is built")
    is_off(a, "after a free build")
    log(st.probe(a, "removegrid Order_Test"))
    tr.remove(a)


def turn_access(a, b) -> None:
    with_config(a, **stt.PAY, ShipyardTurnMinutes=2)
    log(st.probe(a, "balance 300000"))
    stt.turn(a)
    state = yard(a)
    check(rs.theirs(state, "OctTester"), f"the slot's player has the blocks: {state}")
    check(
        state["welderson"] == "24" and state["connectorson"] == "8",
        f"on for a slot: {state}",
    )
    seen = access(a, "OctTester")
    check(
        seen["cargo"] == seen["welders"] == seen["connectors"] == "True",
        f"the slot's player has access: {seen}",
    )
    check(seen["reactors"] == "False", f"but not to the reactors: {seen}")
    locked(a, "OctOther", "another player in a slot")
    stays_on(a, "in a slot")
    st.probe(a, "faction KRO founder")
    with Second():
        st.probe(b, "faction KRO member")
    seen = access(a, "OctOther")
    check(
        seen["cargo"] == seen["welders"] == seen["connectors"] == "True",
        f"their faction has access: {seen}",
    )
    check(seen["reactors"] == "False", f"but not to the reactors: {seen}")
    with Second():
        st.probe(b, "faction KRO none")
    st.probe(a, "faction KRO none")
    locked(a, "OctOther", "out of the faction")
    rig.chat(a, "!oct shipyard done", 4)
    is_off(a, "after a slot")
    tr.remove(a)


def rejoin(a=None):
    a = rig.start_client()
    rig.ensure_character(a)
    a.set_admin_flag("invulnerable", True)
    sa.at_table(a, STATION)
    return a


def paid(a, blueprint: str, plates: int) -> None:
    stt.turn(a, blueprint)
    log(st.probe(a, f"yardload {STATION} {PLATE} {plates}"))
    reply = stt.start(a)
    check("builds" in reply or "bought" in reply, f"the press starts it: {reply}")
    stt.placed(a, SHIP)


def delivered(b, blocks: int, what: str) -> None:
    """The ship left its scaffold, all of it, DLC blocks too"""

    have = 0

    def ship():
        # It is pasted anew off the arm, so the first id seen may be gone already
        nonlocal have
        for g in b.list_grids():
            if g.get("name") == SHIP and not g["isStatic"]:
                have = int(rs.info(b, g["entityId"]).get("blocks", 0))
                if have == blocks:
                    return True
        return False

    rs.wait_for(ship, 500, 5)
    check(have > 0, f"{what}: the build ends without the player: {tr.queue(b)}")
    check(have == blocks, f"{what}: all of it, DLC blocks too: {have} of {blocks}")


def leaves_a_build(a, b):
    """The server forgets the DLCs: ToolCore skips those blocks of the player's welders. Gone, it doesn't."""
    with_config(a, **stt.PAY, ShipyardTurnMinutes=1)
    paid(a, WITH_DLC, RUNS * 25)
    reply = st.probe(a, "nodlc")
    check("NODLC" in reply, f"the server forgets what they own: {reply}")
    owned = st.probe(a, "dlc OctTester").split()
    check(owned[:2] == ["DLC", "0"], f"so it says they own nothing: {owned}")
    rig.stop_client()

    def all_owned() -> bool:
        reply = st.probe(b, "dlc OctTester").split()
        return reply[1] == reply[2]

    # A client that quit stays online for a while
    check(rs.wait_for(all_owned, 180, 5), "for a player who is away it doesn't ask")
    state = yard(b)
    check(state["welderson"] == "24", f"their welders go on: {state}")
    delivered(b, RUNS + DLC_BLOCKS, "left")
    is_off(b, "the deadline passed without them")
    a = rejoin()
    log(st.probe(a, "removegrid " + SHIP.replace(" ", "_")))
    tr.remove(a)
    return a


def restart_while_away(a, b):
    """The restarted server can't ask about a player who isn't back"""
    with_config(a, **stt.PAY, ShipyardTurnMinutes=1)
    paid(a, WITH_DLC, RUNS * 25)
    # The server first: the welders need only half a minute
    rig.stop_server()
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.start_server()
    with Second():
        b = rig.start_client()
        rig.ensure_character(b)
    b.set_admin_flag("invulnerable", True)
    sa.at_table(b, STATION)
    log(tr.queue(b))
    # The build may be over by the time this client is back. Had the restart turned the welders
    # off, or taken the blocks, no ship would come out of it.
    delivered(b, RUNS + DLC_BLOCKS, "restarted")
    is_off(b, "after the restart's build")
    a = rejoin()
    log(st.probe(a, "removegrid " + SHIP.replace(" ", "_")))
    tr.remove(a)
    return a, b


def leaves_a_repair(a, b):
    with_config(a, **stt.PAY, ShipyardRepairIdleMinutes=1, ShipyardTurnMinutes=1)
    reply = press(a)
    check("Repair slot" in reply, f"a slot: {reply}")
    check(rs.wait_for(lambda: rs.slot_runs(a) is not None, 20), "it runs")
    docked = re.search(r"SHIP (\d+)", st.probe(a, f"yardship {STATION}"))
    check(docked is not None, f"a ship with the components: {docked}")
    check(st.wait_docked(a, docked[1] if docked else "0"), "it docks")
    target = re.search(
        r"TARGET (\d+)", st.probe(a, f"yardtarget {STATION} {REPAIR_BLOCKS}")
    )
    check(target is not None, f"a ship to repair: {target}")
    damaged = target[1] if target else "0"
    rig.stop_client()
    before = int(rs.info(b, damaged).get("blocks", 0))

    def repaired():
        return rs.info(b, damaged).get("blocks") == str(3 + REPAIR_BLOCKS)

    check(
        before < 3 + REPAIR_BLOCKS,
        f"the player left before it was done: {before} blocks",
    )
    check(rs.wait_for(repaired, 400, 5), f"the slot goes on: {rs.info(b, damaged)}")
    check(
        rs.wait_for(lambda: rs.slot_runs(b) is None, 150, 5),
        "the slot ends by its idle time",
    )
    # The turn lasts another minute for taking out what is left
    is_off(b, "the repair slot over without them", 100)
    a = rejoin()
    st.probe(a, "undock")
    log(st.probe(a, "removegrid Repair_Target"))
    tr.remove(a)
    return a


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
        starts_off(a)
        with_config(a, **tr.CONFIG)
        log(tr.remove(a))
        for item in STOCKED:
            sa.admin(a, f"!oct market set {STATION} Component/{item} 20000")
        free_build(a)
        turn_access(a, b)
        a = leaves_a_build(a, b)
        a, b = restart_while_away(a, b)
        a = leaves_a_repair(a, b)
        with Second():
            rig.stop_client()
        a = sa.restart(a)
        sa.at_table(a, STATION)
        state = yard(a)
        check(off(state), f"off after a restart with no job: {state}")
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
        f"{len(checks) - len(failed)}/{len(checks)} shipyard off checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
