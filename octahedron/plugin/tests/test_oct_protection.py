"""Checks OCT's protections (SE-1160) with the headless client, an OCT member, admin, but not
one of OctLeaders:

- OctNoDeposits: the Deposit button of the faction screen takes no money from the player into
  OCT's account.
- OctGridsNoGrinding: a hand grinder does nothing to a hull of OCT's NPC or of a trader bot (a
  player whose Steam ID is in OctProtectedSteamIds), and still grinds a hull of another player
  and of an NPC of another faction.

Then the options go off with !oct reload, the control: the same deposit goes through and the
grinder takes OCT's and the bot's hulls apart, as before SE-1160. One server run, about 4
minutes, most of it the server and client starting.

    uv run --no-project --with httpx python tests/test_oct_protection.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1160")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check  # noqa: E402
from test_client import log  # noqa: E402

RIG_AT = (250_000.0, 0.0, 0.0)
DEPOSIT = 1000
# The plugin's log line of a refused deposit
REFUSED = "OCT's account takes no deposits"
# How long a protected block is ground, and the most an open one may take
HOLD_SECONDS = 15
GROUND_SECONDS = 60
PRESS_SECONDS = 8
BOT_STEAM_ID = 76561202000000077
PLAYER_STEAM_ID = 76561198000000077
BOT = 144115188075990001
PLAYER = 144115188075990002
SPACE_PIRATES = 144115188075855894


def oct_npc() -> int:
    """OCT's founder, the NPC that owns the stations"""
    text = (rig.WORLD_SOURCE / "Sandbox.sbc").read_text(encoding="utf-8")
    faction = re.search(r"<Tag>OCT</Tag>.*?</Members>", text, re.S)[0]
    return int(re.search(r"<PlayerId>(\d+)</PlayerId>", faction)[1])


def hulls() -> dict[str, tuple[int, int, bool]]:
    """Name -> (entity id, owner, protected). The open ones first: one ground proves the
    grinder reaches the server before a protected one is held."""
    return {
        "Player": (777001160000003, PLAYER, False),
        "Pirate": (777001160000004, SPACE_PIRATES, False),
        "OCT": (777001160000001, oct_npc(), True),
        "Trader bot": (777001160000002, BOT, True),
    }


def hull_at(index: int) -> tuple[float, float, float]:
    return (RIG_AT[0] + index * 40, RIG_AT[1], RIG_AT[2])


# The ground block: the hull's third, two cells from the owned battery
TARGET = (2, 0, 0)


def hull(name: str, entity_id: int, owner: int, index: int) -> str:
    own = f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy><ShareMode>None</ShareMode>"
    blocks = [
        rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 0), own),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, 0, 0)),
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", TARGET),
    ]  # fmt: skip
    return rig.grid(f"{name} Hull", entity_id, blocks, hull_at(index)).replace(
        "<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>"
    )


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


