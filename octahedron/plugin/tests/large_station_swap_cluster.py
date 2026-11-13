"""SE-1449: the Octahedron Large Station replaces Alpha and Moon Station of a kept world by the staged patches
2026-11-14-large-station-*, on a local test cluster (WA + 2 nodes + one headless admin client), the setup of
zone_evacuate_cluster.py. The world is a copy of the live cluster's source world (OCT_WORLD_SOURCE); the patches
the live world got since its build are staged first, so each station is as it is on the live cluster.

Alpha Station, while the cluster runs:
- two ships docked to it, one parked in its zone, the client on foot in the zone with its helmet open
- the route zone-evacuate moves all of them out of the zone, apart from each other, the client's helmet closed
- the client carries known items and stands where only the new station has a block: the staged swap takes its
  body out (OfflineCharacters.Evict), the client respawns and its new body gets the items
- the swap applies: the same grid, the new blueprint's block count, the ledger with every patch, no islands,
  the Store, market and queue answer

Moon Station, across a graceful stop and start, as the deploy does it:
- a ship docked to it, the client with its items seated in a station seat and gone offline
- with the swap and the Moon move staged, after the start: the station's partition loaded through the Gateway,
  the move waits for the docked ship, zone-evacuate clears it
- the seated body is taken out, the station moves 20 m from its shipyard, the swap applies
- the client comes back with the items on a new body
- after another graceful stop and start nothing applies again

    OCT_TASK=SE-1449 OCT_WORLD_SOURCE=<copy of the live source world> \\
        <Test>/.venv/bin/python tests/large_station_swap_cluster.py <Test>     (from plugin/)
"""

from __future__ import annotations

import gzip
import json
import math
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.parse
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

os.environ.setdefault("OCT_TASK", "SE-1449")
import station_extenders_cluster as sx  # noqa: E402  (takes the bench's folder from argv)
import station_patch_cluster as spc  # noqa: E402
import wa_economy_cluster as wa  # noqa: E402
from wa_economy_cluster import CLIENT_ID, PLUGIN, ClusterError  # noqa: E402

import shipyard_turn_cluster as turn  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_offline_characters import inventory, probe as cprobe, teleport  # noqa: E402
from zone_evacuate_cluster import sx_join  # noqa: E402

NODES = sx.NODES
PROCESSES = sx.PROCESSES
SWAP = "2026-11-14-large-station"
MOVE = "2026-11-14-large-station-moon-move"
BLOCKS = 3890
# The Large Station's bounding sphere
RADIUS = 65.6
MARGIN = 25
CARRIED = {"Ingot/Iron": 321.0, "Component/SteelPlate": 17.0}
CHARACTER_PROBE = "7A2D4E10-8F36-4C51-B7A9-1D3E5F1736AA"
FOUND: dict[str, object] = {}


def cfg_entries(section: str, entry: str) -> list[ET.Element]:
    return list(
        ET.parse(sx.CONFIG / "octahedron.cfg").getroot().iterfind(f"{section}/{entry}")
    )


def staged(*stations: str) -> tuple[str, str]:
    """StationPatches and GridMoves of the config, this ticket's only for the stations named"""

    def mine(e):
        return e.findtext("Grid").split()[0] in stations

    patches = [
        e
        for e in cfg_entries("StationPatches", "StationPatchEntry")
        if SWAP not in e.findtext("Id") or mine(e)
    ]
    moves = [
        e
        for e in cfg_entries("GridMoves", "GridMoveEntry")
        if e.findtext("Id") != MOVE or mine(e)
    ]
    return tuple(
        "".join(ET.tostring(e, encoding="unicode") for e in group)
        for group in (patches, moves)
    )


def put_sections(**sections: str) -> None:
    """Replaces whole sections of the bench's config, child elements and all: wa.set_config replaces text only"""
    text = wa.CONFIG.read_text(encoding="utf-8")
    for key, inner in sections.items():
        text, count = re.subn(
            rf"<{key}>.*?</{key}>|<{key} />",
            f"<{key}>{inner}</{key}>",
            text,
            flags=re.S,
        )
        assert count == 1, (key, count)
    wa.CONFIG.write_text(text, encoding="utf-8")


