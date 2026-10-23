"""Checks the encounter spawner on the test server with a client that has no mods: the
world lists no MES or encounter mod, the timer spawns a space cargo ship after 5 minutes,
each spawn type spawns where MES would spawn it, the client sees the spawned grids, and
the mods' shares of spawn attempts per type and place. Prints a summary; the asserts stop
it at the first rule broken.

    uv run --no-project --with httpx python tests/test_encounters.py

OCT_SKIP_TIMER=1 skips the 5 minute wait for the timer's space cargo ship.
"""

from __future__ import annotations

import os
import re
import time

import rig

ATTEMPTS = 2000
SPAWN_LINE = re.compile(
    r"Encounter (\w+) (.+) \((.+)\) for (\S+) at \{X:(\S+) Y:(\S+) Z:(\S+)\}"
)
GRID_LINE = re.compile(
    r"Encounter grid (\d+) '(.*)', (\d+) blocks, at \{X:(\S+) Y:(\S+) Z:(\S+)\}"
)
started = time.monotonic()
summary: list[str] = []
last_place = None


def log(text: str) -> None:
    print(f"[{time.monotonic() - started:6.0f} s] {text}", flush=True)


def spawns() -> list[tuple]:
    return SPAWN_LINE.findall(rig.server_game_log())


def grids() -> list[tuple]:
    return GRID_LINE.findall(rig.server_game_log())


def here(remote) -> str:
    lines = rig.chat(remote, "!oct encounters", 4)
    for line in lines:
        log(f"  {line}")
    return "\n".join(lines)


def sample(remote, kind: str) -> None:
    lines = rig.chat(remote, f"!oct encounters {kind} {ATTEMPTS}", 15)
    summary.append("\n".join(lines))
    for line in lines:
        log(f"  {line}")


def force(remote, kind: str) -> tuple:
    """!oct encounter <kind>, then the spawn line and the grid lines it logged"""
    before, grids_before = len(spawns()), len(grids())
    for attempt in range(5):
        if last_place and not alive(remote):
            at(remote, last_place)
        reply = " ".join(rig.chat(remote, f"!oct encounter {kind}", 8))
        log(f"!oct encounter {kind}: {reply}")
        if "spawned" in reply:
            break
    else:
        raise AssertionError(f"{kind} did not spawn")
    # The server's log reaches the file a few seconds late
    for _ in range(15):
        time.sleep(2)
        new = spawns()[before:]
        if new or "boss signal" in rig.server_game_log():
            break
    time.sleep(2)
    if kind.startswith("Boss"):
        assert "boss signal" in rig.server_game_log(), kind
        summary.append(f"{kind}: boss signal sent")
        return (), []
    assert new and new[0][0] == kind, new
    # The game builds the grids in parallel, a few seconds after the spawn
    for _ in range(15):
        made = grids()[grids_before:]
        if made:
            break
        time.sleep(2)
    assert made, f"{kind}: no grid"
    summary.append(f"{kind}: {new[0][1]} ({new[0][2]}), {len(made)} grids")
    return new[0], made


def visit(remote, grid) -> None:
    """The client, with no mods, sees the grid once near it"""
    entity_id, name, blocks = int(grid[0]), grid[1], int(grid[2])
    x, y, z = (float(v) for v in grid[3:6])
    rig.ensure_character(remote)
    remote.character_teleport(x, y + 150, z)
    hover(remote, (x, y + 150, z))
    for _ in range(20):
        time.sleep(3)
        try:
            got = remote.get_grid(entity_id)
        except Exception:  # noqa: BLE001 -- not replicated yet
            continue
        if got:
            log(f"client sees {name} ({entity_id}): {str(got)[:200]}")
            summary.append(f"client sees {name} ({blocks} blocks on the server)")
            return
    raise AssertionError(f"the client never saw {name} ({entity_id})")


def planet(remote, name: str) -> dict:
    return next(p for p in remote.get_planets() if p["name"].startswith(name))


