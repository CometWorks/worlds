"""Checks on the test server, with the headless client, that a new block gets Target Neutrals off
however it is built (SE-830): placed by hand, as the first block of a new grid, pasted, pasted onto
a grid, as a starter ship and welded from a projection by a shipyard's tools. The blueprint has the
setting on in every turret type and a custom turret controller, a paste also in a searchlight (it
needs a DLC to weld, which the client lacks); a test mod adds a turret whose definition has it on
by default. With the rule off, the same builds keep it on, which shows the test can fail.

A block that was already on in the world and blocks the client turns on afterwards stay on: the
client reads them, and the saved world holds them, before and after a server restart. A handover
is in tests/target_neutrals_cluster.py.

The client doesn't weld a projection itself here: with Multigrid Projector on the server, its
requests from this rig build nothing. Its weld passes through the same seam as the shipyard's.

    uv run --no-project --with httpx python tests/test_target_neutrals.py

Needs what tests/test_shipyard_order.py needs.
"""

from __future__ import annotations

import os
import re
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-830")
import rig  # noqa: E402
from se_remote import CallOp  # noqa: E402
import test_client as tc  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402

MOD_ID = 999000830
MOD_TURRET = "OctNeutralsTurret"
RIG_AT = (250_000, 0, 0)
BUILD_RIG = 777000830000100
SHIP = "Neutrals Test"
# The tables keep the world's blueprint, and a blueprint of this size may be a starter ship
CONFIG = {
    "ShipyardOrderMinutes": 60,
    "ShipyardTableMinutes": 600,
    "StarterShipMaxShipPcu": 100_000,
    "FreeBuildCooldownMinutes": 0,
}
FLAG_ON = "<Flags><TargetNeutrals>true</TargetNeutrals></Flags>"
started = time.monotonic()
results: list[tuple[str, bool, str]] = []


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def check(label: str, passed, detail="") -> None:
    results.append((label, bool(passed), str(detail)))
    log(f"{'PASS' if passed else 'FAIL'} {label} {detail}")


def weapons(x: int, modded: bool, welded: bool) -> list[str]:
    """One of each block type with the setting, on it, standing on an armor spine along x. The
    searchlight needs a DLC, which the rig's client doesn't have to weld or place it"""
    blocks = [
        ("MyObjectBuilder_InteriorTurret", "LargeInteriorTurret", (x, 1, 0)),
        (
            "MyObjectBuilder_TurretControlBlock",
            "LargeTurretControlBlock",
            (x + 2, 1, 0),
        ),
        ("MyObjectBuilder_LargeGatlingTurret", "", (x + 6, 1, -1)),
        ("MyObjectBuilder_LargeMissileTurret", "", (x + 10, 1, -1)),
    ]
    if not welded:
        blocks.append(
            ("MyObjectBuilder_Searchlight", "LargeSearchlight", (x + 4, 1, 0))
        )
    if modded:
        blocks.append(("MyObjectBuilder_InteriorTurret", MOD_TURRET, (x + 14, 1, 0)))
    return [
        rig.block(
            kind,
            subtype,
            at,
            # As in a real blueprint: without an id, client and server each make up their own for
            # a welded block, and the client's changes go nowhere. A controller's builder must
            # have the rest
            f"<EntityId>{777000830001000 + at[0]}</EntityId>"
            + FLAG_ON
            + ("<ToolIds /><Toolbar><Slots /></Toolbar>" if "Control" in kind else ""),
        )
        for kind, subtype, at in blocks
    ]


def armor(xs, y: int = 0) -> list[str]:
    return [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, y, 0))
        for x in xs
    ]


def blueprint(
    modded: bool, tag: str = "MyObjectBuilder_CubeGrid", welded: bool = False
) -> str:
    return rig.grid(
        SHIP, None, armor(range(1, 19)) + weapons(2, modded, welded), tag=tag
    )


def bp_document(grid: str) -> str:
    """A bp.sbc document of a grid made with tag=CubeGrid"""
    return f"""<?xml version="1.0"?>
<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<ShipBlueprints><ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">
<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="{SHIP}" />
<DisplayName>{SHIP}</DisplayName><CubeGrids>{grid}</CubeGrids>
</ShipBlueprint></ShipBlueprints></Definitions>
"""


