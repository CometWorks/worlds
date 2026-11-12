"""Checks the free-build rule at its defaults and with its options changed, with the
headless client: FreeBuildPcuLimit (30,000), FreeBuildExcludedComponents (the prototech
frame) and FreeBuildBlockedBlocks (none). StarterShipMaxShipPcu stays at its 10,000. Hangar
runs on the server, as in tests/test_client.py, with a 29,950 PCU ship of the player's. All
at the table near the player: it quotes a blueprint loaded into it, and again after each config
reload.

- At a 10,725 PCU blueprint !oct ship is refused: a starter ship has at most 10,000.
- A ship with superconductors (a jump drive, a large reactor) and a large ion thruster is
  free. Excluding the superconductor makes it paid for the two blocks that need it, and
  blocking JumpDrive/* for the jump drive alone.
- A ship with a prototech frame (a prototech battery) is paid. It is free with no excluded
  components, and paid again when FreeBuildBlockedBlocks names the battery.
- With 29,950 PCU a ship is free, and paid with the limit lowered to that.
- The 62 PCU starter ship takes the player to 30,012 PCU, so another !oct ship (cooldown
  off) is refused and the shipyard quotes a paid build, both naming the 30,000 PCU limit.
  With the limit raised by one PCU past that the quote is free.

    OCT_WORLD_SOURCE=<world built by build_world.py> \\
    uv run --no-project --with httpx python tests/test_free_build.py
"""

from __future__ import annotations

import os
import re
import time

os.environ.setdefault("OCT_TASK", "SE-680")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
from test_table_rules import blueprint_xml  # noqa: E402