def new_cell(grid: str) -> tuple[float, float, float]:
    """Where a character stands in a cell the swap builds: an empty cell of the old station above one of its blocks
    that the new station keeps, so the station's gravity holds the character there. The cell's floor, in the world.
    """
    world = (
        ET.parse(sx.rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot().find("SectorObjects")
    )
    found = next(g for g in world if g.findtext("DisplayName") == grid)
    po = found.find("PositionAndOrientation")
    p, f, u = (
        [float(po.find(t).get(a)) for a in "xyz"] for t in ("Position", "Forward", "Up")
    )
    r = [
        f[1] * u[2] - f[2] * u[1],
        f[2] * u[0] - f[0] * u[2],
        f[0] * u[1] - f[1] * u[0],
    ]
    tag = grid.split()[0].lower()
    name = next(
        e.findtext("To")
        for e in cfg_entries("StationPatches", "StationPatchEntry")
        if e.findtext("Id") == f"{SWAP}-{tag}"
    )

    def mins(blocks):
        return {
            tuple(int(b.find("Min").get(a, 0)) for a in "xyz")
            for b in blocks
            if b.find("Min") is not None
        }

    new = mins(
        ET.fromstring(gzip.decompress((sx.STORAGE / name).read_bytes())).iter(
            "MyObjectBuilder_CubeBlock"
        )
    )
    old = mins(found.find("CubeBlocks"))
    cell = min(
        (
            c
            for c in new - old
            if (c[0], c[1] - 1, c[2]) in old and (c[0], c[1] + 1, c[2]) not in old
        ),
        key=lambda c: abs(c[0]) + abs(c[2]),
    )
    # The cell's floor: half a cell below its center, a little above it
    x, y, z = cell[0], cell[1] - 0.45, cell[2]
    return tuple(p[i] + 2.5 * (x * r[i] + y * u[i] - z * f[i]) for i in range(3))


def build_character_probe() -> str:
    out = wa.lc.PLUGINS / ".build" / "CharacterProbe"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(PLUGIN / "tests/offline-characters/probe/CharacterProbe.csproj"),
         "-c", "Release", "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "CharacterProbe"
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(out / "CharacterProbe.dll", folder / "CharacterProbe.dll")
    (folder / "CharacterProbe.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{CHARACTER_PROBE}</Id><FriendlyName>CharacterProbe</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
        "<Commit>test</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return str(folder)


def run(bench) -> None:
    cluster = bench.cluster
    api = None

    def check(label, passed, detail="") -> bool:
        print(
            f"{'PASS' if passed else 'FAIL'}: {label} {str(detail)[:300]}", flush=True
        )
        return bench.check(label, bool(passed), str(detail)[:1000])

    def note(name, data) -> None:
        print(f"NOTE {name}: {str(data)[:600]}", flush=True)
        FOUND[name] = data
        bench.record(name, data)

    def until(fn, seconds: float, every: float = 3.0):
        end = time.monotonic() + seconds
        while True:
            value = fn()
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

    def logs() -> str:
        return "\n".join(
            p.read_text(errors="replace")
            for process in PROCESSES
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def log(process: str) -> str:
        return "\n".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def status(grid: str) -> dict | None:
        """The fields of the grid's last Station patch STATUS line"""
        # The newest line by its time: the processes' logs come one after the other
        lines = re.findall(
            rf"^(\S+ \S+) .*Station patch STATUS {grid} (\d+) (.*)$", logs(), re.M
        )
        if not lines:
            return None
        _, entity, rest = max(lines)
        return {
            "entity": entity,
            **dict(f.split("=", 1) for f in rest.split() if "=" in f),
        }

    def has(grid: str, text: str) -> dict | None:
        found = status(grid)
        return found if found and text in found.get("ledger", "") else None

    def chat(text: str, wait: float = 8) -> str:
        reply = " ".join(sx.rig.chat(api, text, wait))
        print(f"{text} -> {reply[:300]}", flush=True)
        return reply

    def evacuate(node: str, grid: str, dry: bool) -> dict:
        query = urllib.parse.urlencode({"name": grid, "dryRun": "1" if dry else "0"})
        return cluster.node(node, f"/zone-evacuate?{query}", "POST")

    def holder(grid: str) -> str | None:
        for node in NODES:
            try:
                if evacuate(node, grid, True).get("dryRun"):
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def crowded(done: dict, center) -> list[str]:
        """Moved items closer than the margin to each other, or reaching into the new station's sphere"""
        items = [
            (str(x["entityId"]), x["to"], float(x.get("radius", 1.5)))
            for x in done["moved"]["ships"] + done["moved"]["characters"]
            if x.get("moved")
        ]
        bad = [
            f"{a}/{b} {math.dist(pa, pb):.1f} m"
            for i, (a, pa, ra) in enumerate(items)
            for b, pb, rb in items[i + 1 :]
            if math.dist(pa, pb) < ra + rb + MARGIN - 0.1
        ]
        return bad + [
            f"{a} in the station"
            for a, p, r in items
            if math.dist(p, center) < RADIUS + r
        ]

    def stage(*stations: str) -> None:
        patches, moves = staged(*stations)
        put_sections(StationPatches=patches, GridMoves=moves)

    def stop(label: str) -> bool:
        bench.kill_client(CLIENT_ID)
        started = time.monotonic()
        graceful = cluster.stop(grace=240)
        note(
            f"{label}-stop",
            {"graceful": graceful, "seconds": round(time.monotonic() - started)},
        )
        return graceful

    def start() -> None:
        cluster.start(PROCESSES)
        cluster.wait_ready(nodes=PROCESSES)

    def carry() -> int:
        cprobe(api, "clear", "CLEAR")
        for item, amount in CARRIED.items():
            cprobe(api, f"give {item} {amount:.0f}", "GIVE True")
        return inventory(api)[0]

    def came_back(label: str, body: int) -> None:
        def has_them():
            found = inventory(api)
            return (
                found
                if all(found[1].get(i, 0) >= a for i, a in CARRIED.items())
                else None
            )

        got = until(has_them, 300, 5)
        check(f"{label}: a new body has the items", got and got[0] != body, got)

    def swapped(grid: str, entity: str, ids: list[str]) -> None:
        line = until(
            lambda: re.search(
                rf"Station patch APPLIED {SWAP}-\w+ on {grid}[^;\n]*", logs()
            ),
            300,
            5,
        )
        check(f"{grid}: the swap applies", line, line and line[0])
        got = until(lambda: has(grid, SWAP), 60)
        note(f"{grid}-status", got)
        check(
            f"{grid}: the same grid",
            got and got["entity"] == entity,
            got and got["entity"],
        )
        check(
            f"{grid}: the new blueprint's {BLOCKS} blocks",
            got and got.get("blocks") == str(BLOCKS),
            got,
        )
        check(f"{grid}: in one piece", got and got.get("islands") == "0", got)
        missing = [i for i in ids if i not in (got or {}).get("ledger", "").split(",")]
        check(f"{grid}: every patch and move in the ledger", not missing, missing)
        answers = until(
            lambda: line
            and re.findall(
                rf"Station patch ANSWERS {grid}: ([^\n]*)", logs().split(line[0], 1)[-1]
            ),
            90,
        )
        check(
            f"{grid}: the Store, market and queue answer",
            answers and "none (" not in answers[-1] and "store none" not in answers[-1],
            answers,
        )

    def ids(grid: str, *more: str) -> list[str]:
        return [
            e.findtext("Id")
            for e in cfg_entries("StationPatches", "StationPatchEntry")
            if e.findtext("Grid") == grid
        ] + list(more)

    # ============================== setup: the live cluster's patches ==============================
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    spc.PENDING.write_text("")
    spc.OUT.write_text("")
    cluster.start(PROCESSES)
    cluster.wait_ready(nodes=PROCESSES)
    api = sx_join(bench, cluster)
    tables = sa.station_tables()

    # ============================== Alpha: ships and the client in the zone ==============================
    api.character_teleport(*tables["Alpha"])
    time.sleep(10)
    alpha = until(lambda: has("Alpha Station", "howto-entries"), 240)
    note("alpha-before", alpha)
    check("Alpha Station has the live cluster's patches", alpha, alpha)
    ships = []
    for at in ("", ",AT=-13;0;0"):
        reply = chat(f"!sprobe dock Alpha mine BAT=0{at}", 6)
        ship = re.search(r"SHIP (\d+)", reply)
        if ship and until(
            lambda: f"DOCKED {ship[1]} yes" in chat(f"!sprobe docked {ship[1]}", 6), 60
        ):
            ships.append(ship[1])
    check("two ships docked to Alpha Station", len(ships) == 2, ships)
    parked = cprobe(api, "ship inside", r"SHIP (\d+) (\d+)")
    check("a ship parked in the zone", parked, parked[0])
    node = until(lambda: holder("Alpha Station"), 120)
    check("a node holds Alpha Station", node, node)
    me = None
    for _ in range(3):
        rows = cluster.node(node, "/characters")
        rows = rows.get("characters", rows) if isinstance(rows, dict) else rows
        me = next((r for r in rows if r.get("controllingSteamId") == CLIENT_ID), None)
        if not (me and me.get("helmet")):
            break
        api.key("J")
        time.sleep(2)
    check("the client's helmet is open", me and me.get("helmet") is False, me)

    # ============================== Alpha: the zone cleared ==============================
    note("alpha-dry-run", evacuate(node, "Alpha Station", True))
    done = evacuate(node, "Alpha Station", False)
    note("alpha-evacuation", done)
    moved = {str(s["entityId"]) for s in done["moved"]["ships"] if s.get("moved")}
    check(
        "the docked and the parked ships are moved out",
        set(ships) | {parked[2]} <= moved,
        moved,
    )
    me = next((c for c in done["moved"]["characters"] if c.get("moved")), None)
    check(
        "the client is moved out, its helmet closed", me and me.get("closesHelmet"), me
    )
    center = [
        float(v) for v in cprobe(api, "sphere", r"SPHERE (\S+) (\S+) (\S+)").groups()
    ]
    bad = crowded(done, center)
    check("moved items keep the margin apart and off the new station", not bad, bad)

    # ============================== Alpha: the swap with the client in its way ==============================
    teleport(api, *tables["Alpha"])
    body = carry()
    teleport(api, *new_cell("Alpha Station"))
    api.character_set_dampeners(True)
    stage("Alpha")
    chat("!oct reload")
    evicted = until(
        lambda: re.search(rf"body {body} is in a station patch's way[^\n]*", logs()),
        120,
        5,
    )
    check(
        "the swap takes the client's body out of its way",
        evicted,
        evicted and evicted[0],
    )
    removed = until(
        lambda: re.search(rf"body {body} taken from its player, who respawns", logs()),
        300,
        5,
    )
    check("the body goes once a save has its items", removed, removed and removed[0])
    sx.rig.ensure_character(api)
    came_back("Alpha", body)
    swapped("Alpha Station", alpha and alpha["entity"], ids("Alpha Station"))

    # ============================== Moon: a docked ship, the client seated and offline ==============================
    teleport(api, *tables["Moon"])
    time.sleep(10)
    moon = until(lambda: has("Moon Station", "howto-entries"), 240)
    note("moon-before", moon)
    reply = chat("!sprobe dock Moon mine BAT=0", 6)
    docked = re.search(r"SHIP (\d+)", reply)
    check(
        "a ship docked to Moon Station",
        docked
        and until(
            lambda: f"DOCKED {docked[1]} yes" in chat(f"!sprobe docked {docked[1]}", 6),
            60,
        ),
        reply,
    )
    seat = cprobe(api, "seat", r"SEAT (\d+) (\d+)")
    check("a free seat on Moon Station", moon and seat[2] == moon["entity"], seat[0])
    body = carry()
    cprobe(api, f"seatin {seat[1]}", "SEAT")
    check(
        "the client sits in it",
        until(
            lambda: cprobe(api, f"pilot {seat[1]}", r"PILOT (\d+)")[1] == str(body), 30
        ),
    )

    # ============================== Moon: staged, stopped, started ==============================
    stage("Alpha", "Moon")
    check("the cluster stops gracefully", stop("moon"))
    start()
    started = time.monotonic()
    # As the deploy does: load the station's partition, clear its zone while the move or the swap waits for a ship
    loaded = cleared = None
    while time.monotonic() - started < 900:
        found = holder("Moon Station")
        if not found and not (
            loaded and loaded.get("outcome", "").startswith("requested")
        ):
            rows = cluster.get("/registry/partitions/offline").get("Partitions", [])
            part = next(
                (
                    r["Partition"]
                    for r in rows
                    if moon and int(moon["entity"]) in r.get("Members", [])
                ),
                None,
            )
            if part is not None:
                loaded = cluster.post(
                    f"/registry/partitions/offline/load?partition={part}&reason=station-swap"
                )
                note("moon-load", loaded)
        waits = re.search(
            r"(Grid move|Station patch staged:) (WAITING|REFUSED) [^\n]*Moon Station[^\n]*",
            logs(),
        )
        if found and waits and not cleared and "docked" in waits[0]:
            note("moon-waits", waits[0])
            cleared = evacuate(found, "Moon Station", False)
            note("moon-evacuation", cleared)
        if re.search(rf"Station patch APPLIED {SWAP}-moon on Moon Station", logs()):
            break
        time.sleep(10)
    check(
        "the docked ship is moved out of Moon's zone",
        cleared
        and any(
            str(s["entityId"]) == docked[1] and s.get("moved")
            for s in cleared["moved"]["ships"]
        ),
        cleared,
    )
    out = re.search(rf"body {body} is in a station patch's way", logs())
    check("the seated offline body is taken out", out, out and out[0])
    gone = re.search(rf"Offline character of \d+: body {body} removed", logs())
    check("and removed once saved", gone, gone and gone[0])
    move = re.search(
        rf"Grid move APPLIED {MOVE} on Moon Station: moved ([\d.]+) m[^\n]*", logs()
    )
    check(
        "Moon Station moves 20 m",
        move and abs(float(move[1]) - 20) < 0.5,
        move and move[0],
    )
    # Its faction station entry, which the game makes its GPS from, moves with it on every process
    gps = "20350.08,122087.01,-110668.32"
    for process in PROCESSES:
        line = re.search(
            rf"Grid move STATION {MOVE}: Moon Station's faction station from \S+ to (\S+)",
            log(process),
        )
        check(
            f"{process}: Moon's faction station moves to {gps}",
            line and line[1] == gps,
            line and line[0],
        )
    swapped("Moon Station", moon and moon["entity"], ids("Moon Station", MOVE))
    api = sx_join(bench, cluster)
    came_back("Moon", body)

    # ============================== nothing again ==============================
    applied = len(re.findall(r"Station patch APPLIED", logs()))
    placed = len(re.findall(r"Grid move STATION", log("wa-1")))
    check("the cluster stops gracefully again", stop("again"))
    start()
    api = sx_join(bench, cluster)
    for name in ("Alpha", "Moon"):
        teleport(api, *tables[name])
        time.sleep(15)
    check(
        "no patch applies again",
        len(re.findall(r"Station patch APPLIED", logs())) == applied,
    )
    check(
        "the World Authority saved the faction station where it moved",
        len(re.findall(r"Grid move STATION", log("wa-1"))) == placed,
    )


def main() -> int:
    run.world_template = wa.prepare()
    folders = [turn.build_probe(), spc.build_edit_probe(), build_character_probe()]
    os.environ["CLUSTER_EXTRA_PLUGINS"] += "".join(f",node:{f}" for f in folders)
    os.environ.update(EDITPROBE_PENDING=str(spc.PENDING), EDITPROBE_OUT=str(spc.OUT))
    for e in cfg_entries("StationPatches", "StationPatchEntry"):
        for name in (e.findtext("From"), e.findtext("To")):
            if name:
                shutil.copy2(sx.STORAGE / name, wa.CONFIG.parent / name)
    patches, moves = staged()
    put_sections(StationPatches=patches, GridMoves=moves)
    wa.set_config(OfflineCharacterMinutes=15)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-large-station-swap")
    results = wa.bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = wa.bench_module.run_scenario(
        run,
        "P2-OCT-LARGE-STATION-SWAP",
        "SE-1449: the Octahedron Large Station replaces Alpha and Moon Station of a kept world",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    print(json.dumps(FOUND, default=str)[:20000])
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        print(
            ("PASSED: " if item.passed else "FAILED: ")
            + item.label
            + (f" - {item.detail[:300]}" if item.detail else "")
        )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


run.administrators = [CLIENT_ID]

if __name__ == "__main__":
    sys.exit(main())
