"""Clang drives and ramming on the test server (SE-966), without a client. tests/clang-probe pushes ships beyond
their thrust as a clang drive would and reports what the server has for them; the octahedron.cfg defaults apply
(factor 2, 1 s window, 3 m/s² floor).

Switched off, with a log line: a ship pushed at 3x its thrust over mass, one whose speed jumps by 3 s of its
thrust in one frame, and one flying at 50 m/s into Alpha Station's safe zone and onto a wall there. Left on: a ship pushed at
1.5x, one jumping by 1 s of its thrust in one frame (the window takes the spike), one at full thrust override, an
NPC's ship pushed at 3x, the same ramming ship hitting a wall outside safe zones, a ship without thrusters ramming
in the safe zone, and a heavy ship falling toward the planet faster than twice its thrust allows.

    OCT_TASK=SE-966 uv run --no-project --with httpx python tests/test_clang_drive.py
"""

from __future__ import annotations

import os
import re
import sys
import time

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
    "CL Spike 1": (False, True, False),
    "CL Thrust": (False, True, False),
    "CL Ram Zone": (True, True, False),
    "CL Ram Crash": (False, True, False),
    "CL Ram Bare": (False, False, False),
    "CL Fall": (False, True, False),
}
CLANG = "CL Clang"
CLANG_AT = (SPACE[0], SPACE[1] + 5000, SPACE[2])
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


def clang_ship(velocity: float) -> list[str]:
    """Viktor's "Clang with Piston" (se-clang-slayer): a piston on a C frame pushing its head into the frame,
    with a thruster added and the piston extending at the given speed. Owner and builder dropped.
    """
    text = (PROBE[1] / "ClangWithPiston.sbc").read_text(encoding="utf-8")
    text = re.sub(r"<(Owner|BuiltBy)>\d+</\1>", "", text)
    ids = dict.fromkeys(re.findall(r"<EntityId>(\d+)</EntityId>", text))
    for i, old in enumerate(ids):
        text = text.replace(old, str(BASE + 100 + i))
    text = re.sub(
        r"<Velocity>[^<]*</Velocity>", f"<Velocity>{velocity}</Velocity>", text
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
        at = [
            float(v)
            for v in re.search(
                r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', body
            ).groups()
        ]
        moved = [CLANG_AT[k] + at[k] - main[k] for k in range(3)]
        body = re.sub(r"<Position [^>]*/>", rig._vec("Position", moved), body, count=1)
        body = re.sub(
            r"<DisplayName>[^<]*</DisplayName>",
            f"<DisplayName>{CLANG if i == 0 else CLANG + ' Head'}</DisplayName>",
            body,
        )
        if i == 0:
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
        "CL Spike 1",
        "CL Thrust",
    ]
    for i, name in enumerate(spaced):
        grids.append(ship(name, i, (SPACE[0] + 1000 * i, SPACE[1], SPACE[2])))
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
    grids += clang_ship(CLANG_SPEED)
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

    # The rams hit within a second or two, the spikes come 5 s in, the pushes act from the start
    def settled() -> bool:
        seen, off = probe(), switch_offs()
        expected = [n for n, (o, *_) in SHIPS.items() if o]
        hit = all(
            largest_drop(n) > RAM_SPEED / 2
            for n in ("CL Ram Zone", "CL Ram Crash", "CL Ram Bare")
        )
        return hit and set(expected) <= set(off)

    try:
        rig.wait_until(settled, 60, "the rams and the expected switch-offs", interval=1)
    except TimeoutError as error:
        log(str(error))
    # Long enough for any wrong switch-off to show
    time.sleep(10)

    seen, off, left = probe(), switch_offs(), switch_offs("left on")
    for name, (expected, *_) in SHIPS.items():
        state = seen[name]
        log(
            f"{name}: on {state['on']}, producers on {state['producers']}, speed {state['speed']:.1f}, "
            f"fastest {state['fastest']:.1f}, thrust {state['accel']:.2f} m/s², gravity {state['gravity']:.2f}"
        )
        for line in (off.get(name), left.get(name)):
            if line:
                log(line.split(LOG_MARK, 1)[1][:400])
        if expected:
            check(
                not state["on"] and state["producers"] == 0 and name in off,
                f"{name}: switched off, power and battery, with a log line",
            )
        else:
            check(state["on"] and name not in off, f"{name}: stays on")
    check(
        "in a safe zone" in off.get("CL Ram Zone", ""), "the ram's line says safe zone"
    )
    check(
        "a collision" in left.get("CL Ram Crash", ""),
        "the crash was over the limit, left on for the collision",
    )
    check(
        "outside safe zones" in off.get("CL Push 3", ""),
        "the push's line says outside safe zones",
    )
    for name in ("CL Ram Zone", "CL Ram Crash", "CL Ram Bare"):
        check(
            (drop := largest_drop(name)) > RAM_SPEED / 2,
            f"{name} hit its wall, {drop:.1f} m/s within 0.1 s",
        )
    fall = seen["CL Fall"]
    check(
        fall["gravity"] > 2 * fall["accel"] + 3,
        "the faller's gravity is more than the rule would allow without it",
    )
    clang = seen.get(CLANG)
    log(
        f"{CLANG}: {clang}, {'switched off: ' + off[CLANG][:400] if CLANG in off else 'left on'}"
    )
    check(seen["CL Thrust"]["fastest"] > 5, "the thrusting ship flew")
    check(seen["CL Push 1.5"]["fastest"] > 20, "the 1.5x ship was pushed")

    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_server()
