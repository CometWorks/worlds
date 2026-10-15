"""Checks the PCU cap per mechanical grid group on the test server, without a client.
GroupPcuLimit is set to 300 and each rig is a pair: one whose blocks a player built,
which the cap stops, and a copy built by nobody, which it doesn't, to show the game
would have done it.

- Weld: a ship welder in front of three gyros left just below their functional level.
  Each one welded past it adds 49 PCU to the welder's grid (168 PCU): two fit under
  300, the third doesn't.
- Merge: two grids of 215 PCU with merge blocks face to face. Merged they would have
  430.
- Attach: a programmable block asks a rotor base on its grid (215 PCU) to attach the
  free rotor head in front of it, on a grid of 101 PCU. Joined they would have 316.

The server runs a while, stops (it saves) and the saved world is read.

    uv run --no-project --with httpx python tests/test_group_pcu.py
"""

from __future__ import annotations

import os
import re
import time

os.environ.setdefault("OCT_TASK", "SE1-0150")
import rig  # noqa: E402

PLAYER = 144115188075999150
LIMIT = 300
RUN_SECONDS = 180
# The gyro is functional above 84.147 % integrity (its first computer); left just below,
# so a motor and a computer finish it
GYRO_BELOW_FUNCTIONAL = 0.841

IDENTITY = (
    "<MyObjectBuilder_Identity>"
    f"<IdentityId>{PLAYER}</IdentityId><DisplayName>Group Tester</DisplayName>"
    "<CharacterEntityId>0</CharacterEntityId></MyObjectBuilder_Identity>"
)
ITEMS = ("Motor", "Computer", "MetalGrid", "LargeTube", "SteelPlate", "Construction")
INVENTORY = (
    "<ComponentContainer><Components><ComponentData><TypeId>MyInventoryBase</TypeId>"
    '<Component xsi:type="MyObjectBuilder_Inventory"><CanPutItems>true</CanPutItems><Items>'
    + "".join(
        f"<MyObjectBuilder_InventoryItem><Amount>20</Amount>"
        f'<PhysicalContent xsi:type="MyObjectBuilder_Component"><SubtypeName>{item}'
        f"</SubtypeName></PhysicalContent><ItemId>{i}</ItemId></MyObjectBuilder_InventoryItem>"
        for i, item in enumerate(ITEMS)
    )
    + f"</Items><nextItemId>{len(ITEMS)}</nextItemId>"
    "<InventoryFlags>CanSend</InventoryFlags><RemoveEntityOnEmpty>false</RemoveEntityOnEmpty>"
    "</Component></ComponentData></Components></ComponentContainer>"
)


def owned(built_by: int) -> str:
    return f"<Owner>{PLAYER}</Owner><BuiltBy>{built_by}</BuiltBy><ShareMode>Faction</ShareMode>"


def battery(pos, built_by=PLAYER) -> str:
    return rig.block(
        "MyObjectBuilder_BatteryBlock",
        "LargeBlockBatteryBlock",
        pos,
        owned(built_by)
        + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>",
    )


def gyro(pos, built_by=PLAYER, build=None) -> str:
    stage = (
        f"<IntegrityPercent>{build}</IntegrityPercent><BuildPercent>{build}</BuildPercent>"
        if build
        else ""
    )
    return rig.block(
        "MyObjectBuilder_Gyro", "LargeBlockGyro", pos, owned(built_by) + stage
    )


def weld_rig(name: str, entity_id: int, position, built_by: int) -> str:
    """A ship welder (150 PCU), a battery (15) and three gyro construction stages (1 each)
    in front of the welder"""
    return rig.grid(
        name,
        entity_id,
        [
            rig.block(
                "MyObjectBuilder_ShipWelder",
                "LargeShipWelder",
                (0, 0, 0),
                owned(PLAYER) + "<Enabled>true</Enabled>" + INVENTORY,
            ),
            battery((0, 1, 0)),
        ]
        + [
            gyro(pos, built_by, GYRO_BELOW_FUNCTIONAL)
            for pos in ((0, 0, -1), (1, 0, -1), (-1, 0, -1))
        ],
        position=position,
    )


def merge_rig(name: str, entity_id: int, position, built_by: int, side: int) -> str:
    """A merge block (100 PCU) whose face, its local +X, points along side * world X, a
    battery behind it and two gyros: 215 PCU"""
    return rig.grid(
        name,
        entity_id,
        [
            rig.block(
                "MyObjectBuilder_MergeBlock",
                "LargeShipMergeBlock",
                (0, 0, 0),
                owned(built_by) + "<Enabled>true</Enabled>",
                forward="Forward" if side > 0 else "Backward",
            ),
            battery((-side, 0, 0)),
            gyro((0, 1, 0)),
            gyro((0, -1, 0)),
        ],
        position=position,
    )


def merge_pair(name: str, entity_id: int, y: float, built_by: int) -> list[str]:
    return [
        merge_rig(f"{name} A", entity_id, (250_000, y, 0), built_by, 1),
        merge_rig(f"{name} B", entity_id + 1, (250_002.5, y, 0), built_by, -1),
    ]


# Where the game puts a large rotor head's grid: above the base's cell, in the base
# grid's orientation (read from a save)
HEAD_OFFSET = 0.421


