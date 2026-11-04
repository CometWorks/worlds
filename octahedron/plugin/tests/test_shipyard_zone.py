"""Checks welding in a station safe zone that allows no action, the zones of the
Octahedron world since SE-974 (drilling and landing gear locking from 2026-10-14 until
then). Runs the test server, with
OmniArc, ToolCore and Multigrid Projector as on the Octahedron servers, and the headless
client.

- Alpha Station Shipyard's OmniArc beams, switched on, weld a projection of six light
  armor blocks: they may, through the plugin.
- In the same zone, away from the shipyard's reach, a player's ship welder and hand
  welder can't repair a damaged block, the ship welder can't build a projected block, a
  ship grinder and a hand grinder can't grind, and a player's grid named "... Shipyard"
  with a switched on OmniArc beam builds nothing.
- The same player tools outside the zone weld and grind, proving the setup works.

The server runs, the client drives the tools, then the server stops (it saves) and the
saved world is checked. The cases (CASES): the plugin's rules on, on with the zones
holding every test grid from the start, and off, where the shipyard welds nothing.

    uv run --no-project --with httpx python tests/test_shipyard_zone.py [case ...]

Needs what tests/test_projection_limits.py needs.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE1-0204")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402

ZONE_ACTIONS = ""
SHIPYARD = "Alpha Station Shipyard"
PLAYER = pl.PLAYER
OWNED = pl.OWNED
DAMAGED = "<IntegrityPercent>0.2</IntegrityPercent><BuildPercent>1</BuildPercent>"
# The steel plates a light armor block lost, for a hand welder with none of its own
STOCKPILE = (
    '<ConstructionStockpile><Items xsi:type="MyObjectBuilder_StockpileItem"><Amount>25</Amount>'
    '<PhysicalContent xsi:type="MyObjectBuilder_Component"><SubtypeName>SteelPlate</SubtypeName>'
    "</PhysicalContent></Items></ConstructionStockpile>"
)
SHIP_ID = 777000204000100
# Grids at each test spot: (inside, outside) entity ids
TOOLS = (777000204000200, 777000204000300)
GRIND_TARGET = (777000204000210, 777000204000310)
HAND = (777000204000220, 777000204000320)
FAKE_SHIPYARD = 777000204000230
# Seconds each hand tool works, and the least the server runs: the ship tools and the
# shipyard work from the start, as they are switched on
HAND_SECONDS = 20
SERVER_SECONDS = 240
# name: (the plugin's rules on, the zones load after the grids). The world loads its
# zones first, so they hold only the grids that move in later; "contained" has them hold
# every test grid from the start, which the game checks differently. "off" shows the zone
# stops the shipyard without the plugin.
CASES = {"on": (True, False), "contained": (True, True), "off": (False, False)}

started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


# --- geometry ---------------------------------------------------------------------


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def mul(a, k):
    return tuple(x * k for x in a)


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def vec(element) -> tuple[float, float, float]:
    """An x, y, z element; the game leaves out zeros, and a whole Min at the origin"""
    if element is None:
        return (0.0, 0.0, 0.0)
    return tuple(float(element.get(k, 0)) for k in "xyz")


def cells_to_world(grid, cells):
    """World positions of a large grid's cells"""
    po = grid.find("PositionAndOrientation")
    p, f, u = (vec(po.find(t)) for t in ("Position", "Forward", "Up"))
    r, b = cross(f, u), mul(f, -1)
    return [
        add(p, mul(add(add(mul(r, x), mul(u, y)), mul(b, z)), 2.5)) for x, y, z in cells
    ]


# A shipyard's welders: OmniArc's, or the OctahedronFilaments mod's copy of it (SE-1067)
WELDERS = ("OmniArcBeam", "OctahedronFilamentWelder")


def block_cells(grid, subtypes=None):
    """The Min cells of the grid's blocks of subtypes, a subtype or a tuple of them"""
    if isinstance(subtypes, str):
        subtypes = (subtypes,)
    return [
        vec(b.find("Min"))
        for b in grid.find("CubeBlocks")
        if subtypes is None or b.findtext("SubtypeName") in subtypes
    ]


