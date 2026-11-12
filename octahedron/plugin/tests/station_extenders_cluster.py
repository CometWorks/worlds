"""SE-1388: the Station Extenders reach a kept world by the staged station patches of
config/octahedron.cfg, on a local test cluster (CometWorks/cluster Test/ scenario bench, the setup of
tests/wa_economy_cluster.py: WA + 2 nodes + one headless admin client).

The world is built from a commit before the extenders (the live world's), the config has the
StationPatches list and the patch files of the working tree. Checks:
- every station gets its patch when a node loads it: 2 x 61 blocks more, 20 connectors, all
  trading, the grid in one piece, the patch in its ledger
- ships dock at the extenders' large connectors on Alpha Station (side, lower and top ones of both
  extenders), the connectors stay trading and a docked ship's empty battery charges from the
  station
- after a handover of the station's partition to the other node, the ships are still docked and the
  station is as patched
- after a graceful cluster stop and start, the station is as patched and the patch is once in its
  ledger, applied once

    OCT_TASK=SE-1388 OCT_WORLD_SOURCE=<world from build_world.py before the extenders> \\
        <Test>/.venv/bin/python tests/station_extenders_cluster.py <Test>     (from plugin/)

Every result is in the bench's result.json; the summary at the end lists the checks.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import sys
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

os.environ.setdefault("OCT_TASK", "SE-1388")
import station_patch_cluster as spc  # noqa: E402  (takes the bench's folder from argv)
import wa_economy_cluster as wa  # noqa: E402
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    bench_module,
    rig,
    wait_until,
)

import shipyard_turn_cluster as turn  # noqa: E402
import test_support_arm as sa  # noqa: E402

STATION = "Alpha"
GRID = "Alpha Station"
NODES = ("node-1", "node-2")
PROCESSES = ["wa-1", *NODES]
PATCH = "station-extenders"
CONFIG = spc.OCTAHEDRON / "config"
STORAGE = CONFIG / "storage/Octahedron"
ADDED = 2 * 61
CONNECTORS = 20
FOUND: dict[str, object] = {}


def entries() -> list[ET.Element]:
    """The StationPatches entries of the working tree's config"""
    root = ET.parse(CONFIG / "octahedron.cfg").getroot()
    return list(root.iterfind("StationPatches/StationPatchEntry"))


def entry(grid: str) -> ET.Element:
    return next(
        e for e in entries() if e.findtext("Grid") == grid and PATCH in e.findtext("Id")
    )


