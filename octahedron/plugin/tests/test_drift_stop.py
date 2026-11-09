"""Drift stop on the test server with the headless client (SE-1326). Grids drift in empty space with no
thrust: a wreck and debris with no power, a faster wreck, a ship with a charged battery, a ship with no power
the client sits in, and a wreck with no power in a planet's gravity. The probe (tests/drift-probe) reports
each one's speed and whether its body is awake.

1. Off, the default config: for 20 s every grid keeps its speed and stays awake. This is main's behavior.
2. On (stop after 3 s, the client counts at any distance > 0): the wreck and the debris stop and fall
   asleep. The fast wreck, the powered ship, the seated ship and the one in gravity keep moving, and the log
   names only the two stopped ones.
3. Player distance 200 m: the client leaves its seat and floats next to the ship, which keeps drifting.
4. The client goes 5 km away: the ship stops and falls asleep.

    OCT_TASK=SE-1326 uv run --no-project --with httpx python tests/test_drift_stop.py
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1326")
import rig  # noqa: E402
import test_gravity_drive as gd  # noqa: E402
from test_client import log  # noqa: E402

PROBE = (
    "drift-probe",
    rig.PLUGIN / "tests" / "drift-probe",
    "DriftProbe.xml",
    "drift-probe",
)
BASE = 777000132600000
SPACE = (112_000, -300_000, 0)
WRECK = "DS Wreck"
DEBRIS = "DS Debris"
FAST = "DS Fast"
POWERED = "DS Powered"
SEATED = "DS Seated"
GRAVITY = "DS Gravity"
# Under the cluster's offline gate (0.05 m/s)
STOPPED = 0.05
SLEEP_TIMEOUT = 60
SECONDS = 3


def armor(count: int) -> list[str]:
    return [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
        for z in range(1, count + 1)
    ]


def cockpit() -> str:
    return rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0))


def battery() -> str:
    return rig.block(
        "MyObjectBuilder_BatteryBlock",
        "LargeBlockBatteryBlock",
        (0, 0, 5),
        "<CurrentStoredPower>3</CurrentStoredPower>",
    )


def at(index: int):
    """Spots 300 m apart across the drift, so the grids never meet"""
    return (SPACE[0], SPACE[1], SPACE[2] + 300 * index)


# name -> (entity id, grid XML)
GRIDS = {
    WRECK: rig.grid(
        WRECK, BASE + 1, [cockpit(), *armor(4)], at(0), velocity=(0.5, 0, 0)
    ),
    DEBRIS: rig.grid(
        DEBRIS,
        BASE + 2,
        [
            rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (0, 0, z))
            for z in range(3)
        ],
        at(1),
        velocity=(8, 0, 0),
        size="Small",
    ),
    FAST: rig.grid(FAST, BASE + 3, armor(4), at(2), velocity=(15, 0, 0)),
    POWERED: rig.grid(
        POWERED, BASE + 4, [cockpit(), *armor(4), battery()], at(3), velocity=(1, 0, 0)
    ),
    SEATED: rig.grid(
        SEATED, BASE + 5, [cockpit(), *armor(4)], at(4), velocity=(0.5, 0, 0)
    ),
    GRAVITY: rig.grid(GRAVITY, BASE + 6, armor(4), gd.FALLER[2], velocity=(0, 0, -1)),
}
# The one in gravity falls and may land, the log tells whether it was stopped
KEPT_MOVING = (FAST, POWERED, SEATED)


def config_text(**values) -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for key, value in values.items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        assert count == 1, key
    return text


def server() -> dict[str, tuple[float, float, bool]]:
    """name -> (speed, spin, awake) as the server has them now"""
    try:
        text = (rig.SERVER_DATA / "drift-probe.txt").read_text(encoding="utf-8")
    except FileNotFoundError:
        return {}
    found = {}
    for line in text.splitlines():
        name, speed, spin, awake, *_ = line.split(";")
        found[name] = (float(speed), float(spin), awake == "1")
    return found


def awake() -> list[str]:
    return sorted(n for n, (_, _, a) in server().items() if a)


def stops_logged() -> list[str]:
    return [l for l in rig.server_game_log().splitlines() if "Drift stop:" in l]


def asleep(names) -> bool:
    now = server()
    return all(n in now and now[n][0] < STOPPED and not now[n][2] for n in names)


def main() -> int:
    failures = []

    def check(ok: bool, what: str) -> None:
        log(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(list(GRIDS.values()))
    rig.write_plugin_config(config_text(DriftStopEnabled="false"))
    rig.prepare_server(extra_dev_folders=[PROBE])
    (rig.SERVER_DATA / "drift-probe.txt").unlink(missing_ok=True)
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    rig.ensure_character(remote)
    rig.wait_until(lambda: len(server()) == len(GRIDS), 60, "the probe's first lines")

    # The client sits in the seated ship before the rule is on
    position = at(4)
    remote.character_teleport(position[0], position[1] + 30, position[2])
    gd.wait_on_client(remote, BASE + 5)
    gd.sit(remote, BASE + 5)
    check(
        remote.get_character().get("state") == "sitting", f"the client sits in {SEATED}"
    )

    # 1. Off
    began = server()
    time.sleep(20)
    now = server()
    log(f"off: awake {awake()}; speeds {({n: round(v[0], 2) for n, v in now.items()})}")
    before_awake = len(awake())
    check(
        before_awake == len(GRIDS),
        f"off: all {len(GRIDS)} grids awake ({before_awake})",
    )
    check(
        all(now[n][0] > STOPPED for n in GRIDS) and not stops_logged(),
        "off: every grid keeps drifting, no stop logged",
    )
    log(f"off, at the start: {({n: round(v[0], 2) for n, v in began.items()})}")

    # 2. On, the client counts at no distance
    rig.write_plugin_config(
        config_text(
            DriftStopEnabled="true",
            DriftStopSeconds=SECONDS,
            DriftStopPlayerMeters=0,
        )
    )
    log(f"reload: {rig.chat(remote, '!oct reload', 3)}")
    started = time.monotonic()
    try:
        rig.wait_until(
            lambda: asleep([WRECK, DEBRIS]),
            SLEEP_TIMEOUT,
            "wreck and debris asleep",
            0.5,
        )
        slept = True
    except TimeoutError as error:
        log(str(error))
        slept = False
    log(f"on: asleep after {time.monotonic() - started:.0f} s; {server()}")
    check(slept, f"on: {WRECK} and {DEBRIS} stop and fall asleep")
    # Many times the stop delay
    time.sleep(max(0.0, 15 - (time.monotonic() - started)))
    now = server()
    for name in KEPT_MOVING:
        check(
            now[name][0] > 0.4 and now[name][2],
            f"on: {name} keeps moving, awake ({now[name][0]:.2f} m/s)",
        )
    logged = stops_logged()
    log(f"on: {logged}")
    check(
        len(logged) == 2
        and any(WRECK in l for l in logged)
        and any(DEBRIS in l for l in logged),
        "on: the log names the wreck and the debris, once each",
    )
    after_awake = len(awake())
    log(f"on: awake {awake()}")

    # 3. The client next to the ship
    rig.write_plugin_config(
        config_text(
            DriftStopEnabled="true",
            DriftStopSeconds=SECONDS,
            DriftStopPlayerMeters=200,
        )
    )
    log(f"reload: {rig.chat(remote, '!oct reload', 3)}")
    gd.leave(remote)
    time.sleep(4 * SECONDS)
    now = server()
    check(
        now[SEATED][0] > 0.4 and now[SEATED][2],
        f"near: {SEATED} keeps drifting with the client next to it ({now[SEATED][0]:.2f} m/s)",
    )

    # 4. The client leaves
    remote.character_teleport(position[0], position[1] + 5000, position[2])
    try:
        rig.wait_until(lambda: asleep([SEATED]), SLEEP_TIMEOUT, f"{SEATED} asleep", 0.5)
        left = True
    except TimeoutError as error:
        log(str(error))
        left = False
    check(left, f"away: {SEATED} stops and falls asleep")
    final_awake = awake()
    log(f"away: awake {final_awake}")

    print(
        f"awake test grids: off {before_awake}/{len(GRIDS)}, on {after_awake}/{len(GRIDS)}, "
        f"client gone {len(final_awake)}/{len(GRIDS)}"
    )
    rig.stop_client()
    rig.stop_server()
    print("PASS" if not failures else f"FAIL: {failures}")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
