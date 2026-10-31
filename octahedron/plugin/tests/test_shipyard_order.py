"""Checks shipyard orders on the test server, with OmniArc, ToolCore and Multigrid
Projector as on the Octahedron servers, the zones restricted as in the world, and the
headless client.

- "complete": the client orders a 10 block ship ("Order Test") at its station's
  hologram table with !oct order (the table's button for an admin): the quote, then
  the order. The client reads a shipyard welder's ToolCore_Shoot switch. The order ends
  when the ship is complete; the saved world must hold it as a dynamic grid owned and
  built by the player, without the scaffold, and the shipyard without the loaded
  components and with its welders off.
- "expire": a 1,536 block heavy armor ship ("Order Big") with ShipyardOrderMinutes 2.
  The server restarts in the middle, the client gone, and must pick the order up from
  the world and end it once the 2 minutes are up (the restart alone may take that
  long). The saved world must then hold the unfinished ship, dynamic and the
  player's, with no scaffold and no leftover components.

    uv run --no-project --with httpx python tests/test_shipyard_order.py [case ...]

With OCT_SHOTS=<folder> the client renders (1280x720) and saves three screenshots
of the ship being welded to that folder, taken from among the shipyard's welders
(the client teleports there). Only "expire" is still welding by then.

Needs what tests/test_projection_limits.py needs.
"""

from __future__ import annotations

import base64
import math
import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE1-0151")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_zone as sz  # noqa: E402

# The control station mounts only on its bottom, so it sits on the front armor block
SMALL = rig.grid(
    "Order Test",
    777000151000100,
    [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 1)),
    ]
    + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
        for z in range(2, 9)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
BIG = rig.grid(
    "Order Big",
    777000151000200,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, y, z))
        for x in range(8)
        for y in range(8)
        for z in range(24)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
# name: (blueprint, its blocks, ShipyardOrderMinutes)
CASES = {"complete": (SMALL, 10, 60), "expire": (BIG, 1536, 2)}

SHOTS = os.environ.get("OCT_SHOTS")
started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


table_blueprint = ""


def prepare(blueprint: str, minutes: int, plugins=()) -> None:
    """plugins: more dev folders for the server, as prepare_server takes them"""
    global table_blueprint
    table_blueprint = blueprint
    rig.prepare_world(
        settings={"TrashRemovalEnabled": "false"}, table_blueprint=blueprint
    )
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text, count = re.subn(
        r"<AllowedActions>[^<]*</AllowedActions>",
        f"<AllowedActions>{sz.ZONE_ACTIONS}</AllowedActions>",
        sector.read_text(encoding="utf-8"),
    )
    assert count >= 5, count
    sector.write_text(text, encoding="utf-8")
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        path.write_text(
            path.read_text(encoding="utf-8").replace(
                "<Mods />", f"<Mods>{pl.MOD_ITEMS}</Mods>", 1
            ),
            encoding="utf-8",
        )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        for id_ in pl.MODS:
            link = folder / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(pl.WORKSHOP / str(id_))
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "ShipyardOrderMinutes" not in config
    rig.write_plugin_config(
        config.replace(
            "</OctahedronConfig>",
            f"<ShipyardOrderMinutes>{minutes}</ShipyardOrderMinutes>"
            # The tables keep the world's blueprint for the whole test
            "<ShipyardTableMinutes>600</ShipyardTableMinutes></OctahedronConfig>",
        )
    )
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, pl.MGP, *plugins])


def chat_lines(remote, after: int) -> tuple[list[str], int]:
    got = remote.get_chat_messages(after=after)
    lines = [m.get("Text", "") for m in got.get("messages", [])]
    lines += [h.get("text", "") for h in got.get("hud", [])]
    return lines, got.get("latest", after)


def wait_chat(remote, text: str, timeout: float) -> str:
    after = 0
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lines, after = chat_lines(remote, after)
        for line in lines:
            if text in line:
                return line
        time.sleep(10)
    raise TimeoutError(f"no chat line with {text!r}")


def place(remote, name: str) -> str:
    """Presses the button of the nearest table twice: the quote, then the order. Returns
    the chat line of the build's start"""
    reply = tc.ship_reply(remote, "!oct order")
    assert "Your quote for" in reply, reply
    reply = tc.ship_reply(remote, "!oct order")
    assert "is in the queue" in reply, reply
    return wait_chat(remote, f"builds '{name}' for you now", 60)


def order(remote) -> int:
    """Orders at the nearest table and returns the shipyard's grid id"""
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    tc.stand_by(remote, table)
    name = re.search(r"<DisplayName>([^<]*)</DisplayName>", table_blueprint)[1]
    line = place(remote, name)
    shipyard = re.search(r"(\w+ Station Shipyard) builds", line)[1]
    return next(g["entityId"] for g in remote.list_grids() if g["name"] == shipyard)


