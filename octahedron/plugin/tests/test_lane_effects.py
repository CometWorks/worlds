"""A Trade Lanes ship's lane effects end on a client that watched it leave (SE-1229).

Two clients: A pilots a small ship into the lane of a station's lane computer, B floats by the
departure ring. A ship in a lane plays two looping particle effects on every client that has it.
The ship is out of B's sync range a moment into the trip, so B's copy of the grid closes. The mod
stopped those effects only when the trip ended, so B kept drawing them by the ring (the rings that
expand along the lane) until the player logged in again. On a cluster the same happens to a client
whose node hands the ship over.

The lane-effects-probe test plugin (tests/lane-effects-probe) on both clients writes how many of
the mod's effects play.

    OCT_TASK=SE-1229 OCT_WORLD_SOURCE=<world from build_world.py> OCT_TRADE_LANES=<mod folder> \\
        uv run --no-project --with httpx python tests/test_lane_effects.py

OCT_LANE (01) picks the lane computer by its TLID. Prints one line per check and a pass/fail
summary; exit code 0 = all passed.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1229")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
from test_client import log  # noqa: E402
from test_gravity_drive import sit  # noqa: E402
from test_table_rules import Second, both_admins  # noqa: E402

TRADE_LANES = 3816175430
MOD = Path(os.environ["OCT_TRADE_LANES"])
LANE = os.environ.get("OCT_LANE", "01")
PROBE = (
    "lane-effects-probe",
    Path(__file__).resolve().parent / "lane-effects-probe",
    "LaneEffectsProbe.xml",
    "lane-effects-probe",
)
SHIP_ID = 777001229000001
SHIP = "SE-1229 Ship"
SHIP_EFFECTS = ("WarpY", "TradeLaneStarsAndTunnel")
# TradeLaneComputerBlockLogic.RingOffset: the departure ring stands this far to the computer's
# right. The ship waits this far before it, in the ring's detect box (the Trade Lanes MP rig's
# travel.py)
GATE_OFFSET = 220.0
APPROACH = 150.0
# Where B floats: above the ring, in sync range of the ship until it leaves
WATCH = 300.0
# A ship effect B still plays this long after the ship left is stuck
SETTLE = 5.0

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def vec(text: str, tag: str) -> tuple[float, float, float]:
    m = re.search(rf'<{tag} x="([^"]+)" y="([^"]+)" z="([^"]+)"', text)
    return float(m[1]), float(m[2]), float(m[3])


def add(a, b, k: float = 1.0):
    return tuple(x + k * y for x, y in zip(a, b))


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def lane_computer() -> tuple[str, tuple, tuple, tuple]:
    """Name, position, forward and up of the lane computer's grid in the world copy"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    for entity in re.findall(
        r"<MyObjectBuilder_EntityBase .*?</MyObjectBuilder_EntityBase>", text, re.S
    ):
        if "<SubtypeName>TradeLaneComputer</SubtypeName>" in entity and f"TLID:{LANE}\n" in entity:
            orientation = entity[entity.index("<PositionAndOrientation>") :]
            name = re.search(r"<DisplayName>(.*?)</DisplayName>", entity)[1]
            return name, vec(orientation, "Position"), vec(orientation, "Forward"), vec(orientation, "Up")
    raise LookupError(f"no lane computer TLID:{LANE}")


