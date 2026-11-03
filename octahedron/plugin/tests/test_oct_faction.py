"""Checks the OCT faction rules on the test server with the headless client, in a world built by
build_world.py (OCT_WORLD_SOURCE): a new player joins OCT, and blocks they place by hand get
No share, on the client as on the server. With --no-oct the rules are off: the player stays
without a faction and the game's faction share stays.

    OCT_WORLD_SOURCE=<built world> uv run --no-project --with httpx python \\
        tests/test_oct_faction.py [--no-oct]
"""

from __future__ import annotations

import re
import sys
import time

import rig

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
    rig.write_plugin_config(config)
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client()

    tag = remote.get_faction()["tag"]
    print("faction:", tag)
    assert tag == ("OCT" if oct_on else None), f"faction {tag}"

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

    expected = "None" if oct_on else "Faction"
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

    rig.stop_client()
    rig.stop_server()  # saves
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    grid = re.search(
        rf"<EntityId>{BUILD_RIG}</EntityId>.*?</CubeBlocks>", sector, re.S
    )[0]
    saved = {cell: shares(grid).get(cell) for cell in PLACED}
    print("saved shares:", saved)
    assert all(share == expected for share in saved.values()), saved
    print("PASS", "OCT" if oct_on else "no OCT")


if __name__ == "__main__":
    main()
