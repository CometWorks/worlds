"""Screenshots of hologram tables after the plugin fitted their blueprints, at a station's
lounge table (a column and a truss pillar above it) and hangar table (a column above it)
and on a bench in open space, for a blueprint with a cockpit and one without; then the
starter ship's GPS on the HUD, and that it outlives a relog and a server restart. Shots
under ~/.se-test/<task>-shots. Needs a rendering client (5.5 GiB).

    uv run --no-project --with httpx python tests/test_hologram.py [--keep]

--keep leaves the server and the client running.
"""

from __future__ import annotations

import re
import sys
import time
from pathlib import Path

import rig
from se_remote import CallOp

ARMOR = ("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock")


def armor(pos) -> str:
    return rig.block(*ARMOR, pos)


# A cockpit turned to face the grid's left (-X), the hull behind it along +X, a fin on top
# at the back and a block on the ship's right (-Z) at the front: nose along the table's
# forward and fin up when the cockpit orients the hologram
WITH_COCKPIT = rig.grid(
    "Hologram Cockpit",
    777000133000201,
    [
        rig.block(
            "MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0), forward="Left"
        )
    ]
    + [armor((x, 0, 0)) for x in range(1, 6)]
    + [armor((5, 1, 0)), armor((5, 2, 0)), armor((1, 0, -1))],
    tag="MyObjectBuilder_CubeGrid",
)
# No cockpit: the grid's own frame, forward -Z, the hull along +Z, the fin at the back
# (+Z) and a block on the right (+X) at the front
WITHOUT_COCKPIT = rig.grid(
    "Hologram Plain",
    777000133000202,
    [armor((0, 0, z)) for z in range(0, 7)]
    + [armor((0, 1, 6)), armor((0, 2, 6)), armor((1, 0, 0))],
    tag="MyObjectBuilder_CubeGrid",
)

# A bench in open space, out of gravity and away from the station's clutter: a floor, a
# battery and two hologram tables of the stations' owner, 5 m apart, one per blueprint
NPC = 152137438111824285
BENCH_AT = (0, 0, 160_000)
TABLES = {"with cockpit": (-2, 1, 0), "without cockpit": (2, 1, 0)}


def table_block(pos, blueprint: str) -> str:
    return rig.block(
        "MyObjectBuilder_Projector",
        "LargeBlockConsole",
        pos,
        f"<Owner>{NPC}</Owner><ShareMode>All</ShareMode><Enabled>true</Enabled>"
        f"<ProjectedGrids>{blueprint}</ProjectedGrids>",
    )


BENCH = rig.grid(
    "Hologram Bench",
    777000133000200,
    [armor((x, 0, z)) for x in range(-3, 4) for z in range(-2, 3)]
    + [
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 1, 2),
            f"<Owner>{NPC}</Owner><Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>",
        ),
        # Keeps the character upright, as on a station, so the views aren't rolled
        rig.block(
            "MyObjectBuilder_GravityGenerator",
            "",
            (0, 1, -2),
            f"<Owner>{NPC}</Owner><Enabled>true</Enabled>",
        ),
        table_block(TABLES["with cockpit"], WITH_COCKPIT),
        table_block(TABLES["without cockpit"], WITHOUT_COCKPIT),
    ],
    BENCH_AT,
).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
SHOTS = rig.TEST_ROOT / f"{rig.TASK}-shots"
# The character's eye above its position
EYE = 1.6
# The middle of the free space over a table, above its block's center: halfway between
# the table's top (-0.33 m) and the top of the block (1.25 m)
HOLOGRAM = 0.46
# Every station is the same blueprint: in its grid's cells, a lounge table with a column,
# a truss pillar and a corner above it, and a hangar table with a column above it
STATION_TABLES = {"lounge": (3, 7, 0), "hangar": (-6, 1, 0)}
# Where to look from, in cells from the table: free cells around each
# The column fills the cell above a table, so there is no view from above at a station:
# the bench shows that one
STATION_VIEWS = {
    "lounge": {"side": (0, 0.2, -1.1), "back": (1.1, 0.2, 0)},
    "hangar": {"side": (0, 0.2, -1.1), "other-side": (0, 0.2, 1.1)},
}
CYAN = 0xFFFFFF00


