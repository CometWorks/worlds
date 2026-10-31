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
- "exempt" at South Station: "ArmExempt", the cap ship ordered by the player as an admin
  with "Ignore PCU limits" on, over a lowered GroupPcuLimit and with its control station
  blocked for free builds: a free build anyway. The flag is turned off once the build
  starts, as on a cluster node without the admin's settings; the ship is still welded.

After each build the arm and its projector are gone, the rotor head is still on its
rotor and dynamic again, and the ship is the player's dynamic ship. The large ships'
cockpit toolbars turn the battery and the tank on and off by EntityId, and the battery's
block group by name: the client must show the three slots in the delivered cockpit and
have the group, and the saved ship must point the slots at its own blocks and keep the
group (SE-772). The saved world is checked at the end.

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
import se_autopilot as ap  # noqa: E402
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


GROUP = "Arm Batteries"


def toolbar(slots: dict[int, int], cockpit: int) -> str:
    """A cockpit toolbar whose slots turn blocks on and off, by their EntityId, and
    the next slot the GROUP"""
    return (
        "<Toolbar><ToolbarType>Character</ToolbarType>"
        '<SelectedSlot xsi:nil="true" /><Slots>'
        + "".join(
            f"<Slot><Index>{i}</Index><Item />"
            '<Data xsi:type="MyObjectBuilder_ToolbarItemTerminalBlock">'
            f"<Action>OnOff</Action><BlockEntityId>{block}</BlockEntityId></Data></Slot>"
            for i, block in slots.items()
        )
        + f"<Slot><Index>{len(slots)}</Index><Item />"
        '<Data xsi:type="MyObjectBuilder_ToolbarItemTerminalGroup">'
        f"<Action>OnOff</Action><BlockEntityId>{cockpit}</BlockEntityId>"
        f"<GroupName>{GROUP}</GroupName></Data></Slot>" + "</Slots></Toolbar>"
    )


def toolbar_ok(grid) -> tuple[bool, str]:
    """Whether the grid's cockpit has its two block slots, on blocks of the grid, and
    its group slot, on a group of the grid that holds the battery"""
    ids = {b.findtext("EntityId") for b in grid.find("CubeBlocks")}
    cockpit = next(
        b
        for b in grid.find("CubeBlocks")
        if b.findtext("SubtypeName") == "LargeBlockCockpit"
    )
    data = list(cockpit.iterfind("Toolbar/Slots/Slot/Data"))
    blocks = [d.findtext("BlockEntityId") for d in data if not d.findtext("GroupName")]
    named = [d.findtext("GroupName") for d in data if d.findtext("GroupName")]
    groups = {
        g.findtext("Name"): [
            (v.findtext("X"), v.findtext("Y"), v.findtext("Z"))
            for v in g.iterfind("Blocks/Vector3I")
        ]
        for g in grid.iterfind("BlockGroups/MyObjectBuilder_BlockGroup")
    }
    battery = next(
        b.find("Min")
        for b in grid.find("CubeBlocks")
        if b.findtext("SubtypeName") == "LargeBlockBatteryBlock"
    )
    cell = (battery.get("x"), battery.get("y"), battery.get("z"))
    return (
        (
            len(blocks) == 2
            and set(blocks) <= ids
            and named == [GROUP]
            and groups.get(GROUP) == [cell]
        ),
        f"block slots {blocks}, group slots {named}, groups {groups}, battery at {cell}",
    )


