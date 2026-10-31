"""What the shipyard leaves out of a ship (SE-805). Its welders are the station's, which own no DLC.

- owned: a player owning the DLCs, as the server sees this rig's client, gets all of the ship, welded
  by those welders.
- no-dlc: a player without them (!sprobe nodlc) gets the ship without its DLC blocks, told so, and it
  is finished: no component goes to a block it can't place.
- paid-offline: a paid build's welders are the player's (SE-840). With the player gone, the game sees
  no Steam id for them either: the DLC blocks still get built.
- docked: a paid ship with a grid no welder can place, like one docked by a connector, is delivered
  without it. The chat names the grid, and that grid's components stay in the containers for the
  player to take, or to sell at the end of the turn.

    uv run --no-project --with httpx python tests/test_shipyard_left_out.py [case ...]
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-805")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_client as tc  # noqa: E402
import test_paid_builds as pb  # noqa: E402
import test_shipyard_order as so  # noqa: E402

check = st.check

NAME = "Left Out Test"
HULL = [
    rig.block("MyObjectBuilder_RemoteControl", "LargeBlockRemoteControl", (0, 1, 0)),
    *(
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
        for z in range(8)
    ),
]
DLC = [
    ("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlockWarfare2", (0, 1, 2)),
    ("MyObjectBuilder_Reactor", "LargeBlockSmallGeneratorWarfare2", (0, 1, 4)),
]
WITH_DLC = rig.grid(
    NAME,
    777000805000100,
    HULL + [rig.block(*block) for block in DLC],
    tag="MyObjectBuilder_CubeGrid",
)
CONNECTOR, OTHER = 777000805000201, 777000805000202
DOCKED_NAME = "Docked Part"
DOCKED_PLATES = 25 + 150  # the armor block and the connector
WITH_DOCKED = rig.grid(
    NAME,
    777000805000200,
    HULL
    + [
        rig.block(
            "MyObjectBuilder_ShipConnector",
            "Connector",
            (0, 1, 6),
            f"<EntityId>{CONNECTOR}</EntityId><ConnectedEntityId>{OTHER}</ConnectedEntityId>",
        )
    ],
    tag="MyObjectBuilder_CubeGrid",
) + rig.grid(
    DOCKED_NAME,
    777000805000300,
    [
        rig.block(
            "MyObjectBuilder_ShipConnector",
            "Connector",
            (0, 0, 0),
            f"<EntityId>{OTHER}</EntityId><ConnectedEntityId>{CONNECTOR}</ConnectedEntityId>"
            "<IsMaster>true</IsMaster>",
        ),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 1, 0)),
    ],
    position=(0, 5, -15),
    tag="MyObjectBuilder_CubeGrid",
)


def ship_subtypes() -> list[str]:
    sector, _ = so.saved()
    ship = next(
        o
        for o in sector.find("SectorObjects")
        if o.findtext("DisplayName") == NAME and o.find("CubeBlocks") is not None
    )
    return [b.findtext("SubtypeName") or "" for b in ship.find("CubeBlocks")]


def no_plugin_errors(stalled: bool) -> None:
    log = rig.server_game_log()
    check((" no progress " in log) == stalled, f"the order stalled: {stalled}")
    check(
        not re.search(r"Octahedron.*(Exception|Critical)", log),
        "no plugin errors",
    )


def dlc(owned: bool) -> None:
    so.prepare(WITH_DLC, 60)
    rig.start_server()
    remote = rig.start_client()
    try:
        table = min(tc.tables(remote), key=lambda b: b["distance"])
        tc.stand_by(remote, table)
        if not owned:
            reply = st.probe(remote, "nodlc")
            check("NODLC" in reply, f"owns no DLC: {reply}")
        quote = tc.ship_reply(remote, "!oct order")
        check("Your quote for" in quote, f"quote: {quote}")
        check(
            ("Left out" in quote) != owned,
            f"{'nothing' if owned else 'the DLC blocks'} left out: {quote}",
        )
        reply = tc.ship_reply(remote, "!oct order")
        check("is in the queue" in reply, f"ordered: {reply}")
        check(True, so.wait_chat(remote, "is ready", 600))
    finally:
        rig.stop_client()
        rig.stop_server()
    so.check_ship(NAME, len(HULL) + (len(DLC) if owned else 0), complete=True)
    subtypes = set(ship_subtypes())
    wanted = {block[1] for block in DLC}
    check(
        wanted <= subtypes if owned else not wanted & subtypes,
        f"DLC blocks {'built' if owned else 'not built'}: {sorted(subtypes)}",
    )
    no_plugin_errors(stalled=False)


def paid_order(remote, loaded: dict | None = None) -> None:
    """A paid order up to the press at the player's turn, with what is loaded before it"""
    tc.with_config(
        remote,
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=0,
        ShipyardTableMinutes=600,
    )
    for item, count in pb.STOCK.items():
        st.admin(remote, f"!oct market set Alpha {item} {count}")
    st.probe(remote, f"pay me {pb.FUNDS - pb.balance(remote)}")
    pb.at_table(remote)
    quote = pb.press(remote)
    check("Your quote for" in quote, f"quote: {quote}")
    reply = pb.press(remote)
    check("is in the queue" in reply, f"ordered: {reply}")
    check(so.wait_chat(remote, "It is your turn", 60), "the player's turn")
    for item, count in (loaded or {}).items():
        st.probe(remote, f"yardload Alpha {item} {count}")
    reply = pb.press(remote)
    check(f"builds '{NAME}' for you now" in reply, f"the press starts it: {reply}")


