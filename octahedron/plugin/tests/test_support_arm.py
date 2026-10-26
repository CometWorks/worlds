"""Checks the shipyard's support arm on the test server, with OmniArc, ToolCore and
Multigrid Projector as on the Octahedron servers, and the headless client, on a world
built by build_world.py with the final shipyard (its four arm rotors).

Each case orders a blueprint at another station's hologram table with !oct order (the
table's button for an admin) and follows the build with !oct shipyards:

- "dock" at Alpha Station: "ArmDock", a large grid ship with a connector at its back, an
  H2 tank and a battery, a paid build with the ice for its hydrogen in the player's
  ledger. The arm is placed as frames on a large arm rotor's head, with a connector; the
  projector is switched off until the welders complete it. The server restarts while the
  arm is built and picks the order up. When the ship is complete it docks to the arm's
  connector: the tank gets its 500,000 L, the battery is charged.
- "cap" at Beta Station: "ArmCap", the same ship without the connector, paid: the arm
  ends in a cap and the plugin fills the tank itself.
- "small" at Gamma Station: "ArmSmall", a small grid ship with a small connector, a free
  build on a small arm rotor's head.

After each build the arm and its projector are gone, the rotor head is still on its
rotor and dynamic again, and the ship is the player's dynamic ship. The saved world is
checked at the end.

    OCT_TASK=SE-693 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_support_arm.py [case ...]

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a projector that has to take an armor block's place (the planner's tests,
tests/arm/ArmTests, cover it), a cluster.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-693")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_shipyard_order as so  # noqa: E402
from test_client import log, with_config  # noqa: E402

ARMOR = "MyObjectBuilder_CubeBlock"


def hull(blocks: dict, size: tuple[int, int, int], subtype: str) -> list[str]:
    """Armor in a box, and the given blocks in place of some of it (None: left empty)"""
    out = []
    for x in range(size[0]):
        for y in range(size[1]):
            for z in range(size[2]):
                if (x, y, z) not in blocks:
                    out.append(rig.block(ARMOR, subtype, (x, y, z)))
    return out + [b for b in blocks.values() if b]


def large(name: str, entity: int, connector: bool) -> tuple[str, int]:
    special = {
        (0, 0, 2): rig.block(
            "MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 2)
        ),
        (0, 1, 2): None,  # the tank is two blocks high
        (2, 0, 2): rig.block(
            "MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (2, 0, 2)
        ),
    }
    if connector:
        # Flush with the back face, armor beside it: the arm's projector sits on that
        # armor next to the arm's connector, as in the "... Arm Exam" blueprint
        special[(1, 0, 5)] = rig.block(
            "MyObjectBuilder_ShipConnector", "Connector", (1, 0, 5), forward="Backward"
        )
    blocks = hull(special, (3, 2, 6), "LargeBlockArmorBlock")
    # The control station mounts only on its bottom: on the front, on top
    blocks.append(rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (1, 2, 0)))
    grid = rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid")
    return grid, len(blocks)


def small() -> tuple[str, int]:
    connector = rig.block(
        "MyObjectBuilder_ShipConnector", "ConnectorSmall", (1, 1, 7), forward="Backward"
    )
    blocks = hull({(1, 1, 7): connector}, (4, 3, 8), "SmallBlockArmorBlock")
    grid = rig.grid("ArmSmall", 777000693000300, blocks, tag="MyObjectBuilder_CubeGrid")
    return grid.replace(
        "<GridSizeEnum>Large</GridSizeEnum>", "<GridSizeEnum>Small</GridSizeEnum>"
    ), len(blocks)


DOCK, DOCK_BLOCKS = large("ArmDock", 777000693000100, True)
CAP, CAP_BLOCKS = large("ArmCap", 777000693000200, False)
SMALL, SMALL_BLOCKS = small()
# The tables in the world's order: North, South, Alpha, Beta, Gamma, Delta, Moon
TABLES = [DOCK, DOCK, DOCK, CAP, SMALL, DOCK, DOCK]
# name: (station, ship, blocks, paid, connector, rotor size)
CASES = {
    "dock": ("Alpha", "ArmDock", DOCK_BLOCKS, True, True, "Large"),
    "cap": ("Beta", "ArmCap", CAP_BLOCKS, True, False, "Large"),
    "small": ("Gamma", "ArmSmall", SMALL_BLOCKS, False, True, "Small"),
}
# For the paid builds: the components, and the ice for 500,000 L of hydrogen
DEPOSIT = {
    "Component/SteelPlate": 1500,
    "Component/Construction": 300,
    "Component/InteriorPlate": 200,
    "Component/LargeTube": 60,
    "Component/SmallTube": 120,
    "Component/Motor": 60,
    "Component/Computer": 200,
    "Component/Display": 30,
    "Component/PowerCell": 120,
    "Component/MetalGrid": 30,
    "Component/BulletproofGlass": 30,
    "Ore/Ice": 25000,
}
HYDROGEN = 500_000

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def admin(remote, text: str, wait: float = 4.0) -> str:
    reply = " ".join(rig.chat(remote, text, wait))
    log(f"{text} -> {reply}")
    return reply


def station_tables() -> dict[str, tuple[float, float, float]]:
    """Each station's hologram table, in the world: station name prefix -> position"""
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    tables = {}
    for entity in root.iter("MyObjectBuilder_EntityBase"):
        name = entity.findtext("DisplayName") or ""
        if not name.endswith(" Station"):
            continue
        for block in entity.iter("MyObjectBuilder_CubeBlock"):
            if block.findtext("SubtypeName") != "LargeBlockConsole":
                continue
            po = entity.find("PositionAndOrientation")
            pos, fwd, up = (
                [float(po.find(t).get(a)) for a in "xyz"]
                for t in ("Position", "Forward", "Up")
            )
            right = [
                fwd[1] * up[2] - fwd[2] * up[1],
                fwd[2] * up[0] - fwd[0] * up[2],
                fwd[0] * up[1] - fwd[1] * up[0],
            ]
            m = [int(block.find("Min").get(a)) * 2.5 for a in "xyz"]
            tables[name.split()[0]] = tuple(
                pos[i] + m[0] * right[i] + m[1] * up[i] - m[2] * fwd[i]
                for i in range(3)
            )
    return tables


