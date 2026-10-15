"""Checks the projection limits on the test server, without a client: an activated
OmniArc beam welder (ToolCore) next to a projector whose blueprint has six light armor
blocks, owned by a player whose world limit is two light armor blocks. ToolCore welds
projections without checking limits; the plugin must stop it at two, and give back the
steel plate ToolCore took for each refused block.

Each case runs the server for a while, stops it (it saves) and counts the blocks in the
saved world. The cases: the plugin's check off (ToolCore welds all six), on with
Multigrid Projector (as on the Octahedron servers) and on without it. The group case
leaves the check off and caps the rig's PCU (GroupPcuLimit) two light armor blocks
above what it has, so the cap per mechanical grid group stops the welder at two.

    uv run --no-project --with httpx python tests/test_projection_limits.py [case ...]

Needs the Workshop copies of OmniArc and ToolCore, served from the server's Mods folder
by the local-world-mods test plugin (se/se1/notes/trade-lanes-mp), and a Multigrid
Projector working copy (OCT_MGP_REPO).
"""

from __future__ import annotations

import base64
import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE1-0123")
import rig  # noqa: E402

WORKSHOP = Path(
    os.environ.get(
        "OCT_WORKSHOP", rig.HOME / ".steam/steam/steamapps/workshop/content/244850"
    )
)
MODS = {3040438530: "ToolCore", 3412657089: "OmniCorp The OmniArc Project: Adv Tools"}
LOCAL_WORLD_MODS = (
    "local-world-mods",
    rig.WORKSPACE / "se1/notes/trade-lanes-mp/local-world-mods",
    "LocalWorldMods.xml",
    "local-world-mods",
)
MGP = (
    "multigrid-projector",
    Path(
        os.environ.get(
            "OCT_MGP_REPO", rig.WORKSPACE / "se1/plugins/se-multigrid-projector"
        )
    ),
    "MultigridProjectorServer.xml",
    "B91FD7B3-E1AC-4B57-8765-C677A7AC1F5F",
)
# The rig's PCU: projector 50, heavy armor 1, OmniArc beam 250, five batteries 15 each
RIG_PCU = 376
CASES = {
    # name: (check on, Multigrid Projector, GroupPcuLimit)
    "off": (False, True, 0),
    "mgp": (True, True, 0),
    "vanilla": (True, False, 0),
    "group": (False, True, RIG_PCU + 2),
}

PLAYER = 144115188075999123
GRID_ID = 777000123000100
LIMIT = 2
PLATES = 1000
PLATES_PER_BLOCK = 25
RUN_SECONDS = 150

# ToolCore's ToolRepo in the block's mod storage: the shipyard welders' value with
# Activated and TrackTargets set
TOOL_REPO = bytearray(base64.b64decode("CAAQABgIIAAoDzAAOg8NAAAAACUAAAAAPQAAAABAAA=="))
TOOL_REPO[1] = TOOL_REPO[-1] = 1
TOOL_STORAGE = (
    "<ComponentData><TypeId>MyModStorageComponent</TypeId>"
    '<Component xsi:type="MyObjectBuilder_ModStorageComponent"><Storage><dictionary><item>'
    "<Key>75bbb4f5-4fb9-4230-aaaa-bb79c9811618</Key>"
    f"<Value>{base64.b64encode(TOOL_REPO).decode()}</Value>"
    "</item></dictionary></Storage></Component></ComponentData>"
)
INVENTORY = (
    "<ComponentData><TypeId>MyInventoryBase</TypeId>"
    '<Component xsi:type="MyObjectBuilder_Inventory"><CanPutItems>true</CanPutItems><Items>'
    f"<MyObjectBuilder_InventoryItem><Amount>{PLATES}</Amount>"
    '<PhysicalContent xsi:type="MyObjectBuilder_Component"><SubtypeName>SteelPlate</SubtypeName>'
    "</PhysicalContent><ItemId>0</ItemId></MyObjectBuilder_InventoryItem>"
    "</Items><nextItemId>1</nextItemId><Volume>32</Volume>"
    "<InventoryFlags>CanSend</InventoryFlags><RemoveEntityOnEmpty>false</RemoveEntityOnEmpty>"
    "</Component></ComponentData>"
)
OWNED = (
    f"<Owner>{PLAYER}</Owner><BuiltBy>{PLAYER}</BuiltBy><ShareMode>Faction</ShareMode>"
)

# The projection's first block lands on the projector, so the blueprint starts with one
# at the same place: the blueprint's coordinates are the grid's
BLUEPRINT = rig.grid(
    "Projection Limit Blueprint",
    None,
    [
        rig.block("MyObjectBuilder_Projector", "LargeProjector", (0, 0, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (1, 0, 0)),
    ]
    + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, 0))
        for x in range(2, 8)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
