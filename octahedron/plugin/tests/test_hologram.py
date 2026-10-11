"""Screenshots of the station hologram tables after the plugin fitted their blueprints:
from the side and from above, for a blueprint with a cockpit and one without. Saved
under ~/.se-test/<task>-shots. Needs a rendering client (5.5 GiB).

    uv run --no-project --with httpx python tests/test_hologram.py [--keep]

--keep leaves the server and the client running.
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

import rig

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
        table_block(TABLES["with cockpit"], WITH_COCKPIT),
        table_block(TABLES["without cockpit"], WITHOUT_COCKPIT),
    ],
    BENCH_AT,
).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
SHOTS = rig.TEST_ROOT / f"{rig.TASK}-shots"
# The character's eye above its position
EYE = 1.6


def shoot(remote, name: str, eye, target) -> Path:
    """Floats the character so that its eye is at eye, looks at target, saves a shot"""
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

    for kind, (x, y, z) in TABLES.items():
        table = [a + b * 2.5 for a, b in zip(BENCH_AT, (x, y, z))]
        center = [table[0], table[1] - 0.05, table[2]]

        def at(dx, dy, dz):
            return [center[0] + dx, center[1] + dy, center[2] + dz]

        name = kind.replace(" ", "-")
        # The table's forward is the bench's -Z, its operator side
        shoot(remote, f"{name}-side", at(2.0, 0.4, 0), center)
        shoot(remote, f"{name}-front", at(0, 0.4, -2.0), center)
        shoot(remote, f"{name}-above", at(0, 1.6, 0.25), center)
    if "--keep" not in sys.argv:
        rig.stop_client()
        rig.stop_server()


if __name__ == "__main__":
    main()