def large(
    name: str, entity: int, connector: bool, thrust: bool = False
) -> tuple[str, int]:
    special = {
        (0, 0, 2): rig.block(
            "MyObjectBuilder_OxygenTank",
            "LargeHydrogenTankSmall",
            (0, 0, 2),
            f"<EntityId>{entity + 1}</EntityId>",
        ),
        (0, 1, 2): None,  # the tank is two blocks high
        (2, 0, 2): rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (2, 0, 2),
            f"<EntityId>{entity + 2}</EntityId>",
        ),
    }
    if thrust:
        # An ion thruster on top, so the player can fly the ship off the dock, whose
        # battery is charged
        special[(1, 2, 3)] = rig.block(
            "MyObjectBuilder_Thrust", "LargeBlockSmallThrust", (1, 2, 3)
        )
    if connector:
        # Flush with the back face, armor beside it: the arm's projector sits on that
        # armor next to the arm's connector, as in the "... Arm Exam" blueprint
        special[(1, 0, 5)] = rig.block(
            "MyObjectBuilder_ShipConnector", "Connector", (1, 0, 5), forward="Backward"
        )
    blocks = hull(special, (3, 2, 6), "LargeBlockArmorBlock")
    # The control station mounts only on its bottom: on the front, on top
    # Its toolbar turns the battery and the tank on and off, and the battery's group
    # (SE-772)
    blocks.append(
        rig.block(
            "MyObjectBuilder_Cockpit",
            "LargeBlockCockpit",
            (1, 2, 0),
            f"<EntityId>{entity + 3}</EntityId>"
            + toolbar({0: entity + 2, 1: entity + 1}, entity + 3),
        )
    )
    grid = rig.grid(name, entity, blocks, tag="MyObjectBuilder_CubeGrid").replace(
        "</CubeBlocks>",
        f"</CubeBlocks><BlockGroups><MyObjectBuilder_BlockGroup><Name>{GROUP}</Name>"
        "<Blocks><Vector3I><X>2</X><Y>0</Y><Z>2</Z></Vector3I></Blocks>"
        "</MyObjectBuilder_BlockGroup></BlockGroups>",
    )
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


DOCK, DOCK_BLOCKS = large("ArmDock", 777000693000100, True, thrust=True)
CAP, CAP_BLOCKS = large("ArmCap", 777000693000200, False)
SMALL, SMALL_BLOCKS = small()


# Builds that stall: the test takes an item out of the shipyard. Heavy armor needs metal
# grids, nothing else of the ship or the arm does; only the cockpit needs displays.
def heavy(name: str, entity: int, count: int) -> tuple[str, int]:
    """The cap ship with count of its 33 armor blocks heavy"""
    grid, blocks = large(name, entity, False)
    return (
        grid.replace(
            "<SubtypeName>LargeBlockArmorBlock<",
            "<SubtypeName>LargeHeavyBlockArmorBlock<",
            count,
        ),
        blocks,
    )


