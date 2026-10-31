"""Checks the limits on free shipyard builds with the headless client, on the
shipyard test server of tests/test_shipyard_order.py: the cool-down between free
builds (FreeBuildCooldownMinutes, 10) and the blocks a free build can't have
(FreeBuildBlockedBlocks).

Three server starts on one world copy, the tables loaded with another blueprint
before each:
- "Order Test" (10 blocks): the order completes, and the next one is a paid build
  for the 10 minute cool-down.
- "Free Limits Blocked" (a jump drive, a large ion thruster and a prototech
  battery): the quote is still paid for the cool-down, the world kept it over the
  restart. With the cool-down at 0 it is paid for the three blocks.
- "Order Big" (1,536 blocks), the cool-down at 0: pressing again while it builds
  doesn't cancel it.

    OCT_WORLD_SOURCE=<world built by build_world.py> \\
    uv run --no-project --with httpx python tests/test_free_limits.py

Needs what tests/test_projection_limits.py needs.
"""

from __future__ import annotations

import os
import re
import time

os.environ.setdefault("OCT_TASK", "SE-694")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402

BLOCKED = rig.grid(
    "Free Limits Blocked",
    777000694000100,
    [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_JumpDrive", "LargeJumpDrive", (0, 0, 2)),
        rig.block("MyObjectBuilder_Thrust", "LargeBlockLargeThrust", (0, 0, 6)),
        rig.block(
            "MyObjectBuilder_BatteryBlock", "LargeBlockPrototechBattery", (0, 0, 10)
        ),
    ],
    tag="MyObjectBuilder_CubeGrid",
)
COOLDOWN = "next free ship is in"
NO_BLUEPRINT = "Repair slot"
started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def order(remote) -> str:
    """Presses the button of the nearest table. An order clears its table (SE-773), and a
    press there books a repair slot, so that is canceled and this goes on to the other
    stations' tables"""
    reply = tc.ship_reply(remote, "!oct order")
    stations = iter(sorted(sa.station_tables()))
    while NO_BLUEPRINT in reply:
        tc.ship_reply(remote, "!oct order")
        sa.at_table(remote, next(stations))
        reply = tc.ship_reply(remote, "!oct order")
    return reply


def wait_chat_count(remote, text: str, count: int, timeout: float) -> None:
    """Waits until the chat has count lines with text"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lines, _ = so.chat_lines(remote, 0)
        if sum(text in line for line in lines) >= count:
            return
        time.sleep(10)
    raise TimeoutError(f"fewer than {count} chat lines with {text!r}")


def set_tables(blueprint: str, remove: str | None = None) -> None:
    """Loads the blueprint into every table of the saved world, and removes the grid
    named remove from it"""
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    if remove:
        text, count = re.subn(
            r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
            r"(?:(?!</MyObjectBuilder_EntityBase>).)*?"
            rf"<DisplayName>{remove}</DisplayName>.*?</MyObjectBuilder_EntityBase>\s*",
            "",
            text,
            flags=re.S,
        )
        assert count == 1, (remove, count)
    # A cleared table (SE-773) has no blueprint to replace
    text = re.sub(
        r"<ProjectedGrids[^>]*/>|<ProjectedGrids>.*?</ProjectedGrids>",
        "",
        text,
        flags=re.S,
    )
    text, count = re.subn(
        r"(<SubtypeName>LargeBlockConsole</SubtypeName>.*?)<Enabled>(?:true|false)</Enabled>",
        lambda m: f"{m[1]}<Enabled>true</Enabled><ProjectedGrids>{blueprint}</ProjectedGrids>",
        text,
        flags=re.S,
    )
    assert count == 7, count
    sector.write_text(text, encoding="utf-8")
    # The server would load its binary save instead
    sector.with_suffix(".sbsB5").unlink(missing_ok=True)


def start() -> object:
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    # After a restart the station may stream in a little after the character
    for _ in range(12):
        if tables := tc.tables(remote):
            break
        time.sleep(5)
    log(f"character at {remote.get_character()['position']}, {len(tables)} tables near")
    tc.stand_by(remote, min(tables, key=lambda b: b["distance"]))
    return remote


def stop() -> None:
    rig.stop_client()
    rig.stop_server()


def completed_ship() -> None:
    """A completed order starts the cool-down"""
    so.prepare(so.SMALL, 60)
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "FreeBuild" not in config, "the world's config sets no free-build option"
    remote = start()
    so.place(remote, "Order Test")
    wait_chat_count(remote, "is ready", 1, 900)
    reply = order(remote)
    assert re.search(COOLDOWN + " (9|10) min", reply), reply
    # The default could run out before the next server start is up
    tc.with_config(remote, FreeBuildCooldownMinutes=60)
    stop()


def blocked_blocks() -> None:
    """The cool-down survives a restart; blocked blocks are refused"""
    assert any(
        "Octahedron-FreePrints" in path.read_text(encoding="utf-8", errors="ignore")
        for path in rig.WORLD.glob("*.sb[cs]")
    ), "the cool-down is saved with the world"
    set_tables(BLOCKED)
    remote = start()
    reply = order(remote)
    assert COOLDOWN in reply, f"the cool-down survives the restart: {reply}"
    tc.with_config(remote, FreeBuildCooldownMinutes=0)
    reply = order(remote)
    # The screen may show the new quote already, then the press tries to order it
    assert "paid build (a free ship can't have" in reply, reply
    for name in ("Jump Drive", "Ion Thruster", "Prototech Battery"):
        assert name in reply, (name, reply)
    stop()


def one_at_a_time() -> None:
    """A press while the player's build runs cancels it, it can't queue a second one"""
    # The finished ship would be in the way
    set_tables(so.BIG, remove="Order Test")
    remote = start()
    tc.with_config(remote, FreeBuildCooldownMinutes=0)
    so.place(remote, "Order Big")
    reply = order(remote)
    assert "is canceled and removed" in reply, reply
    stop()


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    completed_ship()
    blocked_blocks()
    one_at_a_time()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    assert not errors, errors


if __name__ == "__main__":
    try:
        main()
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed")
