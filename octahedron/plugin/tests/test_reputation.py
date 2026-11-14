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
- The player claims a gatling gun and shoots it at another member's hull, then claims a warhead
  next to another's, starts its countdown and leaves: each costs the player, and nobody else
  (SE-1293).
- Below -10,000 the countdown (20 s in the test) warns at its start and at 15, 10 and 5 s left,
  then removes the player from OCT, telling them why.
- With the world's config/octahedron.cfg as it is, but for the timers (SE-1285): reputation on,
  deposits into OCT allowed, OCT's grids protected from grinding and the trader bots' ships not.
  The trader bot has no reputation of its own. Grinding a hull of OCT's NPC does nothing and costs
  nothing. !claim, the alias of Essentials' !ess claim the deploy sets (here the console probe),
  refuses the trader bot's hull, whose battery is private, and takes another member's hull whose
  battery is shared with OCT: the claim costs nobody reputation and the hull is then the player's
  own, free to grind.
- A second server run, without the client, has ToolCore and OmniArc: an OmniArc Destro owned by a
  third member grinds another member's hull with no player at it, which costs the tool's owner.
- In the same run weapons of three more members, all offline, fire at hulls of another member: a
  gatling gun and a rocket launcher shooting from the start, and a warhead counting down next to
  its target (SE-1293). Each costs its owner what the saved world says the hull lost, and nobody
  else pays.

Two server runs, about 10 minutes, half of it the server and client starting.

    uv run --no-project --with httpx python tests/test_reputation.py [client|toolcore] [weapons]

client or toolcore: only that run; weapons: of the client run, only the player's weapons.

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import base64
import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1283")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from se_autopilot import enter_cockpit  # noqa: E402
from station_tests import check  # noqa: E402
from test_client import log  # noqa: E402

PROBE = ("rep-probe", rig.PLUGIN / "tests" / "rep-probe", "RepProbe.xml", "rep-probe")
# The server's: Essentials from its working copy, at the commit the hub pins, and the console
# probe, which sets the !claim alias the way Quasar's agent does on the cluster's nodes
ESSENTIALS = (
    "essentials",
    Path(
        os.environ.get(
            "OCT_ESSENTIALS_REPO", Path.home() / "ws/se/se1/plugins/essentials"
        )
    ),
    "Essentials.xml",
    # A dev folder's id is its manifest's
    "DF09925B-6D53-41F2-899C-CA6214B5DD6F",
)
CONSOLE_PROBE = (
    "console-probe",
    rig.PLUGIN / "tests" / "console-probe",
    "ConsoleProbe.xml",
    "3B8E1F52-6C47-4D0A-9E21-5A7C0D1553AA",
)
# As deploy.py's COMMAND_ALIASES
ALIASES = {"CONSOLEPROBE_ALIASES": "!claim=!ess claim"}
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
    "Shot": (777001283000008, PLAYER),
    "Bombed": (777001283000009, PLAYER),
    # SE-1285: its battery shared with OCT, so !claim takes it
    "Shared": (777001283000010, PLAYER),
    # OCT's NPC's, which nobody grinds; the owner is read from the world
    "OCT": (777001283000011, None),
}
# SE-1293: an unowned gatling gun 30 m in front of the Shot hull's TARGET block and an unowned
# warhead touching the Bombed hull's; the player claims them, then shoots and starts the countdown.
# The server takes a terminal request only from a character within 15 m of the grid
CLIENT_GUN = 777001283000040
CLIENT_WARHEAD = 777001283000041
CLIENT_COUNTDOWN_MS = 8000
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
# SE-1293: weapons of offline members fire at hulls of PLAYER's from the start: name: (owner,
# Steam ID, target hull id, weapon grid id)
WEAPONS = {
    "Gatling": (
        144115188075990004,
        76561198000000079,
        777001283000030,
        777001283000031,
    ),
    "Rocket": (144115188075990005, 76561198000000080, 777001283000032, 777001283000033),
    "Warhead": (
        144115188075990006,
        76561198000000081,
        777001283000034,
        777001283000035,
    ),
}
WEAPONS_AT = (RIG_AT[0], RIG_AT[1] - 400, RIG_AT[2])
WEAPON_BACK = {"Gatling": 30, "Rocket": 40}
WARHEAD_COUNTDOWN_MS = 30_000
WEAPON_SECONDS = 180
# What a block is worth at the market's reference prices (the store's): SE-1283 measured them
VALUES = {"LargeBlockArmorBlock": 3057, "LargeBlockBatteryBlock": 19708}
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