def write_mod() -> None:
    """A copy of the interior turret, a single cell, whose definition has Target Neutrals on"""
    source = (
        rig.DS64.parent / "Content/Data/CubeBlocks/CubeBlocks_Weapons.sbc"
    ).read_text(encoding="utf-8")
    definition = re.search(
        r'<Definition xsi:type="MyObjectBuilder_LargeTurretBaseDefinition">\s*<Id>\s*'
        r"<TypeId>InteriorTurret</TypeId>\s*<SubtypeId>LargeInteriorTurret</SubtypeId>.*?</Definition>",
        source,
        re.S,
    )[0]
    definition = definition.replace(
        "<SubtypeId>LargeInteriorTurret<", f"<SubtypeId>{MOD_TURRET}<"
    )
    definition = re.sub(
        r"<BlockPairName>[^<]*", f"<BlockPairName>{MOD_TURRET}", definition
    )
    definition = re.sub(
        r"\s*<EnabledTargetingOptions>[^<]*</EnabledTargetingOptions>", "", definition
    )
    definition = definition.replace(
        "</Definition>",
        "<EnabledTargetingOptions>Players SmallShips LargeShips Stations Asteroids Enemies Neutrals"
        "</EnabledTargetingOptions></Definition>",
    )
    for mods in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        data = mods / f"{MOD_ID}.sbm/Data"
        data.mkdir(parents=True, exist_ok=True)
        (data / "OctNeutralsTurret.sbc").write_text(
            '<?xml version="1.0"?>\n<Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">'
            f"<CubeBlocks>{definition}</CubeBlocks></Definitions>\n",
            encoding="utf-8",
        )


def prepare() -> None:
    so.prepare(blueprint(modded=True, welded=True), 60)
    write_mod()
    item = (
        f'<ModItem FriendlyName="Neutrals Test"><Name>{MOD_ID}.sbm</Name><PublishedFileId>{MOD_ID}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        path.write_text(
            path.read_text(encoding="utf-8").replace("</Mods>", item + "</Mods>", 1),
            encoding="utf-8",
        )
    # Already on in the world, and an armor spine to build on
    build_rig = rig.grid(
        "Neutrals Build Rig",
        BUILD_RIG,
        armor(range(0, 40))
        + [
            rig.block(
                "MyObjectBuilder_InteriorTurret",
                "LargeInteriorTurret",
                (0, 1, 0),
                FLAG_ON,
            )
        ],
        position=RIG_AT,
    )
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    sector.write_text(
        sector.read_text(encoding="utf-8").replace(
            "</SectorObjects>",
            build_rig + "</SectorObjects>",
            1,
        ),
        encoding="utf-8",
    )
    rig.write_plugin_config(config_text())
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, pl.MGP])


def config_text(**values) -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "<ClearTargetNeutralsEnabled>true<" in text
    for key, value in (CONFIG | values).items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        if not count:
            text = text.replace(
                "</OctahedronConfig>", f"  <{key}>{value}</{key}>\n</OctahedronConfig>"
            )
    return text


def set_rule(remote, on: bool) -> None:
    rig.write_plugin_config(config_text(ClearTargetNeutralsEnabled=str(on).lower()))
    log(f"rule {'on' if on else 'off'}: {rig.chat(remote, '!oct reload', 3)}")


def targeting_cubes(remote, grid_id: int) -> list[dict]:
    cubes, offset = [], 0
    while True:
        page = remote.list_cubes(grid_id, offset=offset)
        cubes += page["cubes"]
        offset += len(page["cubes"])
        if offset >= page["total"] or not page["cubes"]:
            break
    found = [
        c
        for c in cubes
        if c["typeId"].rsplit("_", 1)[-1]
        in {
            "InteriorTurret",
            "LargeGatlingTurret",
            "LargeMissileTurret",
            "TurretControlBlock",
            "Searchlight",
        }
    ]
    return found


def on_cells(remote, grid_id: int) -> tuple[int, list[tuple]]:
    """The number of blocks with the setting on the grid, and the cells of those with it on, as the
    client reads them"""
    cubes = targeting_cubes(remote, grid_id)
    on = [
        tuple(c["min"])
        for c in cubes
        if remote.get_property(grid_id, tuple(c["min"]), "TargetNeutrals")["value"]
    ]
    return len(cubes), on


def wait_grid(remote, name: str, timeout: float = 60) -> int:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for grid in remote.list_grids():
            if grid["name"] == name:
                return grid["entityId"]
        time.sleep(3)
    raise TimeoutError(f"no grid named {name}")


def saved_on() -> dict[str, int]:
    """Grid name -> blocks with the setting on in the saved world, projections left out"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    text = re.sub(r"<ProjectedGrids>.*?</ProjectedGrids>", "", text, flags=re.S)
    found: dict[str, int] = {}
    for entity in ET.fromstring(text).find("SectorObjects"):
        count = sum(flag.text == "true" for flag in entity.iter("TargetNeutrals"))
        if count:
            name = entity.findtext("DisplayName") or ""
            found[name] = found.get(name, 0) + count
    return found


def saved_weapons(name: str) -> list[int]:
    """The blocks with the setting on each saved grid of this name, projections left out"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    text = re.sub(r"<ProjectedGrids>.*?</ProjectedGrids>", "", text, flags=re.S)
    kinds = (
        "InteriorTurret",
        "LargeGatlingTurret",
        "LargeMissileTurret",
        "TurretControlBlock",
    )
    xsi = "{http://www.w3.org/2001/XMLSchema-instance}type"
    return [
        sum(b.get(xsi, "").endswith(kinds) for b in entity.find("CubeBlocks"))
        for entity in ET.fromstring(text).find("SectorObjects")
        if entity.findtext("DisplayName") == name
    ]


