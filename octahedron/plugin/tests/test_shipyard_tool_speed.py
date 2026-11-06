"""Checks the station shipyards' faster OmniArc tools (SE-1180) on the test server, without a
client: rigs far from the stations, each with an OmniArc beam welder and deconstructor and
batteries, the welder on a row of damaged heavy armor blocks, carrying the components they
lost, the deconstructor on a row of whole ones. Each tool works only on its own row, by its work colour, so neither touches its
own grid nor another rig's. The server runs for a while, stops (it saves), and the saved
world says how much of each row was welded or ground. The rigs:

- oct: a shipyard as build_world.py makes one, "<name> Shipyard", OCT's, static, neither
  editable nor destructible. Its welder must weld ShipyardToolSpeed (2) times as fast as the
  player's, and its deconstructor take apart about as much as its welder builds.
- turn: the same, with the tools the player's, as in a player's turn at a shipyard: as fast.
- player: a player's grid with the same tools: the default speed.
- fake: a player's grid named "... Shipyard" with a block of OCT's, as if merged from an OCT
  grid: the default speed.
- locked: a player's grid made neither editable nor destructible, as an admin can: the default
  speed.

The world's welder and grinder speeds are lowered, so no row is done within the run.

    uv run --no-project --with httpx python tests/test_shipyard_tool_speed.py

Needs the Workshop copies of OmniArc and ToolCore, as tests/test_projection_limits.py says.
Prints a table and one line per check; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import base64
import re
import struct
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1180")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402

OCT_NAME = "Octahedron Authority"
PLAYER = 144115188075999180
# The world's multipliers (3 on the Octahedron), so the rows outlast the run
SPEED = 0.02
SECONDS = 60
BLOCKS = 16
DAMAGED = 0.02
WELD_COLOUR = (0.5, 0.5, 0.5)
GRIND_COLOUR = (0.25, 0.5, 0.5)
# ToolCore's tool modes
WELD, GRIND = 8, 16
# name: (grid name, tools' owner, OCT owns the batteries, locked, fast)
RIGS = {
    "oct": ("Speed Test Shipyard", "oct", True, True, True),
    "turn": ("Speed Turn Shipyard", "player", True, True, True),
    "player": ("Speed Test Ship", "player", False, False, False),
    "fake": ("Speed Fake Shipyard", "player", "one", False, False),
    "locked": ("Speed Locked Shipyard", "player", False, True, False),
}
EXPECTED = 2.0

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    print(f"{'PASS' if ok else 'FAIL'}: {what}", flush=True)


def oct_npc() -> int:
    """OCT's founder, the NPC that owns the stations and their shipyards"""
    text = (rig.WORLD_SOURCE / "Sandbox.sbc").read_text(encoding="utf-8")
    faction = re.search(r"<Tag>OCT</Tag>.*?</Members>", text, re.S)[0]
    npc = int(re.search(r"<PlayerId>(\d+)</PlayerId>", faction)[1])
    name = re.search(rf"<IdentityId>{npc}</IdentityId>\s*<DisplayName>([^<]*)<", text)
    assert name and name[1] == OCT_NAME, name
    return npc


def tool_storage(mode: int, colour) -> str:
    """ToolCore's ToolRepo (protobuf): activated, the mode, all targets, the work colour on
    and set, tracking targets"""

    def f(field: int, value: float) -> bytes:
        return bytes([field << 3 | 5]) + struct.pack("<f", value)

    colour_bytes = f(1, colour[0]) + f(4, colour[1]) + f(7, colour[2])
    repo = (
        bytes([0x08, 1, 0x10, 0, 0x18, mode, 0x20, 0, 0x28, 15, 0x30, 1, 0x3A, len(colour_bytes)])
        + colour_bytes
        + bytes([0x40, 1])
    )  # fmt: skip

    return (
        "<ComponentData><TypeId>MyModStorageComponent</TypeId>"
        '<Component xsi:type="MyObjectBuilder_ModStorageComponent"><Storage><dictionary><item>'
        "<Key>75bbb4f5-4fb9-4230-aaaa-bb79c9811618</Key>"
        f"<Value>{base64.b64encode(repo).decode()}</Value>"
        "</item></dictionary></Storage></Component></ComponentData>"
    )


