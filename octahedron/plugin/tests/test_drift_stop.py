"""Drift stop on the test server with the headless client (SE-1326). Grids drift in empty space with no
thrust: a wreck at 0.15 m/s and one at 0.6 m/s with no power, debris at 8 m/s, a wreck at 25 m/s, a ship with a
charged battery, a ship with no power the client sits in, and a wreck in a planet's gravity. The probe
(tests/drift-probe) reports each one's speed and whether its body is awake. Sync distance is 3 km.

1. Off, the default config: for 20 s every grid keeps its speed and stays awake. This is main's behavior.
2. On, the client seated within sync distance (slow down after 3 s): the two slow wrecks slow down over
   seconds, not at once, then stop and fall asleep. The debris (over 1 m/s), the fast wreck, the powered ship,
   the seated one and the one in gravity keep moving.
3. The client leaves its seat and floats next to the ship (closer than 100 m): it keeps drifting.
4. The client goes 50 km away, out of sync distance: the debris and the ship stop at once and fall asleep.
   The fast wreck (over 20 m/s) and the powered ship keep moving. The log never names the one in gravity.

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
SLOW = "DS Slow"
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
        WRECK, BASE + 1, [cockpit(), *armor(4)], at(0), velocity=(0.15, 0, 0)
    ),
    SLOW: rig.grid(SLOW, BASE + 7, armor(4), at(1), velocity=(0.6, 0, 0)),
    DEBRIS: rig.grid(
        DEBRIS,
        BASE + 2,
        [
            rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (0, 0, z))
            for z in range(3)
        ],
        at(2),
        velocity=(8, 0, 0),
        size="Small",
    ),
    FAST: rig.grid(FAST, BASE + 3, armor(4), at(3), velocity=(25, 0, 0)),
    POWERED: rig.grid(
        POWERED, BASE + 4, [cockpit(), *armor(4), battery()], at(4), velocity=(1, 0, 0)
    ),
    # Slow enough for the client to get into its cockpit
    SEATED: rig.grid(
        SEATED, BASE + 5, [cockpit(), *armor(4)], at(5), velocity=(0.15, 0, 0)
    ),
    GRAVITY: rig.grid(GRAVITY, BASE + 6, armor(4), gd.FALLER[2], velocity=(0, 0, -1)),
}


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


def speeds() -> dict[str, float]:
    return {n: round(v[0], 2) for n, v in server().items()}


def main() -> int:
    failures = []

    def check(ok: bool, what: str) -> None:
        log(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    def moving(names, phase: str) -> None:
        now = server()
        for name in names:
            check(
                now[name][0] > STOPPED and now[name][2],
                f"{phase}: {name} keeps moving, awake ({now[name][0]:.2f} m/s)",
            )

    def until_asleep(names, phase: str, timeline=None) -> None:
        def done() -> bool:
            if timeline is not None:
                timeline.append((round(time.monotonic() - began, 1), speeds()))
            return asleep(names)

        began = time.monotonic()
        try:
            rig.wait_until(done, SLEEP_TIMEOUT, f"{names} asleep", 0.5)
            ok = True
        except TimeoutError as error:
            log(str(error))
            ok = False
        log(f"{phase}: asleep after {time.monotonic() - began:.1f} s")
        check(ok, f"{phase}: {', '.join(names)} stop and fall asleep")

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
    position = at(5)
    remote.character_teleport(position[0], position[1] + 30, position[2])
    gd.wait_on_client(remote, BASE + 5)
    gd.sit(remote, BASE + 5)
    check(
        remote.get_character().get("state") == "sitting", f"the client sits in {SEATED}"
    )

    # 1. Off
    time.sleep(20)
    log(f"off: awake {awake()}; speeds {speeds()}")
    off_awake = len(awake())
    check(off_awake == len(GRIDS), f"off: all {len(GRIDS)} grids awake ({off_awake})")
    check(
        all(v > STOPPED for v in speeds().values()) and not stops_logged(),
        "off: every grid keeps drifting, no stop logged",
    )

    # 2. On, the client within sync distance of all but the one in gravity
    rig.write_plugin_config(
        config_text(
            DriftStopEnabled="true", DriftStopSeconds=SECONDS, DriftStopPlayerMeters=0
        )
    )
    log(f"reload: {rig.chat(remote, '!oct reload', 3)}")
    timeline = []
    until_asleep([WRECK, SLOW], "near", timeline)
    slow = [(t, s[SLOW]) for t, s in timeline if SLOW in s]
    log(f"near: {SLOW} speeds {slow}")
    check(
        any(0.1 < v < 0.5 for _, v in slow),
        f"near: {SLOW} slows down over seconds, not at once",
    )
    moving([DEBRIS, FAST, POWERED, SEATED], "near")
    near_awake = awake()
    log(f"near: awake {near_awake}")

    # 3. The client next to the ship
    rig.write_plugin_config(
        config_text(
            DriftStopEnabled="true", DriftStopSeconds=SECONDS, DriftStopPlayerMeters=100
        )
    )
    log(f"reload: {rig.chat(remote, '!oct reload', 3)}")
    gd.leave(remote)
    time.sleep(4 * SECONDS)
    moving([SEATED], "next to it")

    # 4. The client out of sync distance
    remote.character_teleport(position[0], position[1] + 50_000, position[2])
    began = time.monotonic()
    try:
        rig.wait_until(
            lambda: all(server()[n][0] < STOPPED for n in (DEBRIS, SEATED)),
            15,
            "debris and ship stopped",
            0.2,
        )
    except TimeoutError as error:
        log(str(error))
    stopped_in = time.monotonic() - began
    check(
        stopped_in < 5,
        f"far: the debris and the ship stop at once ({stopped_in:.1f} s)",
    )
    until_asleep([DEBRIS, SEATED], "far")
    moving([FAST, POWERED], "far")
    far_awake = awake()
    log(f"far: awake {far_awake}")
    logged = stops_logged()
    log("\n".join(logged))
    named = {n for n in GRIDS if any(f"{n} (" in l for l in logged)}
    check(
        named == {WRECK, SLOW, DEBRIS, SEATED} and len(logged) == 4,
        f"the log names the four stopped grids once each ({sorted(named)})",
    )

    print(
        f"awake test grids: off {off_awake}/{len(GRIDS)}, near {len(near_awake)}/{len(GRIDS)}, "
        f"far {len(far_awake)}/{len(GRIDS)}"
    )
    rig.stop_client()
    rig.stop_server()
    print("PASS" if not failures else f"FAIL: {failures}")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
