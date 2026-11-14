"""The stations' big map LCD runs the OctahedronRadar script of OctahedronMod, which
leaves out the station's shipyard and lane computers and shows the other grids (SE-1380).

The world is one built before the change (OCT_WORLD_SOURCE, from the commit the live world has),
so the stations' LCDs still run Arthur's Lcd Mod radar. With "after" the plugin gets this working
copy's config, whose station patches switch them to OctahedronRadar on load; with "before" it gets
the config without them. Every station gets a probe ship 600 m from its LCD. The client visits
each station's LCD, reads the sprites it rendered and saves a screenshot.

One station (RADAR_CROWD, default North) gets 36 probe ships 300-1875 m away instead, for the
contact count and the two list columns of SE-1502; the others get 3 each.

After: every LCD runs OctahedronRadar, lists its probe ships and no lane computer ("TL ..."),
shipyard or shipyard arm, has the contact count in the top right corner, and lists at most 15
contacts per column by distance, nearest first, the left column first: the 30 nearest of the 36
on the crowded station. Before: only reports what Arthur's radar draws, for comparison.

    OCT_TASK=SE-1380 OCT_WORLD_SOURCE=<world built from main with --octahedron-mod 3833786664> \\
        uv run --no-project --with httpx python tests/test_station_radar.py after|before [OUT]

Builds OctahedronMod from this working copy (make_octahedron_mod.py) and serves it as
Workshop item 3833786664, with Arthur's Lcd Mod from the local Workshop copy. Screenshots and the
sprite texts go to OUT (default ~/.se-test/<task>-radar-<mode>). Prints one line per check and a
pass/fail summary; exit code 0 = all passed. About 10 minutes.
"""

from __future__ import annotations

import json
import math
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1380")
import rig  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else "after"
OUT = (
    Path(sys.argv[2])
    if len(sys.argv) > 2
    else rig.TEST_ROOT / f"{rig.TASK}-radar-{MODE}"
)
OCTAHEDRON_MOD = 3833786664
ARTHUR = 3730092649
# The lane computers are its blocks: without it their grids load empty and go
TRADE_LANES = 3816175430
LCD_NAME = "LCD Panel - Station Lounge big map"
# Station infrastructure the radar must leave out, by grid name
# Which side of the screen the viewer stands on, along the station grid's forward
SIDE = float(os.environ.get("RADAR_SIDE", "-1"))
CROWD = os.environ.get("RADAR_CROWD", "North")
CROWD_SIZE = 36
DISTANCE = re.compile(r"^(\d+) m$|^(\d+\.\d\d) km$")
RING = re.compile(r"^\d+ k?m$")
COUNT = re.compile(r"^Contacts: (\d+)$")
HIDDEN = re.compile(r"^TL \d|Shipyard|^Subgrid|Alien Shipyard arm")
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"

failures = 0


def check(ok: bool, what: str) -> None:
    global failures
    failures += not ok
    print(f"{'PASS' if ok else 'FAIL'} {what}", flush=True)


def vec(e) -> tuple[float, float, float]:
    return tuple(float(e.get(a)) for a in "xyz")


def add(a, b, k=1.0):
    return tuple(x + k * y for x, y in zip(a, b))


def stations(sector: Path) -> dict[str, dict]:
    """Each station grid's big map LCD: its min cell and world position, and the grid's axes"""
    found = {}
    for entity in (
        ET.parse(sector).getroot().iterfind("SectorObjects/MyObjectBuilder_EntityBase")
    ):
        name = entity.findtext("DisplayName") or ""
        if entity.get(XSI_TYPE) != "MyObjectBuilder_CubeGrid" or not name.endswith(
            " Station"
        ):
            continue
        for b in entity.iterfind("CubeBlocks/MyObjectBuilder_CubeBlock"):
            if b.findtext("CustomName") == LCD_NAME:
                cell = vec(b.find("Min"))
                break
        else:
            continue
        place = entity.find("PositionAndOrientation")
        position, forward, up = (
            vec(place.find(t)) for t in ("Position", "Forward", "Up")
        )
        right = (
            forward[1] * up[2] - forward[2] * up[1],
            forward[2] * up[0] - forward[0] * up[2],
            forward[0] * up[1] - forward[1] * up[0],
        )
        lcd = add(
            add(add(position, right, 2.5 * cell[0]), up, 2.5 * cell[1]),
            forward,
            -2.5 * cell[2],
        )
        found[name] = {
            "cell": tuple(int(c) for c in cell),
            "lcd": lcd,
            "right": right,
            "up": up,
            "forward": forward,
        }
    return found