def oct_npc() -> int:
    """OCT's founder, the NPC that owns the stations"""
    text = (rig.world_source() / "Sandbox.sbc").read_text(encoding="utf-8")
    faction = re.search(r"<Tag>OCT</Tag>.*?</Members>", text, re.S)[0]
    return int(re.search(r"<PlayerId>(\d+)</PlayerId>", faction)[1])


def hull(name: str) -> str:
    entity_id, owner = HULLS[name]
    owner = owner if owner is not None else oct_npc()
    share = "Faction" if name == "Shared" else "None"
    own = f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy><ShareMode>{share}</ShareMode>"
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


def client_weapons() -> list[str]:
    x, y, z = hull_at("Shot")
    gun = [
        rig.block("MyObjectBuilder_SmallGatlingGun", "", (0, 0, 0),
                  "<Enabled>true</Enabled>" + ammo("NATO_25x184mm", 2)),
        rig.block("MyObjectBuilder_BatteryBlock", "SmallBlockBatteryBlock", (-1, 1, 0),
                  "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"),
    ]  # fmt: skip
    bx, by, bz = hull_at("Bombed")
    warhead = [
        rig.block(
            "MyObjectBuilder_Warhead",
            "LargeWarhead",
            (0, 0, 0),
            f"<CountdownMs>{CLIENT_COUNTDOWN_MS}</CountdownMs>",
        )
    ]
    return [
        g.replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
        for g in (
            rig.grid("Client Gun", CLIENT_GUN, gun, (x + 5, y, z + 30), size="Small"),
            rig.grid("Client Warhead", CLIENT_WARHEAD, warhead, (bx + 5, by, bz + 2.5)),
        )
    ]


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


def ammo(magazine: str, count: int) -> str:
    """A weapon's inventory with count magazines"""
    return (
        "<ComponentContainer><Components><ComponentData><TypeId>MyInventoryBase</TypeId>"
        '<Component xsi:type="MyObjectBuilder_Inventory"><CanPutItems>true</CanPutItems><Items>'
        f"<MyObjectBuilder_InventoryItem><Amount>{count}</Amount>"
        f'<PhysicalContent xsi:type="MyObjectBuilder_AmmoMagazine"><SubtypeName>{magazine}'
        "</SubtypeName></PhysicalContent><ItemId>0</ItemId></MyObjectBuilder_InventoryItem>"
        "</Items><nextItemId>1</nextItemId><InventoryFlags>CanReceive CanSend</InventoryFlags>"
        "<RemoveEntityOnEmpty>false</RemoveEntityOnEmpty></Component></ComponentData>"
        "</Components></ComponentContainer>"
    )


def weapon_hull_at(name: str) -> tuple[float, float, float]:
    x, y, z = WEAPONS_AT
    return (x + list(WEAPONS).index(name) * 100, y, z)


