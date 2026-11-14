"""The stations' big map LCD runs the OctahedronRadar script of OctahedronMod, which
leaves out the station's shipyard and lane computers and shows the other grids (SE-1380).

The world is one built before the change (OCT_WORLD_SOURCE, from the commit the live world has),
so the stations' LCDs still run Arthur's Lcd Mod radar. With "after" the plugin gets this working
copy's config, whose station patches switch them to OctahedronRadar on load; with "before" it gets
the config without them. Every station gets a probe ship 600 m from its LCD. The client visits
each station's LCD, reads the sprites it rendered and saves a screenshot.

One station (RADAR_CROWD, default North) gets 17 probe ships 300-1870 m away instead, for the
contact count and the list of SE-1502, and one more hostile one that drifts outward at 4 m/s
from 280 m, overtaking the others: 5 hostile, 8 friendly and 5 neutral, each side spread over
the range (CROWD_SIDES). The others get 3 each: the test player's (friendly, green), nobody's
(neutral, white) and the pirates' (hostile, red).

After: every LCD runs OctahedronRadar, lists its probe ships and no lane computer ("TL ..."),
shipyard or shipyard arm, has the contact count in the top right corner, and lists at most 15
contacts on the left: hostile, friendly, then neutral, each by distance, with "... N" for the
rest. Each listed ship has a shape in its relation's color (SE-1518), drawn as its marker and in
front of its row, different from the other ships of its side while the 15 shapes last, and keeps
it while the drifting probe changes the order. The map is on the right. The gates
(the station's lane computers) are in a table of up to 3 rows and 3 columns below it, laid out as their markers on the map, in cyan
blue, by where they lead: the station at the other end, or Earth for a pole. Before: only reports what Arthur's radar draws, for comparison.

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
# The crowded station's probes, nearest first: F friendly, N neutral, H hostile. With the
# drifting hostile one that makes 18: 5 hostile, 8 friendly, 5 neutral, each side spread over
# 300-1870 m, so the list shows the 5 hostile, the 8 friendly and 2 neutral ones, then "... 3"
CROWD_SIDES = "FNHFFNHFNFHFNFHNF"
CROWD_SIZE = len(CROWD_SIDES)
DISTANCE = re.compile(r"^(\d+) m$|^(\d+\.\d\d) km$")
RING = re.compile(r"^\d+ k?m$")
GATE = re.compile(r"^TL \d+ (\w+) Station > (.+)$")
# The identity the test client plays, so that it owns the friendly probes
TESTER = 144115188075999502
SIDES = ("friendly", "neutral", "hostile")
# The list's order of the sides
FIRST = ("hostile", "friendly", "neutral")
ROWS = 15
MORE = re.compile(r"^\.\.\. (\d+)$")
SHAPES = 15
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
            "gates": [],
        }
    # The lane computers within 2 km of each LCD, by where their gate leads
    for entity in (
        ET.parse(sector).getroot().iterfind("SectorObjects/MyObjectBuilder_EntityBase")
    ):
        m = GATE.match(entity.findtext("DisplayName") or "")
        s = m and found.get(f"{m.group(1)} Station")
        if not s:
            continue
        at = vec(entity.find("PositionAndOrientation/Position"))
        if math.dist(at, s["lcd"]) < 2000:
            to = m.group(2)
            s["gates"].append(
                to[: -len(" Station")] if to.endswith(" Station") else "Earth"
            )
    return found


def probe_spots(station: str, s: dict) -> list[tuple[str, tuple]]:
    """Probe ship names and positions: numbered nearest first, spread around the screen"""
    if station == CROWD:
        rings = [(300 + 98 * i, 0.9 * i, 150 * math.sin(i)) for i in range(CROWD_SIZE)]
    else:
        rings = [(d, a, 150) for d, a in ((600, 0.0), (900, 2.1), (1300, 4.2))]
    spots = []
    for i, (d, a, h) in enumerate(rings, 1):
        at = add(s["lcd"], s["right"], d * math.cos(a))
        at = add(at, s["forward"], d * math.sin(a))
        spots.append((f"Radar Probe {station} {i:02}", add(at, s["up"], h)))
    return spots


def side(probe: str) -> str:
    """Whose a probe is, by its number: the tester's, nobody's or the pirates'. The drifting
    one is the pirates'."""
    number = probe.rsplit(" ", 1)[1]
    if not number.isdigit():
        return "hostile"
    if f" {CROWD} " in probe:
        return {"F": "friendly", "N": "neutral", "H": "hostile"}[
            CROWD_SIDES[int(number) - 1]
        ]
    return SIDES[(int(number) - 1) % 3]


