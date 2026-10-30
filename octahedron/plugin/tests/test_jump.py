"""Checks the jump drive limit on the test server with the headless client. The
server's Octahedron.cfg doesn't name the world edge options, so the plugin runs them
at their defaults, as on a Quasar cluster (SE-780). Ships with a charged jump drive
wait in empty space. For the first ones the jump probe (tests/jump/probe) sends the
server the request a game client sends when its pilot confirms a jump forward; the
server then charges, jumps and tells the player as in a real jump. (A headless client
can't reach a ship that far through a remote control without an antenna network.)

- Jump out: from 400 km straight outward by 100 km. Shortened, it arrives at the
  450 km sphere, and the player is told.
- Jump inside: from 300 km outward by 50 km. Arrives at 350 km, no message.
- Jump beyond: from 470 km outward by 20 km. Refused, the ship stays, and the
  player is told.
- Jump far: from 407 km outward by 4000 km, beyond the drive's range. Arrives at the
  450 km sphere.
- Jump obstacle: toward a grid that sits where the jump would be cut short. The game
  moves the location off the grid, and the ship still ends inside the sphere.

The client's pilot then flies two ships itself, through the jump action and the
confirmation dialog: a blind jump of 2000 km and a jump to a GPS 2600 km away, both
from inside the sphere. Both arrive at the 450 km sphere.

Then the world edge moves what is beyond the world radius back onto it: a grid saved
at 520 km and the character teleported to 530 km end up just inside 500 km.

The arrival positions are read from the world the server saves at the end.

    OCT_TASK=SE-752 uv run --no-project --with httpx python tests/test_jump.py
"""

from __future__ import annotations

import math
import os
import re
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-752")
import rig  # noqa: E402
from se_autopilot import enter_cockpit  # noqa: E402
from test_client import log  # noqa: E402

MIN_JUMP, MAX_JUMP = 5_000, 2_000_000
DRIVE = (0, 0, 2)
SHORTENED = "Jump shortened from 100.0 km to 49.6 km"
REFUSED = "Jump refused"
# name -> (entity id, position, outward direction, jump distance, expected end km)
SHIPS = {
    "Jump out": (777000752000001, (20_000, 0, 400_000), (0, 0, 1), 100_000, 450),
    "Jump inside": (777000752000002, (-300_000, 20_000, 0), (-1, 0, 0), 50_000, None),
    "Jump beyond": (
        777000752000003,
        (30_000, -30_000, -470_000),
        (0, 0, -1),
        20_000,
        None,
    ),
    "Jump far": (
        777000752000004,
        (-20_000, 60_000, 400_000),
        (0, 0, 1),
        4_000_000,
        450,
    ),
    "Jump obstacle": (777000752000005, (0, 200_000, 380_000), (0, 0, 1), 100_000, None),
}
# Flown by the client's pilot: name -> (entity id, position, forward, drive's jump
# distance, GPS target or None for a blind jump)
PILOTED = {
    "Pilot blind": (
        777000752000006,
        (0, -300_000, 200_000),
        (0, 0, 1),
        2_000_000,
        None,
    ),
    "Pilot GPS": (
        777000752000007,
        (100_000, -300_000, 0),
        (0, 0, 1),
        2_000_000,
        (100_000, -300_000, 2_600_000),
    ),
}
# Sits where Jump obstacle would stop at the sphere
OBSTACLE = ("Jump block", 777000752000021, (0, 200_000, 403_113))


PROBE = (
    "jump-probe",
    rig.PLUGIN / "tests" / "jump" / "probe",
    "JumpProbe.xml",
    "9B3E1D52-7C46-4A0B-8F21-5D7E0A752001",
)
# Saved beyond the world radius, moved back onto it
OUTSIDE = ("Outside 520", 777000752000020, (-312_000, 0, 416_000))


def ship(name: str, entity_id: int, position, forward, distance: float) -> str:
    ratio = min(1, (distance - MIN_JUMP) / (MAX_JUMP - MIN_JUMP))
    up = (0, 1, 0) if forward[1] == 0 else (1, 0, 0)
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
            rig.block(
                "MyObjectBuilder_JumpDrive",
                "LargeJumpDrive",
                DRIVE,
                f"<StoredPower>3</StoredPower><JumpRatio>{ratio:.9f}</JumpRatio>"
                "<Recharging>false</Recharging>",
            ),
        ],
        position,
        forward=forward,
        up=up,
    )


def saved_positions() -> dict[str, tuple[float, float, float]]:
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    found = {}
    for entity in sector.find("SectorObjects"):
        name = entity.findtext("DisplayName")
        if name in SHIPS or name in PILOTED or name == OUTSIDE[0]:
            p = entity.find("PositionAndOrientation/Position")
            found[name] = tuple(float(p.get(a)) for a in "xyz")
    return found


def chat_lines(remote, after: int) -> list[str]:
    reply = remote.get_chat_messages(after=after)
    return [m.get("Text", "") for m in reply.get("messages", [])] + [
        h.get("text", "") for h in reply.get("hud", [])
    ]


def jump(remote, name: str) -> list[str]:
    """Blind jumps the ship forward by its distance; returns the chat lines that came"""
    entity_id, _, _, distance, _ = SHIPS[name]
    before = set(chat_lines(remote, 0))
    reply = " ".join(rig.chat(remote, f"!jprobe jump {entity_id} {distance}", 3))
    assert "REQUESTED" in reply, reply
    # The 10 s countdown and the jump
    time.sleep(20)
    lines = [line for line in chat_lines(remote, 0) if line not in before]
    log(f"{name}: {lines}")
    return lines


