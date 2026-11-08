"""Checks OCT's reputation (SE-1283) on a plain server with the headless client, an OCT member,
and the reputation probe (tests/rep-probe), which sends a block terminal's share box, the
ownership claim and the faction screen's Deposit:

- Taking apart your own grid costs nothing; sharing its battery with OCT raises the reputation by
  the battery's value and unsharing takes it back.
- Grinding a hull of a pirate costs nothing; grinding a hull of another member, or of a trader
  bot, costs the value ground, the same per block for both.
- A deposit into OCT's account raises it by the SC paid.
- The first ram of another member's hull is forgiven, the second, with another ship, costs, and
  the player hears they are below zero.
- The server log's changes add up to what !oct rep showed.
- Below -10,000 the countdown (20 s in the test) warns at its start and at 15, 10 and 5 s left,
  then removes the player from OCT, telling them why.
- A second server run, without the client, has ToolCore and OmniArc: an OmniArc Destro owned by a
  third member grinds another member's hull with no player at it, which costs the tool's owner.

Two server runs, about 9 minutes, half of it the server and client starting.

    uv run --no-project --with httpx python tests/test_reputation.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import base64
import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1283")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from se_autopilot import enter_cockpit  # noqa: E402
from station_tests import check  # noqa: E402
from test_client import log  # noqa: E402

PROBE = ("rep-probe", rig.PLUGIN / "tests" / "rep-probe", "RepProbe.xml", "rep-probe")
RIG_AT = (250_000.0, 0.0, 0.0)
PRESS_SECONDS = 8
BOT_STEAM_ID = 76561202000000077
PLAYER_STEAM_ID = 76561198000000077
BOT = 144115188075990001
PLAYER = 144115188075990002
SPACE_PIRATES = 144115188075855894
KICK_SECONDS = 20
WARN = "15,10,5"
RAM_GAP = 4
# The ground or blown block of a hull: two cells from its battery
TARGET = (2, 0, 0)
BATTERY = (0, 0, 0)

HULLS = {
    # name: (entity id, owner)
    "Own": (777001283000001, 0),
    "Pirate": (777001283000002, SPACE_PIRATES),
    "Member": (777001283000003, PLAYER),
    "Trader": (777001283000004, BOT),
    "Ram": (777001283000006, PLAYER),
    "Ram 2": (777001283000007, PLAYER),
}
SHIPS = {"Ram": 777001283000012, "Ram 2": 777001283000013}
SHIP_BACK = 15
# The ship's nose: two armor blocks ahead of its cockpit
NOSE = 5

# ToolCore: an OmniArc Destro grinder of TOOLER's, its target a hull of PLAYER's
MODS = {3040438530: "ToolCore", 3412657089: "OmniCorp The OmniArc Project: Adv Tools"}
TOOLER = 144115188075990003
TOOLER_STEAM_ID = 76561198000000078
TOOL_AT = (RIG_AT[0], RIG_AT[1] + 400, RIG_AT[2])
TOOL_GRID = 777001283000020
TOOL_TARGET = 777001283000021
TOOL_SECONDS = 120
# ToolCore's ToolRepo in the block's mod storage (protobuf): the shipyard welders' value with
# Activated, Mode Grind (16), Targets Friendly (2) and TrackTargets. Friendly leaves out the tool's
# own grid, which the Destro may grind
TOOL_REPO = bytearray(base64.b64decode("CAAQABgIIAAoDzAAOg8NAAAAACUAAAAAPQAAAABAAA=="))
TOOL_REPO[1] = TOOL_REPO[-1] = 1
TOOL_REPO[5] = 16
TOOL_REPO[9] = 2
TOOL_COMPONENTS = (
    "<ComponentContainer><Components>"
    "<ComponentData><TypeId>MyModStorageComponent</TypeId>"
    '<Component xsi:type="MyObjectBuilder_ModStorageComponent"><Storage><dictionary><item>'
    "<Key>75bbb4f5-4fb9-4230-aaaa-bb79c9811618</Key>"
    f"<Value>{base64.b64encode(TOOL_REPO).decode()}</Value>"
    "</item></dictionary></Storage></Component></ComponentData>"
    "</Components></ComponentContainer>"
)


def hull_at(name: str) -> tuple[float, float, float]:
    return (RIG_AT[0] + list(HULLS).index(name) * 60, RIG_AT[1], RIG_AT[2])


def hull(name: str) -> str:
    entity_id, owner = HULLS[name]
    own = f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy><ShareMode>None</ShareMode>"
    blocks = [
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", BATTERY, own),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, 0, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", TARGET),
    ]  # fmt: skip
    return rig.grid(f"{name} Hull", entity_id, blocks, hull_at(name)).replace(
        "<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>"
    )


def ship(name: str) -> str:
    """Unowned, SHIP_BACK m behind the hull, facing it (-Z) with an armor nose, thrusters both ways"""
    x, y, z = hull_at(name)

    def thruster(pos, forward):
        return rig.block(
            "MyObjectBuilder_Thrust", "LargeBlockSmallThrust", pos, forward=forward
        )

    blocks = [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, -1)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, -2)),
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0)),
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 1),
                  "<CurrentStoredPower>3</CurrentStoredPower>"),
        rig.block("MyObjectBuilder_Gyro", "LargeBlockGyro", (1, 0, 0)),
        thruster((1, 0, 1), "Forward"),
        thruster((-1, 0, 1), "Backward"),
    ]  # fmt: skip
    return rig.grid(f"{name} Ship", SHIPS[name], blocks, (x + 5, y, z + SHIP_BACK))


def owned(owner: int) -> str:
    return (
        f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy><ShareMode>None</ShareMode>"
    )


def tool_grids() -> list[str]:
    """The Destro on four batteries (it draws 25 MW), and 15 m away a hull of PLAYER's"""
    battery = (
        owned(TOOLER)
        + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"
    )
    tool = [
        rig.block("MyObjectBuilder_ConveyorSorter", "OmniArcDestro", (0, 0, 0),
                  owned(TOOLER) + TOOL_COMPONENTS + "<Enabled>true</Enabled>"),
        *(rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (-x, 0, 0), battery)
          for x in range(1, 5)),
    ]  # fmt: skip
    target = [
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", BATTERY, owned(PLAYER)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, 0, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", TARGET),
    ]  # fmt: skip
    x, y, z = TOOL_AT
    return [
        rig.grid(name, entity_id, blocks, at).replace(
            "<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>"
        )
        for name, entity_id, blocks, at in (
            ("Tool Rig", TOOL_GRID, tool, TOOL_AT),
            ("Tool Target", TOOL_TARGET, target, (x + 15, y, z)),
        )
    ]


