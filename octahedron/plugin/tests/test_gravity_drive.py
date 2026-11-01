"""Gravity drives on the test server with the headless client (SE-881). Ships with a battery, a gravity
generator (saved off) and either artificial mass or a space ball wait in empty space. Each is left alone,
or piloted by the client, while its generator is turned on. Server speeds come from the probe
(tests/gravity-probe), the client's own view from Remote.

- Stopped, the default: no ship moves on the server, and the log says the patch is on. On a version without
  the plugin's patch this fails, and so does it on main.
- Moving, with --game-version X (another version than 1.210.*): the gate is off, the log says so and the
  ships fly as in the game.

Either way a character and a loose object in a generator's field get pulled, and a grid with artificial
mass falls in a planet's natural gravity.

    OCT_TASK=SE-881 uv run --no-project --with httpx python tests/test_gravity_drive.py [--game-version 1.211.0]
"""

from __future__ import annotations

import argparse
import math
import os
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-881")
import rig  # noqa: E402
from se_autopilot import enter_cockpit  # noqa: E402
from test_client import log  # noqa: E402

PROBE = (
    "gravity-probe",
    rig.PLUGIN / "tests" / "gravity-probe",
    "GravityProbe.xml",
    "gravity-probe",
)
GENERATOR = (0, 0, 2)
FIELD = 100
# A working drive passes this within a few seconds, single readings stay far below it otherwise
MOVING = 5.0
STILL = 1.0
WINDOW = 25.0
PULLED = 1.0
BASE = 777000881000000
# name -> (entity id, position, the block that makes it a drive)
MASS = ("MyObjectBuilder_VirtualMass", "VirtualMassLarge", "")
BALL = (
    "MyObjectBuilder_SpaceBall",
    "SpaceBallLarge",
    "<VirtualMass>20000</VirtualMass>",
)
LOOSE = {
    "GD Loose Mass": (BASE + 1, (100_000, -300_000, 0), MASS),
    "GD Loose Ball": (BASE + 2, (101_000, -300_000, 0), BALL),
}
PILOTED = {
    "GD Pilot Mass": (BASE + 3, (102_000, -300_000, 0), MASS),
    "GD Pilot Ball": (BASE + 4, (103_000, -300_000, 0), BALL),
}
PULL = ("GD Field", BASE + 5, (104_000, -300_000, 0))
LOOSE_OBJECT = (BASE + 6, (104_020, -300_000, 0))
CHARACTER_AT = (104_000, -300_000, 20)
FALLER = ("GD Faller", BASE + 7, (20_000, 13_000, 72_000))


def ship(name: str, entity_id, position, drive, **kwargs) -> str:
    return rig.grid(
        name,
        entity_id,
        [
            rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0)),
            rig.block(
                "MyObjectBuilder_BatteryBlock",
                "LargeBlockBatteryBlock",
                (0, 0, 1),
                "<CurrentStoredPower>3</CurrentStoredPower>",
            ),
            generator(),
            rig.block(drive[0], drive[1], (0, 0, 3), drive[2]),
        ],
        position,
        **kwargs,
    )


def generator() -> str:
    return rig.block(
        "MyObjectBuilder_GravityGenerator",
        "",
        GENERATOR,
        "<Enabled>false</Enabled>"
        f'<FieldSize x="{FIELD}" y="{FIELD}" z="{FIELD}" />'
        "<GravityAcceleration>9.81</GravityAcceleration>",
    )


def loose_object(entity_id: int, position) -> str:
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_FloatingObject">'
        f"<SubtypeName /><EntityId>{entity_id}</EntityId>"
        "<PersistentFlags>InScene</PersistentFlags><PositionAndOrientation>"
        + rig._vec("Position", position)
        + rig._vec("Forward", (0, 0, -1))
        + rig._vec("Up", (0, 1, 0))
        + "</PositionAndOrientation>"
        "<Item><Amount>100</Amount>"
        '<PhysicalContent xsi:type="MyObjectBuilder_Ore"><SubtypeName>Stone</SubtypeName></PhysicalContent>'
        "</Item></MyObjectBuilder_EntityBase>"
    )


def world_grids() -> list[str]:
    grids = [ship(n, i, p, d) for n, (i, p, d) in {**LOOSE, **PILOTED}.items()]
    grids.append(
        rig.grid(
            PULL[0],
            PULL[1],
            [
                rig.block(
                    "MyObjectBuilder_BatteryBlock",
                    "LargeBlockBatteryBlock",
                    (0, 0, 1),
                    "<CurrentStoredPower>3</CurrentStoredPower>",
                ),
                generator(),
            ],
            PULL[2],
        ).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
    )
    grids.append(loose_object(*LOOSE_OBJECT))
    grids.append(
        rig.grid(
            FALLER[0],
            FALLER[1],
            [
                rig.block(
                    "MyObjectBuilder_BatteryBlock",
                    "LargeBlockBatteryBlock",
                    (0, 0, 1),
                    "<CurrentStoredPower>3</CurrentStoredPower>",
                ),
                rig.block(MASS[0], MASS[1], (0, 0, 2), MASS[2]),
            ],
            FALLER[2],
            velocity=(0, 0, -1),
        )
    )
    return grids


def server() -> dict[str, tuple[float, tuple[float, float, float]]]:
    """name -> (speed, position) as the server has them now"""
    found = {}
    try:
        text = (rig.SERVER_DATA / "gravity-probe.txt").read_text(encoding="utf-8")
    except FileNotFoundError:
        return found
    for line in text.splitlines():
        name, speed, _, x, y, z = line.split(";")
        found[name] = (float(speed), (float(x), float(y), float(z)))
    return found