def prepare() -> None:
    rig.prepare_world(
        extra_grids=[
            hull(name, entity_id, owner, i)
            for i, (name, (entity_id, owner, _)) in enumerate(hulls().items())
        ]
    )
    path = rig.WORLD / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    people = [
        player(BOT, BOT_STEAM_ID, "[B] Test Trader"),
        player(PLAYER, PLAYER_STEAM_ID, "Test Player"),
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
    path.write_text(text, encoding="utf-8")


def config(protect: bool) -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for option in ("OctNoDeposits", "OctGridsNoGrinding"):
        text, count = re.subn(
            rf"<{option}>\w+</{option}>",
            f"<{option}>{str(protect).lower()}</{option}>",
            text,
        )
        assert count == 1, option
    # The bots' range of the Octahedron config, which has the test's bot
    first, last = re.search(
        r"<OctProtectedSteamIds>(\d+)-(\d+)</OctProtectedSteamIds>", text
    ).groups()
    assert int(first) <= BOT_STEAM_ID <= int(last) and not (
        int(first) <= PLAYER_STEAM_ID <= int(last)
    )
    return text


# --- deposit --------------------------------------------------------------------


def balances(remote) -> tuple[int, int]:
    """OCT's balance and the player's, from !oct money"""
    reply = " ".join(rig.chat(remote, "!oct money", 30))

    def sc(label: str) -> int:
        match = re.search(rf"{label}: ([\d,]+) SC", reply)
        assert match, reply
        return int(match[1].replace(",", ""))

    return sc("OCT"), sc("You")


def screen(remote, kind: str) -> int | None:
    return next(
        (i for i, s in enumerate(remote.list_screens()) if kind in s.get("type", "")),
        None,
    )


def control(controls: list, name: str) -> dict:
    """A control's properties, searched through the children"""
    for c in controls:
        if c.get("name") == name:
            return c.get("properties", {})
        found = control(c.get("children", []), name)
        if found is not None:
            return found
    return None


def amount_text(remote, form: int) -> str:
    return control(remote.get_screen(form)["controls"], "Textbox").get("text") or ""


def deposit(remote, amount: int) -> None:
    """Deposit through the faction screen, as a player does: OCT's row, Deposit, the amount, OK"""
    rig.wait_until(
        lambda: _try(remote.open_factions_terminal), 60, "the faction screen to open"
    )
    rig.wait_until(
        lambda: screen(remote, "Terminal") is not None, 30, "the terminal screen"
    )
    terminal = screen(remote, "Terminal")
    rows = control(remote.get_screen(terminal)["controls"], "FactionsTable")["rows"]
    row = next(r["index"] for r in rows if r["cells"][0] == "OCT")
    remote.control_click(name="FactionsTable", row=row, screen=terminal)
    remote.control_click(name="depositBtn", screen=terminal)
    rig.wait_until(
        lambda: screen(remote, "Transaction") is not None, 30, "the deposit form"
    )
    form = screen(remote, "Transaction")
    # The amount box takes keys once it has the focus, a moment after the form opens. The OK
    # button is the form's only one, named Button
    deadline = time.monotonic() + 20
    while (typed := amount_text(remote, form)) != str(amount):
        assert typed == "" and time.monotonic() < deadline, f"amount box: {typed!r}"
        remote.type_text(str(amount))
        time.sleep(1)
    remote.control_click(name="Button", screen=form)
    rig.wait_until(
        lambda: screen(remote, "Transaction") is None, 30, "the deposit form to close"
    )
    remote.close_screen(screen(remote, "Terminal"))
    rig.wait_until(
        lambda: screen(remote, "Terminal") is None, 30, "the terminal to close"
    )


def _try(call):
    try:
        call()
        return True
    except Exception as e:  # noqa: BLE001 -- not ready yet
        log(f"{call.__name__}: {e}")
        return False


def logged(line: str, before: int, timeout: float = 10) -> bool:
    """Whether the server log has the line more than before times, within the timeout: the
    log is written a moment later"""
    deadline = time.monotonic() + timeout
    while rig.server_game_log().count(line) <= before:
        if time.monotonic() > deadline:
            return False
        time.sleep(1)
    return True


def check_deposit(remote, protect: bool) -> None:
    oct_before, mine_before = balances(remote)
    refusals = rig.server_game_log().count(REFUSED)
    deposit(remote, DEPOSIT)
    expected = mine_before - (0 if protect else DEPOSIT)
    deadline = time.monotonic() + 20
    while True:
        oct_after, mine_after = balances(remote)
        if mine_after == expected or time.monotonic() > deadline:
            break
        time.sleep(2)
    refused = logged(REFUSED, refusals, 10 if protect else 3)
    check(
        mine_after == expected and refused == protect,
        f"deposit of {DEPOSIT} SC with OctNoDeposits {protect}: the player's balance "
        f"{mine_before} -> {mine_after} (OCT {oct_before} -> {oct_after}), expected {expected}, "
        f"refusal logged {refused}",
    )


# --- grinding -------------------------------------------------------------------


def integrity(remote, entity_id: int) -> float:
    """The target block's integrity on the client, 0 when it is gone"""
    for cube in remote.list_cubes(entity_id)["cubes"]:
        if tuple(cube["min"]) == TARGET:
            return 0.0 if cube.get("isDestroyed") else float(cube["integrity"])
    return 0.0


def aim(remote, block) -> None:
    remote.character_teleport(block[0], block[1], block[2] - 3.25)
    time.sleep(2)
    remote.character_set_dampeners(True)
    remote.character_look_at(*block, tolerance=1)


def grind(remote, index: int, entity_id: int, seconds: float) -> tuple[float, float]:
    """Holds the hand grinder on the hull's target block until its integrity drops, or for
    the given seconds. Every few seconds the character goes there again and presses anew: the
    server's character may lag behind a teleport."""
    block = (hull_at(index)[0] + TARGET[0] * 2.5, hull_at(index)[1], hull_at(index)[2])
    aim(remote, block)
    log(f"grinder aims at {remote.get_character_target(5.0)['block']}")
    before = integrity(remote, entity_id)
    start = press = time.monotonic()
    remote.character_primary_action(True)
    while (after := integrity(remote, entity_id)) == before:
        now = time.monotonic()
        if now - start > seconds:
            break
        if now - press > PRESS_SECONDS:
            remote.character_primary_action(False)
            aim(remote, block)
            remote.character_primary_action(True)
            press = time.monotonic()
        time.sleep(1)
    remote.character_primary_action(False)
    return before, after


def check_grinding(remote, protect: bool) -> None:
    """With the options on every hull; off, the protected ones, the others were ground already"""
    remote.character_equip("AngleGrinderItem")
    # On foot in space the server's character doesn't follow a teleport: its grinder reaches
    # nothing near the client's
    if not remote.get_character().get("jetpack"):
        remote.key("X")
    rig.wait_until(lambda: remote.get_character().get("jetpack"), 10, "the jetpack on")
    for i, (name, (entity_id, _, protected)) in enumerate(hulls().items()):
        if not protect and not protected:
            continue
        rig.wait_until(
            lambda: any(g["entityId"] == entity_id for g in remote.list_grids())
            or remote.character_teleport(*hull_at(i)),
            120,
            f"the {name} hull on the client",
        )
        held = protect and protected
        before, after = grind(
            remote, i, entity_id, HOLD_SECONDS if held else GROUND_SECONDS
        )
        refused = held and logged(f"{name} Hull belongs to", 0)
        check(
            (after == before and refused) if held else after < before,
            f"{name} hull with OctGridsNoGrinding {protect}: integrity {before} -> {after}"
            + (
                f", refusal logged {refused}, expected held"
                if held
                else ", expected ground"
            ),
        )


def run() -> None:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.write_plugin_config(config(True))
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client()
    try:
        tag = remote.get_faction()["tag"]
        check(tag == "OCT", f"the player is in {tag}")
        for protect in (True, False):
            if not protect:
                rig.write_plugin_config(config(False))
                log(" ".join(rig.chat(remote, "!oct reload", 15)))
            check_deposit(remote, protect)
            check_grinding(remote, protect)
    finally:
        rig.stop_client()
        rig.stop_server()


if __name__ == "__main__":
    run()
    sys.exit(st.summary("OCT protection"))