def with_trade_lanes() -> None:
    item = (
        f'<ModItem FriendlyName="Trade Lanes MP"><Name>{TRADE_LANES}.sbm</Name>'
        f"<PublishedFileId>{TRADE_LANES}</PublishedFileId>"
        "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("<Mods />", f"<Mods>{item}</Mods>", 1), encoding="utf-8")
    folders = [rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"]
    with Second():
        folders.append(rig.APPDATA / "Mods")
    for folder in folders:
        folder.mkdir(parents=True, exist_ok=True)
        link = folder / f"{TRADE_LANES}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(MOD)


def probe(appdata: Path) -> dict[str, int]:
    path = appdata / "lane-effects-probe.txt"
    try:
        return {k: int(v) for k, v in re.findall(r"(\w+)=(\d+)", path.read_text())}
    except OSError:
        return {}


def ship_effects(appdata: Path) -> int:
    counts = probe(appdata)
    return sum(counts.get(name, 0) for name in SHIP_EFFECTS)


def wait_for(what, timeout: float, every: float = 0.25):
    """what's first true value, or its last value at the timeout"""
    deadline = time.monotonic() + timeout
    while True:
        value = what()
        if value or time.monotonic() > deadline:
            return value
        time.sleep(every)


def at_rest_away(remote, start) -> bool:
    """The trip is over: the ship is far from where it started and has stopped. The lane moves
    it by teleports, so its physics speed says nothing in the lane"""
    before = remote.get_grid(SHIP_ID)["position"]
    time.sleep(1)
    after = remote.get_grid(SHIP_ID)["position"]
    return math.dist(after, start) > 5000 and math.dist(before, after) < 5


def has_ship(remote) -> bool:
    return any(g["entityId"] == SHIP_ID for g in remote.list_grids())


def main() -> int:
    for client in (Second, rig_first):
        with client():
            rig.stop_client()
            (rig.APPDATA / "lane-effects-probe.txt").unlink(missing_ok=True)
    rig.stop_server()

    rig.prepare_world()
    name, position, forward, up = lane_computer()
    ring = add(position, cross(forward, up), GATE_OFFSET)
    start = add(ring, forward, -APPROACH)
    log(f"lane {LANE} {name}: ring at {tuple(round(c) for c in ring)}")
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    ship = rig.grid(SHIP, SHIP_ID, rig.small_ship_blocks(), start, forward, up)
    (rig.WORLD / "SANDBOX_0_0_0_.sbs").write_text(
        text.replace("</SectorObjects>", ship + "</SectorObjects>", 1), encoding="utf-8"
    )
    with_trade_lanes()
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS])
    both_admins()
    with Second():
        observer_data = rig.APPDATA
    pilot_data = rig.APPDATA

    try:
        rig.start_server()
        pilot = rig.start_client(extra_dev_folders=[PROBE])
        with Second():
            observer = rig.start_client(extra_dev_folders=[PROBE])
        for remote in (pilot, observer):
            wait_for(lambda: invulnerable(remote), 60, 3)
        observer.character_teleport(*add(ring, up, WATCH))
        observer.character_set_dampeners(True)
        pilot.character_teleport(*add(start, up, 15))
        sit(pilot, SHIP_ID)
        check(wait_for(lambda: has_ship(observer), 60), "B has the ship by the ring")
        log(f"B before the trip: {probe(observer_data)}")

        pilot.set_dampeners(False)
        played = wait_for(lambda: ship_effects(observer_data), 120)
        check(played > 0, f"B plays the ship's lane effects in the lane: {probe(observer_data)}")
        left = wait_for(lambda: not has_ship(observer), 60)
        check(left, "the ship left B's sync range")
        time.sleep(SETTLE)
        counts = probe(observer_data)
        check(
            all(counts.get(n) == 0 for n in SHIP_EFFECTS),
            f"B plays no ship lane effect {SETTLE:.0f} s after the ship left: {counts}",
        )
        arrived = wait_for(lambda: at_rest_away(pilot, start), 240, 1)
        time.sleep(SETTLE)
        counts = probe(pilot_data)
        check(
            bool(arrived) and all(counts.get(n) == 0 for n in SHIP_EFFECTS),
            f"the pilot's lane effects end with the trip: {counts}",
        )
    finally:
        for client in (Second, rig_first):
            with client():
                rig.stop_client()
        rig.stop_server()
    failed = [what for ok, what in checks if not ok]
    print(f"{len(checks) - len(failed)}/{len(checks)} lane effect checks passed", flush=True)
    for what in failed:
        print("FAILED", what, flush=True)
    return 0 if not failed else 1


class rig_first:
    """rig's own client globals: the pilot"""

    def __enter__(self):
        pass

    def __exit__(self, *_):
        pass


def invulnerable(remote) -> bool:
    try:
        remote.set_admin_flag("invulnerable", True)
        return True
    except Exception as e:  # noqa: BLE001 -- retried by the caller
        log(f"admin flag: {e}")
        return False


if __name__ == "__main__":
    sys.exit(main())
