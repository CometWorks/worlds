"""Checks that an admin with the game's "Ignore PCU limits" on (Alt+F10, set through
Remote) skips the free-build cool-downs and FreeBuildPcuLimit, with the headless
client on the shipyard test server of tests/test_shipyard_order.py ("Order Test",
10 blocks, in every table).

Two server starts on one world copy:
- FreeBuildPcuLimit 0. Flag off: the shipyard quotes a paid build for the PCU limit
  and !oct ship is refused. Flag on: two starter ships in a row (no PCU limit, no
  cool-down), then a free build that completes and records no cool-down, and another
  free order (canceled). Flag off again: the starter ship waits for its cool-down,
  the quote is paid again, and with the default config a free order goes in (canceled).
  The saved world has no cool-down for the player.
- The finished ship taken out of the save. Flag off: a free build that completes,
  then the quote is paid for the 10 minute cool-down. Flag on: a free order
  (canceled). The saved world has the cool-down.

    OCT_WORLD_SOURCE=<world built by build_world.py> \\
    uv run --no-project --with httpx python tests/test_ignore_pcu.py

Needs what tests/test_projection_limits.py needs.
"""

from __future__ import annotations

import os
import re
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-719")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_free_limits as fl  # noqa: E402
import test_shipyard_order as so  # noqa: E402

NAME = "Order Test"
FREE = "a free build."
PCU_LIMIT = "a paid build (you have"
COOLDOWN = "a paid build (your next free ship is in 10 min)"
# The server's log lines of the exemption
EXEMPT = re.compile(r"ignor(es|ing) PCU limits")
started = time.monotonic()

# The plugin quotes the table's shown quote again every 10 s, so after a rule change the
# screen may already show the new quote, and a press orders it instead of quoting. A
# free build ordered and canceled leaves no quote shown, so the next press quotes.


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


def paid_quote(remote, text: str) -> None:
    """With no quote shown, a press quotes"""
    reply = fl.order(remote)
    assert "Your quote for" in reply and text in reply, reply


def order_free(remote) -> None:
    """Presses until a free build is in the queue"""
    reply = fl.order(remote)
    if "is in the queue" not in reply:
        assert "Your quote for" in reply and FREE in reply, reply
        reply = fl.order(remote)
    assert f"is in the queue, {FREE}" in reply, reply


def free_quote(remote) -> None:
    order_free(remote)
    reply = fl.order(remote)
    assert "left the queue" in reply, reply


def build(remote) -> None:
    """Orders a free build and waits until the ship is ready"""
    ready = chat_count(remote, "is ready")
    order_free(remote)
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
    return [line for line in rig.server_game_log().splitlines() if EXEMPT.search(line)]


def without_limits() -> int:
    """Returns the player's identity id"""
    so.prepare(so.SMALL, 60)
    remote = fl.start()
    tc.with_config(remote, FreeBuildPcuLimit=0)

    ignore_pcu(remote, False)
    paid_quote(remote, PCU_LIMIT)
    reply = tc.ship_reply(remote)
    assert "Starter ships are for players under" in reply, reply

    ignore_pcu(remote, True)
    for _ in range(2):
        assert "waits outside" in tc.ship_reply(remote), "no PCU limit or cool-down"
    build(remote)
    free_quote(remote)

    ignore_pcu(remote, False)
    assert "ready in" in tc.ship_reply(remote), "the starter ship's cool-down"
    paid_quote(remote, PCU_LIMIT)
    tc.with_config(remote)
    free_quote(remote)
    fl.stop()

    lines = exempt_lines()
    log(f"server log: {lines}")
    assert any("Starter ship" in line for line in lines), lines
    assert any(f"'{NAME}' is free" in line for line in lines), lines
    assert any(f"no cool-down after '{NAME}'" in line for line in lines), lines
    player = so.player_id((rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8"))
    assert f"{player}:" not in free_prints(), free_prints()
    return player


def remove_ships() -> None:
    """Takes the player's ships named NAME, the starter ships too, out of the saved world"""
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    ids = {
        o.findtext("EntityId")
        for o in ET.fromstring(text).find("SectorObjects")
        if o.findtext("DisplayName") == NAME
    }
    log(f"removing {len(ids)} ships")
    for id_ in ids:
        text, count = re.subn(
            r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">\s*'
            r"<SubtypeName />\s*"
            rf"<EntityId>{id_}</EntityId>.*?</MyObjectBuilder_EntityBase>\s*",
            "",
            text,
            flags=re.S,
        )
        assert count == 1, (id_, count)
    sector.write_text(text, encoding="utf-8")


def cooldown(player: int) -> None:
    remove_ships()
    fl.set_tables(so.SMALL)
    remote = fl.start()

    ignore_pcu(remote, False)
    build(remote)
    paid_quote(remote, COOLDOWN)

    ignore_pcu(remote, True)
    free_quote(remote)
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