def pirates() -> int:
    """The founder of the pirate faction"""
    text = (rig.world_source() / "Sandbox.sbc").read_text(encoding="utf-8")
    return int(re.search(r"<Tag>SPRT</Tag>.*?<PlayerId>(\d+)", text, re.S)[1])


def glyphs(sprites: list[dict], start: int, stop) -> list[dict]:
    """The texture sprites from start on that draw one shape: up to the next text, the next
    stalk (SquareTapered) or a sprite stop says ends it"""
    out = []
    for sp in sprites[start:]:
        if sp.get("type") != "TEXTURE" or sp.get("data") == "SquareTapered" or stop(sp):
            break
        out.append(sp)
    return out


def shape_of(glyph: list[dict], background) -> tuple:
    """What a shape looks like regardless of its size: its sprites, turns and parts in the
    background color (the inside of an outlined one), with sizes and offsets relative to its
    first sprite. The color is its first sprite's."""
    first = glyph[0]
    unit = max(first["size"])
    x0, y0 = first["position"]
    return (
        tuple(first["color"]),
        tuple(
            (
                sp["data"],
                round(sp.get("rotation") or 0.0, 2),
                tuple(sp["color"]) == background,
                tuple(round(v / unit, 1) for v in sp["size"]),
                round((sp["position"][0] - x0) / unit, 1),
                round((sp["position"][1] - y0) / unit, 1),
            )
            for sp in glyph
        ),
    )


def shows(shown: str, name: str) -> bool:
    """A listed name is the name, or its start and "…" where the column cuts it"""
    return shown == name or (shown.endswith("…") and name.startswith(shown[:-1]))


def color_side(color) -> str:
    """What a sprite color shows: green friendly, red hostile, white neutral, blue a gate"""
    r, g, b = color[:3]
    if max(r, g, b) - min(r, g, b) < 40:
        return "neutral"
    if b >= g > r:
        return "gate"
    return "friendly" if g > r else "hostile"


def mover(s: dict) -> tuple[str, tuple, tuple]:
    """The crowded station's drifting probe: name, position and velocity"""
    a = -0.5
    way = add(
        tuple(math.cos(a) * c for c in s["right"]),
        tuple(math.sin(a) * c for c in s["forward"]),
    )
    return (
        f"Radar Probe {CROWD} Mover",
        add(s["lcd"], way, 280),
        tuple(4 * c for c in way),
    )


def ships(station: str, s: dict) -> int:
    return len(probe_spots(station, s)) + (station == CROWD)