# 2 of 33 plain blocks missing is delivered, 4 (over 10%) is not
PARTIAL, PARTIAL_BLOCKS = heavy("ArmPartial", 777000693000400, 2)
HEAVY, HEAVY_BLOCKS = heavy("ArmArmor", 777000693000600, 4)
SCRAP, SCRAP_BLOCKS = large("ArmScrap", 777000693000500, False)
# Ordered by an admin who ignores PCU limits: free although it is over the PCU cap and
# has a blocked block, and still free and welded with the flag off once it started, as
# on a cluster node that has no copy of the admin's settings
EXEMPT, EXEMPT_BLOCKS = large("ArmExempt", 777000693000700, False)
EXEMPT_CONFIG = {"GroupPcuLimit": 40, "FreeBuildBlockedBlocks": "Cockpit/*"}
# The tables in the world's order: North, South, Alpha, Beta, Gamma, Delta, Moon
TABLES = [HEAVY, EXEMPT, DOCK, CAP, SMALL, PARTIAL, SCRAP]
# name: (station, ship, blocks, paid, connector, rotor size)
CASES = {
    "dock": ("Alpha", "ArmDock", DOCK_BLOCKS, True, True, "Large"),
    "cap": ("Beta", "ArmCap", CAP_BLOCKS, True, False, "Large"),
    "small": ("Gamma", "ArmSmall", SMALL_BLOCKS, False, True, "Small"),
    "partial": ("Delta", "ArmPartial", PARTIAL_BLOCKS, True, False, "Large"),
    "scrap": ("Moon", "ArmScrap", SCRAP_BLOCKS, True, False, "Large"),
    "armor": ("North", "ArmArmor", HEAVY_BLOCKS, True, False, "Large"),
    "exempt": ("South", "ArmExempt", EXEMPT_BLOCKS, False, False, "Large"),
}
# Stalled builds: the item taken out, and how the build ends
STALLS = {
    "partial": ("Component/MetalGrid", "The ship is yours without"),
    "scrap": ("Component/Display", "The ship was removed"),
    "armor": ("Component/MetalGrid", "The ship was removed"),
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
    "Component/MetalGrid": 300,
    "Component/BulletproofGlass": 30,
    "Component/Thrust": 200,
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


def litter(station: str) -> None:
    """Puts garbage in the middle of the station shipyard's welders, into the prepared
    world: an unowned armor block and a floating stone. The build there sweeps it away.
    """
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    root = ET.parse(sector).getroot()
    yard = next(
        e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.findtext("DisplayName") == f"{station} Station Shipyard"
    )
    beams = [
        [int(b.find("Min").get(a)) for a in "xyz"]
        for b in yard.iter("MyObjectBuilder_CubeBlock")
        if b.findtext("SubtypeName") == "OmniArcBeam"
    ]
    cell = [sum(c[i] for c in beams) / len(beams) * 2.5 for i in range(3)]
    po = yard.find("PositionAndOrientation")
    pos, fwd, up = (
        [float(po.find(t).get(a)) for a in "xyz"] for t in ("Position", "Forward", "Up")
    )
    right = [
        fwd[1] * up[2] - fwd[2] * up[1],
        fwd[2] * up[0] - fwd[0] * up[2],
        fwd[0] * up[1] - fwd[1] * up[0],
    ]
    at = [
        pos[i] + cell[0] * right[i] + cell[1] * up[i] - cell[2] * fwd[i]
        for i in range(3)
    ]
    wreck = rig.grid(
        WRECK,
        None,
        [rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0))],
        at,
    )
    stone = (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_FloatingObject">'
        "<SubtypeName /><PersistentFlags>CastShadows InScene</PersistentFlags>"
        "<PositionAndOrientation>"
        + rig._vec(
            "Position", (at[0] + 5 * up[0], at[1] + 5 * up[1], at[2] + 5 * up[2])
        )
        + rig._vec("Forward", fwd)
        + rig._vec("Up", up)
        + "</PositionAndOrientation><Item><Amount>100</Amount>"
        '<PhysicalContent xsi:type="MyObjectBuilder_Ore"><SubtypeName>Stone</SubtypeName>'
        "</PhysicalContent></Item></MyObjectBuilder_EntityBase>"
    )
    text = sector.read_text(encoding="utf-8")
    sector.write_text(
        text.replace("</SectorObjects>", wreck + stone + "</SectorObjects>", 1),
        encoding="utf-8",
    )
    log(f"{station}: litter at {[round(x) for x in at]}")


WRECK = "Shipyard Wreck"