def paid_offline() -> None:
    so.prepare(WITH_DLC, 60)
    rig.start_server()
    remote = rig.start_client()
    try:
        # The DLC blocks' parts the station doesn't sell
        paid_order(
            remote,
            {
                "Component/PowerCell": 80,
                "Component/Reactor": 100,
                "Component/MetalGrid": 4,
            },
        )
        rig.stop_client()
        deadline = time.monotonic() + 600
        pattern = rf"'{NAME}' of .* built or broken down"
        while time.monotonic() < deadline and not re.search(
            pattern, rig.server_game_log()
        ):
            time.sleep(5)
        check(re.search(pattern, rig.server_game_log()), "built with the player gone")
    finally:
        rig.stop_client()
        rig.stop_server()
    so.check_ship(NAME, len(HULL) + len(DLC), complete=True)
    subtypes = set(ship_subtypes())
    wanted = {block[1] for block in DLC}
    check(wanted <= subtypes, f"DLC blocks built: {sorted(subtypes)}")
    no_plugin_errors(stalled=False)


def docked() -> None:
    so.prepare(WITH_DOCKED, 60)
    rig.start_server()
    remote = rig.start_client()
    try:
        paid_order(remote)
        line = so.wait_chat(remote, "The ship is yours without", 600)
        check(DOCKED_NAME in line, f"the chat names the grid: {line}")
        left = st.probe(remote, "yardstock Alpha")
        plates = re.search(r"Component/SteelPlate=(\d+)", left)
        check(
            plates is not None and int(plates[1]) == DOCKED_PLATES,
            f"its plates stay in the containers: {left}",
        )
        money = pb.balance(remote)
        st.admin(remote, "!oct shipyard done")
        check(pb.balance(remote) > money, "and sell at the end of the turn")
    finally:
        rig.stop_client()
        rig.stop_server()
    so.check_ship(NAME, len(HULL) + 1, complete=True)
    no_plugin_errors(stalled=True)


CASES = {
    "owned": lambda: dlc(owned=True),
    "no-dlc": lambda: dlc(owned=False),
    "paid-offline": paid_offline,
    "docked": docked,
}


def main() -> int:
    for case in sys.argv[1:] or CASES:
        print(f"--- {case}", flush=True)
        rig.stop_client()
        rig.stop_server()
        CASES[case]()
    return st.summary("left out")


if __name__ == "__main__":
    sys.exit(main())