def at_table(remote, station: str) -> None:
    x, y, z = station_tables()[station]
    remote.character_teleport(x, y + 3, z)
    time.sleep(4)
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    tc.stand_by(remote, table)


def shipyard(remote, station: str) -> list[str]:
    """The !oct shipyards lines of the station's shipyard"""
    lines = rig.chat(remote, "!oct shipyards", 4)
    out, mine = [], False
    for line in lines:
        for part in line.split("\n"):
            if part.startswith(f"{station} Station Shipyard"):
                mine = True
            elif not part.startswith("  "):
                mine = False
            if mine:
                out.append(part)
    return out


def wait_state(remote, station: str, pattern: str, timeout: float) -> str | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lines = shipyard(remote, station)
        for line in lines:
            if re.search(pattern, line):
                log(f"{station}: {line.strip()}")
                return line
        time.sleep(15)
    log(f"{station}: {' | '.join(shipyard(remote, station))}")
    return None


def order(remote, case: str) -> tuple:
    station, ship, blocks, paid, _, _ = CASES[case]
    with_config(remote, FreeBuildPcuLimit=0 if paid else 10000)
    if paid:
        for item, count in DEPOSIT.items():
            admin(remote, f"!oct ledger deposit {station} me {item} {count}", 3)
    at_table(remote, station)
    mark = so_latest(remote)
    reply = admin(remote, "!oct order", 8)
    if "Your quote for" in reply:
        check(f"'{ship}'" in reply, f"{case}: the quote is for {ship}: {reply}")
        reply = admin(remote, "!oct order", 8)
    check("is in the queue" in reply, f"{case}: ordered: {reply}")
    started = wait_chat(remote, f"builds '{ship}' for you now", 90, mark)
    check(started is not None, f"{case}: the build starts: {started}")
    if started is None:
        return None
    arm = wait_state(remote, station, rf"'{ship}'.*: arm \d+/\d+", 30)
    check(
        arm is not None and "projector off" in arm,
        f"{case}: the arm is placed, its projector off: {arm}",
    )
    return mark


def so_latest(remote) -> int:
    return remote.get_chat_messages().get("latest", 0)


def wait_chat(remote, text: str, timeout: float, after: int) -> str | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        got = remote.get_chat_messages(after=after)
        lines = [m.get("Text", "") for m in got.get("messages", [])]
        lines += [h.get("text", "") for h in got.get("hud", [])]
        for line in lines:
            if text in line:
                return line
        time.sleep(5)
    return None


def wait_log(pattern: str, timeout: float) -> str | None:
    """The first server log line of the plugin matching pattern"""
    deadline = time.monotonic() + timeout
    while True:
        for line in rig.server_game_log().splitlines():
            if "[Octahedron]" in line and re.search(pattern, line):
                return line.split("] ")[-1]
        if time.monotonic() > deadline:
            return None
        time.sleep(10)