def new_connectors(grid: str) -> list[tuple[str, str]]:
    """(x;y;z, subtype) of the connectors the grid's extender patch adds"""
    tree = ET.parse(STORAGE / entry(grid).findtext("To"))
    found = []
    for block in tree.getroot().iter("MyObjectBuilder_CubeBlock"):
        if block.get(f"{{{spc.XSI}}}type") != "MyObjectBuilder_ShipConnector":
            continue
        m = block.find("Min")
        cell = ";".join(m.get(a, "0") for a in "xyz") if m is not None else "0;0;0"
        found.append((cell, block.findtext("SubtypeName")))
    return found


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        print(
            f"{'PASS' if passed else 'FAIL'}: {label} {str(detail)[:300]}", flush=True
        )
        return bench.check(label, bool(passed), str(detail)[:1000])

    def note(name, data) -> None:
        print(f"NOTE {name}: {str(data)[:400]}", flush=True)
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

    def seen(text: str) -> int:
        return sum(text in line for line in logs().splitlines())

    def chat(api, text: str, wait: float = 8) -> str:
        reply = " ".join(rig.chat(api, text, wait))
        print(f"{text} -> {reply[:300]}", flush=True)
        return reply

    def probe(api, args: str) -> str:
        return chat(api, f"!sprobe {args}", 6)

    def dump(api, grid: str) -> dict[str, str]:
        reply = until(
            lambda: (lambda r: r if "DUMP" in r else None)(
                chat(api, f"!sedit dump {grid.replace(' ', '_')}")
            ),
            120,
            5,
        )
        return dict(re.findall(r"(\w+)=(\S*)", reply or ""))

    def connectors(api, station: str) -> dict[str, str]:
        """x,y,z -> the probe's words for each connector of the station"""
        reply = probe(api, f"connectors {station}")
        return {
            part.split()[1]: part
            for part in reply.split("CONNECTOR ")[1:]
            if len(part.split()) > 1
        }

    def where() -> str | None:
        for node in NODES:
            try:
                if wa._client_partition(wa._partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def join(at=None):
        wa.link_mods(
            Client(
                bench.run_root,
                CLIENT_ID,
                bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
                cluster.config.gateway_listen,
            ).user_data
            / "Mods"
        )
        for attempt in range(2):
            try:
                api = bench.start_client(CLIENT_ID)
                bench.wait_spawned(CLIENT_ID)
                break
            except (
                Exception
            ) as error:  # noqa: BLE001 -- the bench client's respawn flow fails now and then
                print(f"join attempt {attempt + 1} failed: {error}", flush=True)
                bench.kill_client(CLIENT_ID)
                if attempt:
                    raise
        api.set_admin_flag("invulnerable", True)
        api.character_teleport(*(at or sa.station_tables()[STATION]))
        time.sleep(8)
        return api

    def stop(label: str) -> bool:
        bench.kill_client(CLIENT_ID)
        started = time.monotonic()
        graceful = cluster.stop(grace=240)
        note(f"{label}-stop", {"graceful": graceful, "seconds": round(time.monotonic() - started)})  # fmt: skip
        return graceful

    def start() -> None:
        cluster.start(PROCESSES)
        cluster.wait_ready(nodes=PROCESSES)

    def station_blocks(name: str) -> int:
        objects = (
            ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot().find("SectorObjects")
        )
        for grid in objects:
            if grid.findtext("DisplayName") == name:
                return len(grid.find("CubeBlocks"))
        return -1

    def patched(api, grid: str, before: int) -> dict[str, str]:
        id_ = entry(grid).findtext("Id")
        applied = until(
            lambda: seen(f"Station patch APPLIED {id_} on {grid} (staged)"), 180
        )
        check(f"{grid}: the staged extender patch applies", applied, id_)
        got = dump(api, grid)
        check(f"{grid}: {ADDED} blocks more", got.get("blocks") == str(before + ADDED),
              f"{before} -> {got.get('blocks')}")  # fmt: skip
        check(f"{grid}: the ledger has the patch", id_ in got.get("ledger", "").split(","), got)  # fmt: skip
        found = connectors(api, grid.removesuffix(" Station"))
        trading = [c for c, words in found.items() if "trading=yes" in words]
        check(f"{grid}: {CONNECTORS} connectors, all trading",
              len(found) == CONNECTORS and len(trading) == CONNECTORS, found)  # fmt: skip
        want = {c.replace(";", ",") for c, _ in new_connectors(grid)}
        check(f"{grid}: the extenders' connectors are there", want <= set(found), sorted(want - set(found)))  # fmt: skip
        status = chat(api, f'!oct patch status "{grid}"')
        check(f"{grid}: in one piece", "islands=0" in status, status)
        return got

    # ============================== setup ==============================
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    spc.PENDING.write_text("")
    spc.OUT.write_text("")
    start()
    api = join()
    before = {name: station_blocks(name) for name in wa.stations()}
    note("world-blocks", before)

    # ============================== Alpha: the patch, docking, charging ==============================
    first = patched(api, GRID, before[GRID])
    ships = []
    for cell, subtype in new_connectors(GRID):
        if subtype == "LargeBlockInsetConnectorSmall":
            continue  # the probe's hauler is a large grid
        reply = probe(api, f"dock {STATION} mine BAT=0,AT={cell}")
        ship = re.search(r"SHIP (\d+)", reply)
        check(f"a ship at the extender connector {cell} ({subtype})", ship, reply)
        if not ship:
            continue
        docked = until(lambda: f"DOCKED {ship[1]} yes" in probe(api, f"docked {ship[1]}"), 60)  # fmt: skip
        check(f"it docks at {cell}", docked)
        if docked:
            ships.append((cell, ship[1]))
    note("ships", ships)
    if ships:
        _, ship = ships[0]
        link = probe(api, f"link {ship}")
        check("the extender connector still trades, the grids stay apart",
              "trading=no,yes" in link and "logical=no" in link, link)  # fmt: skip

        def stored() -> float:
            m = re.search(r"stored=(\S+)", probe(api, f"link {ship}"))
            return float(m[1]) if m else -1.0

        check("the docked ship's empty battery charges from the station",
              until(lambda: stored() > 0, 30), stored())  # fmt: skip

    # ============================== handover ==============================
    source = wait_until(where, 120, "the client's partition")
    target = NODES[1 - NODES.index(source)]
    partition = wa._client_partition(wa._partitions(bench, source))
    for attempt in range(3):
        try:
            bench.record(
                f"handover-{source}-{target}",
                cluster.post(
                    wa._handover_url(bench, partition, source=source, target=target),
                    timeout=300,
                ),
            )
            break
        except ClusterError as error:
            print(f"handover attempt {attempt + 1} failed: {error}", flush=True)
            if attempt == 2:
                raise
            time.sleep(10)
    moved = until(lambda: where() == target, 120)
    check(f"the station's partition goes {source} -> {target}", moved)
    time.sleep(10)
    after = dump(api, GRID)
    check("after the handover the station is as patched",
          after.get("hash") == first.get("hash") and after.get("blocks") == first.get("blocks"), after)  # fmt: skip
    still = [s for _, s in ships if f"DOCKED {s} yes" in probe(api, f"docked {s}")]
    check("and the ships are still docked", ships and len(still) == len(ships), still)

    # ============================== the other stations ==============================
    for name, at in wa.stations().items():
        if name != GRID:
            api.character_teleport(*at)
            time.sleep(10)
            patched(api, name, before[name])

    # ============================== restart ==============================
    time.sleep(70)  # a periodic save after the patches
    check("the cluster stops gracefully", stop("restart"))
    start()
    api = join()
    got = dump(api, GRID)
    ledger = got.get("ledger", "").split(",")
    id_ = entry(GRID).findtext("Id")
    check("after a restart the patch is once in the ledger", ledger.count(id_) == 1, ledger)  # fmt: skip
    check("and the station is as patched",
          got.get("hash") == first.get("hash") and got.get("blocks") == first.get("blocks"), got)  # fmt: skip
    check("it was applied once", seen(f"Station patch APPLIED {id_} on {GRID}") == 1)


def main() -> int:
    run.world_template = wa.prepare()
    folders = [turn.build_probe(), spc.build_edit_probe()]
    os.environ["CLUSTER_EXTRA_PLUGINS"] += "".join(f",node:{f}" for f in folders)
    os.environ.update(EDITPROBE_PENDING=str(spc.PENDING), EDITPROBE_OUT=str(spc.OUT))
    # The working tree's patch list and files, next to the plugin's config
    text = (CONFIG / "octahedron.cfg").read_text(encoding="utf-8")
    patches = re.search(r"<StationPatches>(.*)</StationPatches>", text, re.S)[1]
    for e in entries():
        for name in (e.findtext("From"), e.findtext("To")):
            shutil.copy2(STORAGE / name, wa.CONFIG.parent / name)
    wa.set_config(StationPatches=patches)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-station-extenders")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-STATION-EXTENDERS",
        "SE-1388: Station Extenders by staged station patches",
        "3",
        results,
    )
    print(json.dumps({"passed": outcome.passed, "error": outcome.error, "results": str(results)}))  # fmt: skip
    print(json.dumps(FOUND, default=str)[:20000])
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        print(("PASSED: " if item.passed else "FAILED: ") + item.label
              + (f" - {item.detail[:300]}" if item.detail else ""))  # fmt: skip
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


run.administrators = [CLIENT_ID]

if __name__ == "__main__":
    sys.exit(main())