def components() -> str:
    """The welder's inventory: a damaged block needs new components for what it lost"""
    items = "".join(
        f"<MyObjectBuilder_InventoryItem><Amount>{amount}</Amount>"
        '<PhysicalContent xsi:type="MyObjectBuilder_Component">'
        f"<SubtypeName>{name}</SubtypeName></PhysicalContent><ItemId>{i}</ItemId>"
        "</MyObjectBuilder_InventoryItem>"
        for i, (name, amount) in enumerate((("SteelPlate", 800), ("MetalGrid", 300)))
    )
    return (
        "<ComponentData><TypeId>MyInventoryBase</TypeId>"
        '<Component xsi:type="MyObjectBuilder_Inventory"><CanPutItems>true</CanPutItems>'
        f"<Items>{items}</Items><nextItemId>2</nextItemId><Volume>32</Volume>"
        "<InventoryFlags>CanSend</InventoryFlags><RemoveEntityOnEmpty>false</RemoveEntityOnEmpty>"
        "</Component></ComponentData>"
    )


def owned(owner: int) -> str:
    return f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy><ShareMode>Faction</ShareMode>"


def at(index: int) -> tuple[float, float, float]:
    return (250_000.0 + index * 1000, 0.0, 5_000.0)


def rig_id(index: int, part: int) -> int:
    return 777001180000000 + index * 10 + part


def tools_grid(index: int, name: str, tools: int, batteries, locked: bool) -> str:
    """The welder at the origin, the deconstructor next to it, the batteries behind"""
    blocks = [
        rig.block("MyObjectBuilder_ConveyorSorter", "OmniArcBeam", (0, 0, 0),
                  owned(tools) + "<ComponentContainer><Components>"
                  + tool_storage(WELD, WELD_COLOUR) + components() + "</Components></ComponentContainer><Enabled>true</Enabled>"),
        rig.block("MyObjectBuilder_ConveyorSorter", "OmniArcDestro", (0, 0, -1),
                  owned(tools) + "<ComponentContainer><Components>"
                  + tool_storage(GRIND, GRIND_COLOUR) + "</Components></ComponentContainer><Enabled>true</Enabled>"),
    ] + [
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, z),
                  owned(batteries[z - 1]) + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>")
        for z in range(1, 9)
    ]  # fmt: skip
    text = rig.grid(name, rig_id(index, 0), blocks, at(index)).replace(
        "<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>"
    )
    if locked:
        text = text.replace(
            "<DestructibleBlocks>true</DestructibleBlocks>",
            "<DestructibleBlocks>false</DestructibleBlocks><Editable>false</Editable>",
        )
    return text


def row(index: int, part: int, colour, damaged: bool) -> str:
    """BLOCKS heavy armor blocks in a line with the tool, from 10 m away, built by the player
    (a turn's tools work only on the player's blocks, ShipyardOwnBlocks)"""
    x, y, z = at(index)
    if damaged:
        origin = (x + 10, y, z)
    else:
        origin = (x - 10 - 2.5 * (BLOCKS - 1), y, z - 2.5)
    state = (
        f"<IntegrityPercent>{DAMAGED}</IntegrityPercent><BuildPercent>1</BuildPercent>"
        if damaged
        else ""
    )
    blocks = [
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (k, 0, 0),
                  f"<BuiltBy>{PLAYER}</BuiltBy>" + state
                  + f'<ColorMaskHSV x="{colour[0]}" y="{colour[1]}" z="{colour[2]}" />')
        for k in range(BLOCKS)
    ]  # fmt: skip
    return rig.grid(
        f"Speed {'Weld' if damaged else 'Grind'} Row {index}",
        rig_id(index, part),
        blocks,
        origin,
    ).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")


def grids(npc: int) -> list[str]:
    out = []
    for index, (name, tools, oct_batteries, locked, _) in enumerate(RIGS.values()):
        batteries = [
            (
                npc
                if oct_batteries is True or (oct_batteries == "one" and z == 1)
                else PLAYER
            )
            for z in range(1, 9)
        ]
        out.append(
            tools_grid(
                index, name, npc if tools == "oct" else PLAYER, batteries, locked
            )
        )
        out.append(row(index, 1, WELD_COLOUR, True))
        out.append(row(index, 2, GRIND_COLOUR, False))
    return out