def player(identity: int, steam_id: int, name: str) -> tuple[str, str]:
    """An offline player: its identity and its player data"""
    return (
        f"<MyObjectBuilder_Identity><IdentityId>{identity}</IdentityId>"
        f"<DisplayName>{name}</DisplayName><CharacterEntityId>0</CharacterEntityId>"
        "</MyObjectBuilder_Identity>",
        f"<item><Key><ClientId>{steam_id}</ClientId><SerialId>0</SerialId></Key>"
        f"<Value><DisplayName>{name}</DisplayName><IdentityId>{identity}</IdentityId>"
        "<Connected>false</Connected><ForceRealPlayer>false</ForceRealPlayer></Value></item>",
    )


def prepare(toolcore: bool = False) -> None:
    """The hulls and ships, or with toolcore, the ToolCore rig and its mods"""
    rig.prepare_world(
        extra_grids=(
            tool_grids()
            if toolcore
            else [*(hull(n) for n in HULLS), *(ship(n) for n in SHIPS)]
        )
    )
    if toolcore:
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = rig.WORLD / name
            text = path.read_text(encoding="utf-8")
            path.write_text(
                text.replace("<Mods>", f"<Mods>{rig.mod_items(MODS)}", 1),
                encoding="utf-8",
            )
        mods = rig.SERVER_DATA / "Mods"
        mods.mkdir(parents=True, exist_ok=True)
        for id_ in MODS:
            link = mods / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(rig.WORKSHOP / str(id_))
    path = rig.WORLD / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    people = [
        player(BOT, BOT_STEAM_ID, "[B] Test Trader"),
        player(PLAYER, PLAYER_STEAM_ID, "Test Player"),
        player(TOOLER, TOOLER_STEAM_ID, "Tool Owner"),
    ]
    text = text.replace(
        "</Identities>", "".join(p[0] for p in people) + "</Identities>", 1
    )
    text, count = re.subn(
        r"(<AllPlayersData>\s*<dictionary>)",
        lambda m: m[1] + "".join(p[1] for p in people),
        text,
    )
    assert count == 1, "AllPlayersData"
    # Both are OCT members
    oct_id = re.search(r"<FactionId>(\d+)</FactionId>\s*<Tag>OCT</Tag>", text)[1]
    members = "".join(
        f"<MyObjectBuilder_FactionMember><PlayerId>{i}</PlayerId><IsLeader>false</IsLeader>"
        "<IsFounder>false</IsFounder></MyObjectBuilder_FactionMember>"
        for i in (BOT, PLAYER, TOOLER)
    )
    text, count = re.subn(
        r"(<Tag>OCT</Tag>.*?<Members>)",
        lambda m: m[1] + members,
        text,
        count=1,
        flags=re.S,
    )
    assert count == 1, "OCT members"
    start = text.index("<Players>", text.index("<Factions>"))
    text = text[:start] + re.sub(
        r"(<Players>\s*<dictionary>)",
        lambda m: m[1]
        + "".join(
            f'<item><Key xsi:type="xsd:long">{i}</Key><Value xsi:type="xsd:long">{oct_id}</Value></item>'
            for i in (BOT, PLAYER, TOOLER)
        ),
        text[start:],
        count=1,
    )
    path.write_text(text, encoding="utf-8")


