"""A player uses a station's shared blocks but can't change them (SE-936). As a player outside
OCT's faction, without the admin "Use terminals": the terminal reaches only the blocks the station
shares with everyone, and the player's changes to every one of them, its name, custom data, on/off
and every switch and slider, don't hold on the server. The hologram table still switches on and
off and takes a blueprint, a Free Tools box gives an item, and the medical room charges the suit
and fills its O2 and H2. The control, NpcBlocksLocked off: the same changes hold. On a world built by
build_world.py.

    OCT_TASK=SE-936 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_npc_blocks.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 10 minutes.
Not covered: toolbars, LCD texts and block groups, which the player's client can't be driven to
change here; the server refuses them by the same rule.
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
import test_support_arm as sa  # noqa: E402
from se_remote import CallOp  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log, with_config  # noqa: E402

STATION = "Alpha"
# What build_world.py shares with everyone on a station and its shipyard
SHARED = {
    "MedicalRoom", "Projector", "StoreBlock", "ContractBlock", "VendingMachine", "Door",
    "AirtightSlideDoor", "ButtonPanel", "Cockpit", "ReflectorLight",
}  # fmt: skip
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
        {"MedicalRoom", "Projector", "StoreBlock", "ContractBlock"} <= kinds,
        f"the medical room, hologram table, store and contracts among them: {sorted(kinds)}",
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


def locked(a, found: list[dict]) -> None:
    log(probe(a, f"settings {STATION} save"))
    tried = tamper(a, found, skip_table=True)
    time.sleep(10)
    same, moved, reply = changed(a)
    check(
        tried > 50 and moved == 0 and same == len(found),
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


def medical(a, how: str = "") -> None:
    """The medical room charges the suit"""
    reply = probe(a, f"medical {STATION}")
    m = re.search(
        r"MEDICAL (\d+) (\S+ \S+ \S+) FORWARD (\S+ \S+ \S+) UP (\S+ \S+ \S+)", reply
    )
    detectors = [vec(d) for d in re.findall(r"DETECTOR \S+ (\S+ \S+ \S+)", reply)]
    check(
        m is not None and bool(detectors),
        f"the medical room and its use detectors: {reply}",
    )
    if m is None or not detectors:
        return
    forward, up = vec(m[3]), vec(m[4])
    right = tuple(
        forward[(i + 1) % 3] * up[(i + 2) % 3] - forward[(i + 2) % 3] * up[(i + 1) % 3]
        for i in range(3)
    )
    target = None
    for detector in detectors:
        for side in (
            forward,
            right,
            tuple(-c for c in forward),
            tuple(-c for c in right),
        ):
            stand = tuple(detector[i] + side[i] * 1.3 - up[i] * 1.5 for i in range(3))
            a.character_teleport(*stand)
            time.sleep(3)
            a.character_look_at(*detector, tolerance=2)
            time.sleep(1)
            used = a.character_use()
            log(f"use from {side}: {used}")
            if "LifeSupporting" in str(used.get("target")):
                target = detector
                break
        if target:
            break
    check(target is not None, "the player reaches the medical room's panel")
    if not target:
        return
    charge(a, how)


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


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.write_plugin_config()
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        sa.at_table(a, STATION)
        log(probe(a, "whoami"))
        found = reached(a)
        locked(a, found)
        free_tools(a, found)
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