def probe_spots(station: str, s: dict) -> list[tuple[str, tuple]]:
    """Probe ship names and positions: numbered nearest first, spread around the screen"""
    if station == CROWD:
        rings = [(300 + 45 * i, 0.9 * i, 150 * math.sin(i)) for i in range(CROWD_SIZE)]
    else:
        rings = [(d, a, 150) for d, a in ((600, 0.0), (900, 2.1), (1300, 4.2))]
    spots = []
    for i, (d, a, h) in enumerate(rings, 1):
        at = add(s["lcd"], s["right"], d * math.cos(a))
        at = add(at, s["forward"], d * math.sin(a))
        spots.append((f"Radar Probe {station} {i:02}", add(at, s["up"], h)))
    return spots


def prepare(found) -> None:
    probes = [
        rig.grid(probe, None, rig.small_ship_blocks(), position=at)
        for name, s in found.items()
        for probe, at in probe_spots(name.split()[0], s)
    ]
    rig.prepare_world(extra_grids=probes)
    mods = rig.mod_items(
        {
            TRADE_LANES: "Trade Lanes MP",
            ARTHUR: "Arthur's Lcd Mod",
            OCTAHEDRON_MOD: "OctahedronMod",
        }
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        path.write_text(
            path.read_text(encoding="utf-8").replace("<Mods>", "<Mods>" + mods, 1),
            encoding="utf-8",
        )
    built = rig.TEST_ROOT / f"{rig.TASK}-octahedron-mod"
    shutil.rmtree(built, ignore_errors=True)
    subprocess.run(
        [sys.executable, str(rig.PLUGIN.parent / "make_octahedron_mod.py"), str(built)],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        rig.link_mods(folder, [TRADE_LANES, ARTHUR])
        target = folder / f"{OCTAHEDRON_MOD}.sbm"
        if target.is_symlink():
            target.unlink()
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(built, target)

    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    if MODE == "before":
        config = re.sub(
            r"\s*<StationPatchEntry>\s*<Id>[^<]*station-radar[^<]*</Id>.*?</StationPatchEntry>",
            "",
            config,
            flags=re.S,
        )
    rig.write_plugin_config(config)


def grid_ids(remote) -> dict[str, int]:
    return {
        g.get("displayName") or g.get("name"): g["entityId"]
        for g in remote.list_grids()
    }


def visit(remote, name: str, s: dict) -> None:
    station = name.split()[0]
    # The 5x3 panel's middle, and a spot in front of it: the side the character can stand on is
    # tried by the screenshot of each
    lcd = add(add(s["lcd"], s["right"], 5.0), s["up"], 2.5)
    remote.character_teleport(*add(lcd, s["forward"], SIDE * 9.0))
    # The client streams the station in once it is near, and a radar draws its contacts after a
    # splash screen: wait for a frame with more than the splash on it
    deadline = time.monotonic() + 240
    surface = {}
    while time.monotonic() < deadline:
        time.sleep(3)
        grid_id = grid_ids(remote).get(name)
        if grid_id is None:
            continue
        surface = remote.get_surface(grid_id, s["cell"])
        # Arthur's radar renders in a way Remote doesn't capture past its splash: just wait
        sprites = surface.get("sprites", [])
        if MODE == "before" and sprites:
            break
        # All its probes streamed in: the client gets the far ones last
        counts = [COUNT.match(sp.get("data") or "") for sp in sprites]
        shown = max((int(m.group(1)) for m in counts if m), default=0)
        if shown >= len(probe_spots(station, s)):
            break
    time.sleep(15 if MODE == "before" else 5)
    surface = remote.get_surface(grid_id, s["cell"])
    text_sprites = [sp for sp in surface.get("sprites", []) if sp.get("type") == "TEXT"]
    texts = [sp.get("data", "") for sp in text_sprites]
    (OUT / f"{station}.json").write_text(json.dumps(surface, indent=1))
    try:
        remote.character_look_at(*lcd, tolerance=2)
    except Exception as e:  # noqa: BLE001 -- a screenshot without aiming still helps
        print(f"  look-at {station}: {e}")
    time.sleep(2)
    print(
        f"  screenshot: {remote.screenshot_save(str(OUT / f'{station}.png'))}",
        flush=True,
    )
    time.sleep(3)  # The screenshot is written after the call returns
    shown = [t for t in texts if t.strip()]
    print(f"  {station}: script={surface.get('script')} texts={shown}", flush=True)
    # What the radar has to leave out is there: the client has streamed it in
    nearby = sorted(n for n in grid_ids(remote) if n and HIDDEN.search(n))
    print(
        f"  {station}: lane computers and shipyard grids the client has: {nearby}",
        flush=True,
    )
    if MODE == "before":
        return
    check(
        surface.get("script") == "OctahedronRadar",
        f"{station}: the LCD runs OctahedronRadar",
    )
    check(
        any(f"Radar Probe {station}" in t for t in texts),
        f"{station}: the probe ship shows",
    )
    check(
        any(n.startswith("TL ") for n in nearby),
        f"{station}: the client has lane computers nearby",
    )
    check(
        any("Shipyard" in n for n in nearby), f"{station}: the client has the shipyard"
    )
    check(
        not any(HIDDEN.search(t) for t in texts),
        f"{station}: no lane computer or shipyard shows",
    )
    check_list(station, s, text_sprites)


def check_list(station: str, s: dict, sprites: list[dict]) -> None:
    """The count in the top right corner and the two columns sorted by distance"""
    counts = [sp for sp in sprites if COUNT.match(sp.get("data") or "")]
    check(len(counts) == 1, f"{station}: one contact count")
    if not counts:
        return
    count_sprite = counts[0]
    count = int(COUNT.match(count_sprite["data"]).group(1))
    # A row is a distance and the name right of it, at the same height
    rows = []
    for sp in sprites:
        m = DISTANCE.match(sp.get("data") or "")
        if not m:
            continue
        x, y = sp["position"]
        meters = float(m.group(1)) if m.group(1) else 1000 * float(m.group(2))
        name = min(
            (
                o
                for o in sprites
                if abs(o["position"][1] - y) < 1 and 0 < o["position"][0] - x < 200
            ),
            key=lambda o: o["position"][0],
            default={"data": ""},
        )["data"]
        # Not a range ring's label, which has no name beside it
        if name and not RING.match(name):
            rows.append((x, y, meters, name))
    middle = (min(r[0] for r in rows) + max(r[0] for r in rows)) / 2 if rows else 0
    left = sorted((r for r in rows if r[0] <= middle), key=lambda r: r[1])
    right = sorted((r for r in rows if r[0] > middle), key=lambda r: r[1])
    listed = left + right
    probes = len(probe_spots(station, s))
    print(
        f"  {station}: count {count}, left {len(left)}, right {len(right)}: "
        + ", ".join(f"{r[2]:.0f} m {r[3]}" for r in listed),
        flush=True,
    )
    check(count >= probes, f"{station}: the count {count} has the {probes} probes")
    check(
        len(listed) == min(count, 30),
        f"{station}: lists min(count, 30) = {min(count, 30)} contacts",
    )
    check(
        len(left) == min(len(listed), 15) and len(right) == len(listed) - len(left),
        f"{station}: up to 15 in the left column, the rest in the right one",
    )
    check(
        all(a[2] <= b[2] for a, b in zip(listed, listed[1:])),
        f"{station}: sorted by distance, nearest first, left column first",
    )
    check(
        all(count_sprite["position"][1] < r[1] for r in listed)
        and all(count_sprite["position"][0] > r[0] for r in listed),
        f"{station}: the count is in the top right corner, above and right of the list",
    )
    if count == probes:
        wanted = [name for name, _ in probe_spots(station, s)][:30]
        # A long name ends in "…" where the column cuts it
        check(
            len(listed) == len(wanted)
            and all(
                n == w or (n.endswith("…") and w.startswith(n[:-1]))
                for n, w in zip((r[3] for r in listed), wanted)
            ),
            f"{station}: the nearest {len(wanted)} probes are listed in order",
        )


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    source = rig.world_source()
    found = stations(source / "SANDBOX_0_0_0_.sbs")
    check(len(found) == 7, f"7 stations with a big map LCD: {sorted(found)}")
    prepare(found)
    rig.prepare_server()
    rig.start_server()
    try:
        remote = rig.start_client(render=True)
        remote.key(
            "V"
        )  # First person: the screenshots show the screen, not the character
        for name, s in found.items():
            visit(remote, name, s)
    finally:
        rig.stop_client()
        rig.stop_server()
    print(f"{'PASSED' if failures == 0 else f'FAILED {failures}'}; results in {OUT}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
