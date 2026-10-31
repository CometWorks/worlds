"""Target Neutrals off on new blocks on a local test cluster (SE-830): a ship ordered at a station's table,
a paste and blocks placed by hand, the blueprint having the setting on. A block the client turns on keeps it
through a handover in both directions and a graceful cluster restart, and its neighbors stay off.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/target_neutrals_cluster.py CLUSTER_TEST_DIR     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.
"""

from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    _client_partition,
    _handover_url,
    _partitions,
    bench_module,
    link_mods,
    rig,
    so,
    tc,
    wait_until,
)
import test_target_neutrals as tn  # noqa: E402

# Parallel benches sharing a work folder collide on the probe plan
wa.ROOT = Path.home() / f"cluster-runs/target-neutrals-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
NODES = ("node-1", "node-2")
PROCESSES = ("wa-1", *NODES)
BLOCKS = 5


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def join():
        link_mods(
            Client(
                bench.run_root,
                CLIENT_ID,
                bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
                cluster.config.gateway_listen,
            ).user_data
            / "Mods"
        )
        api = bench.start_client(CLIENT_ID)
        bench.wait_spawned(CLIENT_ID)
        return api

    def where() -> str | None:
        for node in NODES:
            try:
                if _client_partition(_partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def to_node(target: str) -> None:
        source = wait_until(where, 120, "the client's partition")
        if source != target:
            partition = _client_partition(_partitions(bench, source))
            bench.record(
                f"handover-{source}-{target}-{int(time.time())}",
                cluster.post(
                    _handover_url(bench, partition, source=source, target=target),
                    timeout=300,
                ),
            )
            wait_until(lambda: where() == target, 120, f"the client on {target}")
        time.sleep(15)

    def press(api) -> str:
        reply = ""
        # A shipyard joins its station's partition by proximity, a little after a start
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order on {where()} -> {reply}", flush=True)
            if "shipyard is busy" not in reply and "has no shipyard" not in reply:
                break
            time.sleep(10)
        return reply

    def read(api, grid: int) -> tuple[int, list[tuple]]:
        return wait_until(
            lambda: _try_read(api, grid),
            120,
            f"the blocks of grid {grid} on the client",
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))
    api = join()
    api.set_admin_flag("creativeTools", True)
    time.sleep(3)

    # Welded by a shipyard, on whichever node has the station
    station = next(iter(wa.stations().values()))
    api.character_teleport(*station)
    table = wait_until(lambda: tc.tables(api), 120, "a table")
    tc.stand_by(api, min(table, key=lambda b: b["distance"]))
    quote = press(api)
    check("the order is quoted", "Your quote for" in quote, quote)
    order = press(api)
    check("the order is in the queue", "is in the queue" in order, order)
    print(so.wait_chat(api, "is ready", 1500), flush=True)
    ship = wait_until(
        lambda: next(
            (g["entityId"] for g in api.list_grids() if g["name"] == tn.SHIP), None
        ),
        120,
        "the ordered ship",
    )
    count, on = read(api, ship)
    check(
        f"ordered ship: its {BLOCKS} blocks have it off",
        count == BLOCKS and not on,
        (count, on),
    )

    # Pasted and placed by hand, in free space
    x, y, z = api.get_character()["position"]
    spot = (x + 3000, y, z)
    api.character_teleport(*spot)
    time.sleep(15)
    pasted = api.paste_blueprint(
        xml=tn.bp_document(tn.blueprint(False, "CubeGrid")),
        position=(spot[0], spot[1], spot[2] + 40),
        display_name="Neutrals Paste",
    )
    grid = pasted[0]["entityId"]
    count, on = read(api, grid)
    check(
        f"pasted: its {BLOCKS} blocks have it off",
        count == BLOCKS and not on,
        (count, on),
    )
    for cell, subtype in (
        ((16, 1, 0), "LargeTurretControlBlock"),
        ((18, 1, 0), "LargeInteriorTurret"),
    ):
        print(
            f"placing {subtype}: {api.character_build_block(grid, cell, subtype)}",
            flush=True,
        )
    time.sleep(10)
    count, on = read(api, grid)
    check("placed by hand: off", count == BLOCKS + 2 and not on, (count, on))

    # The player's later choice
    turned = next(
        tuple(c["min"])
        for c in tn.targeting_cubes(api, grid)
        if "Interior" in c["subtypeId"]
    )
    api.set_property(grid, turned, "TargetNeutrals", True)
    time.sleep(10)
    expected = (BLOCKS + 2, [turned])
    check("the client turns one on", read(api, grid) == expected, read(api, grid))

    first = wait_until(where, 120, "the client's partition")
    for target in [n for n in NODES if n != first] + [first]:
        to_node(target)
        partition = cluster.node(target, f"/partition-of?entity={grid}").get(
            "partition"
        )
        check(f"the grid is on {target}", partition is not None, partition)
        check(
            f"a handover to {target} keeps the choice",
            read(api, grid) == expected,
            read(api, grid),
        )

    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))
    api = join()
    api.character_teleport(*spot)
    time.sleep(20)
    check("a restart keeps the choice", read(api, grid) == expected, read(api, grid))
    count, on = read(api, ship)
    check(
        "a restart leaves the ordered ship's blocks off",
        count == BLOCKS and not on,
        (count, on),
    )


def _try_read(api, grid: int):
    try:
        return tn.on_cells(api, grid) or None
    except Exception as error:  # noqa: BLE001 -- the client may not have the grid yet
        print(f"no read yet: {error}", flush=True)
        return None


def main() -> int:
    so.SMALL = tn.blueprint(False)
    run.world_template = wa.prepare()
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-target-neutrals")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-TARGET-NEUTRALS",
        "SE-830: new blocks get Target Neutrals off, a later choice stays",
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