def prepare(found) -> None:
    owners = {"friendly": TESTER, "neutral": 0, "hostile": pirates()}
    probes = [
        rig.grid(probe, None, rig.small_ship_blocks(owners[side(probe)]), position=at)
        for name, s in found.items()
        for probe, at in probe_spots(name.split()[0], s)
    ]
    crowd = found[f"{CROWD} Station"]
    name, at, velocity = mover(crowd)
    probes.append(
        rig.grid(
            name,
            None,
            rig.small_ship_blocks(owners[side(name)]),
            position=at,
            velocity=velocity,
        )
    )
    rig.prepare_world(extra_grids=probes)
    # The test client plays TESTER, who owns the friendly probes
    path = rig.WORLD / "Sandbox.sbc"
    text = path.read_text(encoding="utf-8")
    text = text.replace(
        "</Identities>",
        f"<MyObjectBuilder_Identity><IdentityId>{TESTER}</IdentityId>"
        f"<DisplayName>{rig.CLIENT_NAME}</DisplayName><CharacterEntityId>0</CharacterEntityId>"
        "</MyObjectBuilder_Identity></Identities>",
        1,
    )
    text, count = re.subn(
        r"(<AllPlayersData>\s*<dictionary>)",
        lambda m: m[1]
        + f"<item><Key><ClientId>{rig.CLIENT_ID}</ClientId><SerialId>0</SerialId></Key>"
        f"<Value><DisplayName>{rig.CLIENT_NAME}</DisplayName><IdentityId>{TESTER}</IdentityId>"
        "<Connected>false</Connected><ForceRealPlayer>false</ForceRealPlayer></Value></item>",
        text,
    )
    assert count == 1, "AllPlayersData"
    path.write_text(text, encoding="utf-8")
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
        names = {sp.get("data") for sp in sprites}
        if shown >= ships(station, s) and all(g in names for g in s["gates"][:9]):
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
    if MODE == "after":
        raised(remote, station, s, lcd)
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
    first = check_list(station, s, surface.get("sprites", []))
    if station != CROWD or not first:
        return
    # The radar redraws once every 197 frames, 3.3 s at 60 frames a second: the drifting
    # probe's distance changes on each redraw
    started = time.monotonic()
    changes, last = [], None
    while time.monotonic() - started < 30:
        texts = tuple(
            sp.get("data")
            for sp in remote.get_surface(grid_id, s["cell"]).get("sprites", [])
            if sp.get("type") == "TEXT"
        )
        if last is not None and texts != last:
            changes.append(time.monotonic())
        last = texts
        time.sleep(0.2)
    gaps = sorted(b - a for a, b in zip(changes, changes[1:]))
    print(
        f"  {station}: {len(changes)} redraws in 30 s, seconds apart: "
        + ", ".join(f"{g:.1f}" for g in gaps),
        flush=True,
    )
    check(
        len(gaps) >= 4 and 2.5 < gaps[len(gaps) // 2] < 4.5,
        f"{station}: the radar redraws about every 197 frames (3.3 s)",
    )
    # The drifting probe moves 400 m in 100 s, past at least one other hostile probe
    time.sleep(70)
    later = check_list(
        station,
        s,
        remote.get_surface(grid_id, s["cell"]).get("sprites", []),
        quiet=True,
    )
    rank = lambda rows, n: next((i for i, r in enumerate(rows) if shows(r[3], n)), None)
    name = mover(s)[0]
    print(
        f"  {station}: the mover went from row {rank(first, name)} to row "
        f"{rank(later, name)}",
        flush=True,
    )
    check(
        rank(first, name) is not None
        and rank(later, name) is not None
        and rank(first, name) < rank(later, name),
        f"{station}: the drifting probe moved down the list",
    )
    before = {r[3]: r[6] for r in first}
    check(
        all(before[r[3]] == r[6] for r in later if r[3] in before),
        f"{station}: every ship listed in both reads kept its shape and color",
    )


def raised(remote, station: str, s: dict, lcd) -> None:
    """A screenshot from 2.5 m higher on the jetpack, above the lounge seats that hide the
    bottom of the screen from the floor"""
    remote.key("X")
    time.sleep(1)
    try:
        flying = remote.get_character().get("jetpack")
        remote.character_teleport(
            *add(add(lcd, s["forward"], SIDE * 9.0), s["up"], 2.5)
        )
        time.sleep(3)
        remote.character_look_at(*lcd, tolerance=2)
        time.sleep(2)
        shot = remote.screenshot_save(str(OUT / f"{station}-raised.png"))
        print(f"  raised screenshot (jetpack {flying}): {shot}", flush=True)
        time.sleep(3)
    except Exception as e:  # noqa: BLE001 -- the floor screenshot is there already
        print(f"  raised screenshot {station}: {e}")
    finally:
        remote.key("X")


def check_list(
    station: str, s: dict, all_sprites: list[dict], quiet: bool = False
) -> list[tuple]:
    """The count in the top right corner, the two columns sorted by distance, their colors, and
    the gates below the map. Returns the ship rows: (x, y, meters, name, color, same color,
    shape, shape sprites)"""
    if quiet:
        global check
        real, check = check, lambda ok, what: None
    try:
        return _check_list(station, s, all_sprites)
    finally:
        if quiet:
            check = real


def _check_list(station: str, s: dict, all_sprites: list[dict]) -> list[tuple]:
    sprites = [sp for sp in all_sprites if sp.get("type") == "TEXT"]
    counts = [sp for sp in sprites if COUNT.match(sp.get("data") or "")]
    check(len(counts) == 1, f"{station}: one contact count")
    if not counts:
        return []
    count_sprite = counts[0]
    count = int(COUNT.match(count_sprite["data"]).group(1))
    # The screen's area from the title (16, 10) and the count (X - 16, 14), in 1/512 of its
    # height
    title = next(sp for sp in sprites if sp.get("data") == "RADAR")
    unit = (count_sprite["position"][1] - title["position"][1]) / 4
    ox, oy = title["position"][0] - 16 * unit, title["position"][1] - 10 * unit
    width, height = count_sprite["position"][0] + 16 * unit - ox, 512 * unit
    # A row is a distance and the name right of it, at the same height, in one color: the list
    # rows start at the title's left edge, the gates right of the list
    background = tuple(all_sprites[0]["color"])
    index = {id(sp): i for i, sp in enumerate(all_sprites)}
    rows, gates = [], []
    for sp in sprites:
        m = DISTANCE.match(sp.get("data") or "")
        if not m:
            continue
        x, y = sp["position"]
        meters = float(m.group(1)) if m.group(1) else 1000 * float(m.group(2))
        beside = min(
            (
                o
                for o in sprites
                if abs(o["position"][1] - y) < 1 and 0 < o["position"][0] - x < 200
            ),
            key=lambda o: o["position"][0],
            default={"data": ""},
        )
        name = beside["data"]
        # Not a range ring's label, which has no name beside it
        if not name or RING.match(name):
            continue
        same = beside.get("color") == sp.get("color")
        # The row's shape is drawn right after its name
        glyph = glyphs(all_sprites, index[id(beside)] + 1, lambda o: False)
        shape = shape_of(glyph, background) if glyph else None
        row = (x, y, meters, name, tuple(sp.get("color") or ()), same, shape, glyph)
        (gates if x > ox + 0.38 * width else rows).append(row)
    listed = sorted(rows, key=lambda r: r[1])
    probes = ships(station, s)
    print(
        f"  {station}: count {count}, listed {len(listed)}: "
        + ", ".join(f"{r[2]:.0f} m {r[3]}" for r in listed),
        flush=True,
    )
    check(count >= probes, f"{station}: the count {count} has the {probes} probes")
    check(
        len(listed) == min(count, ROWS),
        f"{station}: lists min(count, {ROWS}) = {min(count, ROWS)} contacts",
    )
    check(
        len({round(r[0]) for r in listed}) <= 1,
        f"{station}: one column on the left",
    )
    order = [(FIRST.index(side(r[3])), r[2]) for r in listed]
    check(
        order == sorted(order),
        f"{station}: hostile, friendly, then neutral, each by distance",
    )
    more = [sp for sp in sprites if MORE.match(sp.get("data") or "")]
    if count > ROWS:
        check(
            len(more) == 1
            and int(MORE.match(more[0]["data"]).group(1)) == count - ROWS
            and more[0]["position"][1] > listed[-1][1],
            f"{station}: '... {count - ROWS}' below the list",
        )
    else:
        check(not more, f"{station}: no '... N' line with {count} contacts")
    check(
        all(count_sprite["position"][1] < r[1] for r in listed)
        and all(count_sprite["position"][0] > r[0] for r in listed),
        f"{station}: the count is in the top right corner, above and right of the list",
    )
    if count == probes:
        # Whose they are first, then by distance: the probes' numbers go by distance
        names = [r[3] for r in listed if not shows(r[3], mover(s)[0])]
        wanted = sorted(
            (name for name, _ in probe_spots(station, s)),
            key=lambda n: (FIRST.index(side(n)), n),
        )[: len(names)]
        # A long name ends in "…" where the column cuts it
        check(
            len(names) == len(wanted)
            and all(
                n == w or (n.endswith("…") and w.startswith(n[:-1]))
                for n, w in zip(names, wanted)
            ),
            f"{station}: the expected {len(wanted)} probes are listed in order",
        )

    # The map's markers: a stalk, its foot, then the contact's shape
    markers = []
    for i, sp in enumerate(all_sprites):
        if (
            sp.get("data") == "SquareTapered"
            and i + 2 < len(all_sprites)
            and all_sprites[i + 1].get("data") == "Circle"
        ):
            glyph = glyphs(all_sprites, i + 2, lambda o: False)
            if glyph:
                markers.append(glyph)
    marker_shapes = [shape_of(g, background) for g in markers]
    print(
        f"  {station}: {len(listed)} rows, {len(markers)} markers, "
        f"{len(set(marker_shapes))} different marker shapes",
        flush=True,
    )
    check(
        all(r[5] for r in listed),
        f"{station}: each row's distance and name share a color",
    )
    check(
        all(r[6] and r[6][0] == r[4] for r in listed),
        f"{station}: each row starts with a shape in the row's color",
    )
    check(
        all(r[7] and max(sp["position"][0] for sp in r[7]) < r[0] for r in listed),
        f"{station}: each row's shape is left of its text",
    )
    check(
        all(r[6] in marker_shapes for r in listed),
        f"{station}: each row's shape and color is a marker's on the map",
    )
    check(
        all(color_side(r[4]) == side(r[3]) for r in listed),
        f"{station}: friendly ships green, neutral white, hostile red",
    )
    # Telling shapes apart by their sprites and turns, not their sizes
    by_side = {}
    for r in listed:
        if r[6]:
            by_side.setdefault(side(r[3]), []).append(
                (r[6][0], tuple(part[:3] for part in r[6][1]))
            )
    print(
        f"  {station}: different shapes "
        + ", ".join(f"{k} {len(set(v))}/{len(v)}" for k, v in by_side.items()),
        flush=True,
    )
    check(
        all(len(set(v)) == min(len(v), SHAPES) for v in by_side.values()),
        f"{station}: the ships of one side have different shapes, up to {SHAPES}",
    )
    check(
        all(color_side(r[4]) == "gate" for r in gates),
        f"{station}: the gates are cyan blue",
    )

    # The gates below the map in a table, row by row, by distance
    gates.sort(key=lambda r: (r[1], r[0]))
    print(
        f"  {station}: gates "
        + ", ".join(f"{r[2]:.0f} m {r[3]}" for r in gates)
        + f"; expected {sorted(s['gates'])}",
        flush=True,
    )
    check(
        sorted(r[3] for r in gates) == sorted(s["gates"])[:9],
        f"{station}: the gates are listed by where they lead",
    )
    # The map draws the gates' markers nearest first, so the n-th marker is the n-th nearest
    # gate. The table lays them out as on the map (SE-1518): the top row has the highest
    # markers, each row goes left to right.
    spots = [
        g[0]["position"]
        for g in markers
        if g[0]["data"] == "CircleHollow" and color_side(g[0]["color"]) == "gate"
    ][: len(gates)]
    by_height = sorted(range(len(spots)), key=lambda j: spots[j][1])
    layout = [
        j
        for part in (by_height[:3], by_height[3:6], by_height[6:])
        for j in sorted(part, key=lambda j: spots[j][0])
    ]
    nearest = sorted(r[2] for r in gates)
    print(
        f"  {station}: gate markers (x, y) nearest first "
        + ", ".join(f"({x:.0f}, {y:.0f})" for x, y in spots),
        flush=True,
    )
    check(
        len(spots) == len(gates)
        and [r[2] for r in gates] == [nearest[j] for j in layout],
        f"{station}: the gate table is laid out as the gates' markers on the map",
    )
    check(
        len({round(r[1]) for r in gates}) <= 3
        and len({round(r[0]) for r in gates}) <= 3,
        f"{station}: the gates are in at most 3 rows and 3 columns",
    )
    ships_drawn = [g[0]["position"] for g in markers]
    check(
        bool(ships_drawn) and all(x > ox + 0.38 * width for x, _ in ships_drawn),
        f"{station}: the map is on the right side",
    )
    check(
        bool(gates)
        and all(r[0] > ox + 0.38 * width for r in gates)
        and all(r[1] > max(y for _, y in ships_drawn) for r in gates)
        and all(r[1] > oy + 0.75 * height for r in gates),
        f"{station}: the gates are at the bottom, below the map",
    )
    return listed


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
