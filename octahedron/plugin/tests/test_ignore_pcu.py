"""Checks that an admin with the game's "Ignore PCU limits" on (Alt+F10, set through
Remote) skips the free-build cool-downs and FreeBuildPcuLimit, with the headless
client on the shipyard test server of tests/test_shipyard_order.py ("Order Test",
10 blocks, in every table).

Two server starts on one world copy:
- FreeBuildPcuLimit 0. Flag off: the shipyard quotes a paid build for the PCU limit
  and !oct ship is refused. Flag on: two starter ships in a row (no PCU limit, no
  cool-down), then a free build that completes and records no cool-down. Flag off
  again: the starter ship waits for its cool-down, the quote is paid again, and with
  the default config the shipyard quotes a free build. The saved world has no cool-down for the player.
- The finished ship taken out of the save. Flag off: a free build that completes,
  then the quote is paid for the 60 minute cool-down. Flag on: a free quote.

    OCT_WORLD_SOURCE=<world built by build_world.py> \\
    uv run --no-project --with httpx python tests/test_ignore_pcu.py

Needs what tests/test_projection_limits.py needs.
"""

from __future__ import annotations

import os
import re
import time

os.environ.setdefault("OCT_TASK", "SE-719")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_free_limits as fl  # noqa: E402
import test_shipyard_order as so  # noqa: E402

NAME = "Order Test"
FREE = "a free build."
COOLDOWN = "next free ship is in"
EXEMPT = "ignores PCU limits"
started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def ignore_pcu(remote, on: bool) -> None:
    remote.set_admin_flag("ignorePcu", on)
    # The server gets the flag with the admin menu's event
    time.sleep(3)
    log(f"Ignore PCU limits {'on' if on else 'off'}")


def chat_count(remote, text: str) -> int:
    lines, _ = so.chat_lines(remote, 0)
    return sum(text in line for line in lines)


def build(remote) -> None:
    """Orders the quoted free build and waits until the ship is ready"""
    ready = chat_count(remote, "is ready")
    reply = fl.order(remote)
    assert "is in the queue" in reply, reply
    fl.wait_chat_count(remote, "is ready", ready + 1, 900)
    log("the ship is ready")


def free_prints() -> str:
    """The saved cool-downs, <identity>:<unix seconds>,..."""
    text = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    match = re.search(
        r"<Key>Octahedron-FreePrints</Key>\s*<Value[^>]*>([^<]*)</Value>", text
    )
    return match[1] if match else ""


def exempt_lines() -> list[str]:
    return [line for line in rig.server_game_log().splitlines() if EXEMPT in line]


def without_limits() -> int:
    """Returns the player's identity id"""
    so.prepare(so.SMALL, 60)
    remote = fl.start()
    tc.with_config(remote, FreeBuildPcuLimit=0)

    ignore_pcu(remote, False)
    reply = fl.order(remote)
    assert "paid build (you have" in reply, reply
    reply = tc.ship_reply(remote)
    assert "Starter ships are for players under" in reply, reply

    ignore_pcu(remote, True)
    for _ in range(2):
        assert "waits outside" in tc.ship_reply(remote), "no PCU limit or cool-down"
    reply = fl.order(remote)
    assert FREE in reply, reply
    build(remote)
    assert FREE in fl.order(remote), "a free quote right after the build"

    ignore_pcu(remote, False)
    assert "ready in" in tc.ship_reply(remote), "the starter ship's cool-down"
    # A paid quote in between, else the press would order the free build shown
    assert "paid build (you have" in fl.order(remote)
    tc.with_config(remote)
    reply = fl.order(remote)
    assert FREE in reply, f"the build started no cool-down: {reply}"
    fl.stop()

    lines = exempt_lines()
    log(f"server log: {lines}")
    assert any("Starter ship" in line for line in lines), lines
    assert any(f"'{NAME}' is free" in line for line in lines), lines
    assert any(f"no cool-down after '{NAME}'" in line for line in lines), lines
    player = so.player_id((rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8"))
    assert f"{player}:" not in free_prints(), free_prints()
    return player


def cooldown(player: int) -> None:
    fl.set_tables(so.SMALL, remove=NAME)
    remote = fl.start()

    ignore_pcu(remote, False)
    assert FREE in fl.order(remote)
    build(remote)
    reply = fl.order(remote)
    assert f"{COOLDOWN} 60 min" in reply, reply

    ignore_pcu(remote, True)
    reply = fl.order(remote)
    assert FREE in reply, f"no cool-down with the flag on: {reply}"
    ignore_pcu(remote, False)
    fl.stop()
    assert (
        f"{player}:" in free_prints()
    ), "a build without the flag starts the cool-down"


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    player = without_limits()
    cooldown(player)
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
