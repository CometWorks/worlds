"""Checks the jump drive limit on the test server with the headless client. Three
ships with a charged jump drive and a remote control wait in empty space; the client
takes control of each in turn and blind jumps it forward through the vanilla flow
(terminal action, confirmation box, server request):

- Jump out: from 400 km straight outward by 100 km. Shortened, it arrives at the
  450 km sphere, and the player is told.
- Jump inside: from 300 km outward by 50 km. Arrives at 350 km, no message.
- Jump beyond: from 470 km outward by 20 km. Refused, the ship stays, and the
  player is told.

The arrival positions are read from the world the server saves at the end.

    OCT_TASK=SE-752 uv run --no-project --with httpx python tests/test_jump.py
"""

from __future__ import annotations

import math
import os
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-752")
import rig
from se_remote import SetOp
from test_client import log

MIN_JUMP, MAX_JUMP = 5_000, 2_000_000
DRIVE = (0, 0, 2)
SHORTENED = "Jump shortened from 100.0 km to 50.0 km"
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
}


def ship(name: str, entity_id: int, position, forward, distance: float) -> str:
    ratio = (distance - MIN_JUMP) / (MAX_JUMP - MIN_JUMP)
    up = (0, 1, 0) if forward[1] == 0 else (1, 0, 0)
    return rig.grid(
        name,
        entity_id,
        [
            rig.block(
                "MyObjectBuilder_RemoteControl",
                "LargeBlockRemoteControl",
                (0, 0, 0),
                f"<EntityId>{entity_id + 10}</EntityId>",
            ),
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
        if name in SHIPS:
            p = entity.find("PositionAndOrientation/Position")
            found[name] = tuple(float(p.get(a)) for a in "xyz")
    return found


def chat_lines(remote, after: int) -> list[str]:
    reply = remote.get_chat_messages(after=after)
    return [m.get("Text", "") for m in reply.get("messages", [])] + [
        h.get("text", "") for h in reply.get("hud", [])
    ]


def jump(remote, name: str) -> list[str]:
    """Blind jumps the ship from its remote control; returns the chat lines that came"""
    entity_id = SHIPS[name][0]
    control = remote.take_remote_control(entity_id, entity_id + 10, timeout=60)
    assert control.get("controlledByMe"), control
    latest = remote.get_chat_messages().get("latest", 0)
    remote.batch(sets=[SetOp.action(entity_id, DRIVE, "Jump")]).set(0)
    # The confirmation box, then the 10 s countdown
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        boxes = [
            s for s in remote.list_screens() if s.get("type") == "MyGuiScreenMessageBox"
        ]
        if boxes:
            remote.control_click(text="Yes", screen=boxes[0]["index"])
            break
        time.sleep(0.5)
    else:
        raise AssertionError(f"{name}: no confirmation box")
    time.sleep(20)
    lines = chat_lines(remote, latest)
    log(f"{name}: {lines}")
    return lines


def main() -> None:
    grids = [ship(n, i, p, f, d) for n, (i, p, f, d, _) in SHIPS.items()]
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(grids)
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    rig.ensure_character(remote)

    results = {name: jump(remote, name) for name in SHIPS}
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
        elif name == "Jump inside":
            expected = math.dist(start, (0, 0, 0)) / 1000 + distance / 1000
            check(
                abs(km - expected) < 0.5,
                f"{name} arrives at {expected:.1f} km ({km:.3f})",
            )
            check("Jump " not in text, f"{name} gets no message")
        else:
            check(moved < 0.5, f"{name} stays put (moved {moved:.3f} km)")
            check(REFUSED in text, f"{name} told it was refused")
    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