def fastest_on_server(name: str) -> float:
    text = (rig.SERVER_DATA / "gravity-probe.txt").read_text(encoding="utf-8")
    return next(
        float(line.split(";")[2])
        for line in text.splitlines()
        if line.startswith(name + ";")
    )


def wait_on_client(remote, entity_id: int) -> None:
    def there():
        try:
            return remote.get_grid(entity_id)
        except Exception:  # noqa: BLE001 -- not streamed to the client yet
            return None

    rig.wait_until(there, 120, f"grid {entity_id} on the client")


def watch(name: str, sample=None) -> dict:
    """Watches a ship with its drive on, until it clearly moves or the window ends"""
    start = server()[name][1]
    fastest = 0.0
    views = []
    began = time.monotonic()
    while (elapsed := time.monotonic() - began) < WINDOW:
        speed, position = server()[name]
        fastest = max(fastest, speed)
        if sample:
            views.append((elapsed, position, sample()))
        if fastest > MOVING and elapsed > 3:
            break
        time.sleep(0.2)
    return {
        "fastest": fastest,
        "moved": math.dist(start, server()[name][1]),
        "seconds": elapsed,
        "views": views,
    }


def leave(remote) -> None:
    remote.character_use()
    rig.wait_until(
        lambda: remote.get_character().get("state") != "sitting", 30, "out of the seat"
    )
    if not remote.get_character().get("jetpack"):
        remote.key("X")
    # A character leaving a moving ship keeps its speed and can't reach the next one
    last = [remote.get_character()["position"]]

    def slow() -> bool:
        now = remote.get_character()["position"]
        moved, last[0] = math.dist(now, last[0]), now
        return moved < 2

    rig.wait_until(slow, 60, "the character slowing down", interval=1)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-version")
    args = parser.parse_args()
    moving = bool(args.game_version)
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
    (rig.SERVER_DATA / "gravity-probe.txt").unlink(missing_ok=True)
    rig.start_server(
        env={"OCTAHEDRON_GAME_VERSION": args.game_version} if moving else None
    )
    log("server ready")
    remote = rig.start_client()
    rig.ensure_character(remote)
    rig.wait_until(lambda: PULL[0] in server(), 60, "the probe's first lines")

    lines = [
        line
        for line in rig.server_game_log().splitlines()
        if "Gravity drive patch" in line
    ]
    log(f"gate: {lines}")
    wanted = "not applied" if moving else "applied on"
    check(len(lines) == 1 and wanted in lines[0], f"the log says once: {wanted}")

    def judge(name: str, seen: dict) -> None:
        log(
            f"{name}: fastest {seen['fastest']:.2f} m/s, moved {seen['moved']:.1f} m in {seen['seconds']:.0f} s"
        )
        if moving:
            check(seen["fastest"] > MOVING, f"{name} flies on its drive")
        else:
            check(seen["fastest"] < STILL, f"{name} stays still on the server")

    for name, (entity_id, position, _) in LOOSE.items():
        remote.character_teleport(position[0] + 200, position[1], position[2])
        wait_on_client(remote, entity_id)
        remote.set_enabled(entity_id, GENERATOR, True)
        judge(name, watch(name))

    for name, (entity_id, position, _) in PILOTED.items():
        remote.character_teleport(position[0], position[1] + 30, position[2])
        wait_on_client(remote, entity_id)
        enter_cockpit(remote, entity_id)
        check(remote.get_character().get("state") == "sitting", f"pilot in {name}")
        remote.set_enabled(entity_id, GENERATOR, True)
        seen = watch(name, lambda: remote.get_grid(entity_id))
        judge(name, seen)
        speeds = [view["speed"] for _, _, view in seen["views"]]
        offsets = [
            math.dist(position, view["position"]) for _, position, view in seen["views"]
        ]
        log(
            f"{name} on the client: fastest {max(speeds):.2f} m/s, last {speeds[-1]:.2f} m/s, "
            f"at most {max(offsets):.2f} m from the server's position, last {offsets[-1]:.2f} m"
        )
        leave(remote)

    # The character first: off the jetpack, in the field. Below it the field is empty
    remote.character_teleport(*CHARACTER_AT)
    if remote.get_character().get("jetpack"):
        remote.key("X")
    rig.wait_until(
        lambda: not remote.get_character().get("jetpack"), 15, "the jetpack off"
    )
    wait_on_client(remote, PULL[1])
    before = {n: v for n, v in server().items() if n in ("GD Loose", "GD Character")}
    log(f"before the field: {before}")
    remote.set_enabled(PULL[1], GENERATOR, True)
    fastest = {"GD Loose": 0.0, "GD Character": 0.0}

    def pulled() -> bool:
        now = server()
        for name in fastest:
            if name in now:
                fastest[name] = max(fastest[name], now[name][0])
        return min(fastest.values()) > PULLED

    try:
        rig.wait_until(pulled, 20, "the field pulling", interval=0.2)
    except TimeoutError as error:
        log(str(error))
    log(f"in the field: {fastest}")
    check(fastest["GD Loose"] > PULLED, "the loose object gets pulled")
    check(fastest["GD Character"] > PULLED, "the character gets pulled")

    # It may have hit the planet by now
    falling = fastest_on_server(FALLER[0])
    log(f"{FALLER[0]}: fastest {falling:.2f} m/s")
    check(falling > MOVING, "a grid with artificial mass falls in natural gravity")

    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_client()
        rig.stop_server()
