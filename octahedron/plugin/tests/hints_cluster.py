"""Good.bot's hints (SE-1538) on a local test cluster, through the cluster's scenario bench
(CometWorks/cluster Test/): the World Authority times the hints, the nodes only pass them on,
so a player hears each hint once, as tests/test_hints.py checks it on one server.

    OCT_TASK=SE-1538C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/hints_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client.

Run on polaris on 2026-11-14 with cluster 0.3.11: 12/12 checks.
"""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1538C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import CLIENT_ID, Client, bench_module  # noqa: E402

import test_hints as th  # noqa: E402

NODES = ("node-1", "node-2")

wa.ROOT = Path.home() / f"cluster-runs/hints-{os.environ['OCT_TASK']}"
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

    heard = th.watch(api, th.WATCH)
    th.check_heard(lambda ok, what: bench.check(what, bool(ok)), heard, run.hints)

    sent = {p: len(re.findall(r"Hints: ", log(p))) for p in processes}
    bench.check(
        f"the World Authority sent the hints, at least the {len(heard)} the client heard: {sent}",
        sent["wa-1"] >= len(heard) > 0,
    )
    bench.check(
        f"no node sent a hint of its own: {sent}", not any(sent[n] for n in NODES)
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
    text = th.test_config(wa.CONFIG.read_text(encoding="utf-8"))
    wa.CONFIG.write_text(text, encoding="utf-8")
    run.hints = th.config_hints(text)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-hints")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-HINTS",
        "SE-1538: Good.bot says a hint once on a cluster",
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