def finish(remote, case: str, mark: int, restarted: bool = False) -> None:
    station, ship, blocks, paid, connector, size = CASES[case]
    if restarted:
        # The welders finish the ship before the client is back: follow the server's log
        line = wait_log(rf"'{ship}' at .*: (docked|no dock)", 1200)
        check(
            line is not None and ("docked" in line) == connector,
            f"{case}: the ship {'docks at the arm' if connector else 'gets the admin fill'}: {line}",
        )
        line = wait_log(rf"'{ship}' of .* ended", 60)
        ready = line if line and f"'{ship}' is ready" in line else None
    else:
        line = wait_state(remote, station, rf"'{ship}'.*projector on", 1200)
        check(
            line is not None,
            f"{case}: the arm's projector completes and projects: {line}",
        )
        check(
            line is not None and (", connector" in line) == connector,
            f"{case}: the arm {'docks at a connector' if connector else 'ends in a cap'}: {line}",
        )
        ready = wait_chat(remote, f"'{ship}' is ready", 1800, mark)
    check(ready is not None, f"{case}: the ship is ready: {ready}")
    if paid:
        check(
            ready is not None and f"{HYDROGEN:,} L" in ready,
            f"{case}: its H2 tank has {HYDROGEN:,} L: {ready}",
        )
    time.sleep(5)
    lines = shipyard(remote, station)
    heads = re.findall(
        rf"'Advanced Rotor \w+ Arm {size}' head (\d+) blocks( static)?",
        lines[0] if lines else "",
    )
    check(
        len(heads) == 2 and all(n == "1" and not static for n, static in heads),
        f"{case}: the arms are gone, the {size.lower()} rotor heads alone and dynamic: {lines}",
    )
    check(
        not any(f"'{ship}'" in line for line in lines[1:]),
        f"{case}: no build left in the shipyard: {lines}",
    )


def restart(remote):
    # The server first: the welders finish a ship this small in under a minute
    rig.stop_server()
    rig.stop_client()
    rig.start_server()
    remote = rig.start_client()
    rig.ensure_character(remote)
    log("rejoined")
    return remote


def saved(cases: list[str]) -> None:
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    check("Shipyard Order" not in text, "saved: no arm projector is left")
    grids = {
        e.findtext("DisplayName"): e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.find("CubeBlocks") is not None
    }
    for case in cases:
        _, ship, blocks, paid, connector, _ = CASES[case]
        grid = grids.get(ship)
        check(grid is not None, f"saved: '{ship}' is in the world")
        if grid is None:
            continue
        cubes = list(grid.find("CubeBlocks"))
        check(
            len(cubes) == blocks, f"saved: '{ship}' has {len(cubes)} of {blocks} blocks"
        )
        check(grid.findtext("IsStatic") != "true", f"saved: '{ship}' is dynamic")
        builders = {b.findtext("BuiltBy") for b in cubes}
        check(
            len(builders) == 1 and None not in builders,
            f"saved: '{ship}' built by one player: {builders}",
        )
        names = {b.findtext("CustomName") for b in cubes}
        check("Shipyard Order" not in names, f"saved: '{ship}' has no arm block")
        for b in cubes:
            if b.findtext("SubtypeName") == "LargeHydrogenTankSmall":
                ratio = float(b.findtext("FilledRatio") or 0)
                check(
                    abs(ratio - 0.5) < 0.01, f"saved: '{ship}' tank filled to {ratio}"
                )
            if b.findtext("SubtypeName") == "LargeBlockBatteryBlock":
                stored = float(b.findtext("CurrentStoredPower") or 0)
                check(
                    (stored >= 2.99) == connector,
                    f"saved: '{ship}' battery holds {stored} MWh, charged only when docked",
                )
    stators = [
        b
        for b in root.iter("MyObjectBuilder_CubeBlock")
        if re.match(r"Advanced Rotor \w+ Arm \w+", b.findtext("CustomName") or "")
    ]
    attached = [b for b in stators if (b.findtext("TopBlockId") or "") not in ("", "0")]
    check(
        len(stators) == 28 and len(attached) == 28,
        f"saved: all 28 arm rotors keep their heads: {len(attached)}/{len(stators)}",
    )
    # Every head grid is named "Subgrid": list them all, not by name
    heads = [
        e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.find("CubeBlocks") is not None
        and any(
            b.findtext("SubtypeName") in ("LargeAdvancedRotor", "SmallAdvancedRotor")
            for b in e.find("CubeBlocks")
        )
        and len(list(e.find("CubeBlocks"))) == 1
    ]
    check(
        len(heads) == 28 and all(e.findtext("IsStatic") != "true" for e in heads),
        f"saved: the 28 rotor heads are alone on their grids and dynamic: {len(heads)}",
    )


def main() -> int:
    cases = sys.argv[1:] or list(CASES)
    rig.stop_client()
    rig.stop_server()
    so.prepare(TABLES, 60)
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    try:
        for case in cases:
            mark = order(remote, case)
            if mark is None:
                continue  # no build to follow
            if case == "dock":
                remote = restart(remote)
                line = wait_log(r"order 'ArmDock' .*picked up", 60)
                check(line is not None, f"dock: picked up after the restart: {line}")
            finish(remote, case, mark, restarted=case == "dock")
    finally:
        rig.stop_client()
        rig.stop_server()
    saved(cases)
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and (
            "rror" in line or "Shipyard" in line or "xception" in line
        ):
            log("server: " + line.strip())
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} support arm checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
