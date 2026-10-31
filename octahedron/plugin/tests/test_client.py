"""Checks the plugin's rules on the test server with the headless client: the
world edge (grids and the character), the starter ship, the PCU cap per mechanical
grid group and the station refill. Prints what it saw; the asserts stop it at the
first rule broken.

Hangar runs on the server too (OCT_HANGAR_REPO, a working copy of CometWorks/hangar),
with one stored ship of the player's, so that the starter ship's PCU rule counts it.

    uv run --no-project --with httpx python tests/test_client.py
"""

from __future__ import annotations

import math
import os
import re
import time
from pathlib import Path

import rig
from se_remote import CallOp
from edge_grids import EDGE_GRIDS

# Loaded into every station hologram table: a cockpit, a battery, two armor blocks
BLUEPRINT = rig.grid(
    "Starter Test",
    777000133000100,
    rig.small_ship_blocks(),
    tag="MyObjectBuilder_CubeGrid",
)
HANGAR = (
    "hangar",
    Path(os.environ.get("OCT_HANGAR_REPO", rig.WORKSPACE / "se1/plugins/hangar")),
    "Hangar.xml",
    "17C928CE-4645-4E1B-9365-C3C46BFE061D",
)
# The player's ship in Hangar
HANGAR_PCU = 1000
HANGAR_CONFIG = f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig><HangarEntries><HangarEntry>
<Id>oct-test</Id><Scope>Player</Scope><OwnerId>{rig.CLIENT_ID}</OwnerId>
<OwnerName>{rig.CLIENT_NAME}</OwnerName><GridName>Parked</GridName>
<SavedUtc>2026-10-15T00:00:00Z</SavedUtc><Pcu>{HANGAR_PCU}</Pcu><Blocks>10</Blocks>
</HangarEntry></HangarEntries></PluginConfig>
"""
# Pasted for the group cap: two gyros (50 PCU each), a battery (15) and an armor block (1)
CAP_GRID = rig.grid(
    "Cap Test",
    None,
    [
        rig.block("MyObjectBuilder_Gyro", "LargeBlockGyro", (0, 0, 0)),
        rig.block("MyObjectBuilder_Gyro", "LargeBlockGyro", (0, 1, 0)),
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, -1, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, -1, 0)),
    ],
    tag="CubeGrid",
)
CAP_BLUEPRINT = f"""<?xml version="1.0"?>
<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<ShipBlueprints><ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">
<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Cap Test" />
<DisplayName>Cap Test</DisplayName><CubeGrids>{CAP_GRID}</CubeGrids>
</ShipBlueprint></ShipBlueprints></Definitions>
"""
EDGE_LINE = re.compile(
    r"^(?P<name>.*): (?P<km>[\d.]+) km, outward (?P<out>-?[\d.]+) m/s, limit (?P<limit>-?[\d.]+) m/s$"
)
started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def distance(position) -> float:
    return math.dist(position, (0, 0, 0))


def edge_report(remote) -> dict[str, tuple[float, float, float]]:
    """!oct edge as name -> (km, outward m/s, limit m/s)"""
    report = {}
    for line in rig.chat(remote, "!oct edge", 3):
        for part in line.splitlines():
            match = EDGE_LINE.match(part.strip())
            if match:
                report[match["name"]] = (
                    float(match["km"]),
                    float(match["out"]),
                    float(match["limit"]),
                )
    return report


def check_edge(report) -> None:
    for name, (km, out, limit) in report.items():
        log(f"edge {name}: {km:.3f} km, outward {out:.2f} m/s, limit {limit:.2f} m/s")
        # Beyond the edge only until the next scan moves it back onto the sphere
        assert km <= 500.01 or out < 0, name
        if name == "Edge 470 in":
            assert abs(out + 100) < 0.5, "inward motion is left alone"
        # A frame of thrust or prediction may show above the limit; a body at the limit is braked
        assert out <= max(limit, 0) + 1.0, name


def test_character_edge(remote, home) -> None:
    """Flies the character outward from 498 km with its jetpack"""
    remote.character_teleport(0, 498_000, 0)
    time.sleep(5)
    log(
        f"character teleported to {distance(remote.get_character()['position']) / 1000:.3f} km"
    )
    farthest = 0.0
    for _ in range(4):
        result = remote.character_move_to(
            0, 520_000, 0, speed="fly", timeout=30, reach=5
        )
        r = distance(result["position"])
        farthest = max(farthest, r)
        log(
            f"character after flying outward: {r / 1000:.4f} km ({result.get('reason')})"
        )
    assert farthest <= 500_010, farthest
    remote.character_teleport(*home)
    time.sleep(5)


def tables(remote) -> list[dict]:
    return remote.find_nearby_blocks(
        subtype="LargeBlockConsole", max_distance=400, limit=20
    )


def stand_by(remote, table: dict) -> None:
    """Teleports the character into a free cell next to the table"""
    x, y, z = table["min"]
    grid_id = table["gridId"]
    for cell in ((x + 1, y, z), (x - 1, y, z), (x, y, z + 1), (x, y, z - 1)):
        result = remote.call(
            [CallOp.cube_exists(grid_id, cell), CallOp.grid_to_world(grid_id, cell)]
        )
        exists, world = result.call(0), result.call(1)
        exists = exists.get("exists") if isinstance(exists, dict) else exists
        world = (
            world.get("world") or world.get("position")
            if isinstance(world, dict)
            else world
        )
        if exists:
            continue
        remote.character_teleport(*world)
        time.sleep(4)
        position = remote.get_character()["position"]
        log(
            f"standing {math.dist(position, table['worldPosition']):.1f} m from {table['customName']}"
        )
        return
    raise AssertionError("No free cell next to the table")


# Color.Cyan packed as the game sends it, alpha in the high byte
CYAN = 0xFFFFFF00


def starter_gps(remote) -> dict | None:
    """The GPS the plugin gave the player for its starter ship"""
    entries = remote.list_gps().get("entries", [])
    return next((e for e in entries if e["name"] == "Starter Test"), None)


def ship_reply(remote, text: str = "!oct ship") -> str:
    lines = rig.chat(remote, text, 5)
    log(f"{text} -> {lines}")
    return " ".join(lines)


def with_config(remote, **values) -> None:
    """The world's config with some options replaced, reloaded"""
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for key, value in values.items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        if not count:
            text = text.replace(
                "</OctahedronConfig>", f"  <{key}>{value}</{key}>\n</OctahedronConfig>"
            )
    rig.write_plugin_config(text)
    log(f"config {values or 'of the world'}: {rig.chat(remote, '!oct reload', 3)}")


