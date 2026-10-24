"""MES Cluster on a local test cluster: the real MES (Workshop) with MES Cluster and the cluster's NPC census
and sweep (#1354), run through the cluster's own scenario bench (CometWorks/cluster Test/).

    python3 tests/mes/cluster/mes_cluster.py CLUSTER_TEST_DIR     (from plugin/)

CLUSTER_TEST_DIR is the Test/ folder of a CometWorks/cluster checkout at the build under test, with its
host.env (CLUSTER_PACKAGE, test root, port base) and `uv sync` done; run this with that folder's
virtualenv python. One regular node + WA, no client. The world is the DS's Empty World with MES and
encounter mods, pre-seeded (SEED_WORLD below). The Octahedron plugin (its MES gate) and the probe (tests/mes/probe) are
installed on the regular node as local plugins (tests/mes/cluster/build-local-plugins.sh). Cluster processes
never download mods: the extracted Workshop items in MES_MOD_SOURCE (default ~/cluster-runs/mes-1321/mods,
one folder per id, e.g. copied from the standalone run's data/content/244850) are staged into each
process's Mods folder before the start.

Scenario P2-MES-CLUSTER:
1. MES Cluster patches MES on node-1 and reads the cluster census (slot answers, not n/a).
2. Pocket: 40 MES spawn requests from one site. Every placed start is in a cell core, every NPC grid of
   the registry census lies in a core (or within the prefab margin), and the density limit refuses some.
3. Offline: the pocket's partitions go offline. The registry and node-1's slot still count the grids, while
   node-1 itself holds none of them; of 10 more requests at the site most are refused for density, and the
   few placed ones (at the pocket's thin edge) saw fewer than the cap: no second pocket when a player returns.
4. Sweep: an NPC grid in a gap band of MES Cluster's pattern (the real InGap slot) and one in a core, with
   npc.unvisitedMinutes=2 and gapRemovalFactor=4: the gap grid goes after ~30 s, the core grid after ~120 s.
   A third, in another gap band with a viewed camera next to it, stays; once the camera is off it goes too.

The world comes from SEED_WORLD: the Empty World with the mods, loaded and saved once by a plain DS, so it holds
the mods' NPC factions (#1365). Node-1 must not create a faction of its own, and every pocket grid's owner must be
a known NPC identity there.
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
from datetime import datetime, timezone
from pathlib import Path

MES_TESTS = Path(__file__).resolve().parents[1]
TEST_DIR = Path(sys.argv[1]).resolve()
sys.path[:0] = [str(TEST_DIR), str(TEST_DIR.parent / "Cli"), str(MES_TESTS / "ds")]

import check as pattern  # noqa: E402  (the independent Python port of the cell pattern)

ROOT = Path.home() / "cluster-runs/mes-1321"
PLUGINS = ROOT / "plugins"
WORLD = ROOT / "world/MesTest"
PLAN = ROOT / "probe-plan.txt"
# The world's Workshop mods, extracted (e.g. the content/244850 folder of the standalone run, tests/mes/ds):
# cluster processes never download mods, each loads them from its own <data>/Mods/<id>.sbm (T-0290)
MOD_SOURCE = Path(os.environ.get("MES_MOD_SOURCE", ROOT / "mods"))
# The Empty World with the mods below and SyncDistance 3000, loaded and saved once by a plain DS, so it holds the
# mods' NPC factions: the same pre-seeding a clean cluster world needs (#1365). Made by
#   python3 tests/mes/ds/run.py tests/mes/cluster/plan-seed.txt && cp -a ~/.se-test/1509-mes-ds0/data/Saves/MesTest SEED_WORLD
SEED_WORLD = Path(os.environ.get("MES_SEED_WORLD", ROOT / "seeded-world"))
# Falsification runs, which must FAIL their check: "census-local" removes the cluster's census slot on node-1 before
# the offline phase (the offline pocket goes uncounted); "unseeded" runs the Empty World without seeding (#1365's
# dropped founders own the MES grids)
FALSIFY = os.environ.get("MES_FALSIFY", "")
OUT = ROOT / "probe-{node}.txt"
NODE = "node-1"
POCKET = 40  # spawn requests at one site: enough to fill the density cap around it
SITE = pattern.site(3, -2, 1)  # a core site, as in the standalone gate test
GAP_POINT = (
    442074.2,
    18456.6,
    28764.5,
)  # midway between sites (10,0,0) and (11,0,0): 6 km into the gap band
VISITED_POINT = tuple(
    (a + b) / 2 for a, b in zip(pattern.site(10, 2, 0), pattern.site(11, 2, 0))
)  # another gap band
CORE_POINT = pattern.site(-10, 0, 0)
MODS = {
    1521905890: "Modular Encounters Systems",
    2802625870: "MES Wreckage",
    2609155402: "MES Space Pirates Enhanced",
    2615864222: "MES Terran Heavy Industries Engineering",
    888457124: "Suppress Vanilla Cargo Ships",
    888457381: "Suppress Vanilla Encounters",
}


def prepare() -> None:
    subprocess.run(
        [str(MES_TESTS / "cluster/build-local-plugins.sh"), str(PLUGINS)],
        check=True,
        stdout=subprocess.DEVNULL,
    )
    shutil.rmtree(WORLD, ignore_errors=True)
    if FALSIFY == "unseeded":
        build_unseeded(WORLD)
    else:
        # The world as one plain DS saved it (#1365): a world without the mods' NPC factions makes every cluster
        # process create them with its own ids, and MES on a node then owns its grids by founders the WA's snapshot removed
        text = (SEED_WORLD / "Sandbox.sbc").read_text(encoding="utf-8-sig")
        tags = set(re.findall(r"<Tag>([^<]+)</Tag>", text))
        missing = {"WRECKS", "SPRTE", "THIE"} - tags
        if missing:
            raise SystemExit(
                f"{SEED_WORLD} lacks the mod NPC factions {sorted(missing)}: seed it first (see SEED_WORLD)"
            )
        world_mods = {
            int(m)
            for m in re.findall(r"<PublishedFileId>(\d+)</PublishedFileId>", text)
        }
        if "OCT" in tags or not set(MODS) <= world_mods:
            raise SystemExit(
                f"{SEED_WORLD} is not the seeded test world (mods {sorted(world_mods)}, factions {sorted(tags)})"
            )
        shutil.copytree(
            SEED_WORLD,
            WORLD,
            ignore=shutil.ignore_patterns("Backup", "*.sbcB5", "*.sbsB5"),
        )
    PLAN.write_text("")
    for old in ROOT.glob("probe-*.txt"):
        if old != PLAN:
            old.unlink()
    extra = [
        entry
        for entry in os.environ.get("CLUSTER_EXTRA_PLUGINS", "").split(",")
        if entry.strip()
    ]
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(
        extra
        + [f"node:{PLUGINS / 'Octahedron'}", f"node:{PLUGINS / 'MesClusterProbe'}"]
    )
    os.environ.update(
        MESPROBE_PLAN=str(PLAN), MESPROBE_OUT=str(OUT), MESPROBE_NODE=NODE
    )


def build_unseeded(world: Path) -> None:
    """The Empty World with MODS and SyncDistance 3000, never loaded: no mod NPC factions in it."""
    ds64 = Path(
        os.environ.get(
            "SPACE_ENGINEERS_SERVER",
            Path.home() / ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer",
        )
    )
    shutil.copytree(ds64 / "Content/CustomWorlds/Empty World", world)
    mods = "".join(
        f'<ModItem FriendlyName="{name}"><Name>{wid}.sbm</Name><PublishedFileId>{wid}</PublishedFileId>'
        "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
        for wid, name in MODS.items()
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        text = (
            (world / name)
            .read_text(encoding="utf-8-sig")
            .replace("<Mods />", f"<Mods>{mods}</Mods>", 1)
        )
        text = re.sub(
            r"(<ViewDistance>\d+</ViewDistance>)",
            r"\1<SyncDistance>3000</SyncDistance>",
            text,
            count=1,
        )
        (world / name).write_text(text, encoding="utf-8")
    for stale in world.glob("*.sbsB5"):
        stale.unlink()


def probe(*lines: str) -> None:
    with PLAN.open("a") as plan:
        plan.write("".join(line + "\n" for line in lines))


def probe_out() -> str:
    path = Path(str(OUT).replace("{node}", NODE))
    return path.read_text(errors="replace") if path.exists() else ""


def run(bench) -> None:
    from cluster import wait_until
    from scenarios_core import _lines

    def ok(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def require(label, passed, detail=""):
        if not ok(label, passed, detail):
            raise AssertionError(f"{label}: {str(detail)[:600]}")

    def census() -> dict:
        try:
            return bench.cluster.get("/registry/npc-census")
        except Exception as error:  # noqa: BLE001 - 503 before the first complete pass
            if "503" in str(error):
                return {"online": 0, "offline": 0, "grids": []}
            raise

    def decisions(since: int = 0) -> list[str]:
        return _lines(bench, NODE, r"\[Octahedron\].*spawn (placed|refused)")[since:]

    processes = [NODE, "wa-1"]
    bench.cluster.kill()
    bench.cluster.prepare(wipe=True)
    for process in processes:
        for wid in MODS:
            shutil.copytree(
                MOD_SOURCE / str(wid),
                bench.run_root / f"{process}-data/Mods/{wid}.sbm",
                dirs_exist_ok=True,
            )
    bench.cluster.start(processes)
    bench.cluster.wait_ready(nodes=processes)
    bench.record(
        "config-setup",
        bench.cluster.handover_config(
            {"npc.sweepSeconds": "0", "offline.enabled": "false"}
        ),
    )

    # 1. Patched, cluster census in use
    gated = wait_until(
        lambda: _lines(bench, NODE, r"MES spawn placements gated"),
        300,
        "MES Cluster patched MES on node-1",
    )
    hashed = _lines(bench, NODE, r"MES \S+ found, instruction hash")
    ok(
        "MES Cluster patched the Workshop MES on node-1",
        gated and not _lines(bench, NODE, r"UNPATCHED|UNGATED"),
        (hashed + gated)[:2],
    )
    ok(
        "the Octahedron plugin is not installed on the WA",
        not _lines(bench, "wa-1", r"\[Octahedron\]"),
    )
    probe("count start 0 0 0 Infinity")
    first = wait_until(
        lambda: re.findall(r"^count start (\S+)$", probe_out(), re.M),
        120,
        "probe count",
    )[0]
    require(
        "node-1's census slot answers (cluster-wide census published)",
        first != "n/a",
        first,
    )

    # 2. Pocket
    probe(f"spawn RandomEncounter {POCKET} {SITE[0]:.1f} {SITE[1]:.1f} {SITE[2]:.1f} 0")
    wait_until(
        lambda: len(re.findall(r"^request ", probe_out(), re.M)) >= POCKET,
        300,
        f"{POCKET} spawn requests answered",
    )
    pocket = decisions()
    placed = [
        tuple(map(float, m))
        for m in re.findall(
            r"spawn placed: .* start (-?\d+),(-?\d+),(-?\d+)", "\n".join(pocket)
        )
    ]
    refused = re.findall(r"reason (\w+)", "\n".join(pocket))
    nears = [
        int(n)
        for n in re.findall(r"spawn placed: .*, near (-?\d+)$", "\n".join(pocket), re.M)
    ]
    bench.record("pocket-decisions", pocket)
    require("the pocket got spawns", placed, len(placed))
    ok(
        "every placed start is in a cell core",
        all(pattern.core_depth(p)[1] >= 0 for p in placed),
        [round(pattern.core_depth(p)[1]) for p in placed],
    )
    ok(
        "the density limit refused part of the pocket",
        refused.count("Density") > 0,
        refused,
    )
    ok(
        f"every placement saw fewer than {pattern.DENSITY_CAP} NPC grids around it (census + reservations)",
        len(nears) == len(placed) and all(0 <= n < pattern.DENSITY_CAP for n in nears),
        nears,
    )
    # MES spawns the prefabs over the next seconds, and an encounter can have several grids or none: the registry
    # census must match what node-1 itself holds (the probe's census uses MES Cluster's NPC definition)
    time.sleep(20)
    probe("owners pocket", "census pocket")
    out = wait_until(
        lambda: (lambda o: o if re.search(r"^census-end pocket$", o, re.M) else None)(
            probe_out()
        ),
        60,
        "probe pocket view",
    )
    held = len(re.findall(r"^census pocket ", out, re.M))
    # Every process loaded the same NPC factions from the save: the WA's faction snapshot, applied in the first
    # seconds after "Game ready" (so checked only now), removes none of node-1's factions (#1365)
    reconciled = _lines(bench, NODE, r"factions snapshot_reconciled")
    ok(
        "node-1 created no faction of its own (no faction snapshot removed one)",
        not [
            line for line in reconciled if not re.search(r"clients_removed=0\b", line)
        ],
        {"snapshots": len(reconciled), "first": reconciled[:1]},
    )
    owners = re.findall(r"^owners pocket .*$", out, re.M)
    bench.record("owners-pocket", owners)
    # An ownerless grid (debris) is no NPC grid; an owner node-1 doesn't know, or that isn't an NPC, is the #1365 bug
    unresolved = [line for line in owners if re.search(r":-:|:\?:", line)]
    ok(
        "every owned pocket grid is owned by a known NPC identity of node-1",
        owners and not unresolved,
        {
            "grids": len(owners),
            "ownerless": sum(line.endswith("[]") for line in owners),
            "unresolved": unresolved[:3],
        },
    )
    require("node-1 holds NPC grids from the pocket", held > 0, held)
    from cluster import ClusterError

    seen: list = [None]
    try:
        grids = wait_until(
            lambda: (
                lambda c: seen.__setitem__(0, c) or (c if c["online"] == held else None)
            )(census()),
            120,
            "the registry census to match node-1's NPC grids",
        )
    except ClusterError:
        raise AssertionError(
            f"registry census never matched node-1's {held} NPC grids: {json.dumps(seen[0])[:400]}"
        ) from None
    ok(
        "the registry census counts every NPC grid node-1 holds",
        grids["online"] == held,
        {"held": held, "online": grids["online"]},
    )
    depths = [pattern.core_depth((g["x"], g["y"], g["z"]))[1] for g in grids["grids"]]
    bench.record("census-pocket", grids)
    ok(
        f"every NPC grid in the registry census is in a core or within {pattern.EDGE:.0f} m of one",
        depths and min(depths) >= -pattern.EDGE,
        f"{len(depths)} grids, worst {min(depths, default=0):.0f} m",
    )

    # 3. Offline: counted while offline, no second pocket. The registry keeps a partition online while a MOVING one is
    # near (an MES drone on patrol), so the pocket is made static first: the census counts the same grids either way
    probe("freeze")
    frozen = wait_until(
        lambda: re.findall(r"^frozen (\d+)$", probe_out(), re.M),
        60,
        "the pocket frozen",
    )
    bench.record("pocket-frozen", frozen)
    total = grids["online"]
    bench.record(
        "config-offline",
        bench.cluster.handover_config(
            {
                "offline.enabled": "true",
                "offline.minOnlineSeconds": "0",
                "offline.graceSeconds": "0",
            }
        ),
    )
    seen = [None]
    try:
        offline = wait_until(
            lambda: (
                lambda c: seen.__setitem__(0, c)
                or (c if c["online"] == 0 and c["offline"] >= total else None)
            )(census()),
            300,
            "the pocket offline",
        )
    except ClusterError:
        raise AssertionError(
            f"the pocket did not go offline: census online {seen[0] and seen[0]['online']}, "
            f"offline {seen[0] and seen[0]['offline']} of {total}"
        ) from None
    bench.record("census-offline", offline)
    probe(
        "census offline",
        f"count offline {SITE[0]:.1f} {SITE[1]:.1f} {SITE[2]:.1f} 15000",
    )
    out = wait_until(
        lambda: (lambda o: o if re.search(r"^count offline ", o, re.M) else None)(
            probe_out()
        ),
        60,
        "probe offline view",
    )
    local = len(re.findall(r"^census offline ", out, re.M))
    slot = re.search(r"^count offline (\S+)$", out, re.M).group(1)
    ok(
        "node-1 itself holds none of the pocket's grids (the control: a node-local count sees 0)",
        local == 0,
        local,
    )
    ok(
        "node-1's census slot still counts the offline pocket",
        slot.isdigit() and int(slot) >= pattern.DENSITY_CAP,
        slot,
    )
    if FALSIFY == "census-local":
        probe("census-off")
        wait_until(
            lambda: re.search(r"^census-off$", probe_out(), re.M),
            60,
            "the census slot removed",
        )
    before = len(decisions())
    probe(f"spawn RandomEncounter 10 {SITE[0]:.1f} {SITE[1]:.1f} {SITE[2]:.1f} 0")
    wait_until(
        lambda: len(re.findall(r"^request ", probe_out(), re.M)) >= POCKET + 10,
        300,
        "10 more spawn requests answered",
    )
    again = decisions(before)
    bench.record("offline-decisions", again)
    # The limit is "fewer than the cap within the radius", so the pocket's thin edge may still take a spawn, as online.
    # node-1 holds none of the pocket, and this phase's own reservations stay far below the cap, so most requests
    # are refused only when the census counts the offline grids (with the slot removed: 0 of 10 refused).
    offline_nears = [
        int(n) for n in re.findall(r"spawn placed: .*, near (-?\d+)", "\n".join(again))
    ]
    ok(
        "no second pocket: most requests refused for density, and every placement saw fewer than the cap",
        len(again) == 10
        and sum("reason Density" in line for line in again) >= 5
        and all(n < pattern.DENSITY_CAP for n in offline_nears),
        {
            "refused": sum("reason Density" in line for line in again),
            "placedNear": offline_nears,
        },
    )
    bench.record(
        "config-offline-off",
        bench.cluster.handover_config({"offline.enabled": "false"}),
    )

    # 4. Sweep with the real InGap slot: unvisited gap grids go 4x sooner, a visited one stays
    require(
        "the visited probe point lies in a gap band",
        pattern.core_depth(VISITED_POINT)[1] < -3000,
        round(pattern.core_depth(VISITED_POINT)[1]),
    )
    spawned = {
        name: bench.cluster.node(
            NODE,
            f"/trash-test-spawn?x={p[0]}&y={p[1]}&z={p[2]}&static=false&blocks=8&npc=true",
            "POST",
        )
        for name, p in (
            ("gap", GAP_POINT),
            ("core", CORE_POINT),
            ("visited", VISITED_POINT),
        )
    }
    bench.record("sweep-probes", spawned)
    ids = {name: int(s["grid"]) for name, s in spawned.items()}
    # A viewed camera 500 m off the visited grid stands in for a player there (the position registry's demand feed)
    camera = bench.cluster.node(
        NODE,
        f"/camera-test-spawn?x={VISITED_POINT[0] + 500}&y={VISITED_POINT[1]}&z={VISITED_POINT[2]}",
        "POST",
    )
    bench.record("sweep-camera", camera)
    bench.record(
        "sweep-camera-view",
        bench.cluster.node(
            NODE, f"/camera-test-view?entityId={camera['camera']}&active=true", "POST"
        ),
    )
    wait_until(
        lambda: set(ids.values()) <= {int(g["id"]) for g in census()["grids"]},
        120,
        "the three probes in the census",
    )
    bench.record(
        "config-sweep",
        bench.cluster.handover_config(
            {
                "npc.sweepSeconds": "5",
                "npc.unvisitedMinutes": "2",
                "npc.gapRemovalFactor": "4",
            }
        ),
    )
    removed = lambda name: _lines(
        bench, NODE, rf"npc sweep removed grid {ids[name]} "
    )  # noqa: E731
    start = time.time()
    gap_line = wait_until(lambda: removed("gap"), 120, "the gap grid removed")
    gap_after = time.time() - start
    ok(
        "the unvisited gap-band grid is removed first (InGap from MES Cluster), the core grid stays",
        not removed("core") and "gap true" in gap_line[0],
        {"after": round(gap_after), "line": gap_line[0][-200:]},
    )
    core_line = wait_until(lambda: removed("core"), 240, "the core grid removed")
    core_after = time.time() - start
    ok(
        "the unvisited core grid is removed after its 2 min, about 4x later than the gap grid",
        "gap false" in core_line[0] and core_after > 2.5 * gap_after,
        {"gapAfter": round(gap_after), "coreAfter": round(core_after)},
    )
    row = {int(g["id"]): g for g in census()["grids"]}.get(ids["visited"])
    ok(
        "the visited gap-band grid stays, its unvisited count held at 0",
        not removed("visited") and row is not None and row["u"] < 20,
        row,
    )
    # Control: the same grid goes once nobody looks (so it was the visit that kept it)
    bench.record(
        "sweep-camera-unview",
        bench.cluster.node(
            NODE, f"/camera-test-view?entityId={camera['camera']}&active=false", "POST"
        ),
    )
    unviewed = time.time()
    visited_line = wait_until(
        lambda: removed("visited"),
        150,
        "the formerly visited gap grid removed once unvisited",
    )
    ok(
        "control: without the viewer the same gap grid is removed after the gap limit",
        "gap true" in visited_line[0],
        {"after": round(time.time() - unviewed), "line": visited_line[0][-200:]},
    )
    bench.exercised(
        placed=len(placed),
        refused=refused,
        offlineGrids=offline["offline"],
        slotOffline=slot,
        gapRemovedAfter=round(gap_after),
        coreRemovedAfter=round(core_after),
    )


def main() -> int:
    prepare()
    import bench as bench_module

    run.world_template = WORLD
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-mes") + (
        f"-falsify-{FALSIFY}" if FALSIFY else ""
    )
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-MES-CLUSTER",
        "#1321: MES Cluster with the real MES and the cluster's NPC census and sweep",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    for item in outcome.checks:
        print(
            ("PASS " if item.passed else "FAIL ")
            + item.label
            + (f" - {item.detail}" if item.detail else "")
        )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
