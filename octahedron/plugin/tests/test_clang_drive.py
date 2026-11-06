"""Clang drives and ramming on the test server (SE-966), without a client. tests/clang-probe pushes ships beyond
their thrust as a clang drive would and reports what the server has for them; the octahedron.cfg defaults apply
(factor 2, 6 frames, 3 m/s², 20 m/s). The world runs Relative Top Speed with its Octahedron config, as the
live one does: above its cruise speed it brakes a ship with a drag force (SE-1157).

Pushes and jumps are impulses straight on the Havok body, as a clang drive's contacts are. Switched off, with
a log line: a ship pushed at 3x its thrust over mass, and at 1.5x, one whose speed jumps by 3 s of its thrust
in one frame, a heavy ship flying at 50 m/s into Alpha Station's safe zone and onto a wall there, the
same without thrusters, and one ramming a ship at rest there. The holes of SE-1024: a ship pushed at 3x with a
loose block rattling in a cage on it (not a collision), one pushed at 2.5x with a gravity generator on it
(generators don't pull grids), and a player's grid pushed at 3x while it holds a bigger NPC grid (towing it
doesn't make the ship an NPC's). Left on: a ship pushed at 0.8x, one pushed at 3x through AddForce, as a mod's
force is applied, one at 30 m/s jumping by 0.1 s of its thrust in one frame (the window takes the spike), one
jumping by 0.3 s of it from rest (too slow to count), one at full thrust override, an NPC's ship pushed at 3x,
the ramming ship hitting a wall outside safe zones, the ship it rams in the zone (hit by a faster one), a heavy
ship falling toward the planet faster than twice its thrust allows, the push at 3x and the jump by 3 s made by
setting the velocity, as a mod moving the ship does (a Trade Lane pulling it in or dropping it out, SE-977,
SE-978), and DIS-0059's false positives (SE-1157): a ship going full throttle the other way 0.5 s after
flying at Relative Top Speed's boost ceiling, and 1 s after flying near the speed where its thrust holds the
drag, and a small grid missile at 355 m/s thrusting in two directions across its flight. The merge block
drive counts although its merge blocks set the velocity: their pull is its means.

Relative Top Speed comes from OCT_WORKSHOP (the Workshop content folder, as tests/test_projection_limits.py
takes it):

    steamcmd +force_install_dir DIR +login anonymous +workshop_download_item 244850 1359618037 +quit

Two clang drives from the Workshop are switched off once started, a sliding door one and a merge block one,
when CLANG_BLUEPRINTS names a steamcmd folder that has them (see DRIVES):

    steamcmd +force_install_dir DIR +login anonymous +workshop_download_item 244850 2882012087 +workshop_download_item 244850 2889439688 +quit
    CLANG_BLUEPRINTS=DIR/steamapps/workshop/content/244850 OCT_WORKSHOP=DIR/steamapps/workshop/content/244850 \
        OCT_TASK=SE-966 uv run --no-project --with httpx python tests/test_clang_drive.py
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-966")
import rig  # noqa: E402
from test_client import log  # noqa: E402

PROBE = (
    "clang-probe",
    rig.PLUGIN / "tests" / "clang-probe",
    "ClangProbe.xml",
    "clang-probe",
)
LOCAL_WORLD_MODS = (
    "local-world-mods",
    rig.PLUGIN / "tests" / "local-world-mods",
    "LocalWorldMods.xml",
    "local-world-mods",
)
BASE = 777000966000000
SPACE = (100_000, -300_000, 0)


def alpha_zone() -> tuple[float, float, float]:
    """The center of Alpha Station's safe zone (250 m) in the world the test starts from: it depends on the seed"""
    text = (rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    zone = re.search(
        r'xsi:type="MyObjectBuilder_SafeZone">(?:(?!</MyObjectBuilder_EntityBase>).)*?'
        r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"(?:(?!</MyObjectBuilder_EntityBase>).)*?'
        r"<DisplayName>Alpha Station</DisplayName>",
        text,
        re.S,
    )
    return tuple(float(v) for v in zone.groups())


ZONE = alpha_zone()
RAM_SPEED = 50.0
# A speed drop within 0.1 s that only a hit explains: the rams' thrust changes 0.5 m/s in that time
HIT_DROP = 5.0
# Heavy armor on the rams, so that stopping dead is far beyond twice their thrust
HEAVY = 20
# The C frame's piston extends into the frame
CLANG_SPEED = float(os.environ.get("CLANG_SPEED", "2"))
# name -> (expected off, has a thruster, owner is the NPC)
SHIPS = {
    "CL Push 3": (True, True, False),
    "CL Push 1.5": (True, True, False),
    "CL Push 0.8": (False, True, False),
    "CL Force 3": (False, True, False),
    "CL Push 3 NPC": (False, True, True),
    "CL Spike 3": (True, True, False),
    "CL Spike 0.1": (False, True, False),
    "CL Spike 0.3 Slow": (False, True, False),
    "CL Thrust": (False, True, False),
    "CL Drag 3": (False, True, False),
    "CL Set 3": (False, True, False),
    "CL Ram Zone": (True, True, False),
    "CL Ram Crash": (False, True, False),
    "CL Ram Bare": (True, False, False),
    "CL Ram Victim": (True, True, False),
    "CL Victim": (False, True, False),
    "CL Fall": (False, True, False),
    "CL Push 3 Junk": (True, True, False),
    "CL Push 2.5 Gravity": (True, True, False),
    "CL Push 3 Tow": (True, True, False),
    "CL Reverse 0.5": (False, True, False),
    "CL Reverse 1": (False, True, False),
    "CL Missile": (False, True, False),
}
# Relative Top Speed's boost ceilings in config/relative-top-speed.xml: large grids 300 m/s, cruising at 250
# below 500 t; small grids 360, cruising at 300 below 50 t
RTS = 1359618037
WORKSHOP = Path(
    os.environ.get(
        "OCT_WORKSHOP", rig.HOME / ".steam/steam/steamapps/workshop/content/244850"
    )
)
REVERSE_SPEED = 299.0
MISSILE_SPEED = 355.0
# A player's identity, added to the world copy: it has only NPCs
PLAYER = 144115188075855999
TOW = "CL Push 3 Tow"
# The log names a ship by its biggest grid: the tug's by the wreck it holds
LOGGED = {TOW: TOW + " Wreck"}
RAMS = ("CL Ram Zone", "CL Ram Crash", "CL Ram Bare", "CL Ram Victim")
CLANG = "CL Clang"
CLANG_AT = (SPACE[0], SPACE[1] + 5000, SPACE[2])
# Workshop clang drives, each under CLANG_BLUEPRINTS/<item>/bp.sbc as steamcmd leaves them:
# name -> (item, the block the probe starts 5 s in: a timer it triggers or a piston it reverses)
DRIVES = {
    # Zer0's "Piston Door Clang Drive": a piston pushing its head into a sliding door
    "CL Door": (2882012087, "Piston"),
    # Capt_Derpy's "Clang Drives Demonstration", its CG 3: pistons with merge blocks on their heads
    "CL Merge": (2889439688, "Timer Block  - CG 3"),
}
BLUEPRINTS = os.environ.get("CLANG_BLUEPRINTS")
LOG_MARK = "Clang drive: "


def npc_identity() -> int:
    """The founder of the world's NPC faction, which runs the stations"""
    text = (rig.WORLD_SOURCE / "Sandbox.sbc").read_text(encoding="utf-8")
    return int(re.search(r"<Tag>OCT</Tag>.*?<PlayerId>(\d+)", text, re.S)[1])


def ship(
    name: str, index: int, position, velocity=(0, 0, 0), heavy: int = 0, **kwargs
) -> str:
    _, thruster, npc = SHIPS[name]
    own = f"<Owner>{npc_identity()}</Owner><ShareMode>None</ShareMode>" if npc else ""
    blocks = [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0), own),
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 0, 1),
            f"<CurrentStoredPower>3</CurrentStoredPower>{own}",
        ),
    ]
    if thruster:
        # The flame backward, the push forward
        blocks.append(
            rig.block(
                "MyObjectBuilder_Thrust",
                "LargeBlockSmallThrust",
                (0, 0, 2),
                own,
                forward="Backward",
            )
        )
    else:
        blocks.append(
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 2))
        )
    blocks += [
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, y, 1))
        for x in range(1, 6)
        for y in range(6)
    ][:heavy]
    return rig.grid(name, BASE + index, blocks, position, velocity=velocity, **kwargs)