RIG = rig.grid(
    "Projection Limit Rig",
    GRID_ID,
    [
        rig.block(
            "MyObjectBuilder_Projector",
            "LargeProjector",
            (0, 0, 0),
            OWNED + "<Enabled>true</Enabled>"
            f"<ProjectedGrids>{BLUEPRINT}</ProjectedGrids>"
            "<KeepProjection>true</KeepProjection>",
        ),
        rig.block(
            "MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (1, 0, 0), OWNED
        ),
        rig.block(
            "MyObjectBuilder_ConveyorSorter",
            "OmniArcBeam",
            (0, 1, 0),
            OWNED
            + f"<ComponentContainer><Components>{INVENTORY}{TOOL_STORAGE}</Components>"
            "</ComponentContainer><Enabled>true</Enabled>",
        ),
    ]
    + [
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (-x, 0, 0),
            OWNED + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>",
        )
        for x in range(1, 6)
    ],
    position=(250_000, 0, 0),
)
IDENTITY = (
    "<MyObjectBuilder_Identity>"
    f"<IdentityId>{PLAYER}</IdentityId><DisplayName>Projection Tester</DisplayName>"
    "<CharacterEntityId>0</CharacterEntityId></MyObjectBuilder_Identity>"
)
MOD_ITEMS = "".join(
    f'<ModItem FriendlyName="{name}"><Name>{id_}.sbm</Name><PublishedFileId>{id_}'
    "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    for id_, name in MODS.items()
)
TYPE_LIMITS = (
    "<BlockTypeLimits><dictionary><item><Key>LightArmorBlock</Key>"
    f"<Value>{LIMIT}</Value></item></dictionary></BlockTypeLimits>"
)


def prepare(check: bool, mgp: bool, group: int) -> None:
    rig.prepare_world([RIG])
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        text = text.replace("<Mods />", f"<Mods>{MOD_ITEMS}</Mods>", 1)
        text, count = re.subn(
            r"<BlockTypeLimits>.*?</BlockTypeLimits>", TYPE_LIMITS, text, flags=re.S
        )
        assert count == 1, name
        if name == "Sandbox.sbc":
            text = text.replace("</Identities>", IDENTITY + "</Identities>", 1)
        path.write_text(text, encoding="utf-8")

    mods = rig.SERVER_DATA / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    for id_ in MODS:
        link = mods / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(WORKSHOP / str(id_))

    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    config, count = re.subn(
        r"<ProjectionLimitsEnabled>\w+</ProjectionLimitsEnabled>",
        f"<ProjectionLimitsEnabled>{str(check).lower()}</ProjectionLimitsEnabled>",
        config,
    )
    assert count == 1
    config = config.replace(
        "</OctahedronConfig>",
        f"  <GroupPcuLimit>{group}</GroupPcuLimit>\n</OctahedronConfig>",
    )
    rig.write_plugin_config(config)
    rig.prepare_server(extra_dev_folders=[LOCAL_WORLD_MODS] + ([MGP] if mgp else []))


def saved_rig() -> str:
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    match = re.search(
        rf"<EntityId>{GRID_ID}</EntityId>.*?</MyObjectBuilder_EntityBase>", text, re.S
    )
    assert match, "the rig grid is not in the saved world"
    # Without the blueprint the projector keeps
    return re.sub(r"<ProjectedGrids>.*?</ProjectedGrids>", "", match[0], flags=re.S)


def run(case: str) -> None:
    check, mgp, group = CASES[case]
    print(
        f"--- {case}: check {'on' if check else 'off'}, Multigrid Projector {'on' if mgp else 'off'}, "
        f"GroupPcuLimit {group}"
    )
    rig.stop_server()
    prepare(check, mgp, group)
    started = time.monotonic()
    rig.start_server()
    print(
        f"server ready in {time.monotonic() - started:.0f} s, running {RUN_SECONDS} s"
    )
    time.sleep(RUN_SECONDS)
    rig.stop_server()

    grid = saved_rig()
    blocks = re.findall(
        r"<SubtypeName>LargeBlockArmorBlock</SubtypeName>.*?</MyObjectBuilder_CubeBlock>",
        grid,
        re.S,
    )
    built_by = sorted(
        {int(b) for b in re.findall(r"<BuiltBy>(\d+)</BuiltBy>", "".join(blocks))}
    )
    plates = re.search(
        r"<Amount>([\d.]+)</Amount>\s*<PhysicalContent[^>]*>\s*<SubtypeName>SteelPlate<",
        grid,
    )
    plates = float(plates[1]) if plates else 0.0
    log = rig.server_game_log()
    refused = log.count("not built: you reached the limit") + log.count(
        "may have at most"
    )
    print(
        f"light armor blocks: {len(blocks)}, built by {built_by}, steel plates left {plates:.0f}, "
        f"refusals logged {refused}"
    )
    if not check and not group:
        assert len(blocks) == 6, "ToolCore should weld the whole projection"
        return
    assert len(blocks) == LIMIT
    assert built_by == [PLAYER]
    assert refused >= 1
    # Only the welded blocks used plates, the refused ones got theirs back
    assert plates >= PLATES - LIMIT * PLATES_PER_BLOCK, plates


if __name__ == "__main__":
    try:
        for name in sys.argv[1:] or CASES:
            run(name)
    finally:
        rig.stop_server()
    print("passed")