def built(remote, label: str, grid_id: int, blocks: int) -> None:
    time.sleep(5)
    count, on = on_cells(remote, grid_id)
    check(
        f"{label}: all {blocks} blocks have it off",
        count == blocks and not on,
        (count, on),
    )


def rule_off_controls(remote, build_rig: int) -> None:
    set_rule(remote, False)
    report = remote.character_build_block(build_rig, (4, 1, 0), MOD_TURRET)
    log(f"hand placed with the rule off: {report}")
    pasted = remote.paste_blueprint(
        xml=bp_document(blueprint(True, "CubeGrid")),
        position=(RIG_AT[0], 0, 300),
        display_name="Neutrals Rule Off",
    )
    log(f"pasted with the rule off: {pasted}")
    time.sleep(5)
    count, on = on_cells(remote, pasted[0]["entityId"])
    check("rule off: a paste keeps it on", count == 6 == len(on), (count, on))
    count, on = on_cells(remote, build_rig)
    check(
        "rule off: a turret placed by hand takes it from its definition",
        (4, 1, 0) in on,
        on,
    )
    set_rule(remote, True)


def hand_and_paste(remote, build_rig: int) -> tuple[tuple, int, tuple]:
    before = len(targeting_cubes(remote, build_rig))
    for cell, subtype in (
        ((8, 1, 0), MOD_TURRET),
        ((12, 1, 0), "LargeTurretControlBlock"),
        ((16, 1, 0), "LargeInteriorTurret"),
    ):
        log(
            f"placing {subtype}: {remote.character_build_block(build_rig, cell, subtype)}"
        )
    time.sleep(5)
    cubes = targeting_cubes(remote, build_rig)
    new = [c for c in cubes if c["min"][0] >= 8]
    values = {
        c["subtypeId"]: remote.get_property(
            build_rig, tuple(c["min"]), "TargetNeutrals"
        )["value"]
        for c in new
    }
    check(
        "placed by hand: off, also where the definition has it on",
        len(new) == 3 and not any(values.values()) and len(cubes) == before + 3,
        values,
    )
    controller = next(tuple(c["min"]) for c in new if "Control" in c["subtypeId"])

    before_grids = {g["entityId"] for g in remote.list_grids()}
    reply = remote.character_build_grid(MOD_TURRET, (RIG_AT[0], 0, -60))
    log(f"first block of a new grid: {reply}")
    time.sleep(5)
    new_grids = [g for g in remote.list_grids() if g["entityId"] not in before_grids]
    check("the first block's grid is there", len(new_grids) == 1, new_grids)
    if new_grids:
        built(remote, "first block of a new grid", new_grids[0]["entityId"], 1)

    pasted = remote.paste_blueprint(
        xml=bp_document(blueprint(True, "CubeGrid")),
        position=(RIG_AT[0], 0, 150),
        display_name="Neutrals Paste",
    )
    log(f"pasted: {pasted}")
    paste_id = pasted[0]["entityId"]
    built(remote, "pasted", paste_id, 6)

    onto = rig.grid(
        "Neutrals Onto",
        None,
        armor([0])
        + [
            rig.block(
                "MyObjectBuilder_InteriorTurret",
                "LargeInteriorTurret",
                (0, 1, 0),
                FLAG_ON,
            )
        ],
        tag="CubeGrid",
    )
    report = remote.paste_to_grid(build_rig, (25, 0, 1), xml=bp_document(onto))
    log(f"pasted onto a grid: {report}")
    time.sleep(5)
    cubes = targeting_cubes(remote, build_rig)
    pasted_on = [c for c in cubes if c["min"][0] >= 20]
    values = [
        remote.get_property(build_rig, tuple(c["min"]), "TargetNeutrals")["value"]
        for c in pasted_on
    ]
    check("pasted onto a grid: off", values == [False], values)
    turret = next(
        tuple(c["min"])
        for c in targeting_cubes(remote, paste_id)
        if "Interior" in c["subtypeId"]
    )
    return controller, paste_id, turret


