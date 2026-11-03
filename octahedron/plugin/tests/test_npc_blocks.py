"""A player uses a station's blocks but can't change them (SE-936). As a player outside OCT's
faction, without the admin "Use terminals": the terminal reaches only the blocks the station
shares with everyone (medical rooms, hologram table, seats, Free Tools, the shipyard's lights).
The store, ATM, vending machine, contracts, airlocks and button panel are the faction's, with
the game's "Anyone can use" on, but for the doors that are off. The player's changes to all of them,
name, custom data, on/off and every switch and slider, don't hold on the server. The engineering
section's doors are off and stay off and shut. A door opens for the player, the hologram table
still switches on and off and takes a blueprint, a Free Tools box gives an item, and the medical
room charges the suit and fills its O2 and H2. The control, NpcBlocksLocked off: the changes to
a shared block hold. On a world built by build_world.py.

    OCT_TASK=SE-936 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_npc_blocks.py

Needs the Workshop copy of Rotary Airlock, as OCT_WORKSHOP says.
Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 10 minutes.
Not covered: toolbars, LCD texts and block groups, which the player's client can't be driven to
change here; the server refuses them by the same rule. Pressing the button panel: the headless
client's use doesn't hit it (tests/test_paid_builds.py). Buying: tests/test_store.py buys through
the Store block's own requests.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-936")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_support_arm as sa  # noqa: E402
from se_remote import CallOp  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log, with_config  # noqa: E402

STATION = "Alpha"
# The station's airlocks, the engineering section's among them
MODS = {1359954841: "Rotary Airlock"}
# What build_world.py shares with everyone on a station and its shipyard
SHARED = {"MedicalRoom", "Projector", "Cockpit", "ReflectorLight"}
# What it keeps with the faction, with "Anyone can use" on
ANYONE = {
    "StoreBlock",
    "ContractBlock",
    "VendingMachine",
    "AdvancedDoor",
    "ButtonPanel",
}
FREE_TOOLS = "Free Tools"
# Uses, not settings: a door's Open opens it
USES = {"Open"}
HACKED = "Hacked by a player"
# Loaded into the hologram table: a cockpit, a battery, two armor blocks
BLUEPRINT = rig.grid("Starter Test", None, rig.small_ship_blocks(), tag="CubeGrid")


def blocks(a) -> list[dict]:
    return [
        {
            "id": int(m[1]),
            "grid": int(m[2]),
            "min": tuple(int(c) for c in m[3].split(",")),
            "kind": m[4],
            "share": m[5],
            "terminal": m[6],
            "inventory": m[7],
            "name": m[8],
        }
        for m in re.finditer(
            r"BLOCK (\d+) (\d+) (\S+) (\S+) share=(\S+) terminal=(\S+) inventory=(\S+) name=([^\n]*?)(?= BLOCK|\n|$| ACCESS)",
            probe(a, f"access {STATION}"),
        )
    ]


def reached(a) -> list[dict]:
    """The blocks the player's terminal works, all of them shared ones"""
    found = blocks(a)
    for b in found:
        log(
            f"  {b['kind']} '{b['name']}' share={b['share']} inventory={b['inventory']}"
        )
    kinds = {b["kind"].split("/")[0] for b in found}
    stray = [
        b
        for b in found
        if b["kind"].split("/")[0] not in SHARED
        and not (b["kind"].startswith("CargoContainer/") and b["name"] == FREE_TOOLS)
    ]
    check(not stray, f"the terminal reaches only the shared blocks: {stray[:3]}")
    tools = [b for b in found if b["name"] == FREE_TOOLS]
    check(
        len(tools) == 4 and all(b["inventory"] == "yes" for b in tools),
        f"the 4 Free Tools boxes, shown in inventory screens: {len(tools)}",
    )
    check(
        {"MedicalRoom", "Projector"} <= kinds,
        f"the medical room and hologram table among them: {sorted(kinds)}",
    )
    return found