def at_table(remote, station: str) -> None:
    x, y, z = station_tables()[station]
    # Right after the player left a cockpit the client may answer 503 for a while
    for attempt in range(6):
        try:
            remote.character_teleport(x, y + 3, z)
            break
        except Exception as e:  # noqa: BLE001 -- retried, then raised
            if attempt == 5:
                raise
            log(f"teleport: {e}, retrying")
            time.sleep(10)
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
    exempt = case == "exempt"
    with_config(
        remote,
        FreeBuildPcuLimit=0 if paid or exempt else 10000,
        ShipyardRepasteShips=(
            "false" if os.environ.get("OCT_REPASTE") == "0" else "true"
        ),
        **(EXEMPT_CONFIG if exempt else {}),
    )
    if paid:
        for item, count in DEPOSIT.items():
            admin(remote, f"!oct ledger deposit {station} me {item} {count}", 3)
    at_table(remote, station)
    mark = so_latest(remote)
    if exempt:
        reply = admin(remote, "!oct order", 8)
        check(
            "a ship at most" in reply,
            f"{case}: over the PCU cap without the flag: {reply}",
        )
        ignore_pcu(remote, True)
    reply = admin(remote, "!oct order", 8)
    if "Your quote for" in reply:
        check(f"'{ship}'" in reply, f"{case}: the quote is for {ship}: {reply}")
        if exempt:
            check("a free build" in reply, f"{case}: free with the flag: {reply}")
        reply = admin(remote, "!oct order", 8)
    check("is in the queue" in reply, f"{case}: ordered: {reply}")
    started = wait_chat(remote, f"builds '{ship}' for you now", 90, mark)
    if exempt:
        ignore_pcu(remote, False)
    check(started is not None, f"{case}: the build starts: {started}")
    if started is None:
        return None
    if case in STALLS:
        # Before the arm is complete and the projection starts
        log(admin(remote, f"!oct shipyard strip {station} {STALLS[case][0]}"))
    arm = wait_state(remote, station, rf"'{ship}'.*: arm \d+/\d+", 30)
    # The welders may complete a small arm's projector before this looks
    check(arm is not None, f"{case}: the arm is placed: {arm}")
    return mark


def ignore_pcu(remote, on: bool) -> None:
    remote.set_admin_flag("ignorePcu", on)
    # The server gets the flag with the admin menu's event
    time.sleep(3)
    log(f"Ignore PCU limits {'on' if on else 'off'}")


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
        # The welders may finish the ship before the client is back: then the server's log
        # has it
        follow(remote, case, lambda _: False)
        line = wait_log(rf"'{ship}' at .*: (docked|no dock)", 60)
        check(
            line is not None and ("docked" in line) == connector,
            f"{case}: the ship {'docks at the arm' if connector else 'gets the admin fill'}: {line}",
        )
        line = wait_log(rf"'{ship}' of .* ended", 60)
        ready = line if line and f"'{ship}' is ready" in line else None
    else:
        line = watch_build(remote, case)
        check(
            line is not None,
            f"{case}: the arm's projector completes and projects: {line}",
        )
        check(
            line is not None and (", connector" in line) == connector,
            f"{case}: the arm {'docks at a connector' if connector else 'ends in a cap'}: {line}",
        )
        follow(remote, case, lambda _: False)
        ready = wait_chat(remote, f"'{ship}' is ready", 60, mark)
    check(ready is not None, f"{case}: the ship is ready: {ready}")
    if not restarted:
        seen_by_client(remote, case)
    if case in USED:
        usable(remote, case, fly=case == "dock")
    if paid:
        check(
            ready is not None and f"{HYDROGEN:,} L" in ready,
            f"{case}: its H2 tank has {HYDROGEN:,} L: {ready}",
        )
        check(
            ready is not None and "leftover" not in ready,
            f"{case}: a finished ship leaves no components for the ledger: {ready}",
        )
    lines = arms_gone(remote, case)
    check(
        not any(f"'{ship}'" in line for line in lines[1:]),
        f"{case}: no build left in the shipyard: {lines}",
    )
    if case == "exempt":
        line = wait_log(rf"ignores PCU limits, no cool-down after '{ship}'", 10)
        check(line is not None, f"{case}: no cool-down with the flag off: {line}")


def stall(remote, case: str, mark: int) -> None:
    """The build order() took an item out of: the plugin ends it after 60 s without
    progress, as the case expects"""
    station, ship = CASES[case][:2]
    ending = STALLS[case][1]
    line = follow(remote, case, lambda _: False)
    log(f"{case}: the build ended after {line}")
    ended = wait_chat(remote, f"stopped building '{ship}'", 30, mark)
    check(
        ended is not None and ending in ended,
        f"{case}: the stalled build ends with '{ending}': {ended}",
    )
    if case == "partial":
        seen_by_client(remote, case)
    if case in ("scrap", "armor"):
        check(
            ended is not None and "labor fee is paid back" in ended,
            f"{case}: the build cost is paid back: {ended}",
        )
    lines = arms_gone(remote, case)
    check(
        not any(f"'{ship}'" in line for line in lines[1:]),
        f"{case}: no build left in the shipyard: {lines}",
    )


