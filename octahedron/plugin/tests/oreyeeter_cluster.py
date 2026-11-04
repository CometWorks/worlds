"""OreYeeter (OCT_OREYEETER_REPO) on a local test cluster (SE-1014), through the cluster's scenario bench
(CometWorks/cluster Test/), with the station probe and OreYeeter on the nodes at OreYeeter's own
defaults: lists start with Stone here, while Octahedron's config/oreyeeter.cfg starts them empty.
Drills dig in small asteroids 100 m out of Alpha's zone; what a drill digs
stays in its own inventory, the first container the ore would reach.

- A drill not named with the tag voids nothing while !ore auto is off; a tagged one voids the
  default list (Stone). Each rock has its own mix of ores, so what else it digs isn't checked.
- !ore list, add (an unknown ore is refused), auto and remove answer as they should, and with auto
  on, an untagged drill voids every listed ore: the first rock's and Nickel.
- The player's list follows them to the other node: same list, same voiding. A change made there
  is seen back on the first node, which had read the old list. !ore clean empties the listed ores
  from the drill the player looks at.
- On a cluster, !ore master add refuses: a node would change only its own config.

    OCT_TASK=SE-1014 OCT_WORLD_SOURCE=<world from build_world.py> OCT_OREYEETER_REPO=<OreYeeter> \\
        OCT_DS64=<DedicatedServer64> <Test>/.venv/bin/python tests/oreyeeter_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client. About 15 minutes.
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

os.environ.setdefault("OCT_TASK", "SE-1014")
import shipyard_turn_cluster as turn  # noqa: E402  (takes the bench's folder from argv)
from shipyard_turn_cluster import (  # noqa: E402
    CLIENT_ID,
    NODES,
    STATION,
    Client,
    ClusterError,
    bench_module,
    rig,
    sa,
    tc,
    wa,
    wait_until,
)

wa.ROOT = Path.home() / f"cluster-runs/oreyeeter-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"

OREYEETER_ID = "6e0b4a93-d1f7-4c28-b5a6-3f92c8d1e704"
TAG = "!ore"
# How long a drill that should void everything is watched, at least
QUIET = 20


def build_oreyeeter() -> Path:
    """OreYeeter as a local plugin, next to what wa.prepare built"""
    repo = Path(os.environ["OCT_OREYEETER_REPO"])
    out = wa.lc.PLUGINS / ".build" / "OreYeeter"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(repo / "ServerPlugin/ServerPlugin.csproj"),
         "-c", "Release", "--nologo", "-v", "q", "-p:DeployToMagnetar=false",
         f"-p:MagnetarBin={os.environ['MAGNETAR_HOME']}/Libraries/MagnetarInterim", f"-p:Dedicated64={os.environ['OCT_DS64']}",
         "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "OreYeeter"
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(out / "OreYeeter.dll", folder / "OreYeeter.dll")
    (folder / "OreYeeter.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{OREYEETER_ID}</Id><FriendlyName>OreYeeter</FriendlyName>"
        "<RepoId>buttheadbob/OreYeeter</RepoId><Commit>local</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return folder


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

    def to_node(target: str) -> None:
        source = wait_until(where, 120, "the client's partition")
        if source != target:
            partition = wa._client_partition(wa._partitions(bench, source))
            bench.record(
                f"handover-{source}-{target}-{int(time.time())}",
                cluster.post(
                    wa._handover_url(bench, partition, source=source, target=target),
                    timeout=300,
                ),
            )
            wait_until(lambda: where() == target, 120, f"the client on {target}")

    def until(what, timeout: float = 30, every: float = 2):
        end = time.monotonic() + timeout
        while True:
            try:
                value = what()
            except Exception as error:  # noqa: BLE001 -- a node may be coming back
                print(f"not yet: {error}", flush=True)
                value = None
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

    def to_table(api) -> None:
        api.character_teleport(*sa.station_tables()[STATION])
        for _ in range(24):
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

    def join():
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
        to_table(api)
        return api

    def probe(api, args: str) -> str:
        return " ".join(rig.chat(api, f"!sprobe {args}", 6))

    def ore(api, args: str) -> str:
        reply = " ".join(rig.chat(api, f"!ore {args}", 8))
        print(f"!ore {args} on {where()} -> {reply}", flush=True)
        return reply

    def drill(api, name: str = "") -> tuple[str, tuple[float, ...]]:
        reply = probe(api, f"drill {STATION} out {name}".strip())
        found = re.search(r"DRILL (\d+) inzone=no at=(\S+)", reply)
        assert found, reply
        return found[1], tuple(map(float, found[2].split(",")))

    def dug(api, grid: str) -> dict[str, float] | None:
        """The drill's ores by subtype, None while the probe's node doesn't have it"""
        found = re.search(
            r"DRILLSTATE \d+ ore=\S+ on=(\w+) ores=(\S*)",
            probe(api, f"drillstate {grid}"),
        )
        if not found:
            return None
        assert found[1] == "yes", f"drill {grid} isn't working"
        return {
            o: float(kg) for o, kg in (p.split(":") for p in found[2].split(",") if p)
        }

    def your_list(reply: str) -> set[str] | None:
        found = re.search(r"Your list: ([^|]*?) \|", reply)
        return (
            None
            if not found
            else set() if found[1] == "none" else set(found[1].split(", "))
        )

    def stone(api, grid: str):
        """The drill's ores once it has dug stone"""
        return until(lambda: (d := dug(api, grid)) and d.get("Stone", 0) > 0 and d, 40)

    def after_quiet(api, grids: list[str]) -> dict[str, dict[str, float] | None]:
        """What the drills dug in the time one that voids nothing digs plenty"""
        time.sleep(quiet)
        return {g: dug(api, g) for g in grids}

    def none_listed(ores: dict[str, float] | None) -> bool:
        """The drill is there and holds no listed ore. Each rock has its own mix, the rest may come."""
        return ores is not None and not set(ores) & listed

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    # A reused run folder keeps the player lists in the shared storage
    for stale in (bench.run_root / "shared" / "plugins").glob("6e0b4a93-*"):
        shutil.rmtree(stale)
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    first = wait_until(where, 120, "the client's partition")
    other = NODES[1 - NODES.index(first)]

    # --- defaults, the name tag ---
    reply = ore(api, "list")
    check("!ore list: the default list", your_list(reply) == {"Stone"}, reply)
    check(
        "!ore list: only tagged drills yeet",
        f"drills named '{TAG}' yeet" in reply,
        reply,
    )
    plain, _ = drill(api)
    tagged, _ = drill(api, f"{TAG}-drill")
    started = time.monotonic()
    check("an untagged drill digs stone", stone(api, plain), dug(api, plain))
    took = time.monotonic() - started
    quiet = max(QUIET, 3 * took)
    time.sleep(quiet)
    rock = dug(api, plain) or {}
    print(f"the rock: {rock}, stone after {took:.0f} s", flush=True)
    kept = dug(api, tagged)
    check(
        "a tagged drill voids its owner's list",
        kept is not None and "Stone" not in kept,
        kept,
    )
    # Nickel too, so that the list is never the default alone
    listed = set(rock) | {"Nickel"}
    others = listed - {"Stone"}

    # --- the player's own list, all drills ---
    reply = ore(api, "add Bogus")
    check("!ore add: an unknown ore is refused", "is not a known ore" in reply, reply)
    for subtype in sorted(others):
        reply = ore(api, f"add {subtype}")
        check(f"!ore add {subtype}", f"Added {subtype}" in reply, reply)
    reply = ore(api, "auto")
    check("!ore auto: all drills", "All your ship drills now yeet" in reply, reply)
    reply = ore(api, "list")
    check("!ore list: the new list", your_list(reply) == listed, reply)
    check("!ore list: all drills yeet", "all your ship drills yeet" in reply, reply)
    voiding, _ = drill(api)
    state = after_quiet(api, [voiding])
    check(
        f"{first}: an untagged drill voids every listed ore",
        none_listed(state[voiding]),
        state,
    )

    # --- on the other node ---
    to_node(other)
    reply = ore(api, "list")
    check(f"{other}: the same list", your_list(reply) == listed, reply)
    check(f"{other}: still all drills", "all your ship drills yeet" in reply, reply)
    moved, _ = drill(api)
    state = after_quiet(api, [moved, voiding])
    check(
        f"{other}: a new drill voids every listed ore", none_listed(state[moved]), state
    )
    check(f"{other}: the drill that came along too", none_listed(state[voiding]), state)
    reply = ore(api, "remove Stone")
    check(
        "!ore remove Stone",
        "Removed Stone" in reply and your_list(reply) == others,
        reply,
    )
    fresh, _ = drill(api)
    got = stone(api, fresh)
    check(f"{other}: stone comes again", got, got)

    # --- back on the first node, which had read the list before ---
    to_node(first)
    time.sleep(6)  # the nodes read changed lists every 5 s
    reply = ore(api, "list")
    check(f"{first}: the list changed on {other}", your_list(reply) == others, reply)
    back, at = drill(api)
    got = stone(api, back)
    check(f"{first}: stone comes again", got, got)

    # --- !ore clean ---
    reply = ore(api, "add Stone")
    check("!ore add Stone", "Added Stone" in reply, reply)
    near = (at[0] + 50, at[1], at[2])  # beside the rock, clear of it
    api.character_teleport(*near)
    until(lambda: math.dist(api.get_character()["position"], near) < 10, 30)

    def clean() -> str:
        looking = api.character_look_at(*at, tolerance=1)
        print(f"at {api.get_character()['position']}, looking: {looking}", flush=True)
        time.sleep(5)  # the server gets the new head angles
        return ore(api, "clean")

    reply = clean()
    if "look at a grid" in reply:
        time.sleep(61)  # the cool-down starts on every try
        reply = clean()
    found = re.search(r"Yeeted ([\d,]+) ore", reply)
    check("!ore clean", found and int(found[1].replace(",", "")) > 0, reply)
    left = dug(api, back)
    check("!ore clean: no stone left", left is not None and "Stone" not in left, left)
    to_table(api)

    # --- admin ---
    reply = ore(api, "master add Stone")
    check("!ore master add refuses on a cluster", "Quasar" in reply, reply)
    reply = ore(api, "auto")
    check(
        f"!ore auto: back to drills named '{TAG}'",
        f"Only drills with '{TAG}'" in reply,
        reply,
    )


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    oreyeeter_folder = build_oreyeeter()
    os.environ[
        "CLUSTER_EXTRA_PLUGINS"
    ] += f",node:{probe_folder},node:{oreyeeter_folder}"
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-oreyeeter")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-OREYEETER",
        "SE-1014: OreYeeter's lists on a cluster",
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
