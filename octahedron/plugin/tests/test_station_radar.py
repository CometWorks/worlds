"""The stations' big map LCD runs the OctahedronRadar script of the OctahedronFilaments mod, which
leaves out the station's shipyard and lane computers and shows the other grids (SE-1380).

The world is one built before the change (OCT_WORLD_SOURCE, from the commit the live world has),
so the stations' LCDs still run Arthur's Lcd Mod radar. With "after" the plugin gets this working
copy's config, whose station patches switch them to OctahedronRadar on load; with "before" it gets
the config without them. Every station gets a probe ship 600 m from its LCD. The client visits
each station's LCD, reads the sprites it rendered and saves a screenshot.

After: every LCD runs OctahedronRadar, lists its probe ship and no lane computer ("TL ..."),
shipyard or shipyard arm. Before: only reports what Arthur's radar draws, for comparison.

    OCT_TASK=SE-1380 OCT_WORLD_SOURCE=<world built from main with --filament-mod 3833786664> \\
        uv run --no-project --with httpx python tests/test_station_radar.py after|before [OUT]

Builds the OctahedronFilaments mod from this working copy (make_filament_mod.py) and serves it as
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
FILAMENTS = 3833786664
ARTHUR = 3730092649
# The lane computers are its blocks: without it their grids load empty and go
TRADE_LANES = 3816175430
LCD_NAME = "LCD Panel - Station Lounge big map"
# Station infrastructure the radar must leave out, by grid name
# Which side of the screen the viewer stands on, along the station grid's forward
SIDE = float(os.environ.get("RADAR_SIDE", "-1"))
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


def prepare(found) -> None:
    probes = [
        rig.grid(
            f"Radar Probe {name.split()[0]}",
            None,
            rig.small_ship_blocks(),
            position=add(add(s["lcd"], s["right"], 600), s["up"], 150),
        )
        for name, s in found.items()
    ]
    rig.prepare_world(extra_grids=probes)
    mods = rig.mod_items(
        {
            TRADE_LANES: "Trade Lanes MP",
            ARTHUR: "Arthur's Lcd Mod",
            FILAMENTS: "OctahedronFilaments",
        }
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        path.write_text(
            path.read_text(encoding="utf-8").replace("<Mods>", "<Mods>" + mods, 1),
            encoding="utf-8",
        )
    built = rig.TEST_ROOT / f"{rig.TASK}-filaments"
    shutil.rmtree(built, ignore_errors=True)
    subprocess.run(
        [sys.executable, str(rig.PLUGIN.parent / "make_filament_mod.py"), str(built)],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        rig.link_mods(folder, [TRADE_LANES, ARTHUR])
        target = folder / f"{FILAMENTS}.sbm"
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
    deadline = time.monotonic() + 120
    surface = {}
    while time.monotonic() < deadline:
        time.sleep(3)
        grid_id = grid_ids(remote).get(name)
        if grid_id is None:
            continue
        surface = remote.get_surface(grid_id, s["cell"])
        # Arthur's radar renders in a way Remote doesn't capture past its splash: just wait
        sprites = surface.get("sprites", [])
        if (MODE == "before" and sprites) or any(
            f"Radar Probe {station}" in (sp.get("data") or "") for sp in sprites
        ):
            break
    time.sleep(15 if MODE == "before" else 5)
    surface = remote.get_surface(grid_id, s["cell"])
    texts = [
        sp.get("data", "")
        for sp in surface.get("sprites", [])
        if sp.get("type") == "TEXT"
    ]
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