# The cases whose ship the player gets into: the docked one has a thruster and a charged battery
USED = ("dock", "cap")


def usable(remote, case: str, fly: bool) -> None:
    """The player sits in the delivered ship's cockpit and, with fly, flies it, with no
    !fixship. Without the re-paste (OCT_REPASTE=0) the player sits but can't fly."""
    ship = CASES[case][1]
    time.sleep(5)
    grids = [g["entityId"] for g in remote.list_grids() if g["name"] == ship]
    # The ship is the grid whose cockpit is the player's; another of its name is a leftover
    owned = {}
    for g in grids:
        try:
            cockpit = ap.find_cockpit(remote, g)
            owned[g] = remote.get_block(g, tuple(cockpit["min"])).get("ownerId")
        except Exception as e:  # noqa: BLE001 -- logged below
            owned[g] = str(e)
    log(f"{case}: client grids '{ship}', cockpit owners: {owned}")
    for g in grids:
        log(f"{case}: client grid {g}: {remote.get_grid(g)}")
    players = [g for g, o in owned.items() if isinstance(o, int) and o != 0]
    ghosts = ghosts_of(remote, grids, players)
    check(not ghosts, f"{case}: no ghost of '{ship}' on the client: {ghosts}")
    check(len(players) == 1, f"{case}: the client has the player's '{ship}': {owned}")
    if not players:
        return
    grid = players[0]
    try:
        state = ap.enter_cockpit(remote, grid, timeout=60)
    except Exception as e:  # noqa: BLE001 -- the check reports it
        state = {"error": str(e)}
    check(
        state.get("state") == "sitting",
        f"{case}: the player sits in '{ship}''s cockpit: {state}",
    )
    if state.get("state") != "sitting":
        return
    log(f"{case}: in the cockpit, view {remote.get_view()}")
    if case != "small":
        time.sleep(3)
        items = remote.get_toolbar().get("items", [])
        check(
            len(items) == 3,
            f"{case}: the cockpit's toolbar keeps its two blocks and its group on the client: {items}",
        )
        groups = remote.list_block_groups(grid)
        check(
            len(groups.get(GROUP, [])) == 1,
            f"{case}: the client's '{ship}' has the group of its group slot: {groups}",
        )
    if fly:
        speed = 0.0
        for key in ("W", "S"):
            remote.set_input_state(keys=[key])
            time.sleep(3)
            remote.clear_input_state()
            speed = remote.get_grid(grid).get("speed", 0)
            if speed > 0.5:
                break
        check(speed > 0.5, f"{case}: the player flies '{ship}': {speed:.1f} m/s")
    remote.character_use()
    time.sleep(2)


def arms_gone(remote, case: str) -> list[str]:
    """The arm comes down at once after the build: waits up to 10 s for the case's rotor
    heads to be alone and dynamic. Returns the shipyard's lines."""
    station, size = CASES[case][0], CASES[case][5]
    deadline = time.monotonic() + 10
    while True:
        lines = shipyard(remote, station)
        heads = re.findall(
            rf"'Advanced Rotor \w+ Arm {size}' head (\d+) blocks( static)?",
            lines[0] if lines else "",
        )
        alone = len(heads) == 2 and all(n == "1" and not st for n, st in heads)
        if alone or time.monotonic() > deadline:
            break
        time.sleep(3)
    check(
        alone,
        f"{case}: the arms are gone, the {size.lower()} rotor heads alone and dynamic: {lines}",
    )
    return lines


def shipyard_id(remote, station: str) -> int:
    name = f"{station} Station Shipyard"
    return next(g["entityId"] for g in remote.list_grids() if g["name"] == name)