def usable(a) -> list[dict]:
    """The blocks anyone can use, none of them shared"""
    found = [
        {
            "id": int(m[1]),
            "grid": int(m[2]),
            "min": tuple(int(c) for c in m[3].split(",")),
            "kind": m[4],
            "share": m[5],
            "access": m[6],
            "name": m[7],
        }
        for m in re.finditer(
            r"USE (\d+) (\d+) (\S+) (\S+) share=(\S+) access=(\S+) name=([^\n]*?)(?= USE|\n|$| USES)",
            probe(a, f"uses {STATION}"),
        )
    ]
    for b in found:
        log(f"  {b['kind']} '{b['name']}' share={b['share']} access={b['access']}")
    kinds = {b["kind"].split("/")[0] for b in found}
    check(
        ANYONE <= kinds,
        f"the store, ATM, vending machine, contracts, airlocks and button panel are for anyone to use: {sorted(kinds)}",
    )
    check(
        all(b["share"] == "Faction" and b["access"] == "no" for b in found),
        f"none of them shared with the player: {[b for b in found if b['share'] != 'Faction' or b['access'] != 'no'][:3]}",
    )
    return found


def tamper(a, found: list[dict], skip_table: bool) -> int:
    """Every change the player's client can make to the blocks; the number tried"""
    tried = 0
    for b in found:
        at = (b["grid"], b["min"])
        for what, call in (
            ("name", lambda: a.set_custom_name(*at, HACKED)),
            ("custom data", lambda: a.set_custom_data(*at, HACKED)),
        ):
            try:
                call()
                tried += 1
            except Exception as e:  # noqa: BLE001 -- the client refusing counts as held
                log(f"  {b['kind']} {what}: {e}")
        try:
            properties = a.list_properties(*at)
        except Exception as e:  # noqa: BLE001
            log(f"  {b['kind']} properties: {e}")
            continue
        for p in properties:
            pid, kind = p["id"], p["typeName"]
            if pid in USES or (
                skip_table and pid == "OnOff" and b["kind"].startswith("Projector/")
            ):
                continue
            try:
                value = a.get_property(*at, pid)
                if kind == "Boolean":
                    a.set_property(*at, pid, not value["value"])
                elif kind == "Single":
                    lo, hi, v = value["min"], value["max"], value["value"]
                    if not (math.isfinite(lo) and math.isfinite(hi)) or lo == hi:
                        continue
                    a.set_property(*at, pid, hi if v < hi else lo)
                elif kind == "Color":
                    a.set_property(*at, pid, {"r": 1.0, "g": 0.0, "b": 1.0})
                else:
                    continue
                tried += 1
            except Exception as e:  # noqa: BLE001
                log(f"  {b['kind']} {pid}: {e}")
    return tried


def changed(a) -> tuple[int, int, str]:
    reply = probe(a, f"settings {STATION} diff")
    m = re.search(r"SAME (\d+) CHANGED (\d+)", reply)
    return (int(m[1]), int(m[2]), reply) if m else (-1, -1, reply)


def locked(a, found: list[dict], uses: list[dict]) -> None:
    log(probe(a, f"settings {STATION} save"))
    tried = tamper(a, found + uses, skip_table=True)
    time.sleep(10)
    same, moved, reply = changed(a)
    check(
        tried > 50 and moved == 0 and same == len(found) + len(uses),
        f"none of the player's {tried} changes holds on the server: {reply}",
    )
    room = next(b for b in found if b["kind"].startswith("MedicalRoom/"))
    shown = st.wait_until(
        lambda: (b := a.get_block(room["grid"], room["min"]))["enabled"] and b, 15, 2
    )
    check(
        bool(shown),
        "the player's client gets the station's settings back: the medical room is on",
    )
    log(
        f"the player's client shows the medical room as {shown or a.get_block(room['grid'], room['min'])}"
    )


def table(a, found: list[dict]) -> None:
    """The hologram table switches off and on, and takes a blueprint"""
    t = next(b for b in found if b["kind"].startswith("Projector/"))
    at = (t["grid"], t["min"])
    for on in (False, True):
        a.set_enabled(*at, on)
        check(
            bool(
                st.wait_until(
                    lambda: f"ENABLED {t['id']} {'yes' if on else 'no'}"
                    in probe(a, f"enabled {t['id']}"),
                    15,
                    2,
                )
            ),
            f"the player switches the hologram table {'on' if on else 'off'}",
        )
    reply = a.call(
        [CallOp.fat_block_method(t["id"], "LoadProjection", {"xml": blueprint()})]
    ).call(0)
    log(f"LoadProjection: {reply}")
    check(
        bool(
            st.wait_until(
                lambda: f"OWNER {t['id']} player" in probe(a, f"owner {t['id']}"), 20, 2
            )
        ),
        "a blueprint loads into the table, which is the player's then",
    )