def prepare(npc: int) -> None:
    rig.prepare_world(
        grids(npc),
        settings={"WelderSpeedMultiplier": SPEED, "GrinderSpeedMultiplier": SPEED},
    )
    identity = (
        f"<MyObjectBuilder_Identity><IdentityId>{PLAYER}</IdentityId>"
        "<DisplayName>Speed Tester</DisplayName><CharacterEntityId>0</CharacterEntityId>"
        "</MyObjectBuilder_Identity>"
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        text = text.replace("<Mods />", f"<Mods>{pl.MOD_ITEMS}</Mods>", 1)
        if name == "Sandbox.sbc":
            text = text.replace("</Identities>", identity + "</Identities>", 1)
        path.write_text(text, encoding="utf-8")
    mods = rig.SERVER_DATA / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = mods / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    rig.write_plugin_config(rig.CONFIG_SOURCE.read_text(encoding="utf-8"))
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS])


def integrity(saved: str, entity_id: int) -> float:
    """The row's integrity in blocks: each block's IntegrityPercent, 0 for a block ground away"""
    match = re.search(
        rf"<EntityId>{entity_id}</EntityId>.*?</MyObjectBuilder_EntityBase>",
        saved,
        re.S,
    )
    if not match:
        return 0.0
    total = 0.0
    for block in re.findall(
        r"<SubtypeName>LargeHeavyBlockArmorBlock</SubtypeName>.*?</MyObjectBuilder_CubeBlock>",
        match[0],
        re.S,
    ):
        percent = re.search(r"<IntegrityPercent>([^<]+)</IntegrityPercent>", block)
        total += float(percent[1]) if percent else 1.0
    return total


def ratio(a: float, b: float) -> float:
    return a / b if b else float("nan")


def main() -> int:
    npc = oct_npc()
    rig.stop_server()
    prepare(npc)
    started = time.monotonic()
    rig.start_server()
    print(f"server ready in {time.monotonic() - started:.0f} s", flush=True)
    time.sleep(SECONDS)
    rig.stop_server()

    game_log = rig.server_game_log()
    check(
        "Shipyard tools: the station shipyards' welders and deconstructors are faster"
        in game_log,
        "the plugin patched ToolCore",
    )
    for line in game_log.splitlines():
        if "Shipyard tools" in line or ("Octahedron" in line and "xception" in line):
            print("server: " + line.strip())

    saved = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    weld, grind = {}, {}
    for index, key in enumerate(RIGS):
        weld[key] = integrity(saved, rig_id(index, 1)) - BLOCKS * DAMAGED
        grind[key] = BLOCKS - integrity(saved, rig_id(index, 2))
    print(
        f"{SECONDS} s, world welder and grinder speed {SPEED}, blocks welded / ground:"
    )
    print("rig     | welded  x player | ground  x player | ground / welded")
    for key in RIGS:
        print(
            f"{key:7} | {weld[key]:6.3f} {ratio(weld[key], weld['player']):5.2f}x  | "
            f"{grind[key]:6.3f} {ratio(grind[key], grind['player']):5.2f}x  | "
            f"{ratio(grind[key], weld[key]):5.2f}"
        )

    check(weld["player"] > 0.1 and grind["player"] > 0.01, "the player's tools work")
    for key, (*_, fast) in RIGS.items():
        if key == "player":
            continue
        welded = ratio(weld[key], weld["player"])
        if fast:
            check(
                abs(welded / EXPECTED - 1) < 0.15,
                f"{key}: the welder welds {welded:.2f}x the player's, expected {EXPECTED}x",
            )
            matched = ratio(grind[key], weld[key])
            check(
                abs(matched - 1) < 0.25,
                f"{key}: the deconstructor takes apart {matched:.2f}x what the welder builds, expected about 1x",
            )
        else:
            grind_ratio = ratio(grind[key], grind["player"])
            check(
                abs(welded - 1) < 0.15 and abs(grind_ratio - 1) < 0.25,
                f"{key}: the welder welds {welded:.2f}x, the deconstructor grinds {grind_ratio:.2f}x "
                "the player's, expected 1x",
            )

    failed = [what for ok, what in checks if not ok]
    print(f"{len(checks) - len(failed)}/{len(checks)} checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_server()