def shoot(remote, name: str, eye, target, fly: bool = False) -> Path:
    """Puts the character's eye at eye (flying there when it is off the floor), looks at
    target and saves a shot"""
    if fly:
        remote.character_move_to(*eye, speed="fly", timeout=20, reach=0.3)
    else:
        if remote.get_character()["jetpack"]:
            remote.key("X")
        remote.character_teleport(eye[0], eye[1] - EYE, eye[2])
    time.sleep(3)
    remote.character_look_at(*target, tolerance=0.5)
    time.sleep(2)
    path = SHOTS / f"{name}.png"
    remote.screenshot_save(str(path), 1280, 720)
    # The game writes the file a moment later
    time.sleep(2)
    print(f"{name}: {path}", flush=True)
    return path


def station_frame(remote, grid_id: int):
    """World position of a station grid cell, fractions allowed"""
    call = [
        CallOp.grid_to_world(grid_id, c)
        for c in ((0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1))
    ]
    o, x, y, z = (remote.call([c]).call(0)["world"] for c in call)
    axes = [[b - a for a, b in zip(o, v)] for v in (x, y, z)]

    def world(cell):
        return [o[i] + sum(cell[k] * axes[k][i] for k in range(3)) for i in range(3)]

    return world


def station_shots(remote) -> None:
    table = min(
        remote.find_nearby_blocks(
            subtype="LargeBlockConsole", max_distance=400, limit=20
        ),
        key=lambda b: b["distance"],
    )
    world = station_frame(remote, table["gridId"])
    for kind, cell in STATION_TABLES.items():
        center = world([cell[0], cell[1] + HOLOGRAM / 2.5, cell[2]])
        for view, offset in STATION_VIEWS[kind].items():
            eye = world([c + o for c, o in zip(cell, offset)])
            shoot(remote, f"station-{kind}-{view}", eye, center)


def gps_check(remote, name: str, label: str) -> dict:
    entries = remote.list_gps().get("entries", [])
    gps = next((e for e in entries if e["name"] == name), None)
    print(f"GPS {label}: {gps}", flush=True)
    assert (
        gps and gps["showOnHud"] and gps["alwaysVisible"] and gps["color"] == CYAN
    ), gps
    return gps


def starter_ship_gps(remote) -> None:
    """Builds a starter ship at the lounge table, shoots its cyan GPS on the HUD and checks
    that the GPS outlives a relog and a server restart"""
    table = min(
        remote.find_nearby_blocks(
            subtype="LargeBlockConsole", max_distance=400, limit=20
        ),
        key=lambda b: b["distance"],
    )
    world = station_frame(remote, table["gridId"])
    lounge = STATION_TABLES["lounge"]
    remote.character_teleport(*world([lounge[0], lounge[1] - 0.45, lounge[2] - 1]))
    time.sleep(3)
    reply = " ".join(rig.chat(remote, "!oct ship", 5))
    print(reply, flush=True)
    name = re.search(r"Your ship '(.*?)'", reply)[1]
    time.sleep(3)
    gps = gps_check(remote, name, "after the ship")
    remote.character_look_at(gps["x"], gps["y"], gps["z"], tolerance=1)
    time.sleep(2)
    remote.screenshot_save(str(SHOTS / "gps-hud.png"), 1280, 720)
    time.sleep(2)

    rig.stop_client()
    time.sleep(40)
    remote = rig.start_client(render=True)
    gps_check(remote, name, "after a relog")

    rig.stop_client()
    rig.stop_server()
    rig.start_server()
    time.sleep(40)
    remote = rig.start_client(render=True)
    gps_check(remote, name, "after a server restart")


def main() -> None:
    SHOTS.mkdir(parents=True, exist_ok=True)
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world([BENCH], table_blueprint=[WITH_COCKPIT, WITHOUT_COCKPIT])
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client(render=True)
    if remote.get_view()["thirdPerson"]:
        remote.key("V")
    remote.character_set_dampeners(True)
    print(rig.chat(remote, "!oct holograms", 5), flush=True)
    home = remote.get_character()["position"]

    station_shots(remote)
    remote.character_teleport(*home)
    time.sleep(3)
    for kind, (x, y, z) in TABLES.items():
        table = [a + b * 2.5 for a, b in zip(BENCH_AT, (x, y, z))]
        center = [table[0], table[1] + HOLOGRAM, table[2]]

        def at(dx, dy, dz):
            return [center[0] + dx, center[1] + dy, center[2] + dz]

        name = kind.replace(" ", "-")
        # The table's forward is the bench's -Z, its operator side
        shoot(remote, f"{name}-side", at(3.5, 0.3, 0), center)
        shoot(remote, f"{name}-above", at(0, 3.5, 0.3), center, fly=True)
    remote.character_teleport(*home)
    time.sleep(3)
    starter_ship_gps(remote)
    if "--keep" not in sys.argv:
        rig.stop_client()
        rig.stop_server()


if __name__ == "__main__":
    main()
