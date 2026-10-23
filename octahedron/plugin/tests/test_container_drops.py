"""Checks ContainerDropsPersonal on the test server with the headless client: with it
on, !oct drop spawns a personal Unknown Signal (the known positive); with it off, the
same command spawns nothing, and !oct drop strong still spawns a Strong Unknown
Signal. The game's own drop timer is pushed out of the run, so the saved world holds
exactly the drops the commands made. Prints what it saw; the asserts stop it at the
first rule broken.

    uv run --no-project --with httpx python tests/test_container_drops.py
"""

from __future__ import annotations

import re
import time

import rig
from test_client import log, with_config

SMALL = "Unknown Signal"
STRONG = "Strong Unknown Signal"
REFUSED = "Personal container drop (Unknown Signal) refused"
REFUSALS = 3


def drop(remote, kind: str = "") -> str:
    reply = " ".join(rig.chat(remote, f"!oct drop {kind}".strip(), 5))
    log(f"!oct drop {kind}: {reply}")
    return reply


def saved_drops() -> dict[str, int]:
    """Saved grids named like a drop pod, by name"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    names = re.findall(r"<DisplayName>([^<]*)</DisplayName>", text)
    return {name: names.count(name) for name in (SMALL, STRONG)}


def plugin_log() -> str:
    return (
        "".join(
            path.read_text(errors="replace")
            for path in (rig.SERVER_LOG, rig.SERVER_CONFIG / "info.log")
            if path.exists()
        )
        + rig.server_game_log()
    )


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    # The game's timer would add drops of its own; 10000 min keeps it out of the run
    rig.prepare_world(
        settings={
            "EnableContainerDrops": "true",
            "MinDropContainerRespawnTime": 10000,
            "MaxDropContainerRespawnTime": 10000,
        }
    )
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    log("server ready")
    remote = rig.start_client()
    rig.ensure_character(remote)
    before = plugin_log().count(REFUSED)

    with_config(remote, ContainerDropsPersonal="true")
    assert drop(remote) == f"{SMALL} dropped.", "personal drop with the rule off"

    with_config(remote, ContainerDropsPersonal="false")
    for _ in range(REFUSALS):
        assert drop(remote) == f"No {SMALL} dropped.", "personal drop refused"
    assert (
        drop(remote, "strong") == f"{STRONG} dropped."
    ), "strong drop with the rule on"

    # The prefab manager spawns asynchronously; give it time before the save
    time.sleep(20)
    rig.stop_client()
    rig.stop_server()
    log("server stopped and saved")

    refusals = plugin_log().count(REFUSED) - before
    drops = saved_drops()
    log(f"saved drops {drops}, refusals logged {refusals}")
    assert refusals == REFUSALS, refusals
    assert drops == {SMALL: 1, STRONG: 1}, drops
    log("PASS")


if __name__ == "__main__":
    main()