def reverse(name: str, index: int, position, speed: float) -> str:
    """A heavy ship with a thruster each way, flying forward"""
    blocks = [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0)),
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 0, 1),
            "<CurrentStoredPower>3</CurrentStoredPower>",
        ),
        rig.block(
            "MyObjectBuilder_Thrust",
            "LargeBlockSmallThrust",
            (0, 0, 2),
            forward="Backward",
        ),
        # The flame forward, the push backward; two blocks long
        rig.block("MyObjectBuilder_Thrust", "LargeBlockSmallThrust", (0, 0, -2)),
    ] + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, 0, 1))
        for x in range(1, 5)
    ]
    return rig.grid(name, BASE + index, blocks, position, velocity=(0, 0, -speed))


def missile(name: str, index: int, position) -> str:
    """A small grid with a battery and two thrusters, forward and up, flying left near the small grid ceiling"""
    blocks = [
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "SmallBlockBatteryBlock",
            (0, 0, 0),
            "<CurrentStoredPower>0.5</CurrentStoredPower>",
        ),
        rig.block(
            "MyObjectBuilder_Thrust",
            "SmallBlockSmallThrust",
            (1, 0, 3),
            forward="Backward",
        ),
        rig.block(
            "MyObjectBuilder_Thrust",
            "SmallBlockSmallThrust",
            (1, 2, 1),
            forward="Down",
            up="Forward",
        ),
    ]
    return rig.grid(
        name,
        BASE + index,
        blocks,
        position,
        velocity=(-MISSILE_SPEED, 0, 0),
        size="Small",
    )


