"""A ship enters a space elevator from its Earth end, about 1 km above the ground (SE-1358).

The elevators run from North and South Station straight down to about 1 km above EarthLike's
poles (build_world.py ELEVATORS). A ship takes a lane by hovering in the detect box below its
first ring and turning the dampeners off. In gravity it then falls, so the gate has to hold it
up while it aligns the ship and draws it in.

The pilot's ship, level and hovering on its dampeners in the middle of the detect box below the
Earth end's ring 0, turns the dampeners off. The test samples the ship's height above the
planet's center until the lane takes it, and checks that it loses no more than TOLERANCE meters
before it is in the lane, and that the lane then carries it to the station end.

    OCT_TASK=SE-1358t [OCT_TRADE_LANES=<mod folder>] [OCT_LANE=12|13] \\
        uv run --no-project --with httpx python tests/test_elevator_entry.py

Without OCT_TRADE_LANES the Workshop copy of Trade Lanes MP is used. Prints the height every
SAMPLE seconds, one line per check and a pass/fail summary; exit code 0 = all passed.

Run on sirius on 2026-11-11 with the Workshop Trade Lanes MP (= mod main a2c16fd), about 2.5 min
each: lanes 12 and 13 pass 4/4. The gate takes the ship at once, lifts it about 150 m while it
aligns it, and the lane takes it 17 s after the dampeners went off.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1358t")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
from test_client import log  # noqa: E402
from se_remote import CallOp  # noqa: E402

TRADE_LANES = 3816175430
MOD = os.environ.get("OCT_TRADE_LANES")
LANE = os.environ.get("OCT_LANE", "12")
SHIP_ID = 777001358000001
SHIP = "SE-1358 Ship"
# TradeLaneComputerBlockLogic.RingOffset, and the Trade_Lane.mwm detect box: its center this far
# before the ring, on the side ships come from (cluster-regression tools/trader/lanes.py)
GATE_OFFSET = 220.0
DETECT_CENTER = 262.5
# How far the ship may sink before the lane takes it
TOLERANCE = 25.0
SAMPLE = 0.5
# The lane docks a ship in about 15 s (three stages of 300 frames)
DOCK_TIMEOUT = 90.0
TRIP_TIMEOUT = 120.0

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


def earth_end() -> tuple[str, tuple, tuple, tuple, tuple]:
    """Name, position, forward and up of the lane's computer at its Earth end, and the
    station end's position, from the world copy"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    for entity in re.findall(
        r"<MyObjectBuilder_EntityBase .*?</MyObjectBuilder_EntityBase>", text, re.S
    ):
        name = re.search(r"<DisplayName>(.*?)</DisplayName>", entity)
        if (
            "<SubtypeName>TradeLaneComputer</SubtypeName>" in entity
            and name
            and name[1].startswith(f"TL {LANE} ")
            and " Pole &gt; " in name[1]
        ):
            orientation = entity[entity.index("<PositionAndOrientation>") :]
            partner = re.search(
                r"Target:GPS:[^:]*:([-\d.]+):([-\d.]+):([-\d.]+):", entity
            ).groups()
            return (
                name[1].replace("&gt;", ">"),
                vec(orientation, "Position"),
                vec(orientation, "Forward"),
                vec(orientation, "Up"),
                tuple(float(c) for c in partner),
            )
    raise LookupError(f"no Earth end of lane {LANE}")


