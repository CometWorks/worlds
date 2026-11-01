"""Offline characters on a plain server (SE-854), with the headless client and the probe
plugin in tests/offline-characters/probe. OfflineCharacterMinutes is mostly 1 so that it
takes minutes, not hours.

- Away for less than the time (30 minutes here, as a client takes minutes to start): the
  same body, nothing taken.
- Away longer on a station, inside the bounding sphere of its grid: the body goes, its space credits are in the bank once, and the
  rest is in the saved world and back in the next body after a server restart. A second
  spawn gets nothing more.
- A full inventory comes back whole on top of the new body's gear, past the limit.
- Seated in the station: the same, and the seat is free.
- Just inside the sphere, standing or seated in a ship: the body goes.
- Just outside it, standing or seated in a ship reaching into it: the body stays.
- Away far from a station: the body stays.
- Dead when the player left: nothing is kept.

    OCT_TASK=SE-854 uv run --no-project --with httpx python tests/test_offline_characters.py

OCT_WORLD_SOURCE=<folder> runs it on a world built by build_world.py instead of the
committed one.
"""

from __future__ import annotations

import base64
import gzip
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import rig
from test_client import log, with_config
from test_money import log_marks, plugin_log

TESTS = Path(__file__).resolve().parent
PROBE = (
    "character-probe",
    TESTS / "offline-characters" / "probe",
    "CharacterProbe.xml",
    "7A2D4E10-8F36-4C51-B7A9-1D3E5F1736AA",
)
SC = 12_345
CARRIED = {
    "Ingot/Iron": 321.0,
    "Component/SteelPlate": 17.0,
    "PhysicalGunObject/Welder2Item": 1.0,
    "OxygenContainerObject/OxygenBottle": 1.0,
}
REMOVED = re.compile(r"Offline character of (\d+): body (\d+) removed")
# Past the 1 minute, a save's wait, a 30 s scan and a 5 s check; removals are watched for
AWAY = 240
# How long nothing must happen: a body has asked after 5 s, an offline one has gone after 100 s
QUIET = 15
# How far from the sphere's edge "just inside" and "just outside" are
EDGE = 3
QUIET_AWAY = 100
CONFIG = {
    "OfflineCharacterMinutes": 1,
    "EncountersEnabled": "false",
    "MoneySubsidyPerActivePlayer": 0,
    "MoneyPlayerTaxThreshold": 1_000_000_000,
}

failures: list[str] = []
passed = 0


def check(ok: bool, what: str) -> None:
    global passed
    log(("ok   " if ok else "FAIL ") + what)
    if ok:
        passed += 1
    else:
        failures.append(what)


def config_text(**values) -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for key, value in values.items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        if not count:
            text = text.replace(
                "</OctahedronConfig>", f"  <{key}>{value}</{key}>\n</OctahedronConfig>"
            )
    return text


def ask(remote, text: str, pattern: str = r".", timeout: float = 60) -> re.Match:
    """Sends a chat line and returns the first reply that matches, as soon as it is there"""
    before = remote.get_chat_messages()
    seen = {
        (h.get("sender"), h.get("text"), h.get("timestamp"))
        for h in before.get("hud", [])
    }
    remote.send_chat(text)
    deadline = time.monotonic() + timeout
    reply = ""
    while time.monotonic() < deadline:
        after = remote.get_chat_messages(after=before.get("latest", 0))
        lines = [m.get("Text", "") for m in after.get("messages", [])] + [
            h.get("text", "")
            for h in after.get("hud", [])
            if (h.get("sender"), h.get("text"), h.get("timestamp")) not in seen
        ]
        reply = " ".join(line for line in lines if line != text)
        found = re.search(pattern, reply)
        if found:
            return found
        time.sleep(0.5)
    raise TimeoutError(f"{text}: no {pattern} in {reply!r}")


def probe(remote, line: str, pattern: str = r".") -> re.Match:
    return ask(remote, f"!cprobe {line}", pattern)


def balance(remote) -> int:
    return int(ask(remote, "!oct money", r"You: ([\d,]+) SC")[1].replace(",", ""))


def inventory(remote) -> tuple[int, dict[str, float]]:
    found = probe(remote, "inv", r"INV (\d+) (\S*)")
    items = dict(p.split("=") for p in found[2].split(",") if p)
    return int(found[1]), {k: float(v) for k, v in items.items()}


def wait_inventory(remote, wanted, timeout: float = 90) -> tuple[int, dict[str, float]]:
    """The inventory once it holds at least the wanted amounts, or as it is at the timeout: a
    player's items come back a few seconds after the spawn"""
    deadline = time.monotonic() + timeout
    while True:
        body, items = inventory(remote)
        if time.monotonic() > deadline or all(
            items.get(item, 0) >= amount for item, amount in wanted.items()
        ):
            return body, items
        time.sleep(1)