def pick_gps(remote, target) -> None:
    """Selects the GPS as the jump target in the drive's terminal page"""
    remote.add_gps("Far target", *target)
    remote.key("K")
    remote.wait_for_screen(has_focus=True, max_wait=20)
    time.sleep(1)
    remote.control_set("TerminalTabs", 0)
    remote.control_click(name="FunctionalBlockListbox", text="Jump Drive")
    time.sleep(1)
    remote.control_click(name="GpsList", text="Far target")
    remote.control_click(name="SelectBtn")
    time.sleep(1)
    remote.key("Escape")
    time.sleep(1)


def pilot_jump(remote, name: str) -> list[str]:
    """The pilot jumps the ship as a player does; returns the chat lines that came"""
    entity_id, position, _, _, gps = PILOTED[name]
    if remote.get_character().get("state") == "sitting":
        remote.character_use()
        time.sleep(3)
    # A far grid reaches the client only once the character is near it
    remote.character_teleport(position[0], position[1] + 30, position[2])
    time.sleep(10)
    enter_cockpit(remote, entity_id)
    if gps:
        pick_gps(remote, gps)
    before = set(chat_lines(remote, 0))
    remote.apply_action(entity_id, DRIVE, "Jump")
    box = remote.wait_for_screen("MyGuiScreenMessageBox", max_wait=20)
    log(f"{name}: {box[0]}")
    remote.control_click(text="Yes")
    time.sleep(20)
    lines = [line for line in chat_lines(remote, 0) if line not in before]
    log(f"{name}: {lines}")
    return lines


def main() -> None:
    grids = [ship(n, i, p, f, d) for n, (i, p, f, d, _) in SHIPS.items()]
    grids += [ship(n, i, p, f, d) for n, (i, p, f, d, _) in PILOTED.items()]
    grids.append(rig.grid(OUTSIDE[0], OUTSIDE[1], rig.small_ship_blocks(), OUTSIDE[2]))
    grids.append(
        rig.grid(OBSTACLE[0], OBSTACLE[1], rig.small_ship_blocks(), OBSTACLE[2])
    )
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(grids)
    rig.write_plugin_config(
        re.sub(
            r"\s*<WorldEdge\w*>.*?</WorldEdge\w*>",
            "",
            rig.CONFIG_SOURCE.read_text(encoding="utf-8"),
        )
    )
    rig.prepare_server(extra_dev_folders=[PROBE])
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    rig.ensure_character(remote)

    results = {name: jump(remote, name) for name in SHIPS}
    results |= {name: pilot_jump(remote, name) for name in PILOTED}
    if remote.get_character().get("state") == "sitting":
        remote.character_use()
        time.sleep(3)
    remote.character_teleport(0, -530_000, 0)
    time.sleep(10)
    character_km = math.dist(remote.get_character()["position"], (0, 0, 0)) / 1000
    log(f"character teleported to 530 km, now at {character_km:.3f} km")
    rig.stop_client()
    rig.stop_server()
    log("server stopped and saved")

    saved = saved_positions()
    failures = []

    def check(ok: bool, what: str) -> None:
        log(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    for name, (_, start, forward, distance, end_km) in SHIPS.items():
        km = math.dist(saved[name], (0, 0, 0)) / 1000
        moved = math.dist(saved[name], start) / 1000
        log(f"{name}: {km:.3f} km from the center, moved {moved:.3f} km")
        text = " | ".join(results[name])
        if name == "Jump out":
            check(abs(km - 450) < 0.5, f"{name} arrives at 450 km ({km:.3f})")
            check(SHORTENED in text, f"{name} told it was shortened")
        elif name == "Jump far":
            check(abs(km - 450) < 0.5, f"{name} arrives at 450 km ({km:.3f})")
            check(SHORTENED[:14] in text, f"{name} told it was shortened")
        elif name == "Jump obstacle":
            check(km <= 450.001, f"{name} ends inside the sphere ({km:.3f})")
        elif name == "Jump inside":
            expected = math.dist(start, (0, 0, 0)) / 1000 + distance / 1000
            check(
                abs(km - expected) < 0.5,
                f"{name} arrives at {expected:.1f} km ({km:.3f})",
            )
            check(
                SHORTENED[:14] not in text and REFUSED not in text,
                f"{name} gets no message",
            )
        else:
            check(moved < 0.5, f"{name} stays put (moved {moved:.3f} km)")
            check(REFUSED in text, f"{name} told it was refused")
    for name in PILOTED:
        km = math.dist(saved[name], (0, 0, 0)) / 1000
        check(abs(km - 450) < 0.5, f"{name} arrives at 450 km ({km:.3f})")
        check(
            SHORTENED[:14] in " | ".join(results[name]), f"{name} told it was shortened"
        )
    km = math.dist(saved[OUTSIDE[0]], (0, 0, 0)) / 1000
    check(499 < km <= 500, f"{OUTSIDE[0]} moved back onto the sphere ({km:.3f} km)")
    check(499 < character_km <= 500.01, f"character moved back ({character_km:.3f} km)")
    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
