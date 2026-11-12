"""Grid moves on a plain server (SE-1369): the GridMoves of the config turn the shipyards of a kept
world to where a world built now has them, once each, and only while the shipyard is idle.

1. A world built from OCT_OLD_WORLDS (the commit the live worlds were built from) with the config
   of this working copy and a grid in Gamma Station's shipyard: the server moves the other
   shipyards and their rotor heads to their places in a world built now, logs APPLIED, and
   leaves Gamma's where it was, WAITING for the grid.
2. Started again without that grid: Gamma's shipyard moves, the others stay.
3. Started again: nothing moves.
4. A world built from this working copy (rig.WORLD_SOURCE): every shipyard is where the moves
   put it, so each logs THERE and nothing moves.

Each step stops the server, which saves, and compares the saved shipyards and rotor heads with
the worlds built from OLD and from this working copy.

    OCT_TASK=SE-1369m [OCT_OLD_WORLDS=70fcefb] uv run --no-project --with httpx python tests/test_grid_moves.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1369m")
os.environ.setdefault("OCT_OLD_WORLDS", "70fcefb")
import rig  # noqa: E402
from test_client import log  # noqa: E402
from test_world_patch import old_world  # noqa: E402

XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
BLOCKED = "Gamma Station Shipyard"
OBSTACLE = "Move Obstacle"
# The middle of the shipyard blueprint's build space (ShipyardOrders.FreeSpace), in its cells
BUILD_SPACE_CELL = (0, -16, 0)
TIMEOUT = 180.0

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def moves() -> dict[str, str]:
    """Grid name by move id, from the config"""
    config = ET.parse(rig.CONFIG_SOURCE).getroot()
    return {
        e.findtext("Id"): e.findtext("Grid")
        for e in config.iterfind("GridMoves/GridMoveEntry")
    }


def placements(world: Path) -> dict[str, list[tuple]]:
    """(position, forward, up) of the shipyards and rotor heads of a world, by name"""
    sector = ET.parse(world / "SANDBOX_0_0_0_.sbs").getroot()
    out: dict[str, list[tuple]] = {}
    for e in sector.iterfind("SectorObjects/MyObjectBuilder_EntityBase"):
        name = e.findtext("DisplayName") or ""
        if e.get(XSI_TYPE) != "MyObjectBuilder_CubeGrid" or not (
            name.endswith(" Shipyard") or name == "Subgrid"
        ):
            continue
        where = e.find("PositionAndOrientation")
        out.setdefault(name, []).append(
            tuple(
                tuple(float(where.find(p).get(k)) for k in "xyz")
                for p in ("Position", "Forward", "Up")
            )
        )
    return out


def same(a: tuple, b: tuple) -> bool:
    return all(
        abs(x - y) < (0.05 if i == 0 else 0.001)
        for i, (u, v) in enumerate(zip(a, b))
        for x, y in zip(u, v)
    )


def matches(saved: list[tuple], wanted: list[tuple]) -> int:
    """How many of wanted have a placement in saved"""
    return sum(any(same(s, w) for s in saved) for w in wanted)


def obstacle(world: Path) -> str:
    """A one block grid in the middle of the blocked shipyard's build space"""
    yard = placements(world)[BLOCKED][0]
    position, forward, up = yard
    x, y, z = BUILD_SPACE_CELL
    right = (
        forward[1] * up[2] - forward[2] * up[1],
        forward[2] * up[0] - forward[0] * up[2],
        forward[0] * up[1] - forward[1] * up[0],
    )
    center = tuple(
        position[i] + 2.5 * (x * right[i] + y * up[i] - z * forward[i])
        for i in range(3)
    )
    armor = rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0))
    return rig.grid(OBSTACLE, None, [armor], position=center)


def run(label: str, done) -> list[str]:
    """Starts the server on the prepared world until done(lines) holds, stops it: its grid move lines"""
    rig.start_server()
    try:
        rig.wait_until(
            lambda: done(lines(rig.server_game_log())),
            TIMEOUT,
            f"{label}: the grid move lines",
        )
        found = lines(rig.server_game_log())
        log("\n".join(found))
        return found
    finally:
        rig.stop_server()


def lines(text: str) -> list[str]:
    return [
        l.split("Grid move ", 1)[1].strip()
        for l in text.splitlines()
        if "Grid move " in l
    ]