def config() -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    options = {
        "OctNoDeposits": "false",
        "OctGridsNoGrinding": "false",
        "ReputationEnabled": "true",
        "ReputationKickAfterSeconds": str(KICK_SECONDS),
        "ReputationWarnSeconds": WARN,
        "ReputationRamGapSeconds": str(RAM_GAP),
        "ReputationRamGraceSeconds": "3600",
    }
    for option, value in options.items():
        text, count = re.subn(
            rf"<{option}>[^<]*</{option}>", f"<{option}>{value}</{option}>", text
        )
        if count == 0:
            text = text.replace(
                "</OctahedronConfig>",
                f"  <{option}>{value}</{option}>\n</OctahedronConfig>",
            )
    first, last = re.search(
        r"<OctProtectedSteamIds>(\d+)-(\d+)</OctProtectedSteamIds>", text
    ).groups()
    assert int(first) <= BOT_STEAM_ID <= int(last)
    return text


# --- reading ---------------------------------------------------------------------


def reputation(remote) -> float | None:
    """The player's reputation from !oct rep, None when they have none"""
    for _ in range(3):
        reply = " ".join(rig.chat(remote, "!oct rep", 15))
        match = re.search(r"Your reputation in OCT: (-?[\d,]+)\.", reply)
        if match:
            return float(match[1].replace(",", ""))
        if "Only OCT's members" in reply:
            return None
        log(f"!oct rep: {reply!r}")
    raise AssertionError("no answer to !oct rep")


def changes(identity: str = r"\d+") -> list[tuple[str, float, bool]]:
    """The server log's reputation changes so far: kind, amount, forgiven"""
    return [
        (m[1], float(m[2]), bool(m[3]))
        for m in re.finditer(
            rf"Reputation of [^(]*\({identity}\): (\w+) (-?[\d.]+), [-\d.]+ -> [-\d.]+( forgiven)?",
            rig.server_game_log(),
        )
    ]


def check_log(total: float) -> None:
    """The server log's changes add up to what !oct rep showed. The log is written seconds late"""
    deadline = time.monotonic() + 60
    while True:
        found = changes()
        paid = sum(a for _, a, forgiven in found if not forgiven)
        if abs(paid - total) < 2 or time.monotonic() > deadline:
            break
        time.sleep(2)
    kinds = sorted({kind for kind, _, _ in found})
    check(
        abs(paid - total) < 2 and kinds == ["Damage", "Deposit", "Ram", "Share"],
        f"the server log's changes add up to the reputation's: {paid:.2f} vs {total:.2f}, kinds {kinds}",
    )


def settled(remote, before: float, timeout: float = 15) -> float:
    """The reputation once it moved from before, or before after the timeout"""
    deadline = time.monotonic() + timeout
    while (now := reputation(remote)) == before and time.monotonic() < deadline:
        time.sleep(1)
    return now


def probe(remote, text: str) -> str:
    reply = " ".join(rig.chat(remote, text, 10))
    log(f"{text}: {reply}")
    return reply


# --- doing -----------------------------------------------------------------------


def near(remote, entity_id: int, at) -> None:
    """Teleports by the grid until the client has it"""
    rig.wait_until(
        lambda: any(g["entityId"] == entity_id for g in remote.list_grids())
        or bool(remote.character_teleport(at[0], at[1] + 8, at[2]))
        and False,
        120,
        f"grid {entity_id} on the client",
        interval=3,
    )


def claim(remote, entity_id: int, at) -> None:
    near(remote, entity_id, at)
    probe(remote, f"/rep claim {entity_id}")