def test_starter_ship(remote, home) -> None:
    table = min(tables(remote), key=lambda b: b["distance"])
    log(f"nearest table: {table['customName']} {table['distance']:.0f} m away")
    assert "Stand within" in ship_reply(remote), "refused away from the tables"

    stand_by(remote, table)
    before = {g["entityId"] for g in remote.list_grids()}
    assert "waits outside" in ship_reply(remote)
    time.sleep(5)
    new = [g for g in remote.list_grids() if g["entityId"] not in before]
    log(f"new grids: {new}")
    assert len(new) == 1 and new[0]["name"] == "Starter Test", new
    ship = remote.get_grid(new[0]["entityId"])
    log(
        f"ship at {math.dist(ship['position'], table['worldPosition']):.0f} m from the table, owners {ship.get('bigOwners')}"
    )
    gps = starter_gps(remote)
    log(f"GPS: {gps}")
    assert (
        gps and gps["showOnHud"] and gps["alwaysVisible"] and gps["color"] == CYAN
    ), gps
    assert "ready in" in ship_reply(remote), "cooldown"

    with_config(remote, StarterShipCooldownMinutes=0, StarterShipMaxShipPcu=5)
    assert "at most 5" in ship_reply(remote)
    with_config(remote, StarterShipCooldownMinutes=0, GroupPcuLimit=6)
    assert "at most 6" in ship_reply(remote), "the group cap caps a starter ship too"
    # The player has the first ship's 62 PCU and the Hangar ship: at the limit refused,
    # under it allowed
    owned = 62 + HANGAR_PCU
    with_config(remote, StarterShipCooldownMinutes=0, FreeBuildPcuLimit=owned)
    reply = ship_reply(remote)
    assert "shipyard" in reply and f"{owned:,}" in reply, "Hangar counted"
    with_config(remote, StarterShipCooldownMinutes=0, FreeBuildPcuLimit=owned + 1)
    assert "waits outside" in ship_reply(remote), "second ship without a cooldown"
    with_config(remote)
    remote.character_teleport(*home)
    time.sleep(3)