def order(remote, station: str) -> int:
    """Orders the blueprint at a station's table, returns the ship welded from it by the shipyard's
    tools, as on the live cluster"""
    sa.at_table(remote, station)
    before = {g["entityId"] for g in remote.list_grids()}
    ready = so_ready_lines(remote)
    so.place(remote, SHIP)
    deadline = time.monotonic() + 1500
    while so_ready_lines(remote) <= ready and time.monotonic() < deadline:
        time.sleep(10)
    time.sleep(10)
    ships = [
        g["entityId"]
        for g in remote.list_grids()
        if g["name"] == SHIP and g["entityId"] not in before
    ]
    check(f"the ship ordered at {station} is delivered", len(ships) == 1, ships)
    return ships[0] if ships else 0


def so_ready_lines(remote) -> int:
    lines, _ = so.chat_lines(remote, 0)
    return sum("is ready" in line for line in lines)


def starter_and_orders(remote) -> None:
    stations = sorted(sa.station_tables())
    sa.at_table(remote, stations[0])
    before = {g["entityId"] for g in remote.list_grids()}
    reply = tc.ship_reply(remote)
    check("the starter ship waits", "waits outside" in reply, reply)
    time.sleep(5)
    new = [g for g in remote.list_grids() if g["entityId"] not in before]
    if new:
        built(remote, "starter ship", new[0]["entityId"], 5)

    # Each at its own station: a shipyard waits until the ship before is flown out
    set_rule(remote, False)
    ship = order(remote, stations[1])
    if ship:
        count, on = on_cells(remote, ship)
        check(
            "rule off: an ordered ship keeps it on", count == 5 == len(on), (count, on)
        )
    set_rule(remote, True)
    ship = order(remote, stations[2])
    if ship:
        built(remote, "ordered ship", ship, 5)
        # Fresh from the shipyard, the player's change to a welded block
        turn_on(remote, ship, tuple(targeting_cubes(remote, ship)[0]["min"]))


def turn_on(remote, grid_id: int, cell: tuple) -> None:
    """The server takes a client's change only from near the grid, within 15 m, and keeps quiet
    about one from farther away"""
    world = remote.call([CallOp.grid_to_world(grid_id, cell)]).call(0)
    world = (
        world.get("world") or world.get("position")
        if isinstance(world, dict)
        else world
    )
    remote.character_teleport(world[0], world[1] + 10, world[2])
    time.sleep(5)
    remote.set_property(grid_id, cell, "TargetNeutrals", True)
    time.sleep(5)
    value = remote.get_property(grid_id, cell, "TargetNeutrals")["value"]
    check(f"the client turns it on at {cell}", value is True, value)


def run() -> None:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    remote.set_admin_flag("creativeTools", True)
    time.sleep(3)
    remote.character_teleport(RIG_AT[0], 30, 0)
    build_rig = wait_grid(remote, "Neutrals Build Rig")
    count, on = on_cells(remote, build_rig)
    check(
        "in the world: the block that was on stays on",
        count == 1 == len(on),
        (count, on),
    )

    rule_off_controls(remote, build_rig)
    controller, paste_id, turret = hand_and_paste(remote, build_rig)
    turn_on(remote, build_rig, controller)
    turn_on(remote, paste_id, turret)
    starter_and_orders(remote)

    expected = {
        # In the world, the rule off's hand placed one and the one turned on
        "Neutrals Build Rig": 3,
        "Neutrals Rule Off": 6,
        "Neutrals Paste": 1,
        # The ordered ship with the rule off, and the one turned on on the other
        SHIP: 6,
    }
    rig.stop_client()
    rig.stop_server()
    first = saved_on()
    check("the saved world holds exactly these", first == expected, first)
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    ships = re.findall(
        rf"<DisplayName>{SHIP}</DisplayName>",
        re.sub(r"<ProjectedGrids>.*?</ProjectedGrids>", "", text, flags=re.S),
    )
    check(
        "the starter ship and the two ordered ships are saved",
        len(ships) == 3,
        len(ships),
    )
    built_ships = saved_weapons(SHIP)
    check("the ships have their 5 blocks", built_ships == [5, 5, 5], built_ships)

    rig.start_server()
    remote = rig.start_client()
    remote.character_teleport(RIG_AT[0], 30, 0)
    build_rig = wait_grid(remote, "Neutrals Build Rig")
    time.sleep(10)
    _, on = on_cells(remote, build_rig)
    check("after a restart the client reads the three on", len(on) == 3, on)
    rig.stop_client()
    rig.stop_server()
    second = saved_on()
    check("after a restart the saved world is the same", second == expected, second)
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    check("no errors of the plugin in the log", not errors, errors[:3])


if __name__ == "__main__":
    try:
        run()
    finally:
        rig.stop_client()
        rig.stop_server()
    failed = [r for r in results if not r[1]]
    print(f"{len(results) - len(failed)}/{len(results)} checks passed")
    for label, _, detail in failed:
        print(f"FAILED: {label} - {detail}")
    print("PASS" if not failed and results else "FAIL")
    sys.exit(1 if failed or not results else 0)