def layout():
    """The shipyard's grid and beams, and the two test spots:
    one in the zone, the farthest from the station and shipyard blocks on a sphere around
    the center, and one outside it in the same direction"""
    sector = ET.parse(rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").getroot()
    objects = sector.find("SectorObjects")
    zone = next(
        o
        for o in objects
        if o.findtext("DisplayName") == "Alpha Station" and o.find("Radius") is not None
    )
    center = vec(zone.find("PositionAndOrientation/Position"))
    radius = float(zone.findtext("Radius"))
    blocks, yard = [], None
    for grid in objects:
        if grid.find("CubeBlocks") is None:
            continue
        cells = block_cells(grid)
        world = cells_to_world(grid, cells[:: max(1, len(cells) // 2000)])
        if min(math.dist(w, center) for w in world) < radius:
            blocks += world
        if grid.findtext("DisplayName") == SHIPYARD:
            yard = grid
    beams = cells_to_world(yard, block_cells(yard, WELDERS))
    directions = [
        (x, y, z)
        for x in (-1, 0, 1)
        for y in (-1, 0, 1)
        for z in (-1, 0, 1)
        if (x, y, z) != (0, 0, 0)
    ]

    def spot(direction, distance):
        return add(center, mul(direction, distance / math.dist(direction, (0, 0, 0))))

    best = max(
        directions,
        key=lambda d: min(math.dist(spot(d, radius - 60), w) for w in blocks),
    )
    inside, outside = spot(best, radius - 60), spot(best, radius + 200)
    clearance = min(math.dist(inside, w) for w in blocks)
    reach = min(math.dist(inside, b) for b in beams)
    log(
        f"test spot {inside}: {clearance:.0f} m from the nearest block in the zone, "
        f"{reach:.0f} m from the nearest shipyard beam"
    )
    # The beams reach 100 m
    assert clearance > 30 and reach > 130, (clearance, reach)

    return yard, beams, inside, outside


# --- world --------------------------------------------------------------------------


def battery(pos):
    return rig.block(
        "MyObjectBuilder_BatteryBlock",
        "LargeBlockBatteryBlock",
        pos,
        OWNED + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>",
    )


def armor(pos, damaged=False, stockpile=False):
    return rig.block(
        "MyObjectBuilder_CubeBlock",
        "LargeBlockArmorBlock",
        pos,
        OWNED + (DAMAGED if damaged else "") + (STOCKPILE if stockpile else ""),
    )


def projector(pos, blueprint):
    return rig.block(
        "MyObjectBuilder_Projector",
        "LargeProjector",
        pos,
        OWNED + "<Enabled>true</Enabled>"
        f"<ProjectedGrids>{blueprint}</ProjectedGrids><KeepProjection>true</KeepProjection>",
    )


def ship_tool(xsi_type, subtype, pos, inventory=""):
    extra = (
        f"<ComponentContainer><Components>{inventory}</Components></ComponentContainer>"
        if inventory
        else ""
    )
    return rig.block(xsi_type, subtype, pos, OWNED + extra + "<Enabled>true</Enabled>")


# One armor block in front of a welder, the projector beside it at the blueprint's origin
TOOL_BLUEPRINT = rig.grid(
    "Ship Welder Blueprint",
    None,
    [
        rig.block("MyObjectBuilder_Projector", "LargeProjector", (0, 0, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, 0, 0)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)


def tools_grid(entity_id, position):
    """A player's grid: welder A at x=1 faces a damaged armor block, welder B at
    x=3 faces the projected armor block at (3, 0, -1), the grinder at x=5 faces the grind
    target grid. Tools face -Z (the grid's forward) and are two cells long."""
    blocks = [
        battery((0, 0, 0)),
        armor((0, 0, -1)),
        armor((1, 0, -1), damaged=True),
        projector((2, 0, -1), TOOL_BLUEPRINT),
        ship_tool(
            "MyObjectBuilder_ShipWelder", "LargeShipWelder", (1, 0, 0), pl.INVENTORY
        ),
        ship_tool(
            "MyObjectBuilder_ShipWelder", "LargeShipWelder", (3, 0, 0), pl.INVENTORY
        ),
        ship_tool("MyObjectBuilder_ShipGrinder", "LargeShipGrinder", (5, 0, 0)),
    ] + [armor((x, 0, 2)) for x in range(6)]
    return rig.grid(f"Zone Test Tools {entity_id}", entity_id, blocks, position)


def single_block_grid(name, entity_id, position, blocks):
    return rig.grid(name, entity_id, blocks, position)


def fake_shipyard(position):
    """pl.RIG, a projector with an OmniArc beam, owned by a player and named like a
    shipyard"""
    text = pl.RIG.replace(
        "<DisplayName>Projection Limit Rig</DisplayName>",
        "<DisplayName>Zone Test Shipyard</DisplayName>",
    ).replace(
        f"<EntityId>{pl.GRID_ID}</EntityId>", f"<EntityId>{FAKE_SHIPYARD}</EntityId>"
    )
    return re.sub(r"<Position [^>]*/>", rig._vec("Position", position), text, count=1)


def beamless_rig(name, entity_id, position, forward=(0, 0, -1), up=(0, 1, 0)):
    """pl.RIG without its beam: a projector with six light armor blocks to weld"""
    text = re.sub(
        r'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_ConveyorSorter">.*?'
        r"</MyObjectBuilder_CubeBlock>",
        "",
        pl.RIG,
        flags=re.S,
    )
    text = text.replace(
        "<DisplayName>Projection Limit Rig</DisplayName>",
        f"<DisplayName>{name}</DisplayName>",
    ).replace(f"<EntityId>{pl.GRID_ID}</EntityId>", f"<EntityId>{entity_id}</EntityId>")
    for tag, value in (("Position", position), ("Forward", forward), ("Up", up)):
        text = re.sub(rf"<{tag} [^>]*/>", rig._vec(tag, value), text, count=1)
    return text


def shipyard_ship(yard, beams):
    """At the middle of the shipyard's beams, turned like the shipyard"""
    middle = mul(tuple(sum(b[i] for b in beams) for i in range(3)), 1 / len(beams))
    po = yard.find("PositionAndOrientation")
    return beamless_rig(
        "Shipyard Test Ship",
        SHIP_ID,
        middle,
        vec(po.find("Forward")),
        vec(po.find("Up")),
    )


def spot_grids(inside, position):
    i = 0 if inside else 1
    grids = [
        tools_grid(TOOLS[i], position),
        # In front of the grinder at (5, 0, 0)
        single_block_grid(
            f"Zone Test Grind Target {i}",
            GRIND_TARGET[i],
            add(position, (12.5, 0, -2.5)),
            [armor((0, 0, 0))],
        ),
        # Hand welding target at x=0, hand grinding target at x=2
        single_block_grid(
            f"Zone Test Hand Targets {i}",
            HAND[i],
            add(position, (0, 12.5, 0)),
            [
                armor((0, 0, 0), damaged=True, stockpile=True),
                armor((1, 0, 0)),
                armor((2, 0, 0)),
            ],
        ),
    ]
    if inside:
        grids.append(fake_shipyard(add(position, (0, -15, 0))))
    return grids


def prepare(enabled, zones_last, yard, beams, inside, outside) -> None:
    # The trash collector would take the small test grids
    rig.prepare_world(
        [shipyard_ship(yard, beams)]
        + spot_grids(True, inside)
        + spot_grids(False, outside),
        settings={"TrashRemovalEnabled": "false"},
    )
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text, count = re.subn(
        r"<AllowedActions(?: />|>[^<]*</AllowedActions>)",
        f"<AllowedActions>{ZONE_ACTIONS}</AllowedActions>",
        text,
    )
    assert count >= 5, count

    # Switch on the shipyard's beams and give each steel plates
    def activate(match):
        block, n = re.subn(
            r"<Key>75bbb4f5-4fb9-4230-aaaa-bb79c9811618</Key>\s*<Value>[^<]*</Value>",
            re.search(r"<Key>.*?</Value>", pl.TOOL_STORAGE)[0],
            match[0],
        )
        block, m = re.subn(
            r"<ComponentData>\s*<TypeId>MyInventoryBase</TypeId>.*?</ComponentData>",
            pl.INVENTORY,
            block,
            flags=re.S,
        )
        assert n == m == 1
        return block

    yard_start = text.index(f"<DisplayName>{SHIPYARD}</DisplayName>")
    grid_start = text.rindex(
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">',
        0,
        yard_start,
    )
    grid_end = text.index("</MyObjectBuilder_EntityBase>", yard_start)
    yard_text, count = re.subn(
        r"<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_ConveyorSorter\">\s*"
        rf"<SubtypeName>(?:{'|'.join(WELDERS)})</SubtypeName>.*?</MyObjectBuilder_CubeBlock>",
        activate,
        text[grid_start:grid_end],
        flags=re.S,
    )
    assert count == 24, count
    text = text[:grid_start] + yard_text + text[grid_end:]
    if zones_last:
        # A zone takes in the grids already there when it loads; later ones only when
        # they move in, and static ones never. Loaded last, the zones hold every grid.
        zones = re.findall(
            r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_SafeZone">.*?'
            r"</MyObjectBuilder_EntityBase>\s*",
            text,
            flags=re.S,
        )
        for zone in zones:
            text = text.replace(zone, "", 1)
        text = text.replace("</SectorObjects>", "".join(zones) + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")

    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        text = text.replace("<Mods />", f"<Mods>{pl.MOD_ITEMS}</Mods>", 1)
        if name == "Sandbox.sbc":
            text = text.replace("</Identities>", pl.IDENTITY + "</Identities>", 1)
        path.write_text(text, encoding="utf-8")

    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        for id_ in pl.MODS:
            link = folder / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(pl.WORKSHOP / str(id_))

    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "<Enabled>" not in config
    rig.write_plugin_config(
        config.replace(
            "<OctahedronConfig>",
            f"<OctahedronConfig><Enabled>{str(enabled).lower()}</Enabled>",
            1,
        )
    )
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, pl.MGP])


# --- driving the tools --------------------------------------------------------------


def wait_grids(remote, ids, timeout=180.0) -> None:
    deadline = time.monotonic() + timeout
    while True:
        known = {g["entityId"] for g in remote.list_grids()}
        if set(ids) <= known:
            return
        assert (
            time.monotonic() < deadline
        ), f"grids not on the client: {set(ids) - known}"
        time.sleep(3)


def hand_tool(remote, position, x: int, tool: str) -> None:
    """Holds a hand tool on block x of the hand targets grid for HAND_SECONDS"""
    block = add(position, (x * 2.5, 12.5, 0))
    remote.character_teleport(*add(block, (0, 0, -3.25)))
    time.sleep(3)
    remote.character_set_dampeners(True)
    remote.character_equip(tool)
    time.sleep(2)
    remote.character_look_at(*block, tolerance=1)
    target = remote.get_character_target(5.0)
    log(f"{tool} aims at {target}")
    remote.character_primary_action(True)
    time.sleep(HAND_SECONDS)
    remote.character_primary_action(False)


def drive(yard, inside, outside) -> None:
    remote = rig.start_client()
    log("client in the world")
    remote.character_teleport(*add(inside, (0, 12.5, -10)))
    yard_id = int(yard.findtext("EntityId"))
    wait_grids(remote, [TOOLS[0], HAND[0], SHIP_ID, yard_id])
    beam = tuple(int(c) for c in block_cells(yard, WELDERS)[0])
    shoot = remote.get_property(yard_id, beam, "ToolCore_Shoot")
    log(f"client: shipyard beam ToolCore_Shoot {shoot}")

    for spot, inside_ in ((inside, True), (outside, False)):
        i = 0 if inside_ else 1
        remote.character_teleport(*add(spot, (0, 12.5, -10)))
        # The ship grinder outside may have ground its target away already
        wait_grids(remote, [TOOLS[i], HAND[i]])
        hand_tool(remote, spot, 0, "WelderItem")
        hand_tool(remote, spot, 2, "AngleGrinderItem")
    rig.stop_client()


# --- the saved world ----------------------------------------------------------------


def saved_grids() -> dict[int, ET.Element]:
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    return {
        int(o.findtext("EntityId")): o
        for o in sector.find("SectorObjects")
        if o.find("CubeBlocks") is not None
    }


def integrity(grids, grid_id, cell) -> float:
    """A block's integrity, 0 when it is gone"""
    grid = grids.get(grid_id)
    for b in grid.find("CubeBlocks") if grid is not None else []:
        if vec(b.find("Min")) == cell:
            return float(b.findtext("IntegrityPercent") or 1)
    return 0.0


def light_armor(grids, grid_id) -> list[float]:
    grid = grids[grid_id]
    return [
        float(b.findtext("IntegrityPercent") or 1)
        for b in grid.find("CubeBlocks")
        if b.findtext("SubtypeName") == "LargeBlockArmorBlock"
    ]


def plates(grids, grid_id) -> list[str]:
    return re.findall(
        r"<Amount>([\d.]+)</Amount>", ET.tostring(grids[grid_id], encoding="unicode")
    )


def check(enabled: bool) -> None:
    grids = saved_grids()
    ship = light_armor(grids, SHIP_ID)
    log(f"shipyard ship: {len(ship)} light armor blocks, integrity {ship}")
    fake = light_armor(grids, FAKE_SHIPYARD)
    log(
        f"player's 'Zone Test Shipyard': {len(fake)} light armor blocks, "
        f"plates {plates(grids, FAKE_SHIPYARD)}"
    )
    results = {}
    for i, where in enumerate(("inside", "outside")):
        results[where] = r = {
            "ship weld": integrity(grids, TOOLS[i], (1, 0, -1)),
            "ship projection": integrity(grids, TOOLS[i], (3, 0, -1)),
            "ship grind": integrity(grids, GRIND_TARGET[i], (0, 0, 0)),
            "hand weld": integrity(grids, HAND[i], (0, 0, 0)),
            "hand grind": integrity(grids, HAND[i], (2, 0, 0)),
        }
        log(f"{where}: {r}")

    if enabled:
        assert len(ship) == 6 and min(ship) == 1, "the shipyard welds the projection"
    else:
        assert not ship, "without the plugin the zone stops the shipyard"
    assert not fake, "a player's grid named like a shipyard welds nothing in the zone"
    assert plates(grids, FAKE_SHIPYARD) == [str(pl.PLATES)], "and keeps its components"
    assert results["inside"] == {
        "ship weld": 0.2,
        "ship projection": 0.0,
        "ship grind": 1.0,
        "hand weld": 0.2,
        "hand grind": 1.0,
    }
    out = results["outside"]
    assert out["ship weld"] > 0.2 and out["hand weld"] > 0.2, "welding works outside"
    assert out["ship projection"] > 0, "the ship welder builds projections outside"
    assert out["ship grind"] < 1 and out["hand grind"] < 1, "grinding works outside"


def run(case: str) -> None:
    enabled, zones_last = CASES[case]
    log(
        f"--- {case}: the plugin's rules {'on' if enabled else 'off'}, "
        f"zones load {'last' if zones_last else 'first'}"
    )
    rig.stop_client()
    rig.stop_server()
    yard, beams, inside, outside = layout()
    prepare(enabled, zones_last, yard, beams, inside, outside)
    rig.start_server()
    ready = time.monotonic()
    log("server ready")
    try:
        drive(yard, inside, outside)
    finally:
        rig.stop_client()
    time.sleep(max(0.0, SERVER_SECONDS - (time.monotonic() - ready)))
    rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    assert not errors, errors
    check(enabled)


if __name__ == "__main__":
    try:
        for name in sys.argv[1:] or CASES:
            run(name)
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed")
