"""A station connector's countdown through a handover on a local test cluster (SE-1237), through the
cluster's scenario bench (CometWorks/cluster Test/). A stranger's ship docks at Alpha Station while
nobody of its owners is there, so its countdown starts on the node that has the station. The
station's partition is handed over to the other node a third of the way in. The countdown goes on
there from where it was: the connector's storage has it, the target node doesn't start it over, and
the ship is let go about one countdown after it started, by the target node.

    OCT_TASK=SE-1237C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/station_release_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe on the nodes. About 10 minutes.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1237C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    bench_module,
    rig,
    tc,
    wait_until,
)

import shipyard_turn_cluster as turn  # noqa: E402
import test_support_arm as sa  # noqa: E402

STATION = "Alpha"
NODES = ("node-1", "node-2")
CHECK = 2
COUNTDOWN = 60
AT = "13;0;0"

wa.ROOT = Path.home() / f"cluster-runs/release-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1000])

    def where() -> str | None:
        for node in NODES:
            try:
                if wa._client_partition(wa._partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def node_log(node: str) -> str:
        return "\n".join(
            path.read_text(errors="replace")
            for path in sorted(
                (bench.run_root / f"{node}-data").glob("SpaceEngineersDedicated_*.log")
            )
        )

    def logged(node: str, pattern: str) -> list[str]:
        return [
            line
            for line in node_log(node).splitlines()
            if "Station release" in line and re.search(pattern, line)
        ]

    def to_table(api) -> None:
        api.character_teleport(*sa.station_tables()[STATION])
        for _ in range(36):
            time.sleep(5)
            tables = tc.tables(api)
            if not tables:
                continue
            try:
                tc.stand_by(api, min(tables, key=lambda b: b["distance"]))
                return
            except Exception as error:  # noqa: BLE001 -- a grid already gone
                print(f"no stand by a table yet: {error}", flush=True)
        raise AssertionError(f"no table near {api.get_character()['position']}")

    def probe(api, args: str) -> str:
        reply = " ".join(rig.chat(api, f"!sprobe {args}", 6))
        print(f"!sprobe {args} -> {reply}", flush=True)
        return reply

    def state(api, ship: str) -> dict[str, float] | None:
        """The countdown state the connector holding the ship has stored"""
        m = re.search(
            rf"release={ship}:(\S+):(\S+):(\d)", probe(api, f"connectors {STATION}")
        )
        return {"docked": float(m[1]), "left": float(m[2])} if m else None

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *NODES):
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(["wa-1", *NODES])
    cluster.wait_ready(nodes=["wa-1", *NODES])
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
    to_table(api)

    unlock = wait_until(
        lambda: (
            found
            if (
                found := re.findall(
                    r"unlock=(\d+)", probe(api, f"connectors {STATION}")
                )
            )
            and set(found) == {"0"}
            else None
        ),
        60,
        "the game's auto unlock turned off",
    )
    check("the game's auto unlock is off on the station's connectors", unlock, unlock)
    station = re.search(r"CONNECTOR (\d+)", probe(api, f"connectors {STATION}"))[1]

    reply = probe(api, f"dock {STATION} stranger AT={AT}")
    ship = re.search(r"SHIP (\d+)", reply)
    check("a stranger's ship at the station", ship, reply)
    ship = ship[1]
    check(
        "it docks",
        wait_until(
            lambda: f"DOCKED {ship} yes" in probe(api, f"docked {ship}"), 60, "docked"
        ),
    )
    source = wait_until(where, 120, "the client's partition")
    target = NODES[1 - NODES.index(source)]
    started = wait_until(
        lambda: logged(source, rf"\({ship}\) .*started"),
        COUNTDOWN * 10,
        "the countdown",
    )
    start = time.monotonic()
    check(f"the countdown starts on {source}", started, started)

    def frame() -> tuple[float, int]:
        reply = probe(api, f"connectors {STATION}")
        return time.monotonic(), int(re.search(r"FRAME (\d+)", reply)[1])

    (t0, f0), _ = frame(), time.sleep(5)
    t1, f1 = frame()
    print(f"{source} simulates {(f1 - f0) / (t1 - t0):.1f} frames a second", flush=True)

    # A third of the way in, in simulation time: a loaded node runs slower than the wall clock
    before = wait_until(
        lambda: (s := state(api, ship)) and s["left"] <= COUNTDOWN * 2 / 3 and s,
        COUNTDOWN * 10,
        "a third of the countdown",
    )
    check("the connector stores the countdown", before, before)
    partition = wa._client_partition(wa._partitions(bench, source))
    handed = time.monotonic()
    for attempt in range(3):
        try:
            bench.record(
                f"handover-{source}-{target}-{int(time.time())}",
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
    wait_until(lambda: where() == target, 120, f"the client on {target}")
    print(
        f"handover {source} -> {target} took {time.monotonic() - handed:.0f} s",
        flush=True,
    )
    on = cluster.node(target, f"/partition-of?entity={station}").get("partition")
    check(f"the station is on {target}", on is not None, on)

    after = wait_until(lambda: state(api, ship), 60, "the stored countdown")
    check(
        f"{target} goes on from the stored countdown",
        before
        and after
        and after["left"] <= before["left"]
        and after["docked"] >= before["docked"],
        (before, after),
    )
    let_go = wait_until(
        lambda: f"DOCKED {ship} no" in probe(api, f"docked {ship}"),
        COUNTDOWN * 10,
        "the ship let go",
    )
    print(
        f"let go {time.monotonic() - start:.0f} s of wall time after the start",
        flush=True,
    )
    # The node writes its log out a little later
    released = wait_until(
        lambda: logged(target, rf"\({ship}\) .*let go after (\d+) s docked"),
        60,
        "the let go line",
    )
    docked = int(re.search(r"after (\d+) s docked", released[0])[1]) if released else -1
    check(
        f"{target} lets it go one countdown after it started, in simulation time: {docked} s docked",
        let_go and COUNTDOWN <= docked <= COUNTDOWN + CHECK * 3,
        released or node_log(target)[-2000:],
    )
    check(
        f"{target} doesn't start the countdown over",
        not logged(target, rf"\({ship}\) .*started"),
        logged(target, rf"\({ship}\)"),
    )
    print(probe(api, "undock"), flush=True)


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        StationConnectorCheckSeconds=CHECK,
        StationConnectorCountdownSeconds=COUNTDOWN,
        StationDockCharging="false",
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-station-release")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-STATION-RELEASE",
        "SE-1237: a station connector's countdown through a handover",
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