LIMIT = "30,000"
STARTER_LIMIT = "10,000"
HANGAR_PCU = 29_950
STARTER_PCU = 62
FREE = "A free build."
PAID = "A paid build:"
CANT_HAVE = "a free ship can't have"
# 13 rocket launchers of 825 PCU each, one cell apart
BIG = rig.grid(
    "Free Build Big",
    777000680000100,
    [
        rig.block(
            "MyObjectBuilder_SmallMissileLauncher",
            "LargeMissileLauncher",
            (2 * x, 0, 0),
        )
        for x in range(13)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
BIG_PCU = 13 * 825


def ship(name: str, entity_id: int, *blocks) -> str:
    return rig.grid(
        name,
        entity_id,
        [
            rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
            *blocks,
        ],
        tag="MyObjectBuilder_CubeGrid",
    )


SUPERCONDUCTOR = ship(
    "Free Build Superconductor",
    777000927000100,
    rig.block("MyObjectBuilder_JumpDrive", "LargeJumpDrive", (0, 0, 2)),
    rig.block("MyObjectBuilder_Thrust", "LargeBlockLargeThrust", (0, 0, 6)),
    rig.block("MyObjectBuilder_Reactor", "LargeBlockLargeGenerator", (0, 0, 10)),
)
PROTOTECH = ship(
    "Free Build Prototech",
    777000927000200,
    rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockPrototechBattery", (0, 0, 2)),
)
# The tables would clear themselves during the run
HOLD = {"ShipyardTableMinutes": 600}
started = time.monotonic()
failed = []
passed = 0


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def check(ok: bool, what: str, detail: str = "") -> None:
    """Goes on after a failure, so a run shows every rule that is off"""
    global passed
    log(f"{'ok  ' if ok else 'FAIL'} {what}" + (f": {detail}" if not ok else ""))
    if ok:
        passed += 1
    else:
        failed.append(what)


def rules(remote, **values) -> None:
    tc.with_config(remote, **HOLD, **values)


def screen(remote) -> str:
    """The table's screen, whose text wraps, on one line"""
    text = "\n".join(rig.chat(remote, "!oct table show", 2))
    return " ".join(text.split("DETAILS")[0].split())


def load(remote, blueprint: str, name: str) -> None:
    """Loads the blueprint into the table, which quotes it for the player without a press. A
    press now would order it"""
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    block = remote.get_block(table["gridId"], tuple(table["min"]))
    remote.load_projection(block["entityId"], xml=blueprint_xml(blueprint))
    rig.wait_until(
        lambda: f"'{name}' for" in screen(remote), 30, f"the quote of {name}"
    )


def quote(
    remote, want: str, what: str, *names: str, absent: tuple[str, ...] = ()
) -> None:
    """The table quotes want and the names, without the absent ones, in time: the last config's
    quote stays until the next quote"""
    deadline = time.monotonic() + 25
    while True:
        text = screen(remote)
        ok = (
            want in text
            and all(n in text for n in names)
            and not any(n in text for n in absent)
        )
        if ok or time.monotonic() > deadline:
            break
        time.sleep(2)
    check(ok, what, text)


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(table_blueprint=BIG)
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    check(
        "<FreeBuildPcuLimit>30000<" in config
        and "<FreeBuildExcludedComponents>PrototechFrame<" in config
        and "<FreeBuildBlockedBlocks><" in config
        and "StarterShipMaxShipPcu" not in config,
        "the world's config names the free-build options, with their defaults",
    )
    rig.write_plugin_config()
    (rig.SERVER_DATA / "Hangar.cfg").write_text(
        tc.HANGAR_CONFIG.replace(
            f"<Pcu>{tc.HANGAR_PCU}</Pcu>", f"<Pcu>{HANGAR_PCU}</Pcu>"
        ),
        encoding="utf-8",
    )
    rig.prepare_server(extra_dev_folders=[tc.HANGAR])
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    rules(remote)
    # After the login the station may stream in a little after the character
    tables = rig.wait_until(lambda: tc.tables(remote), 60, "a table near the player")
    tc.stand_by(remote, min(tables, key=lambda b: b["distance"]))
    reply = tc.ship_reply(remote)
    check(
        f"has {BIG_PCU:,} PCU, a starter ship at most {STARTER_LIMIT}" in reply,
        "a starter ship keeps its limit",
        reply,
    )

    load(remote, SUPERCONDUCTOR, "Free Build Superconductor")
    quote(remote, FREE, "superconductors and a large ion thruster are free")
    rules(remote, FreeBuildExcludedComponents="Superconductor")
    quote(
        remote,
        CANT_HAVE,
        "excluding the superconductor makes it paid",
        "Jump Drive",
        "Large Reactor",
        absent=("Ion Thruster",),
    )
    rules(remote, FreeBuildBlockedBlocks="JumpDrive/*")
    quote(
        remote,
        CANT_HAVE,
        "blocking the jump drive makes it paid",
        "Jump Drive",
        absent=("Large Reactor", "Ion Thruster"),
    )
    rules(remote)

    load(remote, PROTOTECH, "Free Build Prototech")
    quote(remote, CANT_HAVE, "a prototech frame is paid", "Prototech Battery")
    rules(remote, FreeBuildExcludedComponents="")
    quote(remote, FREE, "with no excluded components it is free")
    rules(
        remote,
        FreeBuildExcludedComponents="",
        FreeBuildBlockedBlocks="BatteryBlock/*Prototech*",
    )
    quote(remote, CANT_HAVE, "blocking the battery makes it paid", "Prototech Battery")
    rules(remote)

    load(remote, tc.BLUEPRINT, "Starter Test")
    quote(remote, FREE, f"free with {HANGAR_PCU:,} PCU, under the limit")
    rules(remote, FreeBuildPcuLimit=HANGAR_PCU)
    quote(
        remote,
        f"you have {HANGAR_PCU:,} PCU, free builds are for players under {HANGAR_PCU:,}",
        "paid with the limit lowered to the player's PCU",
    )
    rules(remote)

    reply = tc.ship_reply(remote)
    check("waits outside" in reply, "a starter ship under the limit", reply)
    owned = HANGAR_PCU + STARTER_PCU
    rules(remote, StarterShipCooldownMinutes=0)
    reply = tc.ship_reply(remote)
    check(
        f"You have {owned:,} PCU" in reply and f"under {LIMIT} PCU" in reply,
        "no starter ship over the limit",
        reply,
    )
    quote(
        remote,
        f"you have {owned:,} PCU, free builds are for players under {LIMIT}",
        "paid over the limit",
    )
    rules(remote, FreeBuildPcuLimit=owned + 1)
    quote(remote, FREE, "free with the limit raised past the player's PCU")

    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    check(not errors, "no plugin errors in the server log", str(errors))
    print(f"{passed} of {passed + len(failed)} checks passed")
    assert not failed, failed


if __name__ == "__main__":
    try:
        main()
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed")
