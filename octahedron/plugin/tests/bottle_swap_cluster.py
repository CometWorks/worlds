"""The Free Tools boxes' bottle swap (SE-947) on a local test cluster, through the cluster's scenario
bench (CometWorks/cluster Test/): on the node that simulates Alpha, a player's half full and empty
bottles left in a box are taken out by the next refill, and the box is topped up with full ones,
as tests/test_bottle_swap.py checks it on one server.

    OCT_TASK=SE-947C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/bottle_swap_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe on the nodes. About 5 minutes.
"""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-947C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    bench_module,
    rig,
    tc,
)

import shipyard_turn_cluster as turn  # noqa: E402
import station_tests as st  # noqa: E402
import test_bottle_swap as bs  # noqa: E402
import test_support_arm as sa  # noqa: E402

NODES = ("node-1", "node-2")

wa.ROOT = Path.home() / f"cluster-runs/bottles-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def probe(args: str) -> str:
        reply = " ".join(rig.chat(api, f"!sprobe {args}", 6))
        print(f"!sprobe {args} -> {reply}", flush=True)
        return reply

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    wa.link_mods(
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
    api.set_admin_flag("invulnerable", True)
    # The probe's chat command runs on the client's node, the one simulating the station
    api.character_teleport(*sa.station_tables()[bs.STATION])
    st.wait_until(lambda: tc.tables(api), 180, 5)

    bs.swap_and_refill(
        probe,
        lambda ok, what: bench.check(what, bool(ok)),
        "cluster",
        hydrogen_back=True,
    )

    bench.check("graceful stop", cluster.stop(grace=240))
    for process in processes:
        bench.check(
            f"{process}: no Octahedron failure logged",
            not re.search(r"\[Octahedron\].*(failed|Failed)", log(process)),
        )


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-bottle-swap")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-BOTTLE-SWAP",
        "SE-947: the Free Tools boxes swap used bottles for full ones on a cluster",
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