def junk(name: str, index: int, position) -> list[str]:
    """The ship, with a closed light armor cage beside its battery and a loose small grid block in it"""
    cage = (0, 3, 1)
    grid = ship(name, index, position)
    walls = "".join(
        rig.block(
            "MyObjectBuilder_CubeBlock",
            "LargeBlockArmorBlock",
            (cage[0] + x, cage[1] + y, cage[2] + z),
        )
        for x in (-1, 0, 1)
        for y in (-1, 0, 1)
        for z in (-1, 0, 1)
        if (x, y, z) != (0, 0, 0)
    )
    inside = [position[k] + 2.5 * cage[k] for k in range(3)]
    loose = rig.grid(
        "Loose Block",
        BASE + index + 500,
        [rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (0, 0, 0))],
        inside,
        size="Small",
    )
    return [grid.replace("</CubeBlocks>", walls + "</CubeBlocks>", 1), loose]


def tow(name: str, index: int, position) -> list[str]:
    """A player's ship with a rotor on its top, the rotor's head an NPC's grid with more blocks: a tug
    holding a wreck"""
    stator, rotor = BASE + index * 10 + 1, BASE + index * 10 + 2
    own = f"<Owner>{PLAYER}</Owner><ShareMode>None</ShareMode>"
    tug = ship(name, index, position).replace(
        "</CubeBlocks>",
        rig.block(
            "MyObjectBuilder_MotorStator",
            "LargeStator",
            (0, 1, 0),
            f"<EntityId>{stator}</EntityId>{own}<TopBlockId>{rotor}</TopBlockId>",
        )
        + "</CubeBlocks>",
        1,
    )
    # The player's blocks: the cockpit, battery and thruster ship() left unowned
    tug = re.sub(
        r'(<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_(?:Cockpit|BatteryBlock|Thrust)">)',
        rf"\1{own}",
        tug,
    )
    npc = f"<Owner>{npc_identity()}</Owner><ShareMode>None</ShareMode>"
    head = [
        rig.block(
            "MyObjectBuilder_MotorRotor",
            "LargeRotor",
            (0, 0, 0),
            f"<EntityId>{rotor}</EntityId><ParentEntityId>{stator}</ParentEntityId>",
        ),
        rig.block(
            "MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 1, 0), npc
        ),
    ] + [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 2, z))
        for x in (-1, 0, 1)
        for z in (-1, 0, 1)
    ]
    wreck = rig.grid(
        name + " Wreck",
        BASE + index + 600,
        head,
        (position[0], position[1] + 5, position[2]),
    )
    return [tug, wreck]


