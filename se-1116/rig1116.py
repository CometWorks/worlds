"""SE-1116 rig (from SE-1095's): filaments linger after a tool stops, the storm sound plays
to its end; weld and grind times unchanged.

    OCT_TASK=SE-1116-new FILAMENT_OCT=<worlds/octahedron with the mod> [FILAMENT_LINGER=2]
        python3 rig1116.py [--trace] [--audio] [--shots-only]

FILAMENT_LINGER overrides the mod's Linger constant (branch mod only).
--audio records the game's mixed output (Remote --audio-file) beside one welder: tools
off, the welder working, then switched off; prints how long the sound goes on after.
The shot bursts now also switch a working tool's Shoot off and on again within the
linger time (relink), and the retract burst is long enough for linger plus retract.

Like the SE-1067/SE-1089 rigs: a creative Empty World with ToolCore, OmniArc and the
OctahedronFilaments mod built from FILAMENT_OCT (main's or the branch's), the station
shipyard with filament tools and a 15x3x15 slab of partly built heavy armor. Then:
- weld the slab fully, then grind it away fully, timed in game frames and seconds;
- paste a fresh slab and shoot short screenshot bursts from beside the slab: welders on
  (extend), working (glide), welders off (retract), the same for the deconstructors;
- with --trace (the branch's mod only), a rig-only script logs the drawn end of a few
  tools every frame, and the rig checks extend and retract times and the glide.
Prints PASS/FAIL lines and a "RESULT" json line; exit code 0 = all passed.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

import audio_check
import trace_check

HERE = Path(__file__).resolve().parent
OCT = HERE / "worlds-new/octahedron"
FILAMENT_OCT = Path(os.environ.get("FILAMENT_OCT", OCT))
sys.path.insert(0, str(OCT / "plugin/tests"))
import rig  # noqa: E402
from se_remote import CallOp, OpError  # noqa: E402

LINGER = os.environ.get("FILAMENT_LINGER")
AUDIO = None
if "--audio" in sys.argv:
    AUDIO = rig.TEST_ROOT / f"{rig.TASK}-audio.wav"
    _start = rig._start

    def _start_with_audio(args, *a, **k):
        args = [x for x in args if x != "--no-audio"] + ["--audio-file", str(AUDIO)]
        return _start(args, *a, **k)

    rig._start = _start_with_audio
STEAM = Path.home() / ".steam/steam/steamapps"
TEMPLATE = STEAM / "common/SpaceEngineers/Content/CustomWorlds/Empty World"
WORKSHOP = STEAM / "workshop/content/244850"
MODS = {3040438530: "ToolCore", 3412657089: "OmniArc"}
LOCAL_MOD, LOCAL_ID = "OctahedronFilaments", 1067000001
BLUEPRINT = OCT / "blueprints/omni-arc-beam-shipyard.sbc"
WELDER, DECON = "OctahedronFilamentWelder", "OctahedronFilamentDeconstructor"
SHOTS = rig.TEST_ROOT / f"{rig.TASK}-shots"
WORK_TIME = 600
SLAB = (15, 3, 15)
# --sparse: only every other block along the slab's width and depth, 5 m apart, so the
# tools turn farther between blocks, more like a ship than a solid slab
SPARSE = "--sparse" in sys.argv


def in_slab(x, y, z):
    return not SPARSE or (x % 2 == 0 and z % 2 == 0)


BLOCKS = sum(in_slab(x, y, z) for x in range(SLAB[0]) for y in range(SLAB[1]) for z in range(SLAB[2]))
TRACE = "--trace" in sys.argv
# Only the shots, no timed weld and grind
SHOTS_ONLY = "--shots-only" in sys.argv
BURST_SIZE = (960, 540)

# Logs every frame's drawn end of every welder and deconstructor
TRACE_SCRIPT = """using System.Collections.Generic;
using System.Globalization;
using VRage.Game.Components;
using VRage.Utils;
using Sandbox.ModAPI;