def over(p: dict, altitude: float, direction=(0.0, 0.0, 1.0)) -> tuple:
    r = p["maximumRadius"] + altitude
    return tuple(c + d * r for c, d in zip(p["center"], direction))


def alive(remote) -> bool:
    try:
        return remote.get_character().get("state") not in (None, "dead")
    except Exception:  # noqa: BLE001 -- 503 without a character
        return False


def hover(remote, position) -> None:
    """Jetpack and dampeners on, so the character stays where it was teleported"""
    remote.character_move_to(*position, speed="fly", timeout=3, reach=0.5)
    remote.character_set_dampeners(True)


def at(remote, position) -> str:
    """Teleports there, after a respawn if the character fell to its death"""
    global last_place
    last_place = position
    rig.ensure_character(remote)
    remote.character_teleport(*position)
    hover(remote, position)
    time.sleep(3)
    return here(remote)


def main() -> None:
    world = (rig.WORLD_SOURCE / "Sandbox.sbc").read_text(encoding="utf-8")
    mods = re.findall(r"<PublishedFileId>(\d+)</PublishedFileId>", world)
    for mes in ("1521905890", "2802625870", "2390106048", "973526550", "2310821218"):
        assert mes not in mods, f"the world still lists mod {mes}"
    summary.append(f"world mods: {len(mods)}, no MES or encounter mod")

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    loaded = re.search(r"Loaded (\d+) encounter spawn groups", rig.server_game_log())
    log(f"server ready, {loaded and loaded[0]}")
    remote = rig.start_client()
    rig.wait_world(remote)
    rig.ensure_character(remote)
    joined = time.monotonic()
    log("client joined, at the station")
    here(remote)

    if not os.environ.get("OCT_SKIP_TIMER"):
        # The first space cargo attempt is 300 s after the plugin first sees the player
        while time.monotonic() - joined < 420 and not any(
            s[0] == "SpaceCargoShip" for s in spawns()
        ):
            time.sleep(10)
        timed = [s for s in spawns() if s[0] == "SpaceCargoShip"]
        log(f"timer: {timed[:1]} after {time.monotonic() - joined:.0f} s")
        assert timed, "no space cargo ship within 420 s of joining"
        summary.append(f"timer: space cargo ship {timed[0][1]} ({timed[0][2]})")

    # Space, by Alpha Station's safe zone
    sample(remote, "SpaceCargoShip")
    sample(remote, "SpaceRandomEncounter")
    station = remote.get_character()["position"]
    force(remote, "SpaceCargoShip")
    _, encounter = force(remote, "SpaceRandomEncounter")
    # Bosses need a threat score of 1500 within 15 km; the test player has none
    assert "BossEncounterSpace: 0 eligible" in here(remote)
    summary.append("BossEncounterSpace: none eligible at threat 0")

    earth = planet(remote, "EarthLike")
    low = over(earth, 500)
    text = at(remote, low)
    altitude = float(re.search(r"altitude (\d+) m", text)[1])
    if altitude > 6000:
        low = over(earth, 500 - (altitude - 2000))
        at(remote, low)
    sample(remote, "AtmosphericCargoShip")
    sample(remote, "PlanetaryInstallation")
    _, installation = force(remote, "PlanetaryInstallation")
    at(remote, low)
    force(remote, "AtmosphericCargoShip")

    at(remote, over(earth, 15000))
    sample(remote, "GravityCargoShip")
    force(remote, "GravityCargoShip")

    moon = planet(remote, "Moon")
    at(remote, over(moon, 300))
    sample(remote, "PlanetaryInstallation")
    sample(remote, "AtmosphericCargoShip")
    force(remote, "PlanetaryInstallation")

    visit(remote, installation[0])
    visit(remote, encounter[0])
    rig.ensure_character(remote)
    remote.character_teleport(*station)

    print("\n=== summary")
    for line in summary:
        print(line)
    print("PASS")


if __name__ == "__main__":
    try:
        main()
    finally:
        rig.stop_client()
        rig.stop_server()