def blueprint() -> str:
    return (
        '<?xml version="1.0"?>\n<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Starter Test" />'
        f"<DisplayName>Starter Test</DisplayName><CubeGrids>{BLUEPRINT}</CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>"
    )


def free_tools(a, found: list[dict]) -> None:
    """A Free Tools box gives an item to another one"""
    boxes = [b for b in found if b["name"] == FREE_TOOLS]
    src, dst = boxes[0], boxes[1]
    items = a.get_inventory(src["grid"], src["min"])["items"]
    check(bool(items), f"the Free Tools box has items: {len(items)}")
    if not items:
        return
    item = items[0]
    name = f"{item['typeId'].replace('MyObjectBuilder_', '')}/{item['subtypeId']}"

    def amounts() -> tuple[float, float]:
        return tuple(
            float(re.search(r"ITEM \d+ (\S+)", probe(a, f"item {b['id']} {name}"))[1])
            for b in (src, dst)
        )

    before = amounts()
    a.transfer_inventory(
        src["grid"], src["min"], dst["grid"], dst["min"], item["itemId"], 1_000_000
    )
    after = st.wait_until(lambda: (x := amounts()) != before and x, 15, 2) or amounts()
    check(
        after[0] == before[0] - 1 and after[1] == before[1] + 1,
        f"one {name} moves from one Free Tools box to another: {before} -> {after}",
    )


def vec(text: str) -> tuple[float, float, float]:
    return tuple(float(c) for c in text.split())


def placed(a, entity_id: int) -> tuple[list, tuple, tuple]:
    """Where to aim at a block: its use detectors, else its center; its forward and up"""
    reply = probe(a, f"where {entity_id}")
    m = re.search(
        r"WHERE \d+ (\S+ \S+ \S+) FORWARD (\S+ \S+ \S+) UP (\S+ \S+ \S+)", reply
    )
    if m is None:
        return [], (0, 0, 1), (0, 1, 0)
    points = [vec(d) for d in re.findall(r"DETECTOR \S+ (\S+ \S+ \S+)", reply)]
    return points or [vec(m[1])], vec(m[2]), vec(m[3])


def use(a, entity_id: int, want: str) -> bool:
    """Stands by the block, looks at it and uses it (F) until the use hits what is wanted"""
    points, forward, up = placed(a, entity_id)
    right = tuple(
        forward[(i + 1) % 3] * up[(i + 2) % 3] - forward[(i + 2) % 3] * up[(i + 1) % 3]
        for i in range(3)
    )
    for point in points:
        for side in (
            forward,
            right,
            tuple(-c for c in forward),
            tuple(-c for c in right),
        ):
            stand = tuple(point[i] + side[i] * 1.3 - up[i] * 1.5 for i in range(3))
            a.character_teleport(*stand)
            time.sleep(3)
            a.character_look_at(*point, tolerance=2)
            time.sleep(1)
            used = a.character_use()
            log(f"use {entity_id} from {side}: {used}")
            if want in str(used.get("target")):
                return True
    return False


def medical(a, how: str = "") -> None:
    """The medical room charges the suit"""
    room = re.search(r"MEDICAL (\d+)", probe(a, f"medical {STATION}"))
    check(
        room is not None and use(a, int(room[1]), "LifeSupporting"),
        f"the player reaches the medical room's panel{how}",
    )
    if room is not None:
        charge(a, how)


def doors(a) -> list[dict]:
    return [
        {
            "id": int(m[1]),
            "grid": int(m[2]),
            "min": tuple(int(c) for c in m[3].split(",")),
            "kind": m[4],
        }
        | dict(re.findall(r"(\w+)=(\S+)", m[5]))
        | {"name": m[6]}
        for m in re.finditer(
            r"DOOR (\d+) (\d+) (\S+) (\S+) (.*?) name=([^\n]*?)(?= DOOR|\n|$)",
            probe(a, f"doors {STATION}"),
        )
    ]


def door(a) -> None:
    """A door that anyone can use opens for the player. The lounge's airlocks stand side by
    side, so the use may hit the next one: any of them turning counts"""
    before = {d["id"]: d for d in doors(a)}
    for d in [
        d for d in before.values() if d["enabled"] == "yes" and d["anyone"] == "yes"
    ]:
        if not use(a, d["id"], "Door"):
            continue

        def turned() -> list[str]:
            return [
                f"'{x['name']}' open={before[x['id']]['open']} -> {x['open']}"
                for x in doors(a)
                if x["anyone"] == "yes" and x["open"] != before[x["id"]]["open"]
            ]

        moved = st.wait_until(turned, 10, 1)
        check(bool(moved), f"the player opens or closes an airlock: {moved}")
        return
    check(False, "the player reaches a door anyone can use")


