"""The stations' shipyard mode panel: a 4-button sci-fi panel whose screens show the game's Unknown
faction logo, a welder, the Core Systems logo and a grinder for Build, Repair, Hack and Grind
(SE-1390).

"patched": the world is one built before the change (OCT_WORLD_SOURCE, from the commit the live
world has), with the old one-button pedestal; this working copy's config has the station patches
that swap in the new panel on load. "fresh": a world built from this working copy (no
OCT_WORLD_SOURCE), which has the panel from the start. The client visits each station's panel,
reads the sprites its four screens rendered and saves a screenshot.

    OCT_TASK=SE-1390 [OCT_WORLD_SOURCE=<world built from main with --octahedron-mod 3833786664>] \\
        uv run --no-project --with httpx python tests/test_mode_panel.py patched|fresh [OUT]

Builds OctahedronMod (the station radar) from this working copy and serves it as Workshop
item 3833786664. Screenshots and the surfaces go to OUT (default ~/.se-test/<task>-panel-<mode>).
Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1390")
import rig  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else "patched"
OUT = (
    Path(sys.argv[2])
    if len(sys.argv) > 2
    else rig.TEST_ROOT / f"{rig.TASK}-panel-{MODE}"
)
OCTAHEDRON_MOD = 3833786664
TRADE_LANES = 3816175430
PANEL_NAME = "Button Panel - Book Shipyard"
# The one-button pedestal it replaces, in a world from before the change
OLD_PANEL_NAME = "Button Panel - Station ShipyardConfirm"
ICONS = [
    r"Textures\FactionLogo\Unknown.dds",
    "MyObjectBuilder_PhysicalGunObject/WelderItem",
    r"Textures\FactionLogo\DLC\CoreSystems.dds",
    "MyObjectBuilder_PhysicalGunObject/AngleGrinderItem",
]
# Where the viewer stands: this far from the panel along the station's right, up and forward
VIEW = tuple(float(v) for v in os.environ.get("PANEL_VIEW", "-3,0.3,0").split(","))
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
    """Each station's shipyard button: its min cell, subtype and world position, and the grid's axes"""
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
            if b.findtext("CustomName") in (PANEL_NAME, OLD_PANEL_NAME):
                cell, subtype = vec(b.find("Min")), b.findtext("SubtypeName")
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
        panel = add(
            add(add(position, right, 2.5 * cell[0]), up, 2.5 * cell[1]),
            forward,
            -2.5 * cell[2],
        )
        found[name] = {
            "cell": tuple(int(c) for c in cell),
            "subtype": subtype,
            "panel": panel,
            "right": right,
            "up": up,
            "forward": forward,
        }
    return found


def prepare() -> None:
    rig.prepare_world()
    mods = rig.mod_items(
        {TRADE_LANES: "Trade Lanes MP", OCTAHEDRON_MOD: "OctahedronMod"}
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        if f"<PublishedFileId>{OCTAHEDRON_MOD}<" not in text:
            text = text.replace("<Mods>", "<Mods>" + mods, 1)
        path.write_text(text, encoding="utf-8")
    built = rig.TEST_ROOT / f"{rig.TASK}-octahedron-mod"
    shutil.rmtree(built, ignore_errors=True)
    subprocess.run(
        [sys.executable, str(rig.PLUGIN.parent / "make_octahedron_mod.py"), str(built)],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        rig.link_mods(folder, [TRADE_LANES])
        target = folder / f"{OCTAHEDRON_MOD}.sbm"
        if target.is_symlink():
            target.unlink()
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(built, target)
    rig.write_plugin_config(rig.CONFIG_SOURCE.read_text(encoding="utf-8"))


def grid_ids(remote) -> dict[str, int]:
    return {
        g.get("displayName") or g.get("name"): g["entityId"]
        for g in remote.list_grids()
    }


def textures(surface: dict) -> list[str]:
    return [
        sp.get("data", "")
        for sp in surface.get("sprites", [])
        if sp.get("type") == "TEXTURE"
    ]


def visit(remote, name: str, s: dict) -> None:
    station = name.split()[0]
    remote.character_teleport(
        *add(
            add(add(s["panel"], s["right"], VIEW[0]), s["up"], VIEW[1]),
            s["forward"],
            VIEW[2],
        )
    )
    try:
        remote.character_look_at(*s["panel"], tolerance=2)
    except Exception as e:  # noqa: BLE001 -- the surfaces still tell
        print(f"  look-at {station}: {e}")
    # The client streams the station in and renders the screens it looks at
    deadline = time.monotonic() + 120
    surfaces = []
    while time.monotonic() < deadline:
        time.sleep(3)
        grid_id = grid_ids(remote).get(name)
        if grid_id is None:
            continue
        surfaces = [remote.get_surface(grid_id, s["cell"], i) for i in range(4)]
        if all(textures(x) for x in surfaces):
            break
    time.sleep(3)
    (OUT / f"{station}.json").write_text(json.dumps(surfaces, indent=1))
    print(
        f"  screenshot: {remote.screenshot_save(str(OUT / f'{station}.png'))}",
        flush=True,
    )
    time.sleep(3)  # The screenshot is written after the call returns
    shown = [textures(x) for x in surfaces]
    print(f"  {station}: {shown}", flush=True)
    for i, icon in enumerate(ICONS):
        check(
            i < len(shown) and icon in shown[i],
            f"{station}: button {i + 1} shows {icon}",
        )


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    source = rig.world_source()
    found = stations(source / "SANDBOX_0_0_0_.sbs")
    check(len(found) == 7, f"7 stations with the shipyard button: {sorted(found)}")
    old = {s["subtype"] for s in found.values()}
    print(f"  the world's shipyard buttons: {old}", flush=True)
    check(
        old
        == {
            "LargeButtonPanelPedestal" if MODE == "patched" else "LargeSciFiButtonPanel"
        },
        "the world has the expected buttons before loading",
    )
    prepare()
    rig.prepare_server()
    rig.start_server()
    try:
        remote = rig.start_client(render=True)
        remote.key(
            "V"
        )  # First person: the screenshots show the panel, not the character
        for name, s in found.items():
            visit(remote, name, s)
    finally:
        rig.stop_client()
        rig.stop_server()
    log = rig.server_game_log()
    if MODE == "patched":
        for name in found:
            patch = f"2026-11-13-mode-panel-{name.split()[0].lower()}"
            check(
                re.search(rf"Station patch STATUS {name} .*ledger=\S*{patch}", log)
                is not None,
                f"{name}: the ledger has {patch}",
            )
    print(f"{'PASSED' if failures == 0 else f'FAILED {failures}'}; results in {OUT}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
