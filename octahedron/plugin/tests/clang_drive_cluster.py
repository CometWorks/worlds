"""Clang drives and ramming on a local test cluster (SE-966), as tests/test_clang_drive.py checks them on one
server, without a client: the nodes' own test routes give the ships their speed. A heavy ship whose speed jumps
by 50 m/s in one write (no collision) stays on: a velocity set from code, as a Trade Lane sets it, isn't the
ship's doing (SE-978). One that flies into Alpha Station's safe zone at 50 m/s and rams a static wall there is
switched off, with the line in the log of the node that simulates them. The same ship
crashing into a wall outside safe zones stays on. The rams get their speed bit by bit, below twice their
thrust, so that only the hit counts.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/clang_drive_cluster.py CLUSTER_TEST_DIR     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes, no client.
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import ClusterError, bench_module, link_mods  # noqa: E402
import test_clang_drive as cd  # noqa: E402
import test_target_neutrals as tn  # noqa: E402

wa.ROOT = Path.home() / f"cluster-runs/clang-drive-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
NODES = ("node-1", "node-2")
PROCESSES = ("wa-1", *NODES)
SPEED = cd.RAM_SPEED
CRASH = (cd.SPACE[0], cd.SPACE[1] - 5000, cd.SPACE[2])
# name -> (where its first grid goes, the wall's position or None, expected off, safe zone)
# The rams get 19 m/s at once, below the speed that counts, then 0.3 m/s every 0.15 s: a step is 3 m/s² over
# the 6-frame window, three of them in one still below twice the thrust. Up to speed in about 16 s and 550 m:
# they start 700 m before their wall, the zone's edge 55 m before it.
BELOW = 19.0
STEP = 0.3
STEP_SECONDS = 0.15
CASES = {
    "CL Spike": ((cd.SPACE[0], cd.SPACE[1], cd.SPACE[2]), None, False, False),
    "CL Ram Zone": (
        (cd.ZONE[0], cd.ZONE[1], cd.ZONE[2] + 895),
        (cd.ZONE[0], cd.ZONE[1], cd.ZONE[2] + 195),
        True,
        True,
    ),
    "CL Ram Crash": ((CRASH[0], CRASH[1], CRASH[2] + 700), CRASH, False, False),
}


def node_log(bench, node: str) -> str:
    return "\n".join(
        path.read_text(errors="replace")
        for path in sorted(
            (bench.run_root / f"{node}-data").glob("SpaceEngineersDedicated_*.log")
        )
    )


def switch_offs(bench, kind: str = "switched off") -> dict[str, tuple[str, str]]:
    """name -> (node, log line) of its last switch-off, with kind "left on" of the last time the rule let it be,
    or with kind "" of its last line of either
    """
    found = {}
    for node in NODES:
        for line in node_log(bench, node).splitlines():
            if cd.LOG_MARK in line and f"{kind}, " in line:
                found[line.split(cd.LOG_MARK, 1)[1].split(" (", 1)[0]] = (node, line)
    return found


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def spawn(name: str, grid: str, at, static: bool = False) -> int:
        path = wa.ROOT / f"{name.replace(' ', '')}.sbc"
        path.write_text(tn.bp_document(grid), encoding="utf-8")
        query = urllib.parse.urlencode(
            {
                "path": str(path),
                "x": at[0],
                "y": at[1],
                "z": at[2],
                "name": name,
                "static": str(static).lower(),
            }
        )
        return cluster.node(
            "node-1", f"/blueprint-test-spawn?{query}", method="POST", timeout=120
        )["grids"][0]

    def motion(entity: int) -> dict:
        """From the node that has the grid now"""
        for node in NODES:
            try:
                state = cluster.node(node, f"/fast-test-velocity?entity={entity}")
            except (ClusterError, OSError):
                continue
            if state.get("found"):
                return {**state, "node": node}
        return {"found": False}

    def push(entity: int, velocity) -> str:
        vx, vy, vz = velocity
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            node = motion(entity).get("node")
            if node:
                cluster.node(
                    node,
                    f"/fast-test-velocity?entity={entity}&vx={vx}&vy={vy}&vz={vz}",
                    method="POST",
                )
                return node
            time.sleep(1)
        raise TimeoutError(f"grid {entity} on no node")

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))

    wa.ROOT.mkdir(parents=True, exist_ok=True)
    grids = {}
    for name, (at, wall_at, _, _) in CASES.items():
        if wall_at:
            spawn(
                name + " Wall",
                cd.wall(name + " Wall", 50, (0, 0, 0), tag="CubeGrid"),
                wall_at,
                static=True,
            )
        grids[name] = spawn(
            name,
            cd.ship(
                "CL Ram Zone", 51, (0, 0, 0), heavy=cd.HEAVY, tag="CubeGrid"
            ).replace(
                "<DisplayName>CL Ram Zone</DisplayName>",
                f"<DisplayName>{name}</DisplayName>",
            ),
            at,
        )
    print(f"spawned: {grids}", flush=True)
    # Settled where they are, then toward -Z, where the walls are
    time.sleep(5)
    node = push(grids["CL Spike"], (0, 0, -SPEED))
    print(f"CL Spike: {SPEED} m/s in one write on {node}", flush=True)
    # Too slow to count in one write, then in steps the window takes
    speed = BELOW
    while speed < SPEED:
        for name in ("CL Ram Zone", "CL Ram Crash"):
            push(grids[name], (0, 0, -speed))
        speed = min(SPEED, speed + STEP)
        time.sleep(STEP_SECONDS)
    for name in ("CL Ram Zone", "CL Ram Crash"):
        print(f"{name}: {SPEED} m/s on {push(grids[name], (0, 0, -SPEED))}", flush=True)

    slowest = {name: SPEED for name in CASES}

    def hit(name: str) -> bool:
        """Slowed down, or over the limit at all: a crash may break the ship up, its main grid gone or flying on"""
        return slowest[name] < SPEED * 0.8 or name in switch_offs(bench, "")

    def settled() -> bool:
        for name in CASES:
            state = motion(grids[name])
            if state.get("found"):
                slowest[name] = min(slowest[name], state["speed"])
        off = switch_offs(bench)
        return all(hit(n) for n in ("CL Ram Zone", "CL Ram Crash")) and all(
            n in off for n, (*_, expected, _) in CASES.items() if expected
        )

    try:
        wa.wait_until(settled, 60, "the rams and the switch-offs")
    except Exception as error:  # noqa: BLE001 -- the checks below report it
        print(error, flush=True)
    time.sleep(10)
    off = switch_offs(bench)
    print(f"slowest: {slowest}", flush=True)
    for name, (_, _, expected, zone) in CASES.items():
        line = off.get(name, ("", ""))[1]
        print(f"{name}: {line[:400] or 'left on'}", flush=True)
        if expected:
            check(
                f"{name} switched off on {off.get(name, ('?',))[0]}", name in off, line
            )
        else:
            check(f"{name} stays on", name not in off, line)
        if zone:
            check(f"{name}'s line says safe zone", "in a safe zone" in line, line)
    for name in ("CL Ram Zone", "CL Ram Crash"):
        check(f"{name} hit its wall", hit(name), slowest[name])
    left = switch_offs(bench, "left on").get("CL Ram Crash", ("", ""))[1]
    print(f"CL Ram Crash: {left[:400]}", flush=True)
    check(
        "the crash was over the limit, left on for the collision",
        "a collision" in left,
        left,
    )
    bench.record(
        "clang", {"slowest": slowest, "off": {k: v[1] for k, v in off.items()}}
    )


def main() -> int:
    run.world_template = wa.prepare()
    run.administrators = []
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-clang-drive")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-CLANG-DRIVE",
        "SE-966: a ship whose speed changes much faster than its thrusters allow is switched off",
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
