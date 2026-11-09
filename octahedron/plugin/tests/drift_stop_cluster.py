"""Drift stop on a local test cluster (SE-1326): drifting wrecks keep their partitions online, stopped ones go
offline. A World Authority and one regular node, no client. Far from the stations, each in a partition of its
own, 20 km apart: a wreck with no power drifting at 0.5 m/s, debris at 8 m/s and a wreck at 25 m/s, faster than
DriftStopMaxSpeed (20). No player is within sync distance.

1. Off, offline grace 0: for 120 s all three stay awake and their partitions online. This is main's behavior.
2. `!oct reload` on the node with DriftStopEnabled: the wreck and the debris stop at once, fall asleep and
   their partitions go offline. The fast wreck stays awake and online.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/drift_stop_cluster.py CLUSTER_TEST_DIR     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env.
"""

from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import bench_module, link_mods, rig, wait_until  # noqa: E402
import test_drift_stop as ds  # noqa: E402

wa.ROOT = Path.home() / f"cluster-runs/drift-stop-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
PROCESSES = ("wa-1", "node-1")
BASE = 777000132610000
# name -> (entity id, blocks, size, speed)
GRIDS = {
    "DS Wreck": (BASE + 1, [ds.cockpit(), *ds.armor(4)], "Large", 0.5),
    "DS Debris": (
        BASE + 2,
        [
            rig.block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (0, 0, z))
            for z in range(3)
        ],
        "Small",
        8,
    ),
    "DS Fast": (BASE + 3, ds.armor(4), "Large", 25),
}
STOPPED = ("DS Wreck", "DS Debris")
OFF_SECONDS = 120


def add_grids() -> None:
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    grids = [
        rig.grid(
            name,
            entity_id,
            blocks,
            (ds.SPACE[0], ds.SPACE[1], ds.SPACE[2] + 20_000 * index),
            velocity=(speed, 0, 0),
            size=size,
        )
        for index, (name, (entity_id, blocks, size, speed)) in enumerate(GRIDS.items())
    ]
    text = sector.read_text(encoding="utf-8")
    text = text.replace("</SectorObjects>", "".join(grids) + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def offline() -> set[int]:
        return {
            int(member)
            for row in cluster.get("/registry/partitions/offline").get("Partitions", [])
            for member in row.get("Members") or []
        }

    def grids() -> dict[str, dict]:
        """name -> the node's entity row (physicsActive, velocity), for the test grids it holds"""
        found = {}
        cursor = 0
        while cursor is not None:
            dump = cluster.node(
                "node-1",
                f"/cluster/diag?depth=entities&cursor={cursor}",
                timeout=60,
            )
            for partition in dump.get("partitions", []):
                for entity in partition.get("entities", []):
                    if entity.get("displayName") in GRIDS:
                        found[entity["displayName"]] = entity
            cursor = dump.get("nextCursor")
        return found

    def state() -> dict[str, str]:
        """name -> offline, awake or asleep"""
        gone = offline()
        held = grids()
        return {
            name: (
                "offline"
                if entity_id in gone
                else (
                    "awake"
                    if held.get(name, {}).get("physicsActive")
                    else "asleep" if name in held else "missing"
                )
            )
            for name, (entity_id, *_) in GRIDS.items()
        }

    def stops() -> list[str]:
        return [
            line
            for log in (bench.run_root / "node-1-data").glob(
                "SpaceEngineersDedicated_*.log"
            )
            for line in log.read_text(errors="replace").splitlines()
            if "Drift stop:" in line
        ]

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))
    # The world's partitions start offline but for the stations': nobody came by yet. Live, a drifting wreck
    # is online because somebody did, so the test loads them all, with nothing going offline meanwhile.
    cluster.handover_config(
        {
            "offline.enabled": "false",
            # Unowned grids nobody visits would be removed after 5 minutes
            "cleanup.unownedMinutes": "0",
        }
    )

    def placed() -> bool:
        now = state()
        print(f"placed: {json.dumps(now)}", flush=True)
        if all(s not in ("missing", "offline") for s in now.values()):
            return True
        # A load asked for right after the start can be lost: ask again each poll
        for row in cluster.get("/registry/partitions/offline").get("Partitions", []):
            cluster.post(
                f"/registry/partitions/offline/load?partition={row['Partition']}&reason=test"
            )
        return False

    wait_until(placed, 300, "the grids on node-1", interval=10)
    cluster.handover_config(
        {
            "offline.enabled": "true",
            "offline.minOnlineSeconds": "0",
            "offline.graceSeconds": "0",
        }
    )

    # 1. Off
    samples = []
    began = time.monotonic()
    while time.monotonic() - began < OFF_SECONDS:
        samples.append(state())
        time.sleep(10)
    print(f"off: {json.dumps(samples[-1])}", flush=True)
    before = {name: [s[name] for s in samples] for name in GRIDS}
    bench.record("off", before)
    for name in GRIDS:
        check(
            f"off: {name} stays awake and online for {OFF_SECONDS} s",
            all(s == "awake" for s in before[name]),
            before[name],
        )
    check("off: no drift stop logged", not stops(), stops())

    # 2. On
    wa.set_config(DriftStopEnabled="true", DriftStopSeconds=ds.SECONDS)
    print(f"reload: {probe.run('!oct reload', timeout=120, settle=3)}", flush=True)
    started = time.monotonic()
    timeline = []

    def settled() -> bool:
        now = state()
        timeline.append((round(time.monotonic() - started), now))
        return all(now[name] == "offline" for name in STOPPED)

    try:
        wait_until(settled, 300, "the stopped grids offline")
        went = True
    except Exception as error:  # noqa: BLE001 -- the check below reports it
        print(error, flush=True)
        went = False
    print(f"on: {json.dumps(timeline)}", flush=True)
    bench.record("on", timeline)
    check("on: the wreck and the debris go offline", went, timeline[-1:])
    after = state()
    check(
        "on: the fast wreck stays awake and online", after["DS Fast"] == "awake", after
    )
    logged = stops()
    print(f"on: {logged}", flush=True)
    check(
        "on: the log names the wreck and the debris, once each",
        len(logged) == 2 and all(any(n in l for l in logged) for n in STOPPED),
        logged,
    )
    print(
        f"awake test grids: off {sum(before[n][-1] == 'awake' for n in GRIDS)}/{len(GRIDS)}, "
        f"on {sum(after[n] == 'awake' for n in GRIDS)}/{len(GRIDS)}; offline: off 0, "
        f"on {sum(after[n] == 'offline' for n in GRIDS)}",
        flush=True,
    )


def main() -> int:
    run.world_template = wa.prepare()
    run.regular_nodes = 1
    add_grids()
    wa.set_config(DriftStopEnabled="false", DriftStopSeconds=ds.SECONDS)
    # The probe runs the reload on the node, where the rule runs
    os.environ["CLUSTER_EXTRA_PLUGINS"] = os.environ["CLUSTER_EXTRA_PLUGINS"].replace(
        f"wa:{wa.lc.PLUGINS / 'ConsoleProbe'}", str(wa.lc.PLUGINS / "ConsoleProbe")
    )
    os.environ["CONSOLEPROBE_NODE"] = "node-1"
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-drift-stop"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-DRIFT-STOP",
        "SE-1326: drifting wrecks stop, sleep and go offline",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
