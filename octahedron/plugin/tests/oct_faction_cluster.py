"""New players join OCT on a local test cluster (SE-1012), through the cluster's scenario bench
(CometWorks/cluster Test/): the World Authority makes the joining player's identity, so it is
the one that joins them to OCT, once. The client and its node both see the player in OCT. A
player who leaves OCT stays out of it when they connect again. The first player, an admin, joins
as a plain member; a second player, whom OctLeaders names in Viktor's place, joins as OCT's
leader (SE-1015).

    OCT_TASK=SE-1012C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/oct_faction_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, a second admin client for the leader, with the station probe on the nodes.

Run on polaris on 2026-11-04 on cluster 0.2.1: 12/12 checks in 5 minutes; 8/12 without the fix.
With the leader (SE-1015): 16/16 on polaris, 2026-11-04.
"""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1012C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    bench_module,
    rig,
)

import shipyard_turn_cluster as turn  # noqa: E402
import station_tests as st  # noqa: E402

NODES = ("node-1", "node-2")
JOINED = re.compile(r"New player (\d+) joins OCT")
RANK = re.compile(r"New player \d+ joins OCT as (\w+)")
# The bench's second client, as in its own scenarios
CLIENT2_ID = CLIENT_ID + 1
LEADER_PORT = 24160

wa.ROOT = Path.home() / f"cluster-runs/oct-faction-{os.environ['OCT_TASK']}"
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

    def connect(client_id=CLIENT_ID, port=bench_module.LEGACY_REMOTE_PORT):
        wa.link_mods(
            Client(
                bench.run_root,
                client_id,
                bench.remote_port(port),
                cluster.config.gateway_listen,
            ).user_data
            / "Mods"
        )
        api = bench.start_client(client_id, port)
        bench.wait_spawned(client_id)
        return api

    def probe(api, args: str) -> str:
        reply = " ".join(rig.chat(api, f"!sprobe {args}", 6))
        print(f"!sprobe {args} -> {reply}", flush=True)
        return reply

    def tag(api):
        return api.get_faction().get("tag")

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)

    api = connect()
    me = api.get_faction()["identityId"]
    st.wait_until(lambda: tag(api) == "OCT", 60, 2)
    bench.check("the client sees its player in OCT", tag(api) == "OCT", tag(api))
    bench.check("the player's node has them in OCT", "ME OCT" in probe(api, "whoami"))
    joined = JOINED.findall(log("wa-1"))
    bench.check(
        "the World Authority joined the player once", joined == [str(me)], joined
    )
    for node in NODES:
        bench.check(f"{node} joined no one", not JOINED.search(log(node)))
    bench.check(
        "the first player, an admin, is a plain member",
        "ME OCT member" in probe(api, "whoami"),
    )

    # Viktor's stand-in, whom OctLeaders names, leads OCT; the first player stays a member
    leader = connect(CLIENT2_ID, LEADER_PORT)
    st.wait_until(lambda: tag(leader) == "OCT", 60, 2)
    bench.check(
        "the player OctLeaders names leads OCT",
        "ME OCT leader" in probe(leader, "whoami"),
    )
    bench.check(
        "the first player is still a member", "ME OCT member" in probe(api, "whoami")
    )
    ranks = RANK.findall(log("wa-1"))
    bench.check("the WA's log", ranks == ["member", "leader"], ranks)
    viktor = leader.get_faction()["identityId"]
    bench.kill_client(CLIENT2_ID)

    # Leaving OCT is the player's choice: connecting again doesn't put them back
    probe(api, "faction OCT none")
    st.wait_until(lambda: tag(api) is None, 60, 2)
    bench.kill_client(CLIENT_ID)
    api = connect()
    bench.check(
        "the same player after reconnecting",
        api.get_faction()["identityId"] == me,
        api.get_faction(),
    )
    bench.check("a player who left stays out", tag(api) is None, tag(api))
    joined = [j for p in processes for j in JOINED.findall(log(p))]
    bench.check("no second join", joined == [str(me), str(viktor)], joined)

    bench.check("graceful stop", cluster.stop(grace=240))
    for process in processes:
        bench.check(
            f"{process}: no Octahedron failure logged",
            not re.search(r"\[Octahedron\].*(failed|Failed)", log(process)),
        )


# Both admins: the station probe answers admins only, and OCT no longer has only NPCs when the
# second joins, so the game would not make them leader
run.administrators = [CLIENT_ID, CLIENT2_ID]


def main() -> int:
    run.world_template = wa.prepare()
    wa.set_config(OctLeaders=CLIENT2_ID)
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-oct-faction")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-FACTION",
        "SE-1012: new players join OCT on a cluster",
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
