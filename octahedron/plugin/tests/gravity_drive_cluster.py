"""Gravity drives on a local test cluster (SE-881), as tests/test_gravity_drive.py checks them on one server: a
client pilots a ship with artificial mass, then one with space balls, on a regular node and turns the generator
on. The node turns both blocks off, the nodes don't see the ship move, the client's view stays with theirs, and the
log of each process says the patch is applied.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/gravity_drive_cluster.py CLUSTER_TEST_DIR     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.
"""

from __future__ import annotations

import json
import math
import os
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    ClusterError,
    _partitions,
    bench_module,
    link_mods,
    rig,
    wait_until,
)
from se_autopilot import enter_cockpit  # noqa: E402
import test_gravity_drive as gd  # noqa: E402
import test_target_neutrals as tn  # noqa: E402

wa.ROOT = Path.home() / f"cluster-runs/gravity-drive-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
NODES = ("node-1", "node-2")
PROCESSES = ("wa-1", *NODES)
SHIPS = (
    ("GD Mass", (100_000, -300_000, 0), gd.MASS),
    ("GD Ball", (102_000, -300_000, 0), gd.BALL),
)


def node_speed(bench) -> float:
    """The fastest member of any partition, as the nodes report it"""
    speeds = [0.0]
    for node in NODES:
        try:
            speeds += [
                row.get("maxMemberSpeed") or 0 for row in _partitions(bench, node)
            ]
        except (ClusterError, OSError):
            pass
    return max(speeds)


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))

    for process in PROCESSES:
        logs = (bench.run_root / f"{process}-data").glob(
            "SpaceEngineersDedicated_*.log"
        )
        lines = [
            line
            for log in logs
            for line in log.read_text(errors="replace").splitlines()
            if "Gravity drive patch" in line
        ]
        print(f"{process}: {lines}", flush=True)
        check(
            f"{process} logs once that the patch is applied",
            len(lines) == 1 and "applied on" in lines[0],
            lines,
        )

    link_mods(
        wa.Client(
            bench.run_root,
            CLIENT_ID,
            bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
            cluster.config.gateway_listen,
        ).user_data
        / "Mods"
    )
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    bench.ensure_gameplay_focus(CLIENT_ID)

    for name, at, drive in SHIPS:
        path = wa.ROOT / f"{name.replace(' ', '')}.sbc"
        path.write_text(
            tn.bp_document(gd.ship(name, None, (0, 0, 0), drive, tag="CubeGrid")),
            encoding="utf-8",
        )
        query = urllib.parse.urlencode(
            {"path": str(path), "x": at[0], "y": at[1], "z": at[2], "name": name}
        )
        spawn = cluster.node(
            "node-1", f"/blueprint-test-spawn?{query}", method="POST", timeout=120
        )
        grid = spawn["grids"][0]
        api.character_teleport(at[0], at[1] + 60, at[2])
        gd.wait_on_client(api, grid)
        seat = enter_cockpit(api, grid)
        check(f"the client sits in {name}", seat.get("state") == "sitting", seat)
        bench.ensure_gameplay_focus(CLIENT_ID)
        start = api.get_grid(grid)["position"]
        api.set_enabled(grid, gd.GENERATOR, True)
        try:
            wait_until(
                lambda: gd.drives_on(api, grid) == [False, False],
                10,
                f"{name}: both blocks off",
            )
            turned_off = True
        except Exception as error:  # noqa: BLE001 -- the check below reports it
            print(error, flush=True)
            turned_off = False
        check(f"the node turns both blocks of {name} off", turned_off)

        fastest_node = fastest_client = farthest = 0.0
        began = time.monotonic()
        while time.monotonic() - began < gd.WINDOW:
            state = api.get_grid(grid)
            fastest_client = max(fastest_client, state["speed"])
            farthest = max(farthest, math.dist(start, state["position"]))
            fastest_node = max(fastest_node, node_speed(bench))
            if fastest_node > gd.MOVING:
                break
            time.sleep(0.2)
        result = {
            "node": round(fastest_node, 2),
            "client": round(fastest_client, 2),
            "client_moved": round(farthest, 2),
            "end": round(math.dist(start, api.get_grid(grid)["position"]), 2),
        }
        print(f"{name}: {json.dumps(result)}", flush=True)
        bench.record(name, result)
        check(f"{name} stays still on the nodes", fastest_node < gd.STILL, result)
        check(
            f"the client's view of {name} stays put",
            farthest < gd.JOINED,
            result,
        )
        gd.leave(api)
        # The nodes report the fastest of a partition: a ship flying on doesn't blur the next one
        api.close_grid(grid)
        wait_until(lambda: node_speed(bench) < gd.STILL, 60, f"{name} gone")


def main() -> int:
    run.world_template = wa.prepare()
    run.administrators = [CLIENT_ID]
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-gravity-drive")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-GRAVITY-DRIVE",
        "SE-881: artificial mass and space balls don't react to gravity generators",
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