def gravity(name: str, index: int, position) -> str:
    """A heavy ship with a working gravity generator on it"""
    generator = rig.block("MyObjectBuilder_GravityGenerator", "", (0, 1, 1))
    return ship(name, index, position, heavy=HEAVY).replace(
        "</CubeBlocks>", generator + "</CubeBlocks>", 1
    )


def wall(name: str, index: int, position, **kwargs) -> str:
    """A static heavy armor slab across the Z axis, three blocks thick"""
    return rig.grid(
        name,
        BASE + index,
        [
            rig.block(
                "MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, y, z)
            )
            for x in range(-4, 5)
            for y in range(-4, 5)
            for z in range(-2, 1)
        ],
        position,
        **kwargs,
    ).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")


def blueprint(
    path: Path,
    name: str,
    at,
    base: int,
    start: str | None = None,
    piston_velocity: float | None = None,
    thruster: bool = False,
) -> list[str]:
    """The grids of a blueprint, its first one at the given position and named name, the others "name Head",
    owner and builder dropped and entity ids renumbered from base. The block named start is renamed "CL Start"
    for the probe to start the drive with. For Viktor's "Clang with Piston" (se-clang-slayer), a piston on a
    C frame pushing its head into the frame: the piston's speed, and a thruster added to the frame.
    """
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"<(Owner|BuiltBy)>\d+</\1>", "", text)
    # A blueprint saved from a projection has no physics
    text = text.replace("<CreatePhysics>false</CreatePhysics>", "")
    ids = dict.fromkeys(re.findall(r"<EntityId>(\d+)</EntityId>", text))
    for i, old in enumerate(ids):
        text = text.replace(old, str(base + i))
    if start:
        text = text.replace(
            f"<CustomName>{start}</CustomName>", "<CustomName>CL Start</CustomName>"
        )
    if piston_velocity is not None:
        text = re.sub(
            r"<Velocity>[^<]*</Velocity>",
            f"<Velocity>{piston_velocity}</Velocity>",
            text,
        )
    grids = re.findall(r"<CubeGrid>(.*?)</CubeGrid>", text, re.S)
    main = [
        float(v)
        for v in re.search(
            r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', grids[0]
        ).groups()
    ]
    found = []
    for i, body in enumerate(grids):
        here = [
            float(v)
            for v in re.search(
                r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', body
            ).groups()
        ]
        moved = [at[k] + here[k] - main[k] for k in range(3)]
        body = re.sub(r"<Position [^>]*/>", rig._vec("Position", moved), body, count=1)
        body = re.sub(
            r"<DisplayName>[^<]*</DisplayName>",
            f"<DisplayName>{name if i == 0 else name + ' Head'}</DisplayName>",
            body,
        )
        if i == 0 and thruster:
            body = body.replace(
                "</CubeBlocks>",
                rig.block(
                    "MyObjectBuilder_Thrust",
                    "LargeBlockSmallThrust",
                    (1, 0, 2),
                    forward="Backward",
                )
                + "</CubeBlocks>",
            )
        found.append(
            f'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">{body}</MyObjectBuilder_EntityBase>'
        )
    return found