def watch_build(remote, case: str) -> str | None:
    """Follows the build until the arm's projector is on: the whole arm must be complete
    by then, and the client must see the shipyard's welders shoot (in the no-welding zone
    a client turned them off, SE-441). Returns the !oct shipyards line."""
    station, ship = CASES[case][:2]
    yard = shipyard_id(remote, station)
    shooting = []

    def projecting(line: str) -> bool:
        shooting.append(shoots(remote, yard))
        return "projector on" in line

    line = follow(remote, case, projecting)
    if line is not None:
        arm = re.search(r"arm (\d+)/(\d+)", line)
        if "projector on" in line:
            log(f"{station}: {line.strip()}")
            check(
                arm is not None and arm[1] == arm[2],
                f"{case}: the arm is complete when the projector turns on: {line.strip()}",
            )
            shooting.append(shoots(remote, yard))
            seen = [x for x in shooting if x is not None]
            check(
                len(seen) > 0 and all(seen),
                f"{case}: the client sees the shipyard's welders shoot: {shooting}",
            )
            return line
    check(False, f"{case}: the arm's projector turns on: {line}")
    return None


# The test's own watchdog: the build failed when no arm or ship block completes for this
# long. The plugin ends a build after 60 s without progress, on its 10 s checks.
STALL = 90.0


def follow(remote, case: str, done) -> str | None:
    """Polls the build's !oct shipyards line until done(line) or the build is gone, and
    returns the last line. Fails the build when the complete blocks of its arm and ship,
    or the ticks left to dock, don't change for STALL seconds."""
    station, ship = CASES[case][:2]
    seen, since, last = None, time.monotonic(), None
    while True:
        line = next((x for x in shipyard(remote, station) if f"'{ship}'" in x), None)
        if line is None or done(line):
            return line if line is not None else last
        last = line
        counts = re.findall(r"(\d+)/\d+|(\d+) ticks left", line)
        if counts != seen:
            seen, since = counts, time.monotonic()
        elif time.monotonic() - since > STALL:
            log(f"{station}: {line.strip()}")
            check(
                False, f"{case}: a block completes every {STALL:.0f} s: {line.strip()}"
            )
            return None
        time.sleep(3)


def shoots(remote, yard: int) -> bool | None:
    """ToolCore_Shoot of a shipyard welder on the client; None when it can't be read"""
    try:
        return so.beam_shoot(remote, yard).get("value") is True
    except Exception as e:  # noqa: BLE001 -- the client may not have the grid yet
        log(f"ToolCore_Shoot unreadable: {e}")
        return None


def seen_by_client(remote, case: str) -> None:
    """The finished ship as the ordering player's client sees it, checked in saved()
    against the ship's entity id"""
    ship = CASES[case][1]
    time.sleep(10)
    seen[case] = {}
    for g in remote.list_grids():
        if g["name"] != ship:
            continue
        found = set()
        for block in remote.list_blocks(g["entityId"], 0, 500)["blocks"]:
            if "isFunctional" in block:
                got = remote.get_block(g["entityId"], tuple(block["min"]))
                found.add(got.get("ownerId"))
        static = remote.get_grid(g["entityId"])["isStatic"]
        seen[case][g["entityId"]] = (static, sorted(found))
    log(f"{case}: the client's '{ship}' grids, (static, block owners): {seen[case]}")
    players = [g for g, (_, owners) in seen[case].items() if any(owners)]
    ghosts = ghosts_of(remote, list(seen[case]), players)
    check(
        len(players) == 1 and not ghosts,
        f"{case}: the client sees one '{ship}' of the player's, no ghost over it: {ghosts}",
    )


def ghosts_of(remote, grids: list[int], players: list[int]) -> list[int]:
    """Grids of the ship's name within 50 m of the player's: a split left the clients a
    copy of the ship over it. The hologram table's quote, by the same name, is farther.
    """
    where = {g: remote.get_grid(g)["position"] for g in grids}
    return [
        g
        for g in grids
        if g not in players and any(math.dist(where[g], where[p]) < 50 for p in players)
    ]