namespace OctahedronFilaments
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class RigTrace : MySessionComponentBase
    {
        readonly Dictionary<string, HashSet<long>> traced = new Dictionary<string, HashSet<long>>();

        public override void LoadData()
        {
            FilamentWeb.Trace = (tool, extension, end, target__WORKING__) =>
            {
                var subtype = tool.BlockDefinition.SubtypeId;
                HashSet<long> ids;
                if (!traced.TryGetValue(subtype, out ids))
                    traced[subtype] = ids = new HashSet<long>();
                if (!ids.Contains(tool.EntityId) && (ids.Count >= 24 || !ids.Add(tool.EntityId)))
                    return;
                MyLog.Default.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "FilamentTrace {0:F4} {1} {2} {3:F4} {4:F3} {5:F3} {6:F3} {7:F3} {8:F3} {9:F3} {10}",
                    MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds, tool.EntityId, subtype, extension,
                    end.X, end.Y, end.Z, target.X, target.Y, target.Z, __W__));
            };
        }
    }
}
"""

failures = 0
result = {"mod": str(FILAMENT_OCT), "task": rig.TASK}


def check(ok, what):
    global failures
    failures += not ok
    print(("PASS " if ok else "FAIL ") + what, flush=True)


def world() -> Path:
    path = rig.APPDATA / "Saves" / str(rig.CLIENT_ID) / "FilamentShipyard"
    shutil.rmtree(path, ignore_errors=True)
    shutil.copytree(TEMPLATE, path)
    mods = rig.APPDATA / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    items = []
    for mod, name in [*MODS.items(), (LOCAL_ID, LOCAL_MOD)]:
        items.append(
            f'<ModItem FriendlyName="{name}"><Name>{mod}.sbm</Name><PublishedFileId>{mod}</PublishedFileId>'
            "<PublishedServiceName>Steam</PublishedServiceName><IsDependency>false</IsDependency></ModItem>"
        )
        target = mods / f"{mod}.sbm"
        if target.is_symlink() or target.is_file():
            target.unlink()
        shutil.rmtree(target, ignore_errors=True)
        if mod != LOCAL_ID:
            target.symlink_to(WORKSHOP / str(mod))
    local = mods / f"{LOCAL_ID}.sbm"
    subprocess.run([sys.executable, str(FILAMENT_OCT / "make_filament_mod.py"), str(local)], check=True)
    web = local / "Data/Scripts/OctahedronFilaments/FilamentWeb.cs"
    code = web.read_text()
    new = "const double Linger" in code
    result["linger_s"] = None
    if new:
        m = re.search(r"const double Linger = ([\d.]+);", code)
        if LINGER:
            code = code.replace(m.group(0), f"const double Linger = {LINGER};")
            web.write_text(code)
        result["linger_s"] = float(LINGER or m.group(1))
    if TRACE:
        script = TRACE_SCRIPT.replace("__WORKING__", ", working" if new else "").replace(
            "__W__", "working ? 1 : 0" if new else "-1"
        )
        (local / "Data/Scripts/OctahedronFilaments/RigTrace.cs").write_text(script)
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        f = path / name
        text = f.read_text(encoding="utf-8-sig")
        text = text.replace("<BlockLimitsEnabled>GLOBALLY<", "<BlockLimitsEnabled>NONE<")
        text = re.sub(r"<Mods\s*/>", "<Mods>" + "".join(items) + "</Mods>", text)
        assert all(i in text for i in items)
        f.write_text(text, encoding="utf-8")
    return path


def shipyard_xml() -> tuple[str, list, list]:
    tree = ET.parse(BLUEPRINT)
    main = tree.getroot().find(".//CubeGrid")
    welders, decons = [], []
    for grid in tree.getroot().iter("CubeGrid"):
        grid.find("DestructibleBlocks").text = "false"
    for b in main.find("CubeBlocks"):
        subtype = b.findtext("SubtypeName")
        if subtype not in ("OmniArcBeam", "OmniArcDestro"):
            continue
        b.find("SubtypeName").text = WELDER if subtype == "OmniArcBeam" else DECON
        e = b.find("Enabled")
        if e is None:
            e = ET.SubElement(b, "Enabled")
        e.text = "false"
        m = b.find("Min")
        cell = tuple(int(m.get(a, 0)) for a in "xyz") if m is not None else (0, 0, 0)
        (welders if subtype == "OmniArcBeam" else decons).append(cell)
    return ET.tostring(tree.getroot(), encoding="unicode"), welders, decons


def plate_xml(name):
    blocks = [
        rig.block(
            "MyObjectBuilder_CubeBlock",
            "LargeHeavyBlockArmorBlock",
            (x - SLAB[0] // 2, y - SLAB[1] // 2, z - SLAB[2] // 2),
            "<IntegrityPercent>0.05</IntegrityPercent><BuildPercent>0.05</BuildPercent>",
        )
        for x in range(SLAB[0])
        for y in range(SLAB[1])
        for z in range(SLAB[2])
        if in_slab(x, y, z)
    ]
    g = rig.grid(name, None, blocks).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
    g = g.replace("MyObjectBuilder_EntityBase", "CubeGrid").replace(' xsi:type="MyObjectBuilder_CubeGrid"', "")
    return (
        '<?xml version="1.0"?><Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints><ShipBlueprint>'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Plate" /><CubeGrids>'
        + g
        + "</CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>"
    )


def to_world(remote, grid_id, cell=(0, 0, 0)):
    r = remote.batch(calls=[CallOp.grid_to_world(grid_id, cell)]).call(0)
    if isinstance(r, dict):
        r = next(v for v in r.values() if isinstance(v, (dict, list))) if "x" not in {k.lower() for k in r} else r
    if isinstance(r, dict):
        r = [r[k] for k in r if k.lower() in "xyz"]
    return np.array(r, float)


def plate_state(remote, gid):
    """(blocks left, fully built)"""
    if gid not in {g["entityId"] for g in remote.list_grids()}:
        return 0, 0
    cubes, total = [], None
    while total is None or len(cubes) < total:
        try:
            page = remote.list_cubes(gid, offset=len(cubes))
        except OpError:  # ground away since the grid list
            return 0, 0
        total = page["total"]
        if not page["cubes"]:
            break
        cubes += page["cubes"]
    return len(cubes), sum(c["isFullIntegrity"] for c in cubes)


def set_tools(remote, gid, cells, on):
    for c in cells:
        remote.set_enabled(gid, c, True)
        for prop in ("ToolCore_TrackTargets", "ToolCore_Shoot"):
            remote.set_property(gid, c, prop, on)


def frame(remote):
    h = remote.perf_health()
    return h["gameFrame"], h["monoS"]


def look(remote, eye, at):
    remote.character_teleport(*eye)
    time.sleep(4)
    if remote.get_view()["thirdPerson"]:
        remote.key("V")
        time.sleep(1)
    remote.character_look_at(*at, tolerance=0.5)
    time.sleep(2)


def burst(remote, name, seconds, action=None, at=0.3):
    """Screenshots as fast as the client takes them, for some seconds, with an action
    (switching a tool) at a time into them; their names carry the milliseconds since
    the action"""
    start = time.monotonic()
    if action:
        threading.Timer(at, action).start()
    taken = 0
    while (t := time.monotonic() - start) < seconds:
        data = remote.screenshot_bytes(*BURST_SIZE, ignore_sprites=True)
        ms = int((t - (at if action else 0)) * 1000)
        (SHOTS / f"{name}-{'+' if ms >= 0 else '-'}{abs(ms):05d}ms.png").write_bytes(data)
        taken += 1
    print(f"burst {name}: {taken} shots", flush=True)
    return taken


def timed_phase(remote, main_id, plate_id, on, off, done, name):
    set_tools(remote, main_id, off, False)
    f0, m0 = frame(remote)
    set_tools(remote, main_id, on, True)
    while True:
        state = plate_state(remote, plate_id)
        f1, m1 = frame(remote)
        if done(state) or m1 - m0 > WORK_TIME:
            break
        time.sleep(1)
    set_tools(remote, main_id, on, False)
    frames, seconds = f1 - f0, m1 - m0
    result[name] = {"frames": frames, "game_s": frames / 60, "wall_s": round(seconds, 1)}
    check(done(state), f"{name}: slab {state} after {frames} frames ({frames / 60:.1f} game s, {seconds:.1f} s)")


def audio_size():
    return AUDIO.stat().st_size


def audio_phase(remote, main_id, cell, near):
    """One welder, from 10 m: tools quiet, then eight times working 4-10 s and switched
    off (both TrackTargets and Shoot: with TrackTargets on, ToolCore keeps working the
    tool by itself). audio_check finds the wave playing at each stop and whether its
    rest plays after the stop"""
    # 10 m from the welder: from the shot position, 60 m away, other sounds drowned the
    # storm. The jetpack stays on: without it the suit is louder than the storm
    remote.character_teleport(*near)
    time.sleep(15)  # earlier sounds end, the turret settles
    marks = {"quiet": audio_size()}
    time.sleep(4)
    marks["quiet_end"] = audio_size()
    cycles = []
    # Different working times, so the stops come at different points of a wave
    for work in (8, 5, 9.5, 6.5, 4, 7, 10, 5.5):
        set_tools(remote, main_id, [cell], True)
        time.sleep(3)  # the turret aims
        on = audio_size()
        time.sleep(work)
        stop = audio_size()
        set_tools(remote, main_id, [cell], False)
        time.sleep(17)
        cycles.append((on, stop, audio_size()))
    marks["cycles"] = cycles
    (rig.TEST_ROOT / f"{rig.TASK}-audio-marks.json").write_text(json.dumps(marks))
    stops = audio_check.playing_at_stops(AUDIO, marks)
    result["audio_stops"] = stops
    for k, stop in enumerate(stops):
        print("audio stop", k, stop, flush=True)
    # Stops that can tell: the wave identified, heard at the stop, with thunder left
    known = [st for st in stops if st["match_before"] >= 0.7 and st["rest_audible"] and st["level_before_db"] >= 3]
    # The quiet level wanders by up to about 1.5 dB
    plays_on = [st["level_after_db"] >= 2 for st in known]
    print(f"audio: {sum(plays_on)} of {len(known)} identified waves with sound left play on after the stop", flush=True)
    if result["linger_s"] is not None:
        check(known and all(plays_on), f"audio: the waves play to their end after the stop ({sum(plays_on)}/{len(known)})")


def main():
    path = world()
    bp, welders, decons = shipyard_xml()
    check(len(welders) == 24 and len(decons) == 24, f"{len(welders)} welders, {len(decons)} deconstructors")
    SHOTS.mkdir(parents=True, exist_ok=True)
    for old in SHOTS.glob("*.png"):
        old.unlink()
    if AUDIO:
        # No music in the recording, only the game's sounds
        cfg = rig.APPDATA / "SpaceEngineers.cfg"
        if not cfg.exists():
            shutil.copy(Path.home() / ".config/SpaceEngineers/SpaceEngineers.cfg", cfg)
        text = re.sub(r"\s*<item>\s*<Key>Music_Volume</Key>.*?</item>", "", cfg.read_text(encoding="utf-8"), flags=re.S)
        item = '<item><Key>Music_Volume</Key><Value><Value xsi:type="xsd:string">0</Value></Value></item>'
        cfg.write_text(text.replace("<item>", item + "<item>", 1), encoding="utf-8")
    # The game writes its log anew on each start
    started = time.time()
    remote = rig.start_client(connect=False, render=True, timeout=1200)
    try:
        remote.load(str(path))
        rig.wait_world(remote)
        rig.ensure_character(remote)
        me = np.array(remote.get_character()["position"], float)
        pasted = remote.paste_blueprint(
            xml=bp, position=tuple(me + [0, 0, -150]), forward=(0, 0, -1), up=(0, 1, 0), timeout_seconds=600
        )
        main_id = next(g["entityId"] for g in pasted if g["name"] == "Omni Arc Beam Shipyard")
        tools = [to_world(remote, main_id, c) for c in welders + decons]
        center = np.mean(tools, axis=0)
        plate_id = remote.paste_blueprint(
            xml=plate_xml("Plate"), position=tuple(center), forward=(0, 0, -1), up=(0, 1, 0), timeout_seconds=120
        )[0]["entityId"]
        time.sleep(5)
        check(plate_state(remote, plate_id) == (BLOCKS, 0), "slab pasted, nothing welded with the tools off")

        origin = to_world(remote, main_id)
        grid_up = to_world(remote, main_id, (0, 1, 0)) - origin
        grid_up /= np.linalg.norm(grid_up)
        far = max(tools, key=lambda t: np.linalg.norm(t - center))
        axes = []
        for cell in ((1, 0, 0), (0, 1, 0), (0, 0, 1)):
            a = to_world(remote, main_id, cell) - origin
            axes.append(a / np.linalg.norm(a))
        if not remote.get_character()["jetpack"]:
            remote.key("X")
        remote.character_set_dampeners(True)
        look(remote, center + 0.25 * (far - center) + 5 * grid_up, center)

        if not SHOTS_ONLY:
            timed_phase(remote, main_id, plate_id, welders, decons, lambda s: s[1] == BLOCKS, "weld")
            timed_phase(remote, main_id, plate_id, decons, welders, lambda s: s[0] == 0, "grind")
            # The shots, on a fresh slab
            plate_id = remote.paste_blueprint(
                xml=plate_xml("Plate2"), position=tuple(center), forward=(0, 0, -1), up=(0, 1, 0), timeout_seconds=120
            )[0]["entityId"]
            time.sleep(5)

        # One tool at a time, the nearest of its kind, seen from the side of its
        # filaments: switched on (extend), working (glide), switched off (retract)
        for name, cells, offset in (("weld", welders, 0), ("grind", decons, len(welders))):
            i = min(range(len(cells)), key=lambda k: np.linalg.norm(tools[offset + k] - center))
            cell, pos = cells[i], tools[offset + i]
            along = (center - pos) / np.linalg.norm(center - pos)
            # Sideways along the grid axis most across the filaments, into the frame's
            # open middle
            side = min(axes, key=lambda a: abs(a @ along))
            side = side - along * (side @ along)
            side /= np.linalg.norm(side)
            half = (center + pos) / 2
            print(f"{name}: tool {np.linalg.norm(center - pos):.0f} m from the slab, along {along.round(2)}", flush=True)
            look(remote, half + 0.75 * np.linalg.norm(center - pos) * side, half)
            check(burst(remote, f"{name}-1-extend", 6, lambda: set_tools(remote, main_id, [cell], True)) > 5, f"{name}: extend burst")
            time.sleep(2)
            check(burst(remote, f"{name}-2-glide", 2.5) > 5, f"{name}: glide burst")
            check(
                burst(remote, f"{name}-4-retract", 2 + (result["linger_s"] or 0.4) + 1.5, lambda: set_tools(remote, main_id, [cell], False)) > 5,
                f"{name}: retract burst",
            )
            time.sleep(2)
            if AUDIO and name == "weld":
                audio_phase(remote, main_id, cell, tuple(pos + 10 * along + 3 * grid_up))
        print("slab", plate_state(remote, plate_id), flush=True)

        game_log = "".join(
            p.read_text(errors="replace")
            for p in rig.APPDATA.glob("SpaceEngineers*.log")
            if p.stat().st_mtime > started
        )
        errors = re.findall(r".*(?:Resource not found|Compilation of|error CS|Exception: )\S*.*", game_log)
        check(not errors, f"no mod errors ({errors[:3]})")
        check("OctahedronFilaments:" not in game_log, "the script found the OmniArc originals")
        if TRACE:
            print("trace lines", game_log.count("FilamentTrace "), flush=True)
            (rig.TEST_ROOT / f"{rig.TASK}-trace.log").write_text(
                "".join(line + "\n" for line in game_log.splitlines() if "FilamentTrace " in line)
            )
            result.update(trace_check.analyze(game_log, check, result["linger_s"]))
    finally:
        rig.stop_client()
    print("RESULT " + json.dumps(result), flush=True)
    return failures


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