def main() -> int:
    rig.stop_server()
    wanted = moves()
    if not wanted:
        print("no GridMoves in the config: nothing to test", flush=True)
        return 1
    old = old_world()
    new = rig.world_source()
    before, after = placements(old), placements(new)
    moved = set(wanted.values())
    heads_new = [
        p for p in after["Subgrid"] if not any(same(p, q) for q in before["Subgrid"])
    ]
    check(
        all(not same(before[n][0], after[n][0]) for n in moved),
        f"the world of {os.environ['OCT_OLD_WORLDS']} has {len(moved)} shipyards elsewhere, "
        f"{len(heads_new)} rotor heads",
    )
    rig.write_plugin_config()

    # 1. The kept world, with a grid in Gamma Station's shipyard
    os.environ["OCT_WORLD_SOURCE"] = str(old)
    rig._world_source = None
    rig.prepare_world(extra_grids=[obstacle(old)])
    rig.prepare_server()
    found = run(
        "old world",
        lambda ls: sum(l.startswith("APPLIED") for l in ls) >= len(moved) - 1
        and any(l.startswith("WAITING") for l in ls),
    )
    applied = [l for l in found if l.startswith("APPLIED")]
    check(
        len(applied) == len(moved) - 1 and not any(BLOCKED in l for l in applied),
        f"{len(applied)} shipyards moved, not {BLOCKED}",
    )
    check(
        any(l.startswith("WAITING") and BLOCKED in l and OBSTACLE in l for l in found),
        f"{BLOCKED} waits for {OBSTACLE}",
    )
    saved = placements(rig.WORLD)
    check(
        all(same(saved[n][0], after[n][0]) for n in moved - {BLOCKED}),
        "the moved shipyards are where a world built now has them",
    )
    check(same(saved[BLOCKED][0], before[BLOCKED][0]), f"{BLOCKED} stayed")

    # 2. Without the grid
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text, removed = re.subn(
        r"<MyObjectBuilder_EntityBase [^>]*>(?:(?!</MyObjectBuilder_EntityBase>).)*?"
        rf"<DisplayName>{OBSTACLE}</DisplayName>.*?</MyObjectBuilder_EntityBase>\s*",
        "",
        text,
        flags=re.S,
    )
    sector.write_text(text, encoding="utf-8")
    # The game loads its binary copy of the sector if there is one
    (rig.WORLD / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    check(removed == 1, f"{OBSTACLE} removed from the saved world")
    found = run("obstacle gone", lambda ls: any(l.startswith("APPLIED") for l in ls))
    applied = [l for l in found if l.startswith("APPLIED")]
    check(
        len(applied) == 1 and BLOCKED in applied[0],
        f"only {BLOCKED} moved: {len(applied)} APPLIED",
    )
    saved = placements(rig.WORLD)
    check(
        all(same(saved[n][0], after[n][0]) for n in moved)
        and all(
            same(saved[n][0], after[n][0]) for n in after if n.endswith(" Shipyard")
        ),
        "every shipyard is where a world built now has it",
    )
    check(
        matches(saved["Subgrid"], after["Subgrid"]) == len(after["Subgrid"]),
        f"every rotor head went with its shipyard: {matches(saved['Subgrid'], heads_new)}/{len(heads_new)} moved ones",
    )

    # 3. Started again
    found = run("restart", lambda ls: any(l.startswith("READY") for l in ls))
    check(
        not any(l.startswith(("APPLIED", "WAITING", "THERE")) for l in found),
        "nothing moves on a restart",
    )
    again = placements(rig.WORLD)
    check(
        all(matches(again[n], saved[n]) == len(saved[n]) for n in saved),
        "the shipyards and rotor heads stay put",
    )

    # 4. A world built now
    os.environ.pop("OCT_WORLD_SOURCE")
    rig._world_source = None
    rig.prepare_world()
    found = run(
        "new world", lambda ls: sum(l.startswith("THERE") for l in ls) >= len(moved)
    )
    check(
        sum(l.startswith("THERE") for l in found) == len(moved)
        and not any(l.startswith(("APPLIED", "WAITING")) for l in found),
        f"a world built now: {len(moved)} THERE, nothing moved",
    )
    check(
        all(same(placements(rig.WORLD)[n][0], after[n][0]) for n in moved),
        "its shipyards stay as built",
    )

    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} grid move checks passed", flush=True
    )
    for what in failed:
        print("FAILED", what, flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