def with_trade_lanes() -> None:
    item = (
        f'<ModItem FriendlyName="Trade Lanes MP"><Name>{TRADE_LANES}.sbm</Name>'
        f"<PublishedFileId>{TRADE_LANES}</PublishedFileId>"
        "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("<Mods>", f"<Mods>{item}", 1), encoding="utf-8")
    source = Path(MOD) if MOD else rig.WORKSHOP / str(TRADE_LANES)
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        link = folder / f"{TRADE_LANES}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(source)


def ship_blocks() -> list[str]:
    """The rig's cockpit, a charged battery, armor, and an atmospheric thruster each way along
    the ship's up: it hovers on its dampeners"""
    blocks = rig.small_ship_blocks()
    blocks[1] = rig.block(
        "MyObjectBuilder_BatteryBlock",
        "LargeBlockBatteryBlock",
        (0, 0, 1),
        "<CurrentStoredPower>3</CurrentStoredPower>",
    )
    for x, facing in ((1, "Up"), (-1, "Down")):
        blocks.append(
            rig.block(
                "MyObjectBuilder_Thrust",
                "LargeBlockSmallAtmosphericThrust",
                (x, 0, 1),
                forward=facing,
                up="Forward",
            )
        )
    return blocks


def board(remote, up) -> None:
    """Into the cockpit. A character falls in the planet's gravity, so it uses the seat
    right after each teleport above it (se_autopilot.enter_cockpit waits 3 s first)."""
    for _ in range(40):
        seat = remote.batch(calls=[CallOp.grid_to_world(SHIP_ID, (0, 0, 0))]).call(0)
        seat = tuple(float(c) for c in seat["world"])
        remote.character_teleport(*add(seat, up, 2.5))
        remote.character_look_at(*seat)
        if remote.character_use().get("used"):
            time.sleep(1)
            if remote.get_character().get("state") == "sitting":
                return
        time.sleep(0.5)
    raise RuntimeError("could not sit in the ship's cockpit")


def has_ship(remote) -> bool:
    return any(g["entityId"] == SHIP_ID for g in remote.list_grids())


def main() -> int:
    rig.stop_client()
    rig.stop_server()

    rig.prepare_world()
    name, position, forward, up, partner = earth_end()
    length = math.dist(position, partner)
    axis = tuple((p - q) / length for p, q in zip(partner, position))
    ring = add(position, cross(forward, up), GATE_OFFSET)
    start = add(ring, axis, -DETECT_CENTER)
    center = lambda p: math.dist(p, (0, 0, 0))
    log(
        f"lane {LANE} {name}: computer {center(position):.0f} m from EarthLike's center,"
        f" ring 0 {center(ring):.0f} m, ship {center(start):.0f} m"
    )
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    # Level: its up away from the planet, along the lane
    ship = rig.grid(SHIP, SHIP_ID, ship_blocks(), start, up, axis)
    (rig.WORLD / "SANDBOX_0_0_0_.sbs").write_text(
        text.replace("</SectorObjects>", ship + "</SectorObjects>", 1), encoding="utf-8"
    )
    with_trade_lanes()
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS])

    samples: list[tuple[float, float]] = []
    try:
        rig.start_server()
        pilot = rig.start_client()
        rig.wait_until(lambda: invulnerable(pilot), 60, "the pilot invulnerable")
        pilot.character_teleport(*add(start, axis, 15))
        rig.wait_until(lambda: has_ship(pilot), 60, "the ship on the client")
        board(pilot, axis)
        pilot.set_dampeners(True)
        # Settle on the dampeners where it is
        time.sleep(5)
        height = lambda: center(pilot.get_grid(SHIP_ID)["position"])
        before = height()
        hovered = abs(before - center(start))
        check(
            hovered < 10,
            f"the ship hovers by the ring on its dampeners ({hovered:.1f} m off)",
        )

        latest = pilot.get_hud_notifications().get("latest", 0)
        hud: list[str] = []
        pilot.set_dampeners(False)
        t0 = time.monotonic()
        entered = None
        while time.monotonic() - t0 < DOCK_TIMEOUT:
            grid = pilot.get_grid(SHIP_ID)
            h = center(grid["position"])
            t = time.monotonic() - t0
            samples.append((t, h - before))
            notes = pilot.get_hud_notifications(after=latest)
            latest = notes.get("latest", latest)
            hud += [n["text"][:70] for n in notes.get("notifications", [])]
            log(
                f"t {t:5.1f} s  height {h - before:+8.1f} m  ring {math.dist(grid['position'], ring):6.0f} m"
                f"  hud {hud[-1] if hud else ''}"
            )
            # In the lane: well past the ring toward the station
            if h > center(ring) + 1000:
                entered = t
                break
            time.sleep(SAMPLE)
        lowest = min(s[1] for s in samples)
        check(
            lowest > -TOLERANCE,
            f"the ship lost at most {TOLERANCE:.0f} m before the lane took it: lowest {lowest:+.1f} m",
        )
        check(entered is not None, f"the lane took the ship in {entered or 0:.0f} s")
        arrived = False
        if entered is not None:
            deadline = time.monotonic() + TRIP_TIMEOUT
            while time.monotonic() < deadline:
                if math.dist(pilot.get_grid(SHIP_ID)["position"], partner) < 3000:
                    arrived = True
                    break
                time.sleep(2)
        check(arrived, "the lane carried the ship to the station end")
        log("HUD: " + "; ".join(dict.fromkeys(hud)))
    finally:
        rig.stop_client()
        rig.stop_server()
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} elevator entry checks passed (lane {LANE})",
        flush=True,
    )
    for what in failed:
        print("FAILED", what, flush=True)
    return 0 if not failed else 1


def invulnerable(remote) -> bool:
    try:
        remote.set_admin_flag("invulnerable", True)
        return True
    except Exception as e:  # noqa: BLE001 -- retried by the caller
        log(f"admin flag: {e}")
        return False


if __name__ == "__main__":
    sys.exit(main())
