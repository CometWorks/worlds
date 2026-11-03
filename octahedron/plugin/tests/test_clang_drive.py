"""Clang drives and ramming on the test server (SE-966), without a client. tests/clang-probe pushes ships beyond
their thrust as a clang drive would and reports what the server has for them; the octahedron.cfg defaults apply
(factor 2, 6 frames, 3 m/s² above gravity, 20 m/s).

Switched off, with a log line: a ship pushed at 3x its thrust over mass, one whose speed jumps by 3 s of its
thrust in one frame, a heavy ship flying at 50 m/s into Alpha Station's safe zone and onto a wall there, the
same without thrusters, and one ramming a ship at rest there. Left on: a ship pushed at 1.5x, one at 30 m/s
jumping by 0.1 s of its thrust in one frame (the window takes the spike), one jumping by 0.3 s of it from
rest (too slow to count), one at full thrust override, an NPC's ship pushed at 3x, the ramming ship hitting
a wall outside safe zones, the ship it rams in the zone (hit by a faster one), and a heavy ship falling
toward the planet faster than twice its thrust allows.

Two clang drives from the Workshop are switched off once started, a sliding door one and a merge block one,
when CLANG_BLUEPRINTS names a steamcmd folder that has them (see DRIVES):

    steamcmd +force_install_dir DIR +login anonymous +workshop_download_item 244850 2882012087 +workshop_download_item 244850 2889439688 +quit
    CLANG_BLUEPRINTS=DIR/steamapps/workshop/content/244850 OCT_TASK=SE-966 uv run --no-project --with httpx python tests/test_clang_drive.py
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
    "CL Push 1.5": (False, True, False),
    "CL Push 3 NPC": (False, True, True),
    "CL Spike 3": (True, True, False),
    "CL Spike 0.1": (False, True, False),
    "CL Spike 0.3 Slow": (False, True, False),
    "CL Thrust": (False, True, False),
    "CL Ram Zone": (True, True, False),
    "CL Ram Crash": (False, True, False),
    "CL Ram Bare": (True, False, False),
    "CL Ram Victim": (True, True, False),
    "CL Victim": (False, True, False),
    "CL Fall": (False, True, False),
}
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
        "CL Push 3 NPC",
        "CL Spike 3",
        "CL Spike 0.1",
        "CL Spike 0.3 Slow",
        "CL Thrust",
    ]
    for i, name in enumerate(spaced):
        # The spike that is to be absorbed comes on a ship already moving: a slow one doesn't count
        velocity = (0, 0, 30) if name == "CL Spike 0.1" else (0, 0, 0)
        grids.append(ship(name, i, (SPACE[0] + 1000 * i, SPACE[1], SPACE[2]), velocity))
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
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[PROBE])
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
        for line in (off.get(name), left.get(name)):
            if line:
                log(line.split(LOG_MARK, 1)[1][:400])
        return state

    for name, (expected_off, *_) in SHIPS.items():
        state = show(name)
        if expected_off:
            check(
                not state["on"] and state["producers"] == 0 and name in off,
                f"{name}: switched off, power and battery, with a log line",
            )
        else:
            check(state["on"] and name not in off, f"{name}: stays on")
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
    check(seen["CL Push 1.5"]["fastest"] > 20, "the 1.5x ship was pushed")
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
    if not BLUEPRINTS:
        log("CLANG_BLUEPRINTS not set: the Workshop clang drives were not tried")

    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_server()