def integrity(remote, entity_id: int, at=TARGET) -> float:
    for cube in remote.list_cubes(entity_id)["cubes"]:
        if tuple(cube["min"]) == at:
            return 0.0 if cube.get("isDestroyed") else float(cube["integrity"])
    return 0.0


def aim(remote, block) -> None:
    remote.character_teleport(block[0], block[1], block[2] - 3.25)
    time.sleep(2)
    remote.character_set_dampeners(True)
    remote.character_look_at(*block, tolerance=1)


def grind(remote, name: str, seconds: float = 60) -> tuple[float, float]:
    """Grinds the hull's target block until its integrity drops by a fifth, or for the seconds"""
    entity_id = HULLS[name][0]
    x, y, z = hull_at(name)
    block = (x + TARGET[0] * 2.5, y, z)
    near(remote, entity_id, (x, y, z - 5))
    aim(remote, block)
    before = integrity(remote, entity_id)
    start = press = time.monotonic()
    remote.character_primary_action(True)
    while (after := integrity(remote, entity_id)) > before * 0.8:
        now = time.monotonic()
        if now - start > seconds:
            break
        if now - press > PRESS_SECONDS:
            remote.character_primary_action(False)
            aim(remote, block)
            remote.character_primary_action(True)
            press = time.monotonic()
        time.sleep(0.5)
    remote.character_primary_action(False)
    time.sleep(3)  # the last changes reach the authority within a second or two
    return before, integrity(remote, entity_id)


def check_grinding(remote) -> None:
    remote.character_equip("AngleGrinderItem")
    if not remote.get_character().get("jetpack"):
        remote.key("X")
    rig.wait_until(lambda: remote.get_character().get("jetpack"), 10, "the jetpack on")
    rates: list[float] = []
    claim(remote, HULLS["Own"][0], hull_at("Own"))
    for name, costs in (
        ("Own", False),
        ("Pirate", False),
        ("Member", True),
        ("Trader", True),
    ):
        rep = reputation(remote)
        before, after = grind(remote, name)
        now = settled(remote, rep, 10 if costs else 3)
        # The share of the block's value: the same block on both hulls
        rate = (rep - now) / ((before - after) / before) if after < before else 0
        if costs:
            check(
                after < before and now < rep,
                f"grinding the {name} hull ({before:.0f} -> {after:.0f} integrity) costs: "
                f"{rep:.0f} -> {now:.0f}, {rate:.0f} SC per armor block",
            )
            rates.append(rate)
        else:
            check(
                after < before and now == rep,
                f"grinding the {name} hull ({before:.0f} -> {after:.0f} integrity) costs nothing: "
                f"{rep:.0f} -> {now:.0f}",
            )
    check(
        len(rates) == 2 and abs(rates[0] - rates[1]) < 0.05 * max(rates),
        f"both hulls' armor blocks are worth the same: {rates}",
    )


def check_share(remote) -> None:
    entity_id = HULLS["Own"][0]
    near(remote, entity_id, hull_at("Own"))
    rep = reputation(remote)
    probe(remote, f"/rep share {entity_id} Faction")
    shared = settled(remote, rep)
    probe(remote, f"/rep share {entity_id} None")
    unshared = settled(remote, shared)
    check(
        shared > rep and abs(unshared - rep) < 1.5,
        f"sharing the own hull's battery with OCT: {rep:.0f} -> {shared:.0f}, unsharing -> {unshared:.0f}",
    )


def check_deposit(remote) -> None:
    rep = reputation(remote)
    probe(remote, "/rep deposit 1000")
    now = settled(remote, rep)
    check(now == rep + 1000, f"a deposit of 1,000 SC: {rep:.0f} -> {now:.0f}")


def ram(remote, name: str) -> None:
    """In the ship, full thrust at its hull until its nose touches it, then out"""
    ship_id = SHIPS[name]
    near(remote, ship_id, (hull_at(name)[0] + 5, 0, hull_at(name)[2] + SHIP_BACK))
    rig.wait_until(
        lambda: _seated(remote, ship_id), 120, f"the client in the {name} ship"
    )
    hull_z = hull_at(name)[2]
    remote.set_input_state(keys=["W"])
    try:
        rig.wait_until(
            lambda: remote.get_grid(ship_id)["position"][2] < hull_z + 2.5 + NOSE + 0.5,
            8,
            f"the {name} ship at its hull",
            interval=0.1,
        )
    except TimeoutError as e:
        log(str(e))
    time.sleep(0.5)
    remote.clear_input_state()
    time.sleep(2)
    remote.character_use()
    rig.wait_until(
        lambda: remote.get_character().get("state") != "sitting", 30, "out of the seat"
    )