def teleport(remote, x: float, y: float, z: float, timeout: float = 60) -> None:
    remote.character_teleport(x, y, z)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        at = remote.get_character()["position"]
        if sum((a - b) ** 2 for a, b in zip(position(at), (x, y, z))) < 400:
            return
        time.sleep(0.5)
    raise TimeoutError(f"not at {x} {y} {z}")


def position(at) -> tuple[float, float, float]:
    return (at["x"], at["y"], at["z"]) if isinstance(at, dict) else tuple(at)


def sphere(remote):
    """The nearest station's bounding sphere: center, radius, how far the body is from the center,
    and a direction with open space at the sphere's edge"""
    found = probe(remote, "sphere", r"SPHERE " + " ".join([r"(\S+)"] * 8))
    values = [float(found[i]) for i in range(1, 9)]
    return values[:3], values[3], values[4], values[5:]


def to_edge(remote, offset: float) -> tuple[float, float]:
    """Into open space that far from the sphere's edge. The body's distance from the center and
    the radius."""
    center, radius, _, clear = sphere(remote)
    at = [c + d * (radius + offset) for c, d in zip(center, clear)]
    teleport(remote, *at)
    # A new body has its dampeners off and a teleport keeps its velocity: it would drift off
    remote.character_move_to(*at, speed="fly", timeout=3, reach=0.5)
    remote.character_set_dampeners(True)
    last = None

    def there() -> float | None:
        nonlocal last
        distance = sphere(remote)[2]
        log(
            f"body {distance:.2f} m from the center of a {radius:.2f} m sphere, wanted {offset:+}"
        )
        if abs(distance - radius - offset) >= 1:
            teleport(remote, *at)
        at_rest = last is not None and abs(distance - last) < 0.05
        last = distance
        return distance if at_rest and abs(distance - radius - offset) < 1 else None

    return rig.wait_until(there, 60, "the body at rest at the sphere's edge"), radius


def sit(remote, seat: str, grid: str) -> None:
    """Into a seat of a grid, once the client has it: see the probe's seat command"""

    def there() -> bool:
        at = position(remote.get_character()["position"])
        entities = remote.get_entities(*at, 1000)["entities"]
        return any(e["entityId"] == int(grid) and e["inScene"] for e in entities)

    rig.wait_until(there, 120, "the client to have the grid")
    probe(remote, f"seatin {seat}", "SEAT")


def next_body(remote, body: int, timeout: float = 240) -> None:
    """Killed: through the respawn screen to a new body"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        rig.ensure_character(remote)
        if inventory(remote)[0] != body:
            return
        time.sleep(1)
    raise TimeoutError("no new body")


def stations() -> dict[str, tuple[float, float, float]]:
    found = {}
    for grid in (
        ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot().find("SectorObjects")
    ):
        name = grid.findtext("DisplayName") or ""
        if name.endswith(" Station"):
            at = grid.find("PositionAndOrientation/Position")
            found[name] = tuple(float(at.get(axis)) for axis in "xyz")
    return found


def spawned() -> "rig.RemoteAPI":
    return rig.start_client()


def leave_and_drop() -> None:
    """The client quits and the server sees it gone: a join before that is refused"""

    def drops() -> int:
        return rig.SERVER_LOG.read_text(errors="replace").count("User left")

    count = drops()
    rig.stop_client()
    rig.wait_until(lambda: drops() > count, 300, "the server to drop the client")


def leave_and_wait(pattern: re.Pattern, timeout: float = AWAY) -> re.Match | None:
    """The client quits; the first match in the server's logs since then"""
    marks = log_marks()
    rig.stop_client()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found = pattern.search(plugin_log(marks))
        if found:
            return found
        time.sleep(3)
    return None


def saved_book() -> list[list[str]]:
    """The saved world's rows of kept inventories"""
    text = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    found = re.search(
        r"<Key>Octahedron\.OfflineCharacters</Key>\s*<Value[^>]*>([^<]*)</Value>", text
    )
    rows = [line.split(" ") for line in (found[1] if found else "").split("\n")]
    return [row for row in rows if row[0] == "P"]


def saved_items(row: list[str]) -> str:
    return gzip.decompress(base64.b64decode(row[6])).decode()


