"""Checks the free-build limits at their defaults with the headless client: the
world's config sets neither FreeBuildPcuLimit nor StarterShipMaxShipPcu, so both are
10,000 PCU. Hangar runs on the server, as in tests/test_client.py, with a 9,950 PCU
ship of the player's.

- At a table holding a 10,725 PCU blueprint, !oct ship is refused: a starter ship
  has at most 10,000.
- At a table holding the 62 PCU starter test blueprint the player gets the ship and
  then has 10,012 PCU, so another !oct ship (cooldown off) is refused and the
  shipyard quotes a paid build, both naming the 10,000 PCU limit.

The old defaults, 20,000 each, pass all three.

    uv run --no-project --with httpx python tests/test_free_build.py
"""

from __future__ import annotations

import os
import re
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-680")
import rig  # noqa: E402
import test_client as tc  # noqa: E402

LIMIT = "10,000"
HANGAR_PCU = 9_950
STARTER_PCU = 62
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
started = time.monotonic()


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def table_blueprints() -> dict[tuple[int, tuple[int, int, int]], str]:
    """(grid id, cell) of each station table -> the name of the blueprint loaded into it"""
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    names = {}
    for grid in sector.find("SectorObjects"):
        blocks = grid.find("CubeBlocks")
        if blocks is None:
            continue
        for block in blocks:
            if block.findtext("SubtypeName") != "LargeBlockConsole":
                continue
            cell = tuple(int(block.find("Min").get(a)) for a in "xyz")
            names[(int(grid.findtext("EntityId")), cell)] = block.findtext(
                "ProjectedGrids/MyObjectBuilder_CubeGrid/DisplayName"
            )
    return names


def table_with(remote, blueprints, name: str) -> dict:
    found = [
        t
        for t in tc.tables(remote)
        if blueprints.get((t["gridId"], tuple(t["min"]))) == name
    ]
    assert found, f"no table near the player holds {name}"
    return min(found, key=lambda t: t["distance"])


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    # The tables take the two blueprints in turn, so every station has both
    rig.prepare_world(table_blueprint=[BIG, tc.BLUEPRINT])
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    assert "FreeBuildPcuLimit" not in config and "StarterShipMaxShipPcu" not in config
    rig.write_plugin_config()
    (rig.SERVER_DATA / "Hangar.cfg").write_text(
        tc.HANGAR_CONFIG.replace(
            f"<Pcu>{tc.HANGAR_PCU}</Pcu>", f"<Pcu>{HANGAR_PCU}</Pcu>"
        ),
        encoding="utf-8",
    )
    blueprints = table_blueprints()
    rig.prepare_server(extra_dev_folders=[tc.HANGAR])
    rig.start_server()
    log("server ready")
    remote = rig.start_client()

    tc.stand_by(remote, table_with(remote, blueprints, "Free Build Big"))
    reply = tc.ship_reply(remote)
    assert f"has {BIG_PCU:,} PCU, a starter ship at most {LIMIT}" in reply, reply

    tc.stand_by(remote, table_with(remote, blueprints, "Starter Test"))
    reply = tc.ship_reply(remote)
    assert "waits outside" in reply, reply

    owned = HANGAR_PCU + STARTER_PCU
    tc.with_config(remote, StarterShipCooldownMinutes=0)
    reply = tc.ship_reply(remote)
    assert f"You have {owned:,} PCU" in reply and f"under {LIMIT} PCU" in reply, reply
    reply = tc.ship_reply(remote, "!oct order")
    assert (
        f"a paid build (you have {owned:,} PCU" in reply and f"under {LIMIT})" in reply
    ), reply

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
