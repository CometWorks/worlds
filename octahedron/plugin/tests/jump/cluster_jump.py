"""SE-780: the jump limit and the world edge on a cluster. The nodes have no Octahedron config,
so the plugin runs at its defaults, as on a Quasar cluster. The client's pilot flies a blind
jump of 2000 km and a jump to a GPS 2600 km away, both from inside the sphere: both end at the
450 km sphere. A grid spawned at 520 km and the character sent to 530 km end up just inside 500 km.

Runs from a cluster checkout's Test folder, with CLUSTER_EXTRA_PLUGINS=node:<package.sh output>/Octahedron:
    PYTHONPATH=$PWD:<remote>/skills/se-remote uv run python <this file> <label>
"""

import json
import math
import os
import sys
import tempfile
import time
import urllib.parse
from pathlib import Path

from bench import run_scenario
from cluster import wait_until
from scenarios_core import CLIENT_ID
from se_autopilot import enter_cockpit
from se_remote import CallOp, OpError

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
OUT = (
    Path(os.environ.get("JUMP_OUT", str(Path.home() / "cluster-test-results/se780")))
    / f"jump-{LABEL}"
)
OUT.mkdir(parents=True, exist_ok=True)
GPS = (100_000, -300_000, 2_600_000)


def block(subtype: str, kind: str, z: int, extra: str = "") -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{kind}"><SubtypeName>{subtype}</SubtypeName>'
        f'<Min x="0" y="0" z="{z}" />{extra}</MyObjectBuilder_CubeBlock>'
    )


BLUEPRINT = (
    '<?xml version="1.0"?><Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"'
    ' xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints><ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
    '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="SE780" /><CubeGrids><CubeGrid>'
    "<PersistentFlags>InScene</PersistentFlags>"
    '<PositionAndOrientation><Position x="0" y="0" z="0" /><Forward x="0" y="0" z="1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
    "<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>"
    + block(
        "LargeBlockCockpit",
        "Cockpit",
        0,
        '<Toolbar><Slots><Slot><Index>0</Index><Item /><Data xsi:type="MyObjectBuilder_ToolbarItemTerminalBlock">'
        "<BlockEntityId>780003</BlockEntityId><Action>Jump</Action></Data></Slot></Slots></Toolbar>",
    )
    + block(
        "LargeBlockBatteryBlock",
        "BatteryBlock",
        1,
        "<CurrentStoredPower>3</CurrentStoredPower>",
    )
    + block(
        "LargeJumpDrive",
        "JumpDrive",
        2,
        "<EntityId>780003</EntityId><StoredPower>3</StoredPower><JumpRatio>1</JumpRatio><Recharging>false</Recharging>",
    )
    + "</CubeBlocks><IsStatic>false</IsStatic></CubeGrid></CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>"
)


def km(position) -> float:
    return math.dist(position, (0, 0, 0)) / 1000


def chat(api) -> list[str]:
    reply = api.get_chat_messages(after=0)
    return [m.get("Text", "") for m in reply.get("messages", [])] + [
        h.get("text", "") for h in reply.get("hud", [])
    ]


def pick_gps(api) -> None:
    api.add_gps("Far target", *GPS)
    api.key("K")
    api.wait_for_screen(has_focus=True, max_wait=20)
    time.sleep(1)
    api.control_set("TerminalTabs", 1)
    time.sleep(1)
    # Battery, Control Station, Jump Drive
    api.control_set("FunctionalBlockListbox", 2)
    time.sleep(2)
    api.control_set("GpsList", 0)
    time.sleep(1)
    api.control_click(name="SelectBtn")
    time.sleep(1)
    api.key("Escape")
    time.sleep(1)


def _missing(api, grid: int) -> bool:
    try:
        api.list_blocks(grid, limit=1)
        return False
    except OpError:
        return True


def fly(bench, api, path: str, name: str, start, gps: bool) -> None:
    spawn = bench.cluster.node(
        "node-1",
        f"/blueprint-test-spawn?path={urllib.parse.quote(path)}&x={start[0]}&y={start[1]}&z={start[2]}&name={name}",
        method="POST",
        timeout=60,
    )
    bench.record(f"spawn-{name}", spawn)
    grid = spawn["grids"][0]
    if api.get_character().get("state") == "sitting":
        api.character_use()
        time.sleep(3)
    # A far grid reaches the client only once the character is near it
    api.character_teleport(start[0], start[1] + 30, start[2])
    wait_until(lambda: not _missing(api, grid), 90, f"{name} on the client")
    enter_cockpit(api, grid)
    if gps:
        pick_gps(api)
    before = set(chat(api))
    bench.ensure_gameplay_focus(CLIENT_ID)
    api.key("D1")
    api.wait_for_screen("MyGuiScreenMessageBox", max_wait=20)
    api.control_click(text="Yes")
    time.sleep(25)
    lines = [line for line in chat(api) if line not in before]
    distance = km(api.get_character()["position"])
    bench.record(f"result-{name}", {"km": distance, "chat": lines})
    bench.check(
        f"{name} ends at the 450 km sphere",
        abs(distance - 450) < 0.5,
        f"{distance:.3f} km",
    )
    bench.check(
        f"{name}: the player is told",
        any("Jump shortened" in line for line in lines),
        json.dumps(lines),
    )


def check(bench) -> None:
    bench.start_cluster(nodes=["wa-1", "node-1", "node-2"])
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)
    path = Path(tempfile.mkdtemp()) / "bp.sbc"
    path.write_text(BLUEPRINT, encoding="utf-8")

    fly(bench, api, str(path), "Blind", (0, -300_000, 200_000), False)
    fly(bench, api, str(path), "Gps", (100_000, -300_000, 0), True)

    api.character_use()
    time.sleep(3)
    outside = bench.cluster.node(
        "node-1",
        f"/blueprint-test-spawn?path={urllib.parse.quote(str(path))}&x=-312000&y=0&z=416000&name=Outside",
        method="POST",
        timeout=60,
    )
    grid = outside["grids"][0]
    time.sleep(10)
    api.character_teleport(-310_000, 0, 412_000)
    time.sleep(10)
    world = api.batch(calls=[CallOp.grid_to_world(grid, (0, 0, 0))]).call(0)["world"]
    bench.check(
        "a grid at 520 km is moved back inside 500 km",
        499 < km(world) <= 500,
        f"{km(world):.3f} km",
    )
    api.character_teleport(0, -530_000, 0)
    time.sleep(10)
    distance = km(api.get_character()["position"])
    bench.check(
        "the character at 530 km is moved back inside 500 km",
        499 < distance <= 500.01,
        f"{distance:.3f} km",
    )


result = run_scenario(
    check,
    "T780-jump",
    "jump limit and world edge on cluster nodes at plugin defaults",
    "3",
    OUT,
)
print("PASSED" if result.passed else f"FAILED {result.error}")
sys.exit(0 if result.passed else 1)