# The game attaches a rotor's top only when asked, and an attach a save asks for is
# dropped on load
ATTACH_SCRIPT = (
    "public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update100; }\n"
    "public void Main(string argument, UpdateType updateSource) {\n"
    '    (GridTerminalSystem.GetBlockWithName("Cap Rotor") as IMyMotorStator)?.Attach();\n'
    "}"
)


def attach_pair(name: str, entity_id: int, y: float, built_by: int) -> list[str]:
    """A rotor base with a battery and a programmable block that keeps asking it to
    attach, and a detached head grid with two gyros on top"""
    base = rig.grid(
        f"{name} Base",
        entity_id,
        [
            rig.block(
                "MyObjectBuilder_MotorStator",
                "LargeStator",
                (0, 0, 0),
                owned(built_by)
                + "<CustomName>Cap Rotor</CustomName><Enabled>true</Enabled>",
            ),
            battery((0, -1, 0)),
            rig.block(
                "MyObjectBuilder_MyProgrammableBlock",
                "LargeProgrammableBlock",
                (0, -2, 0),
                owned(PLAYER)
                + f"<Enabled>true</Enabled><Program>{ATTACH_SCRIPT}</Program>",
            ),
        ],
        position=(250_000, y, 0),
    )
    head = rig.grid(
        f"{name} Head",
        entity_id + 1,
        [
            rig.block(
                "MyObjectBuilder_MotorRotor", "LargeRotor", (0, 0, 0), owned(PLAYER)
            ),
            gyro((0, 1, 0)),
            gyro((0, 2, 0)),
        ],
        position=(250_000, y + HEAD_OFFSET, 0),
    )
    return [base, head]


GRIDS = [
    weld_rig("Weld Capped", 777000150000100, (250_000, 3_000, 0), PLAYER),
    weld_rig("Weld Free", 777000150000200, (250_000, 6_000, 0), 0),
    *merge_pair("Merge Capped", 777000150000300, 9_000, PLAYER),
    *merge_pair("Merge Free", 777000150000400, 12_000, 0),
    *attach_pair("Attach Capped", 777000150000500, 15_000, PLAYER),
    *attach_pair("Attach Free", 777000150000600, 18_000, 0),
]


def prepare() -> None:
    rig.prepare_world(GRIDS)
    path = rig.WORLD / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    path.write_text(
        text.replace("</Identities>", IDENTITY + "</Identities>", 1), "utf-8"
    )
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8").replace(
        "</OctahedronConfig>",
        f"  <GroupPcuLimit>{LIMIT}</GroupPcuLimit>\n</OctahedronConfig>",
    )
    rig.write_plugin_config(config)
    rig.prepare_server()


def saved_grids() -> dict[str, list[str]]:
    """Grid name -> the saved grids of that name"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    grids: dict[str, list[str]] = {}
    for entity in re.findall(
        r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">.*?'
        r"</MyObjectBuilder_EntityBase>",
        text,
        re.S,
    ):
        name = re.search(r"<DisplayName>(.*?)</DisplayName>", entity)
        if name:
            grids.setdefault(name[1], []).append(entity)
    return grids


def functional_gyros(grid: str) -> int:
    """Gyros past their functional level; a complete block saves no BuildPercent"""
    count = 0
    for block in re.findall(
        r"<SubtypeName>LargeBlockGyro</SubtypeName>.*?</MyObjectBuilder_CubeBlock>",
        grid,
        re.S,
    ):
        build = re.search(r"<BuildPercent>([\d.]+)</BuildPercent>", block)
        count += build is None or float(build[1]) > 0.8415
    return count


def top_block(grid: str) -> int:
    """The rotor base's TopBlockId, 0 without a top"""
    top = re.search(r"<TopBlockId>(\d+)</TopBlockId>", grid)
    return int(top[1]) if top else 0


def main() -> None:
    rig.stop_server()
    prepare()
    started = time.monotonic()
    rig.start_server()
    print(
        f"server ready in {time.monotonic() - started:.0f} s, running {RUN_SECONDS} s"
    )
    time.sleep(RUN_SECONDS)
    rig.stop_server()

    grids = saved_grids()
    for name in ("Weld Capped", "Weld Free"):
        print(f"{name}: functional gyros {functional_gyros(grids[name][0])}")
    for name in ("Merge Capped", "Merge Free"):
        print(f"{name}: grids {sorted(n for n in grids if n.startswith(name))}")
    for name in ("Attach Capped", "Attach Free"):
        print(f"{name}: top block {top_block(grids[name + ' Base'][0])}")
    refused = rig.server_game_log().count("may have at most 300 PCU")
    print(f"refusals logged {refused}")

    assert functional_gyros(grids["Weld Free"][0]) == 3, "the welder welds all three"
    assert functional_gyros(grids["Weld Capped"][0]) == 2
    assert len([n for n in grids if n.startswith("Merge Free")]) == 1, "merged"
    assert sorted(n for n in grids if n.startswith("Merge Capped")) == [
        "Merge Capped A",
        "Merge Capped B",
    ]
    assert top_block(grids["Attach Free Base"][0]), "attached"
    assert not top_block(grids["Attach Capped Base"][0])
    assert refused >= 3
    print("passed")


if __name__ == "__main__":
    try:
        main()
    finally:
        rig.stop_server()
