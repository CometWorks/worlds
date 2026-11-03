"""Checks the OCT faction rules on the test server with the headless client, in a world built by
build_world.py (OCT_WORLD_SOURCE): a new player joins OCT, blocks they place by hand get
No share, on the client as on the server, and the leaderboard, with LeaderboardOctPlayers on,
ranks them by name with their grids. With --no-oct the rules and LeaderboardOctPlayers are off:
the player stays without a faction, the game's faction share stays, and the player isn't on the
leaderboard. With --own-faction the rules are on, but the player makes a faction of their own
(the station probe's founder command): their blocks keep the game's faction share and the faction
is on the leaderboard.

    OCT_WORLD_SOURCE=<built world> uv run --no-project --with httpx python \\
        tests/test_oct_faction.py [--no-oct | --own-faction]
"""

from __future__ import annotations

import re
import sys
import time

import rig
import station_tests as st

RIG_AT = (250_000, 0, 0)
BUILD_RIG = 777000975000100
# A cargo container and a battery: blocks with an owner
PLACED = {
    (2, 1, 0): "LargeBlockSmallContainer",
    (4, 1, 0): "LargeBlockBatteryBlock",
}


def shares(xml: str) -> dict[tuple[int, int, int], str]:
    """Cell -> share mode of every block in a grid's object builder; the game leaves None out"""
    found = {}
    for block in re.findall(
        r"<MyObjectBuilder_CubeBlock .*?</MyObjectBuilder_CubeBlock>", xml, re.S
    ):
        cell = re.search(r'<Min x="(-?\d+)" y="(-?\d+)" z="(-?\d+)"', block)
        share = re.search(r"<ShareMode>(\w+)</ShareMode>", block)
        if cell:
            found[tuple(int(v) for v in cell.groups())] = share[1] if share else "None"
    return found


def client_share(remote, cell) -> str | None:
    """A placed block's share mode, as the client sees it"""
    try:
        block = remote.get_block(BUILD_RIG, cell)
        xml = remote.get_object_builder(block["entityId"])
    except Exception:  # noqa: BLE001 -- not there yet
        return None
    share = re.search(r"<ShareMode>(\w+)</ShareMode>", xml)
    return share[1] if share else "None"


def main() -> None:
    oct_on = "--no-oct" not in sys.argv
    own = "--own-faction" in sys.argv
    rig.stop_client()
    rig.stop_server()
    spine = [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, 0))
        for x in range(8)
    ]
    rig.prepare_world(extra_grids=[rig.grid("OCT Build Rig", BUILD_RIG, spine, RIG_AT)])
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for option in ("NewPlayersJoinOct", "OctBuildsPrivate"):
        config, count = re.subn(
            rf"<{option}>\w+</{option}>",
            f"<{option}>{str(oct_on).lower()}</{option}>",
            config,
        )
        assert count == 1, option
    if oct_on and not own:
        config = config.replace(
            "<OctBuildsPrivate>",
            "<LeaderboardOctPlayers>true</LeaderboardOctPlayers>\n  <OctBuildsPrivate>",
        )
    rig.write_plugin_config(config)
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()

    tag = remote.get_faction()["tag"]
    print("faction:", tag)
    assert tag == ("OCT" if oct_on else None), f"faction {tag}"
    if own:
        print(st.probe(remote, "faction KRS founder"))
        tag = remote.get_faction()["tag"]
        print("faction:", tag)
        assert tag == "KRS", f"faction {tag}"

    remote.set_admin_flag("creativeTools", True)
    remote.character_teleport(RIG_AT[0], 30, 0)
    deadline = time.monotonic() + 60
    while not any(g["entityId"] == BUILD_RIG for g in remote.list_grids()):
        assert time.monotonic() < deadline, "the build rig never showed up"
        time.sleep(2)
    for cell, subtype in PLACED.items():
        print(
            "placed",
            subtype,
            remote.character_build_block(BUILD_RIG, cell, subtype)["sent"],
        )

    expected = "None" if oct_on and not own else "Faction"
    deadline = time.monotonic() + 30
    while True:
        client = {cell: client_share(remote, cell) for cell in PLACED}
        if all(share == expected for share in client.values()):
            break
        assert (
            time.monotonic() < deadline
        ), f"client shares {client}, expected {expected}"
        time.sleep(2)
    print("client shares:", client)

    me = remote.get_faction()["identityId"]
    identities = remote.list_identities()
    if isinstance(identities, dict):  # the plugin may wrap the list
        identities = next(v for v in identities.values() if isinstance(v, list))
    name = next(i["displayName"] for i in identities if i["identityId"] == me)
    board = rig.chat(remote, "!oct leaderboard", 15)
    mine = [line for line in board if f". {name}:" in line]
    if own:
        name = "KRS Station Testers"
        mine = [line for line in board if f". [KRS] {name}:" in line]
    print("leaderboard:", board)
    if oct_on:
        assert mine and not mine[0].endswith(" 0 PCU"), board
    else:
        assert not mine, board

    rig.stop_client()
    rig.stop_server()  # saves
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    grid = re.search(
        rf"<EntityId>{BUILD_RIG}</EntityId>.*?</CubeBlocks>", sector, re.S
    )[0]
    saved = {cell: shares(grid).get(cell) for cell in PLACED}
    print("saved shares:", saved)
    assert all(share == expected for share in saved.values()), saved
    print("PASS", "own faction" if own else "OCT" if oct_on else "no OCT")


if __name__ == "__main__":
    main()