def shipyard_grid(shipyard_id: int) -> ET.Element:
    sector = ET.parse(rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").getroot()
    return next(
        o
        for o in sector.find("SectorObjects")
        if o.findtext("EntityId") == str(shipyard_id)
    )


def beam_shoot(remote, shipyard_id: int) -> object:
    cell = tuple(
        int(c) for c in sz.block_cells(shipyard_grid(shipyard_id), "OmniArcBeam")[0]
    )
    return remote.get_property(shipyard_id, cell, "ToolCore_Shoot")


def screenshots(remote, shipyard_id: int, case: str) -> None:
    """From the free space, 70% of the way from the welders' middle (the ship) to the
    farthest welder, looking at the ship"""
    grid = shipyard_grid(shipyard_id)
    beams = sz.cells_to_world(grid, sz.block_cells(grid, "OmniArcBeam"))
    middle = tuple(sum(c) / len(beams) for c in zip(*beams))
    far = max(beams, key=lambda b: math.dist(b, middle))
    remote.character_teleport(*(m + 0.7 * (f - m) for m, f in zip(middle, far)))
    time.sleep(5)
    remote.character_look_at(*middle)
    for shot in range(3):
        time.sleep(5)
        path = Path(SHOTS) / f"{case}-welding-{shot}.png"
        remote.screenshot_save(str(path), 1280, 720)
        log(f"screenshot {path}")
    time.sleep(5)


# --- the saved world ----------------------------------------------------------------


def saved() -> tuple[ET.Element, str]:
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    return sector, (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")


def player_id(sandbox: str) -> int:
    return int(
        re.search(
            rf"<IdentityId>(\d+)</IdentityId>\s*<DisplayName>{rig.CLIENT_NAME}<",
            sandbox,
        )[1]
    )


def check_ship(name: str, blocks: int | None, complete: bool) -> None:
    sector, sandbox = saved()
    player = player_id(sandbox)
    grids = [
        o
        for o in sector.find("SectorObjects")
        if o.findtext("DisplayName") == name and o.find("CubeBlocks") is not None
    ]
    assert len(grids) == 1, f"{len(grids)} grids named {name}"
    ship = grids[0]
    cubes = list(ship.find("CubeBlocks"))
    # Armor blocks have no owner
    owners = {b.findtext("Owner") for b in cubes if b.find("Owner") is not None}
    built_by = {b.findtext("BuiltBy") for b in cubes}
    partial = [b for b in cubes if float(b.findtext("BuildPercent") or 1) < 1]
    log(
        f"{name}: {len(cubes)} blocks, {len(partial)} unfinished, static "
        f"{ship.findtext('IsStatic')}, owners {owners}, built by {built_by}, player {player}"
    )
    assert ship.findtext("IsStatic") != "true", "the ship is handed over dynamic"
    assert owners <= {str(player)} and built_by == {str(player)}, (owners, built_by)
    assert "Shipyard Order" not in ET.tostring(ship, encoding="unicode"), "scaffold"
    if complete:
        assert len(cubes) == blocks and not partial
    else:
        assert 1 < len(cubes) < blocks or partial, "an unfinished ship"


def check_shipyards() -> None:
    """No components left in any shipyard, every welder off"""
    sector, _ = saved()
    for grid in sector.find("SectorObjects"):
        if not (grid.findtext("DisplayName") or "").endswith(" Shipyard"):
            continue
        text = ET.tostring(grid, encoding="unicode")
        components = re.findall(
            r'xsi:type="MyObjectBuilder_Component">\s*<SubtypeName>(\w+)<', text
        )
        active = 0
        for value in re.findall(
            r"<Key>75bbb4f5-4fb9-4230-aaaa-bb79c9811618</Key>\s*<Value>([^<]*)</Value>",
            text,
        ):
            repo = base64.b64decode(value)
            active += repo[0] == 0x08 and repo[1] == 1
        log(
            f"{grid.findtext('DisplayName')}: component stacks {components}, {active} welders on"
        )
        assert not components and not active


def run(case: str) -> None:
    blueprint, blocks, minutes = CASES[case]
    name = re.search(r"<DisplayName>([^<]*)</DisplayName>", blueprint)[1]
    log(f"--- {case}: {name}, {blocks} blocks, {minutes} min")
    rig.stop_client()
    rig.stop_server()
    prepare(blueprint, minutes)
    rig.start_server()
    log("server ready")
    remote = rig.start_client(render=bool(SHOTS))
    shipyard = order(remote)
    ordered = time.monotonic()
    time.sleep(20)
    log(f"client: a welder's ToolCore_Shoot {beam_shoot(remote, shipyard)}")
    if SHOTS:
        screenshots(remote, shipyard, case)

    if case == "complete":
        log(wait_chat(remote, "is ready", 900))
        rig.stop_client()
        rig.stop_server()
        check_ship(name, blocks, complete=True)
    else:
        rig.stop_client()
        rig.stop_server()
        sector, _ = saved()
        text = ET.tostring(sector, encoding="unicode")
        assert "<CustomName>Shipyard Order</CustomName>" in text, "the order is saved"
        rig.start_server()
        log("server restarted without the client")
        # The restart may take longer than the order, then it ends at the first check
        deadline = (
            time.monotonic()
            + max(0.0, minutes * 60 - (time.monotonic() - ordered))
            + 120
        )
        while time.monotonic() < deadline:
            lines = [
                line
                for line in rig.server_game_log().splitlines()
                if "Shipyard order" in line
            ]
            if any(" ended: " in line for line in lines):
                break
            time.sleep(10)
        rig.stop_server()
        log(lines)
        assert any(" picked up " in line for line in lines), "the order is picked up"
        check_ship(name, blocks, complete=False)
    check_shipyards()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    assert not errors, errors


if __name__ == "__main__":
    try:
        for name in sys.argv[1:] or CASES:
            run(name)
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed")