def world_grids() -> list[str]:
    grids = []
    spaced = [
        "CL Push 3",
        "CL Push 1.5",
        "CL Push 0.8",
        "CL Force 3",
        "CL Push 3 NPC",
        "CL Spike 3",
        "CL Spike 0.1",
        "CL Spike 0.3 Slow",
        "CL Thrust",
        "CL Drag 3",
        "CL Set 3",
    ]
    for i, name in enumerate(spaced):
        # The spike that is to be absorbed comes on a ship already moving: a slow one doesn't count
        velocity = (0, 0, 30) if name == "CL Spike 0.1" else (0, 0, 0)
        grids.append(
            ship(name, 30 + i, (SPACE[0] + 1000 * i, SPACE[1], SPACE[2]), velocity)
        )
    # Ramming toward -Z at 50 m/s: in the safe zone from outside it, onto a wall 105 m ahead; the crash 45 m ahead
    ram = (0, 0, -RAM_SPEED)
    grids.append(
        ship("CL Ram Zone", 10, (ZONE[0], ZONE[1], ZONE[2] + 300), ram, heavy=HEAVY)
    )
    grids.append(wall("CL Wall Zone", 11, (ZONE[0], ZONE[1], ZONE[2] + 195)))
    grids.append(
        ship(
            "CL Ram Bare", 12, (ZONE[0] + 30, ZONE[1], ZONE[2] + 300), ram, heavy=HEAVY
        )
    )
    grids.append(wall("CL Wall Bare", 13, (ZONE[0] + 30, ZONE[1], ZONE[2] + 195)))
    crash = (SPACE[0], SPACE[1] - 5000, SPACE[2])
    grids.append(
        ship("CL Ram Crash", 14, (crash[0], crash[1], crash[2] + 45), ram, heavy=HEAVY)
    )
    grids.append(wall("CL Wall Crash", 15, crash))
    # 68 km from the planet's center, above its highest hills
    grids.append(ship("CL Fall", 16, (39_260, 39_260, 39_260), heavy=30))
    # A lighter ship at rest in the zone, rammed: it flies off faster than the rule allows
    grids.append(
        ship(
            "CL Ram Victim",
            17,
            (ZONE[0] + 60, ZONE[1], ZONE[2] + 300),
            ram,
            heavy=HEAVY,
        )
    )
    grids.append(
        ship("CL Victim", 18, (ZONE[0] + 60, ZONE[1], ZONE[2] + 195), heavy=HEAVY // 2)
    )
    # The new cases of SE-1024 and SE-1157, in a row 10 km further out
    row = (SPACE[0], SPACE[1] + 10_000, SPACE[2])
    grids += junk("CL Push 3 Junk", 20, row)
    grids.append(gravity("CL Push 2.5 Gravity", 21, (row[0] + 1000, row[1], row[2])))
    grids += tow(TOW, 25, (row[0] + 2000, row[1], row[2]))
    # Flat out at the boost ceiling, slowing in the drag; and near the speed where its thrust holds
    # the drag, which LJS flew at
    grids.append(
        reverse("CL Reverse 0.5", 22, (row[0] + 3000, row[1], row[2]), REVERSE_SPEED)
    )
    grids.append(
        reverse("CL Reverse 1", 23, (row[0] + 4000, row[1], row[2]), REVERSE_SPEED - 24)
    )
    # Flying toward -X, above the row
    grids.append(missile("CL Missile", 24, (row[0] + 5000, row[1] + 1000, row[2])))
    grids += blueprint(
        PROBE[1] / "ClangWithPiston.sbc",
        CLANG,
        CLANG_AT,
        BASE + 100,
        piston_velocity=CLANG_SPEED,
        thruster=True,
    )
    for i, (name, (item, start)) in enumerate(DRIVES.items() if BLUEPRINTS else []):
        grids += blueprint(
            Path(BLUEPRINTS) / str(item) / "bp.sbc",
            name,
            (CLANG_AT[0] + 3000 * (i + 1), CLANG_AT[1], CLANG_AT[2]),
            # Every block has an entity id too
            BASE + 100_000 * (i + 1),
            start=start,
        )
    return grids


def with_relative_top_speed() -> bool:
    """Relative Top Speed in the world copy, with the Octahedron config in its storage, as build_world.py
    writes it. False when OCT_WORKSHOP doesn't have it"""
    if not (WORKSHOP / str(RTS)).is_dir():
        return False
    item = (
        f'<ModItem FriendlyName="Relative Top Speed"><Name>{RTS}.sbm</Name><PublishedFileId>{RTS}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(
            text.replace("<Mods />", f"<Mods>{item}</Mods>", 1), encoding="utf-8"
        )
    storage = rig.WORLD / "Storage" / f"{RTS}.sbm_RelativeTopSpeed"
    storage.mkdir(parents=True, exist_ok=True)
    (storage / "RelativeTopSpeed.cfg").write_text(
        (rig.PLUGIN.parent / "config" / "relative-top-speed.xml").read_text(
            encoding="utf-8"
        ),
        encoding="utf-8",
    )
    mods = rig.SERVER_DATA / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    link = mods / f"{RTS}.sbm"
    link.unlink(missing_ok=True)
    link.symlink_to(WORKSHOP / str(RTS))
    return True


def with_player() -> None:
    """A player's identity in the world copy, for the tug: blocks of an unknown owner come out unowned"""
    path = rig.WORLD / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    path.write_text(
        text.replace(
            "<Identities>",
            f"<Identities><MyObjectBuilder_Identity><IdentityId>{PLAYER}</IdentityId>"
            "<DisplayName>CL Pilot</DisplayName></MyObjectBuilder_Identity>",
            1,
        ),
        encoding="utf-8",
    )


def probe() -> dict[str, dict]:
    found = {}
    try:
        text = (rig.SERVER_DATA / "clang-probe.txt").read_text(encoding="utf-8")
    except FileNotFoundError:
        return found
    for line in text.splitlines():
        name, speed, fastest, power, producers, accel, gravity, *_ = line.split(";")
        found[name] = {
            "speed": float(speed),
            "fastest": float(fastest),
            "on": power == "True",
            "producers": int(producers),
            "accel": float(accel),
            "gravity": float(gravity),
        }
    return found


def slowest(name: str) -> float:
    """The lowest speed in the probe's samples"""
    text = (rig.SERVER_DATA / "clang-trace.txt").read_text(encoding="utf-8")
    return min(
        float(speed)
        for _, grid, speed, *_ in (line.split(";") for line in text.splitlines())
        if grid == name
    )


def largest_drop(name: str) -> float:
    """The largest speed drop between two of the probe's samples, 0.1 s apart"""
    last, drop = None, 0.0
    text = (rig.SERVER_DATA / "clang-trace.txt").read_text(encoding="utf-8")
    for line in text.splitlines():
        _, grid, speed, *_ = line.split(";")
        if grid == name:
            if last is not None:
                drop = max(drop, last - float(speed))
            last = float(speed)
    return drop


def switch_offs(kind: str = "switched off") -> dict[str, str]:
    """name -> the log line of its last switch-off, or with kind "left on" of the last time the rule let it be"""
    return {
        line.split(LOG_MARK, 1)[1].split(" (", 1)[0]: line
        for line in rig.server_game_log().splitlines()
        if LOG_MARK in line and f") {kind}, " in line
    }


def main() -> int:
    failures = []

    def check(ok: bool, what: str) -> None:
        log(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(world_grids())
    with_player()
    rts = with_relative_top_speed()
    check(rts, f"Relative Top Speed in {WORKSHOP}")
    rig.write_plugin_config()
    # The server can't download a world's mods without Steam: it takes them from its Mods folder
    rig.prepare_server(extra_dev_folders=[PROBE, LOCAL_WORLD_MODS])
    (rig.SERVER_DATA / "clang-probe.txt").unlink(missing_ok=True)
    (rig.SERVER_DATA / "clang-trace.txt").unlink(missing_ok=True)
    rig.start_server()
    log("server ready")
    rig.wait_until(lambda: set(SHIPS) <= set(probe()), 120, "every ship in the probe")
    expected = [n for n, (o, *_) in SHIPS.items() if o]
    if BLUEPRINTS:
        expected += list(DRIVES)

    # The rams hit within a second or two, the spikes and the drives' starts come 5 s in, the pushes act from the start
    def settled() -> bool:
        hit = all(largest_drop(n) > HIT_DROP for n in RAMS)
        return hit and set(expected) <= set(switch_offs())

    try:
        rig.wait_until(settled, 60, "the rams and the expected switch-offs", interval=1)
    except TimeoutError as error:
        log(str(error))
    # Long enough for any wrong switch-off to show
    time.sleep(10)

    seen, off, left = probe(), switch_offs(), switch_offs("left on")

    def show(name: str) -> dict | None:
        state = seen.get(name)
        if state:
            log(
                f"{name}: on {state['on']}, producers on {state['producers']}, speed {state['speed']:.1f}, "
                f"fastest {state['fastest']:.1f}, thrust {state['accel']:.2f} m/s², gravity {state['gravity']:.2f}"
            )
        else:
            log(f"{name}: not in the probe")
        logged = LOGGED.get(name, name)
        for line in (off.get(logged), left.get(logged)):
            if line:
                log(line.split(LOG_MARK, 1)[1][:400])
        return state

    for name, (expected_off, *_) in SHIPS.items():
        state = show(name)
        if expected_off:
            check(
                not state["on"]
                and state["producers"] == 0
                and LOGGED.get(name, name) in off,
                f"{name}: switched off, power and battery, with a log line",
            )
        else:
            check(
                state["on"] and LOGGED.get(name, name) not in off, f"{name}: stays on"
            )
    for name in ("CL Ram Zone", "CL Ram Bare", "CL Ram Victim"):
        check("in a safe zone" in off.get(name, ""), f"{name}'s line says safe zone")
    check(
        "a collision" in left.get("CL Ram Crash", ""),
        "the crash was over the limit, left on for the collision",
    )
    check(
        "hit by a faster ship" in left.get("CL Victim", ""),
        "the victim was over the limit, left on for the faster ship that hit it",
    )
    check(
        "outside safe zones" in off.get("CL Push 3", ""),
        "the push's line says outside safe zones",
    )
    for name in RAMS:
        check(
            (drop := largest_drop(name)) > HIT_DROP,
            f"{name} hit, {drop:.1f} m/s dropped within 0.1 s",
        )
    fall = seen["CL Fall"]
    check(
        fall["gravity"] > 2 * fall["accel"] + 3,
        "the faller's gravity is more than the rule would allow without it",
    )
    check(seen["CL Thrust"]["fastest"] > 5, "the thrusting ship flew")
    check(seen["CL Push 0.8"]["fastest"] > 20, "the 0.8x ship was pushed")
    check(seen["CL Force 3"]["fastest"] > 20, "the 3x force pushed its ship")
    check(
        "a collision" not in left.get("CL Push 3 Junk", ""),
        "the loose block in the cage didn't count as a collision",
    )

    # The drag is what made these over-zealous: the ships must have felt it
    for name, top in (
        ("CL Reverse 0.5", REVERSE_SPEED),
        ("CL Reverse 1", REVERSE_SPEED - 24),
        ("CL Missile", MISSILE_SPEED),
    ):
        state = seen[name]
        check(
            state["fastest"] > top - 10 and (low := slowest(name)) < top - 30,
            f"{name}: Relative Top Speed braked it from {state['fastest']:.0f} to {low:.0f} m/s or less",
        )
    check(
        10 < seen["CL Spike 0.3 Slow"]["fastest"] < 20,
        "the slow ship's spike stayed below the speed that counts",
    )
    show(CLANG)
    for name in DRIVES if BLUEPRINTS else ():
        state = show(name)
        check(
            bool(state) and not state["on"] and name in off,
            f"{name}: the Workshop clang drive was switched off",
        )
    check(
        f"({PLAYER}) switched off" in off.get(LOGGED[TOW], ""),
        f"{TOW}: the player's ship holding a bigger NPC grid on a rotor was switched off, as the player's",
    )
    if not BLUEPRINTS:
        log("CLANG_BLUEPRINTS not set: the Workshop clang drives were not tried")

    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_server()