def shut(a) -> None:
    """The engineering section's doors are off and stay off and shut to players"""
    engineering = [d for d in doors(a) if "Engineering" in d["name"]]
    for d in engineering:
        log(f"  {d}")
    check(
        len(engineering) == 2
        and all(
            d["enabled"] == "no"
            and d["anyone"] == "no"
            and d["share"] == "Faction"
            and d["access"] == "no"
            for d in engineering
        ),
        f"the engineering section's 2 doors are off, the faction's, and not for anyone to use: {len(engineering)}",
    )
    for d in engineering:
        at = (d["grid"], d["min"])
        for what, call in (
            ("on", lambda: a.set_enabled(*at, True)),
            ("anyone can use", lambda: a.set_property(*at, "AnyoneCanUse", True)),
            ("open", lambda: a.set_property(*at, "Open", d["open"] != "yes")),
        ):
            try:
                call()
            except Exception as e:  # noqa: BLE001 -- the client refusing counts as held
                log(f"  {d['name']} {what}: {e}")
        use(a, d["id"], "Door")
    time.sleep(10)
    after = {d["id"]: d for d in doors(a)}
    held = [
        d["name"]
        for d in engineering
        if (after[d["id"]]["enabled"], after[d["id"]]["anyone"], after[d["id"]]["open"])
        != ("no", "no", d["open"])
    ]
    check(
        not held,
        f"the player can't switch them on, open them or make them anyone's: {held}",
    )


def charge(a, how: str) -> None:
    """Uses the medical room's panel from where the character stands"""
    drained = probe(a, "suit drain")
    for _ in range(40):
        a.character_use()
        time.sleep(0.2)
    a.set_input_state(keys=["F"], mode="override")
    time.sleep(8)
    a.clear_input_state()
    after = probe(a, "suit")
    d = [float(x) for x in re.search(r"SUIT (\S+) (\S+) (\S+) (\S+)", drained).groups()]
    s = [float(x) for x in re.search(r"SUIT (\S+) (\S+) (\S+) (\S+)", after).groups()]
    check(
        s[1] > d[1] and s[2] > d[2] and s[3] > d[3],
        f"the medical room charges the suit and fills its O2 and H2{how}: {d[1:]} -> {s[1:]}",
    )
    # Not a check: healing comes with the same request as the charge
    log(f"health{how}: {d[0]} -> {s[0]}")


def control(a, found: list[dict]) -> None:
    """With NpcBlocksLocked off the same changes hold"""
    with_config(a, NpcBlocksLocked="false")
    log(probe(a, f"settings {STATION} save"))
    room = [b for b in found if b["kind"].startswith("MedicalRoom/")]
    tamper(a, room, skip_table=True)
    time.sleep(10)
    _, moved, reply = changed(a)
    check(moved >= 1, f"control, unlocked: the player's changes hold: {reply}")
    medical(a, " unlocked")
    with_config(a)


def prepare() -> None:
    """The world with the airlock mod, as tests/test_binoculars_zone.py loads its mods"""
    rig.prepare_world()
    items = "".join(
        f'<ModItem FriendlyName="{name}"><Name>{id_}.sbm</Name><PublishedFileId>{id_}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
        for id_, name in MODS.items()
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(
            text.replace("<Mods />", f"<Mods>{items}</Mods>", 1), encoding="utf-8"
        )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        for id_ in MODS:
            link = folder / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(pl.WORKSHOP / str(id_))
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, st.PROBE])


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        log(probe(a, "whoami"))
        found = reached(a)
        uses = usable(a)
        # Before the tampering: the game doesn't send a refused change of a block the player
        # has no access to back, so the player's own client would keep "Anyone can use" off
        door(a)
        locked(a, found, uses)
        free_tools(a, found)
        shut(a)
        medical(a)
        sa.at_table(a, STATION)
        table(a, found)
        control(a, found)
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    refused = [
        line
        for line in rig.server_game_log().splitlines()
        if "refused" in line and "NPC block" in line
    ]
    log(f"{len(refused)} kinds of change refused, for example: {refused[:5]}")
    return st.summary("NPC block")


if __name__ == "__main__":
    sys.exit(main())