# The client's grids by the finished ships' names, (static, block owners) by entity id
seen: dict[str, dict[int, tuple[bool, list[int]]]] = {}


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
    if "small" in cases:
        station = CASES["small"][0]
        swept = [
            x
            for x in rig.server_game_log().splitlines()
            if f"Shipyard {station} Station Shipyard: removed" in x
        ]
        check(
            any(
                re.search(r"removed [1-9]\d* floating objects", x) and WRECK in x
                for x in swept
            )
            and WRECK not in text,
            f"saved: the garbage in {station}'s shipyard was swept: {swept}",
        )
    grids = {
        e.findtext("DisplayName"): e
        for e in root.iter("MyObjectBuilder_EntityBase")
        if e.find("CubeBlocks") is not None
    }
    log_text = rig.server_game_log()
    check(
        "the welders stalled on" not in log_text,
        "saved: the arm was built at once: "
        + str(re.findall(r"(\d+) arm blocks built at once", log_text)),
    )
    if "partial" in cases:
        grid = grids.get("ArmPartial")
        cubes = list(grid.find("CubeBlocks")) if grid is not None else []
        check(
            grid is not None and grid.findtext("IsStatic") != "true",
            "saved: 'ArmPartial' was delivered without some heavy armor, dynamic: "
            + f"{len(cubes)} of {PARTIAL_BLOCKS} blocks",
        )
        projectors = [
            b
            for b in cubes
            if b.get("{http://www.w3.org/2001/XMLSchema-instance}type")
            == "MyObjectBuilder_Projector"
        ]
        check(
            not projectors,
            "saved: 'ArmPartial' comes without the build's projector: "
            + str([p.findtext("CustomName") for p in projectors]),
        )
    for case in ("scrap", "armor"):
        if case in cases:
            ship = CASES[case][1]
            check(ship not in grids, f"saved: '{ship}' was removed")
    for case in cases:
        if case in STALLS:
            continue
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
        owned = {b.findtext("Owner") for b in cubes if b.find("Owner") is not None}
        check(
            owned == builders,
            f"saved: '{ship}' owned by the player who built it: {owned} vs {builders}",
        )
        if case in seen:
            client = seen[case].get(int(grid.findtext("EntityId")))
            check(client is not None, f"saved: the client had '{ship}': {seen[case]}")
            if client is not None:
                check(not client[0], f"saved: the client saw '{ship}' dynamic")
                check(
                    client[1] == [int(x) for x in builders],
                    f"saved: the client saw '{ship}''s blocks owned by its builder: {client[1]}",
                )
        names = {b.findtext("CustomName") for b in cubes}
        check("Shipyard Order" not in names, f"saved: '{ship}' has no arm block")
        if ship != "ArmSmall":
            ok, what = toolbar_ok(grid)
            check(ok, f"saved: '{ship}''s cockpit toolbar keeps its blocks: {what}")
        for b in cubes:
            if b.findtext("SubtypeName") == "LargeHydrogenTankSmall":
                ratio = float(b.findtext("FilledRatio") or 0)
                # A free build comes without hydrogen
                check(
                    abs(ratio - (0.5 if paid else 0)) < 0.01,
                    f"saved: '{ship}' tank filled to {ratio}",
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
    if "small" in cases:
        litter(CASES["small"][0])
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    try:
        for case in cases:
            # The long run may outlast the suit's oxygen in an unpressurized station
            rig.ensure_character(remote)
            mark = order(remote, case)
            if mark is None:
                continue  # no build to follow
            if case == "dock":
                remote = restart(remote)
                line = wait_log(r"order 'ArmDock' .*picked up", 60)
                check(line is not None, f"dock: picked up after the restart: {line}")
            if case in STALLS:
                stall(remote, case, mark)
            else:
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