def check_ram(remote) -> None:
    rep = reputation(remote)
    since = remote.get_chat_messages()
    ram(remote, "Ram")
    time.sleep(RAM_GAP)
    middle = reputation(remote)
    told = [t for t in rig.new_chat_lines(remote, since, "") if "accident" in t]
    since = remote.get_chat_messages()
    ram(remote, "Ram 2")
    now = settled(remote, middle, 15)
    below = [t for t in rig.new_chat_lines(remote, since, "") if "below zero" in t]
    check(
        told and middle == rep,
        f"the first ram is forgiven: {rep:.0f} -> {middle:.0f}, told {told}",
    )
    check(now < middle, f"the second ram costs: {middle:.0f} -> {now:.0f}")
    check(
        now < 0 and below,
        f"below zero at {now:.0f}, the player is told: {below}",
    )


def _seated(remote, ship_id: int) -> bool:
    try:
        return enter_cockpit(remote, ship_id).get("state") == "sitting"
    except Exception as e:  # noqa: BLE001 -- Remote right after a teleport
        log(f"entering the ship: {e}")
        rig.ensure_character(remote)
        return False


def check_removal(remote) -> None:
    before = remote.get_chat_messages()
    log(" ".join(rig.chat(remote, "!oct rep add -40000", 10)))
    told: list[str] = []
    rig.wait_until(
        lambda: told.extend(
            t for t in rig.new_chat_lines(remote, before, "") if t not in told
        )
        or any(t.startswith("You were removed from OCT") for t in told),
        KICK_SECONDS + 30,
        "the removal",
        interval=1,
    )
    log("\n".join(told))
    expected = [
        "If it stays there for 20 seconds",
        "15 seconds left",
        "10 seconds left",
        "5 seconds left",
        "You were removed from OCT",
    ]
    # "5 seconds left" is in "15 seconds left" too: the warnings start their lines
    order = [
        next(
            (
                i
                for i, t in enumerate(told)
                if t.startswith(e) or e.startswith("If") and e in t
            ),
            -1,
        )
        for e in expected
    ]
    check(
        all(i >= 0 for i in order) and order == sorted(order),
        f"below zero, the countdown, its warnings and the removal are told in order: {order}",
    )
    rig.wait_until(lambda: remote.get_faction().get("tag") != "OCT", 20, "out of OCT")
    check(reputation(remote) is None, "the player is out of OCT, !oct rep says so")


def check_toolcore() -> None:
    """Server only: the Destro grinds PLAYER's hull; TOOLER, its owner, pays"""
    deadline = time.monotonic() + TOOL_SECONDS
    while not (found := changes(str(TOOLER))) and time.monotonic() < deadline:
        time.sleep(3)
    time.sleep(10)
    found = changes(str(TOOLER))
    others = [c for c in changes() if c not in found]
    lost = sum(a for _, a, _ in found)
    check(
        found and all(kind == "Damage" and a < 0 for kind, a, _ in found),
        f"ToolCore grinding another member's hull costs the tool's owner: {len(found)} changes, "
        f"{lost:.0f} in all",
    )
    check(not others, f"and no one else: {others[:5]}")


def run_toolcore() -> None:
    rig.stop_server()
    prepare(toolcore=True)
    rig.write_plugin_config(config())
    rig.prepare_server()
    rig.start_server()
    try:
        check_toolcore()
    finally:
        rig.stop_server()


def run() -> None:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.write_plugin_config(config())
    rig.prepare_server()
    rig.start_server()
    rig.prepare_client(extra_dev_folders=[PROBE])
    remote = rig.start_client(extra_dev_folders=[PROBE])
    try:
        tag = remote.get_faction()["tag"]
        check(tag == "OCT", f"the player is in {tag}")
        check(reputation(remote) == 10_000, "the player starts at 10,000")
        check_grinding(remote)
        check_share(remote)
        check_deposit(remote)
        check_ram(remote)
        check_log(reputation(remote) - 10_000)
        check_removal(remote)
    finally:
        rig.stop_client()
        rig.stop_server()


if __name__ == "__main__":
    if "toolcore" not in sys.argv[1:]:
        run()
    if "client" not in sys.argv[1:]:
        run_toolcore()
    sys.exit(st.summary("Reputation"))