def weapon_grids() -> list[str]:
    """Per weapon a static hull of PLAYER's like hull()'s, and the weapon facing its TARGET block
    (-Z): a small grid gatling gun and a rocket launcher shooting, a warhead touching it
    """
    grids = []
    for name, (owner, _, hull_id, weapon_id) in WEAPONS.items():
        x, y, z = weapon_hull_at(name)
        target = [
            rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", BATTERY, owned(PLAYER)),
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (1, 0, 0)),
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", TARGET),
        ]  # fmt: skip
        grids.append(rig.grid(f"{name} Target", hull_id, target, (x, y, z)))
        battery = (
            owned(owner)
            + "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"
        )
        shoot = (
            owned(owner)
            + "<Enabled>true</Enabled><IsShootingFromTerminal>true</IsShootingFromTerminal>"
        )
        size = "Large"
        if name == "Gatling":
            size = "Small"
            blocks = [
                rig.block("MyObjectBuilder_SmallGatlingGun", "", (0, 0, 0), shoot + ammo("NATO_25x184mm", 2)),
                rig.block("MyObjectBuilder_BatteryBlock", "SmallBlockBatteryBlock", (-1, 1, 0), battery),
            ]  # fmt: skip
        elif name == "Rocket":
            blocks = [
                rig.block("MyObjectBuilder_SmallMissileLauncher", "LargeMissileLauncher", (0, 0, 0),
                          shoot + ammo("Missile200mm", 2)),
                rig.block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 2), battery),
            ]  # fmt: skip
        else:
            blocks = [
                rig.block("MyObjectBuilder_Warhead", "LargeWarhead", (0, 0, 0),
                          owned(owner) + f"<CountdownMs>{WARHEAD_COUNTDOWN_MS}</CountdownMs>"
                          "<IsArmed>true</IsArmed><IsCountingDown>true</IsCountingDown>"),
            ]  # fmt: skip
        at = (x + TARGET[0] * 2.5, y, z + WEAPON_BACK.get(name, 2.5))
        grids.append(rig.grid(f"{name} Weapon", weapon_id, blocks, at, size=size))
    return [
        g.replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
        for g in grids
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
            [*tool_grids(), *weapon_grids()]
            if toolcore
            else [
                *(hull(n) for n in HULLS),
                *(ship(n) for n in SHIPS),
                *client_weapons(),
            ]
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
    add_members(rig.WORLD)


def add_members(world: Path) -> None:
    """The trader bot and the other players, offline members of OCT, into the world's Sandbox.sbc"""
    path = world / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    people = [
        player(BOT, BOT_STEAM_ID, "[B] Test Trader"),
        player(PLAYER, PLAYER_STEAM_ID, "Test Player"),
        player(TOOLER, TOOLER_STEAM_ID, "Tool Owner"),
        *(player(o, s, f"{n} Owner") for n, (o, s, _, _) in WEAPONS.items()),
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
    # All are OCT members
    oct_id = re.search(r"<FactionId>(\d+)</FactionId>\s*<Tag>OCT</Tag>", text)[1]
    members = "".join(
        f"<MyObjectBuilder_FactionMember><PlayerId>{i}</PlayerId><IsLeader>false</IsLeader>"
        "<IsFounder>false</IsFounder></MyObjectBuilder_FactionMember>"
        for i in (BOT, PLAYER, TOOLER, *(w[0] for w in WEAPONS.values()))
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
            for i in (BOT, PLAYER, TOOLER, *(w[0] for w in WEAPONS.values()))
        ),
        text[start:],
        count=1,
    )
    path.write_text(text, encoding="utf-8")


# The world's config sets these as the cluster runs them (SE-1285); the test takes them as they are
SHIPPED = {
    "ReputationEnabled": "true",
    "OctNoDeposits": "false",
    "OctGridsNoGrinding": "true",
    "OctProtectedSteamIds": "",
    "ReputationStart": "200000",
    "ReputationKickBelow": "-200000",
}


def check_shipped_config() -> None:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")

    def value(option: str) -> str | None:
        match = re.search(rf"<{option}>([^<]*)</{option}>", text)
        return match and match[1]

    found = {option: value(option) for option in SHIPPED}
    check(found == SHIPPED, f"config/octahedron.cfg as the cluster runs it: {found}")
    bots = [
        [int(i) for i in r.split("-")] if "-" in r else [int(r)] * 2
        for r in (value("ShipyardBotSteamIds") or "").split(",")
        if r.strip()
    ]
    check(
        any(first <= BOT_STEAM_ID <= last for first, last in bots),
        f"the test's trader bot {BOT_STEAM_ID} is in ShipyardBotSteamIds {bots}",
    )


def config() -> str:
    """The world's config, with timers short enough for a test"""
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    options = {
        "ReputationKickAfterSeconds": str(KICK_SECONDS),
        "ReputationWarnSeconds": WARN,
        "ReputationRamGapSeconds": str(RAM_GAP),
        "ReputationRamGraceSeconds": "3600",
        # The checks count from the old start and line (SE-1495 moved both to 200,000)
        "ReputationStart": "10000",
        "ReputationKickBelow": "-10000",
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


def equip_grinder(remote) -> None:
    remote.character_equip("AngleGrinderItem")
    if not remote.get_character().get("jetpack"):
        remote.key("X")
    rig.wait_until(lambda: remote.get_character().get("jetpack"), 10, "the jetpack on")


def check_grinding(remote) -> None:
    equip_grinder(remote)
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


def look_at(remote, name: str) -> None:
    """The player by the hull, looking at its target block, as for !claim"""
    x, y, z = hull_at(name)
    near(remote, HULLS[name][0], (x, y, z - 5))
    aim(remote, (x + TARGET[0] * 2.5, y, z))


def integration(remote, rep, logged):
    """SE-1285: the trader bot, OCT's grids and !claim with the world's config, as (check, passed,
    detail). rep: the player's reputation; logged: the logged reputation changes of an identity
    (the World Authority's on a cluster)"""

    def steady(before: float, seconds: float = 6) -> float:
        """The reputation once it moved from before, or before after the seconds"""
        deadline = time.monotonic() + seconds
        while (now := rep(remote)) == before and time.monotonic() < deadline:
            time.sleep(1)
        return now

    reply = " ".join(rig.chat(remote, "!oct rep add -1 [B] Test Trader", 10))
    yield "the trader bot has no reputation", "has no reputation" in reply, reply
    equip_grinder(remote)
    start = rep(remote)
    before, after = grind(remote, "OCT", 20)
    now = steady(start, 3)
    yield (
        "grinding OCT's hull does nothing and costs nothing",
        after == before and now == start,
        f"{before:.0f} -> {after:.0f} integrity, {start:.0f} -> {now:.0f}",
    )
    look_at(remote, "Trader")
    reply = " ".join(rig.chat(remote, "!claim", 10))
    yield (
        "!claim refuses the trader bot's hull, its battery private",
        "Not enough shared ownership" in reply,
        reply,
    )
    look_at(remote, "Shared")
    reply = " ".join(rig.chat(remote, "!claim", 10))
    now = steady(start)
    yield (
        "!claim takes another member's hull shared with OCT, at no reputation",
        "Claimed 1 grid" in reply and now == start,
        f"{reply}, {start:.0f} -> {now:.0f}",
    )
    before, after = grind(remote, "Shared")
    now = steady(start)
    yield (
        "grinding the claimed hull costs nothing",
        after < before and now == start,
        f"{before:.0f} -> {after:.0f} integrity, {start:.0f} -> {now:.0f}",
    )
    yield (
        "the claim leaves the former owner's reputation alone",
        not logged(PLAYER),
        logged(PLAYER),
    )


def check_integration(remote) -> None:
    for name, passed, detail in integration(
        remote, reputation, lambda identity: changes(str(identity))
    ):
        check(passed, f"{name}: {detail}")


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


def damage_by_identity() -> dict[str, float]:
    found: dict[str, float] = {}
    for m in re.finditer(
        r"Reputation of [^(]*\((\d+)\): Damage (-?[\d.]+),", rig.server_game_log()
    ):
        found[m[1]] = found.get(m[1], 0) + float(m[2])
    return found


def charged(
    remote, rep: float, before: dict[str, float], timeout: float
) -> tuple[float, dict]:
    """The reputation once it fell from rep and then held for 10 s, and the log's new damage by
    identity"""
    deadline = time.monotonic() + timeout
    now = settled(remote, rep, timeout)
    while time.monotonic() < deadline:
        time.sleep(10)
        if (later := reputation(remote)) == now:
            break
        now = later
    # The log is written seconds late
    log_deadline = time.monotonic() + 60
    while True:
        after = damage_by_identity()
        paid = {
            i: a - before.get(i, 0) for i, a in after.items() if a != before.get(i, 0)
        }
        if abs(sum(paid.values()) - (now - rep)) < 2 or time.monotonic() > log_deadline:
            return now, paid
        time.sleep(2)


def beside(remote, at) -> None:
    """Teleports 5 m above a grid, in reach of its terminal, out of the gun's line of fire"""
    remote.character_teleport(at[0], at[1] + 5, at[2])
    time.sleep(2)


def check_client_weapons(remote) -> None:
    """The player shoots a gatling gun they claimed at another member's hull, then starts the
    countdown of a warhead they claimed next to another's and leaves. Each costs the player, and
    only them"""
    # Room below zero: the countdown must not start
    log(" ".join(rig.chat(remote, "!oct rep add 100000", 10)))
    rep = settled(remote, reputation(remote))
    shot = HULLS["Shot"][0]
    gun_at = (hull_at("Shot")[0] + 5, 0, hull_at("Shot")[2] + 30)
    near(remote, shot, hull_at("Shot"))
    before_hp = integrity(remote, shot)
    claim(remote, CLIENT_GUN, gun_at)
    beside(remote, gun_at)
    before = damage_by_identity()
    probe(remote, f"/rep shoot {CLIENT_GUN} on")
    try:
        now, paid = charged(remote, rep, before, 40)
    finally:
        probe(remote, f"/rep shoot {CLIENT_GUN} off")
    after_hp = integrity(remote, shot)
    check(
        now < rep and len(paid) == 1 and abs(sum(paid.values()) - (now - rep)) < 2,
        f"shooting the claimed gatling gun at another member's hull ({before_hp:.0f} -> "
        f"{after_hp:.0f} integrity) costs the player alone: {rep:.0f} -> {now:.0f}, log {paid}",
    )
    rep = now
    x, y, z = hull_at("Bombed")
    warhead_at = (x + 5, y, z + 2.5)
    claim(remote, CLIENT_WARHEAD, warhead_at)
    beside(remote, warhead_at)
    before = damage_by_identity()
    probe(remote, f"/rep countdown {CLIENT_WARHEAD}")
    remote.character_teleport(x, y + 150, z)
    now, paid = charged(remote, rep, before, CLIENT_COUNTDOWN_MS / 1000 + 30)
    check(
        now < rep and len(paid) == 1 and abs(sum(paid.values()) - (now - rep)) < 2,
        f"the claimed warhead's countdown next to another member's hull costs the player alone: "
        f"{rep:.0f} -> {now:.0f}, log {paid}",
    )


def check_removal(remote) -> None:
    before = remote.get_chat_messages()
    # From where the reputation is to 20,000 below the line
    change = -(reputation(remote) + 20_000)
    log(" ".join(rig.chat(remote, f"!oct rep add {change:.0f}", 10)))
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
    lost = sum(a for _, a, _ in found)
    check(
        found and all(kind == "Damage" and a < 0 for kind, a, _ in found),
        f"ToolCore grinding another member's hull costs the tool's owner: {len(found)} changes, "
        f"{lost:.0f} in all",
    )


def weapons_done() -> dict[str, float]:
    """Each weapon owner's damage once nothing changed for 15 s, or after WEAPON_SECONDS"""
    deadline = time.monotonic() + WEAPON_SECONDS
    last, quiet = None, time.monotonic()
    while time.monotonic() < deadline:
        now = len(changes())
        if now != last:
            last, quiet = now, time.monotonic()
        elif time.monotonic() - quiet > 15 and all(
            changes(str(o)) for o, *_ in WEAPONS.values()
        ):
            break
        time.sleep(3)
    return {
        name: sum(a for _, a, _ in changes(str(owner)))
        for name, (owner, *_) in WEAPONS.items()
    }


def hull_loss(hull_id: int) -> float:
    """What the saved hull lost, in SC: each block's value times the integrity it lost"""
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    # The save leaves out a Min of 0, 0, 0 and its coordinates of 0
    saved = {
        tuple(
            int(c.get(k, 0)) if (c := b.find("Min")) is not None else 0 for k in "xyz"
        ): float(b.findtext("IntegrityPercent") or 1)
        for o in sector.find("SectorObjects")
        if o.findtext("EntityId") == str(hull_id)
        for b in o.find("CubeBlocks")
    }
    cells = {BATTERY: "LargeBlockBatteryBlock", (1, 0, 0): "LargeBlockArmorBlock",
             TARGET: "LargeBlockArmorBlock"}  # fmt: skip
    return sum(VALUES[s] * (1 - saved.get(cell, 0.0)) for cell, s in cells.items())


def check_weapons(paid: dict[str, float]) -> None:
    """After the server's save: each weapon's owner paid what its target hull lost"""
    for name, (owner, _, hull_id, _) in WEAPONS.items():
        lost = hull_loss(hull_id)
        kinds = {k for k, _, _ in changes(str(owner))}
        check(
            lost > 0
            and kinds == {"Damage"}
            and 0.85 * lost <= -paid[name] <= 1.05 * lost,
            f"{name}: its offline owner pays for the other member's hull: paid {-paid[name]:.0f} SC, "
            f"the saved hull lost {lost:.0f} SC",
        )
    payers = {str(TOOLER), *(str(o) for o, *_ in WEAPONS.values())}
    others = [
        m[0]
        for m in re.finditer(r"Reputation of [^(]*\((\d+)\): .*", rig.server_game_log())
        if m[1] not in payers
    ]
    check(not others, f"and no one else pays: {others[:5]}")


def run_toolcore() -> None:
    rig.stop_server()
    prepare(toolcore=True)
    rig.write_plugin_config(config())
    rig.prepare_server()
    rig.start_server()
    try:
        check_toolcore()
        paid = weapons_done()
    finally:
        rig.stop_server()
    check_weapons(paid)


def run() -> None:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.write_plugin_config(config())
    rig.prepare_server(extra_dev_folders=[ESSENTIALS, CONSOLE_PROBE])
    rig.start_server(env=ALIASES)
    rig.prepare_client(extra_dev_folders=[PROBE])
    remote = rig.start_client(extra_dev_folders=[PROBE])
    try:
        tag = remote.get_faction()["tag"]
        check(tag == "OCT", f"the player is in {tag}")
        if "weapons" in sys.argv[1:]:
            check_client_weapons(remote)
            return
        check(reputation(remote) == 10_000, "the player starts at 10,000")
        check_grinding(remote)
        check_integration(remote)
        check_share(remote)
        check_deposit(remote)
        check_ram(remote)
        check_log(reputation(remote) - 10_000)
        check_client_weapons(remote)
        check_removal(remote)
    finally:
        rig.stop_client()
        rig.stop_server()


if __name__ == "__main__":
    check_shipped_config()
    if "toolcore" not in sys.argv[1:]:
        run()
    if "client" not in sys.argv[1:]:
        run_toolcore()
    sys.exit(st.summary("Reputation"))