def run() -> None:
    at = stations()
    log(f"stations: {sorted(at)}")
    alpha = at["Alpha Station"]
    remote = spawned()
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    to_edge(remote, -EDGE)
    probe(remote, "clear", "CLEAR")
    for item, amount in CARRIED.items():
        probe(remote, f"give {item} {amount:.0f}", "GIVE True")
    probe(remote, f"give PhysicalObject/SpaceCredit {SC}", "GIVE True")
    body, carried = inventory(remote)
    check(carried.get("PhysicalObject/SpaceCredit") == SC, f"carries the SC: {carried}")
    before = balance(remote)

    # --- back in time: a client takes minutes to start, longer than the short limit ---
    with_config(remote, **{**CONFIG, "OfflineCharacterMinutes": 30})
    leave_and_drop()
    remote = spawned()
    again, items = inventory(remote)
    check(again == body, f"back in time: the same body {body}: {again}")
    check(items == carried, f"back in time: nothing taken: {items}")
    check(balance(remote) == before, "back in time: the bank is as it was")
    with_config(remote, **CONFIG)
    # The limit, two status scans and the check after them
    time.sleep(100)
    again, items = inventory(remote)
    check(again == body and items == carried, "online past the time: nothing happens")

    # --- away: removed, kept through a restart, restored once ---
    removed = leave_and_wait(REMOVED)
    check(removed is not None and removed[2] == str(body), f"the body goes: {removed}")
    rig.stop_server()
    book = saved_book()
    check(len(book) == 1 and book[0][2] == str(body), f"kept in the save: {book}")
    if book:
        kept = saved_items(book[0])
        check(book[0][3] == str(SC), f"the SC went to the bank: {book[0][:6]}")
        check("SpaceCredit" not in kept, "no SC among the kept items")
        check("Welder2Item" in kept and "OxygenBottle" in kept, "the items are kept")
    rig.start_server()
    remote = spawned()
    new_body, items = wait_inventory(remote, CARRIED)
    check(new_body != body, f"a new body after the restart: {new_body}")
    for item, amount in CARRIED.items():
        check(items.get(item, 0) >= amount, f"back: {item} {amount}: {items.get(item)}")
    check("PhysicalObject/SpaceCredit" not in items, f"no SC came back: {items}")
    check(balance(remote) == before + SC, "the bank has the SC once")
    kept = ask(remote, "!oct offline", r"kept inventories: (\d+)")
    check(kept[1] == "0", f"nothing kept any more: {kept[0]}")
    restored = items

    probe(remote, "kill", "KILLED")
    next_body(remote, new_body)
    # A body asks within 5 s, and a kept inventory would be there in another few
    _, items = wait_inventory(remote, CARRIED, QUIET)
    check(
        all(items.get(item, 0) < amount for item, amount in CARRIED.items()),
        f"a second spawn gets nothing more: {items} after {restored}",
    )
    check(balance(remote) == before + SC, "and no more SC")

    # --- a full inventory comes back whole, past the limit ---
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    to_edge(remote, -EDGE)
    probe(remote, "fill", "FILL")
    body, full = inventory(remote)
    check(full.get("Ore/Stone", 0) > 0, f"full of stone: {full}")
    removed = leave_and_wait(REMOVED)
    check(removed is not None and removed[2] == str(body), "the full body goes")
    remote = spawned()
    _, items = wait_inventory(remote, {"Ore/Stone": full["Ore/Stone"]})
    check(
        items.get("Ore/Stone") == full["Ore/Stone"],
        f"all the stone is back on top of the new gear: {items} of {full}",
    )

    # --- seated ---
    teleport(remote, alpha[0] + 20, alpha[1], alpha[2])
    probe(remote, "clear", "CLEAR")
    probe(remote, "give Ingot/Gold 7", "GIVE True")
    probe(remote, "give PhysicalObject/SpaceCredit 100", "GIVE True")
    seat = probe(remote, "seat", r"SEAT (\d+|none) ?(\d*)")
    bank = balance(remote)
    if seat[1] != "none":
        sit(remote, seat[1], seat[2])
        body, _ = inventory(remote)
        check(probe(remote, "bodies", ":seated"), "seated at the station")
        _, radius, distance, _ = sphere(remote)
        check(distance < radius, f"seated: {distance:.1f} of {radius:.1f} m")
        removed = leave_and_wait(REMOVED)
        check(removed is not None and removed[2] == str(body), "the seated body goes")
        remote = spawned()
        new_body, items = wait_inventory(remote, {"Ingot/Gold": 7})
        check(new_body != body and items.get("Ingot/Gold") == 7, f"its items: {items}")
        check(balance(remote) == bank + 100, "its SC in the bank")
        pilot = probe(remote, f"pilot {seat[1]}", r"PILOT (\S+)")
        check(pilot[1] == "0", f"the seat is free: {pilot[0]}")
    else:
        check(False, "a seat near the station")

    # --- just inside the sphere ---
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    distance, radius = to_edge(remote, -EDGE)
    check(distance < radius, f"just inside: {distance:.1f} of {radius:.1f} m")
    probe(remote, "clear", "CLEAR")
    probe(remote, "give Ingot/Gold 3", "GIVE True")
    body, _ = inventory(remote)
    removed = leave_and_wait(REMOVED)
    check(removed is not None and removed[2] == str(body), "just inside: the body goes")
    remote = spawned()
    _, items = wait_inventory(remote, {"Ingot/Gold": 3})
    check(items.get("Ingot/Gold") == 3, f"just inside: its items: {items}")

    # --- just outside the sphere ---
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    distance, radius = to_edge(remote, EDGE)
    check(distance > radius, f"just outside: {distance:.1f} of {radius:.1f} m")
    body, items = inventory(remote)
    bank = balance(remote)
    stayed = leave_and_wait(REMOVED, QUIET_AWAY)
    check(stayed is None, f"just outside: the body stays: {stayed}")
    remote = spawned()
    again, items_again = inventory(remote)
    check(
        again == body and items_again == items, f"just outside: the same body: {again}"
    )
    check(balance(remote) == bank, "just outside: the bank as it was")
    distance = sphere(remote)[2]
    check(distance > radius, f"just outside: still there: {distance:.1f} m")

    # --- seated in a ship with the seat just inside: the body goes, the ship stays ---
    ship = probe(remote, "ship inside", r"SHIP (\d+) (\d+)")
    seat = ship[1]
    sit(remote, seat, ship[2])
    probe(remote, "clear", "CLEAR")
    probe(remote, "give Ingot/Gold 4", "GIVE True")
    _, radius, distance, _ = sphere(remote)
    check(distance < radius, f"in a ship, inside: {distance:.1f} of {radius:.1f} m")
    body, _ = inventory(remote)
    removed = leave_and_wait(REMOVED)
    check(
        removed is not None and removed[2] == str(body),
        "in a ship, inside: the body goes",
    )
    remote = spawned()
    _, items = wait_inventory(remote, {"Ingot/Gold": 4})
    check(items.get("Ingot/Gold") == 4, f"in a ship, inside: its items: {items}")
    pilot = probe(remote, f"pilot {seat}", r"PILOT (\S+)")
    check(
        pilot[1] == "0",
        f"in a ship, inside: the ship is there, the seat free: {pilot[0]}",
    )

    # --- seated in a ship reaching into the sphere with the seat just outside: it stays ---
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    ship = probe(remote, "ship outside", r"SHIP (\d+) (\d+)")
    sit(remote, ship[1], ship[2])
    _, radius, distance, _ = sphere(remote)
    check(distance > radius, f"in a ship, outside: {distance:.1f} of {radius:.1f} m")
    body, items = inventory(remote)
    stayed = leave_and_wait(REMOVED, QUIET_AWAY)
    check(stayed is None, f"in a ship, outside: the body stays: {stayed}")
    remote = spawned()
    again, items_again = inventory(remote)
    check(
        again == body and items_again == items,
        f"in a ship, outside: the same body: {again}",
    )
    probe(remote, "kill", "KILLED")
    next_body(remote, body)

    # --- far from a station ---
    teleport(remote, alpha[0] + 5_000, alpha[1], alpha[2])
    body, items = inventory(remote)
    bank = balance(remote)
    stayed = leave_and_wait(REMOVED, QUIET_AWAY)
    check(stayed is None, f"far from a station the body stays: {stayed}")
    remote = spawned()
    again, items_again = inventory(remote)
    check(again == body and items_again == items, f"the same body: {again}, {body}")
    check(balance(remote) == bank, "and the bank as it was")

    # --- dead when the player left ---
    teleport(remote, alpha[0] + 150, alpha[1], alpha[2])
    to_edge(remote, -EDGE)
    probe(remote, "give Ingot/Silver 9", "GIVE True")
    probe(remote, "kill", "KILLED")
    gone = leave_and_wait(REMOVED, QUIET_AWAY)
    check(gone is None, f"a dead character isn't kept: {gone}")
    remote = spawned()
    _, items = wait_inventory(remote, {"Ingot/Silver": 9}, QUIET)
    check("Ingot/Silver" not in items, f"nothing from the dead one: {items}")
    rig.stop_client()


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config(config_text(**CONFIG))
    rig.prepare_server(extra_dev_folders=[PROBE])
    rig.start_server()
    try:
        run()
    finally:
        rig.stop_client()
        rig.stop_server()
    print(f"{passed}/{passed + len(failures)} checks passed")
    for failure in failures:
        print("FAILED: " + failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