def cap_grids(remote) -> list[dict]:
    return [g for g in remote.list_grids() if g["name"] == "Cap Test"]


def group_refusals() -> int:
    return rig.server_game_log().count("may have at most")


def refused_since(count: int, timeout: float = 60) -> bool:
    """Whether the server logged a group cap refusal since there were count; its log
    reaches the file some seconds late"""
    deadline = time.monotonic() + timeout
    while group_refusals() <= count:
        if time.monotonic() > deadline:
            return False
        time.sleep(2)
    return True


def test_group_cap(remote, home) -> None:
    """Pasting, a rotor's new head and placing a block, each refused at the cap and
    allowed under it. With creative tools a placed block has its full PCU at once."""
    x, y, z = home
    remote.character_teleport(x + 3000, y, z)
    time.sleep(5)
    position = remote.get_character()["position"]
    spot = (position[0], position[1], position[2] - 25)
    remote.set_admin_flag("creativeTools", True)

    refusals = group_refusals()
    with_config(remote, GroupPcuLimit=100)
    try:
        remote.paste_blueprint(xml=CAP_BLUEPRINT, position=spot, timeout_seconds=15)
    except Exception as e:  # the server never answers a refused paste
        log(f"paste at cap 100: {e}")
    assert refused_since(refusals) and not cap_grids(remote), "116 PCU refused at 100"
    with_config(remote, GroupPcuLimit=200)
    remote.paste_blueprint(xml=CAP_BLUEPRINT, position=spot)
    time.sleep(3)
    (grid,) = cap_grids(remote)
    grid_id = grid["entityId"]
    log(f"pasted at cap 200: {grid}")

    def grids() -> set[int]:
        return {g["entityId"] for g in remote.list_grids()}

    def build(cell, subtype="LargeBlockArmorBlock") -> None:
        report = remote.character_build_block(grid_id, cell, subtype)
        log(f"build {subtype} at {cell}: sent {report.get('sent')}")
        time.sleep(5)

    # A rotor base (100 PCU) fits under 216, its new head (1) doesn't
    before = grids()
    refusals = group_refusals()
    with_config(remote, GroupPcuLimit=216)
    build((2, -1, 0), "LargeStator")
    assert refused_since(refusals) and grids() == before, "no head at the cap"
    with_config(remote, GroupPcuLimit=400)
    build((-1, -1, 0), "LargeStator")
    (head,) = grids() - before
    log(f"rotor head grid {head} at cap 400")

    # 116 + two rotor bases + one head: 317. An armor block (1 PCU) doesn't fit under
    # 317 and does under 318.
    def blocks() -> int:
        return remote.list_cubes(grid_id)["total"]

    count = blocks()
    refusals = group_refusals()
    with_config(remote, GroupPcuLimit=317)
    build((1, -2, 0))
    assert blocks() == count and refused_since(refusals), "placing refused"
    with_config(remote, GroupPcuLimit=318)
    build((1, -2, 0))
    assert blocks() == count + 1, "placing allowed"

    remote.set_admin_flag("creativeTools", False)
    with_config(remote)
    remote.character_teleport(*home)
    time.sleep(3)


def main() -> None:
    grids = [
        rig.grid(n, i, rig.small_ship_blocks(), p, velocity=v)
        for n, (i, p, v) in EDGE_GRIDS.items()
    ]
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(
        grids, settings={"EconomyTickInSeconds": 60}, table_blueprint=BLUEPRINT
    )
    rig.write_plugin_config()
    (rig.SERVER_DATA / "Hangar.cfg").write_text(HANGAR_CONFIG, encoding="utf-8")
    rig.prepare_server(extra_dev_folders=[HANGAR])
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    home = remote.get_character()["position"]
    log(f"spawned at {home}, {distance(home) / 1000:.1f} km from the center")

    check_edge(edge_report(remote))
    test_starter_ship(remote, home)
    test_group_cap(remote, home)
    test_character_edge(remote, home)

    def edge_done() -> bool:
        report = edge_report(remote)
        check_edge(report)
        km, out, _ = report.get("Edge 480 out", (0.0, 0.0, 0.0))
        return "Edge 445 out" in report and km > 499.9 and out < 1

    rig.wait_until(edge_done, 600, "a grid entering and one braked at the edge", 15)

    rig.stop_client()
    rig.stop_server()
    log("server stopped and saved")


if __name__ == "__main__":
    main()
