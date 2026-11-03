"""Clang drives and ramming on the test server (SE-966), without a client. tests/clang-probe pushes ships beyond
their thrust as a clang drive would and reports what the server has for them; the octahedron.cfg defaults apply
(factor 2, 1 s window, 3 m/s² floor).

Switched off, with a log line: a ship pushed at 3x its thrust over mass, one whose speed jumps by 3 s of its
thrust in one frame, and one ramming a wall at 50 m/s inside Alpha Station's safe zone. Left on: a ship pushed at
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
ZONE = (140_000, 0, 0)  # Alpha Station's safe zone, 250 m
RAM_SPEED = 50.0
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
LOG_MARK = "Clang drive: "


def npc_identity() -> int:
    """The founder of the world's NPC faction, which runs the stations"""
    text = (rig.WORLD_SOURCE / "Sandbox.sbc").read_text(encoding="utf-8")
    return int(re.search(r"<Tag>OCT</Tag>.*?<PlayerId>(\d+)", text, re.S)[1])


def ship(name: str, index: int, position, velocity=(0, 0, 0), heavy: int = 0) -> str:
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
        rig.block("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (x, y, 3))
        for x in range(-2, 3)
        for y in range(6)
    ][:heavy]
    return rig.grid(name, BASE + index, blocks, position, velocity=velocity)


def wall(name: str, index: int, position) -> str:
    """A static armor slab across the Z axis"""
    return rig.grid(
        name,
        BASE + index,
        [
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, y, 0))
            for x in range(-3, 4)
            for y in range(-3, 4)
        ],
        position,
    ).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")


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
    # Ramming toward -Z: the ship forward, at 50 m/s, a wall 45 m ahead
    ram = (0, 0, -RAM_SPEED)
    grids.append(ship("CL Ram Zone", 10, (ZONE[0], ZONE[1], ZONE[2] + 240), ram))
    grids.append(wall("CL Wall Zone", 11, (ZONE[0], ZONE[1], ZONE[2] + 195)))
    grids.append(ship("CL Ram Bare", 12, (ZONE[0] + 30, ZONE[1], ZONE[2] + 240), ram))
    grids.append(wall("CL Wall Bare", 13, (ZONE[0] + 30, ZONE[1], ZONE[2] + 195)))
    crash = (SPACE[0], SPACE[1] - 5000, SPACE[2])
    grids.append(ship("CL Ram Crash", 14, (crash[0], crash[1], crash[2] + 45), ram))
    grids.append(wall("CL Wall Crash", 15, crash))
    # 68 km from the planet's center, above its highest hills
    grids.append(ship("CL Fall", 16, (39_260, 39_260, 39_260), heavy=30))
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


def switch_offs() -> dict[str, str]:
    return {
        line.split(LOG_MARK, 1)[1].split(" (", 1)[0]: line
        for line in rig.server_game_log().splitlines()
        if LOG_MARK in line
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
    rig.start_server()
    log("server ready")
    rig.wait_until(lambda: set(SHIPS) <= set(probe()), 120, "every ship in the probe")

    # The rams hit within a second or two, the spikes come 5 s in, the pushes act from the start
    def settled() -> bool:
        seen, off = probe(), switch_offs()
        expected = [n for n, (o, *_) in SHIPS.items() if o]
        hit = all(
            seen[n]["fastest"] - seen[n]["speed"] > RAM_SPEED / 2
            for n in ("CL Ram Zone", "CL Ram Crash", "CL Ram Bare")
        )
        return hit and set(expected) <= set(off)

    try:
        rig.wait_until(settled, 60, "the rams and the expected switch-offs", interval=1)
    except TimeoutError as error:
        log(str(error))
    # Long enough for any wrong switch-off to show
    time.sleep(10)

    seen, off = probe(), switch_offs()
    for name, (expected, *_) in SHIPS.items():
        state = seen[name]
        log(
            f"{name}: on {state['on']}, producers on {state['producers']}, speed {state['speed']:.1f}, "
            f"fastest {state['fastest']:.1f}, thrust {state['accel']:.2f} m/s², gravity {state['gravity']:.2f}"
        )
        if name in off:
            log(off[name][:400])
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
        "no collision" in off.get("CL Push 3", ""), "the push's line says no collision"
    )
    for name in ("CL Ram Zone", "CL Ram Crash", "CL Ram Bare"):
        check(
            seen[name]["fastest"] - seen[name]["speed"] > RAM_SPEED / 2,
            f"{name} hit its wall",
        )
    fall = seen["CL Fall"]
    check(
        fall["gravity"] > 2 * fall["accel"] + 3,
        "the faller's gravity is more than the rule would allow without it",
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
