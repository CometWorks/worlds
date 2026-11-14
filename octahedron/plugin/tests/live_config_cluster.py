"""Live config on a cluster (SE-1474): an admin's !oct config set changes a market price on the World
Authority, every node and the hot spare without a restart, and the value outlasts a node kill, a World
Authority kill and a cluster restart. Same bench and world as world_config_cluster.py, with the
world's octahedron.cfg, but no client: the console probe (tests/console-probe) runs on every process,
and !oct market stores <item> says what the Store blocks of the stations on each node ask and bid.

The set and the reset go to node-2 only (the probe's "@node-2" lines), and the plan is emptied before
the cluster restart, so no process that restarts runs them again: what it shows after its start came
from live.cfg in the shared storage.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/live_config_cluster.py CLUSTER_TEST_DIR     (from plugin/)
"""

from __future__ import annotations

import json
import re
import sys
import time
from datetime import datetime, timezone

import wa_economy_cluster as wa
from wa_economy_cluster import CONFIG, PLUGIN, bench_module, link_mods

ITEM = "Component/SteelPlate"
PRICES = "Ore/Ice=0.01, Component/SteelPlate=1000"
# The market tick, and the 15 s each process takes to read live.cfg on top
LIMIT = 75
HIGH = 500
STORE = re.compile(rf"STORE (\S+) (\S+) {re.escape(ITEM)} ask (\d+) bid (\d+)")


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()
    # The WA, the two regular nodes and the hot spare
    processes = [node.node_id for node in cluster.config.nodes]
    every = len(processes)

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def answers_to(step: int) -> list[str]:
        """Each process's replies to the step, from its own file"""
        return [
            l.split("\t", 1)[1]
            for f in sorted(wa.lc.OUT.parent.glob(wa.lc.OUT.name + ".*"))
            for l in f.read_text(errors="replace").splitlines()
            if l.split("\t", 1)[0] == str(step)
        ]

    def everywhere(line: str, count: int, timeout: float = 120) -> list[str]:
        """Runs a line on every process with the probe; the replies once there are count of them"""
        step = probe.step
        probe.step += 1
        with wa.lc.PLAN.open("a") as plan:
            plan.write(line + "\n")
        deadline = time.monotonic() + timeout
        while len(answers_to(step)) < count and time.monotonic() < deadline:
            time.sleep(1)
        time.sleep(2)
        answers = answers_to(step)
        print(f"{line} -> {answers}", flush=True)
        return answers

    def stores() -> dict[tuple[str, str, str], int]:
        """The item's prices at the Store blocks of the stations on each node, per (node, station, ask or
        bid): only the offers with stock and the orders with demand, the others show 0
        """
        replies = everywhere(f"!oct market stores {ITEM}", every, 60)
        found = {}
        for reply in replies:
            for node, station, ask, bid in STORE.findall(reply):
                for side, price in (("ask", int(ask)), ("bid", int(bid))):
                    if price > 0:
                        found[(node, station, side)] = price
        return found

    def until(predicate, timeout: float) -> tuple[float | None, dict]:
        """Seconds until the stores satisfy the predicate, or None, and what they last showed"""
        start = time.time()
        while True:
            shown = stores()
            if predicate(shown):
                return time.time() - start, shown
            if time.time() - start > timeout:
                return None, shown
            time.sleep(5)

    # Stations move between the nodes as the balancer likes: whichever node has them must show it
    def raised(shown) -> bool:
        return bool(shown) and all(ask >= HIGH for ask in shown.values())

    def lowered(shown) -> bool:
        return bool(shown) and all(ask < HIGH for ask in shown.values())

    def run_live(label: str) -> None:
        replies = everywhere("!oct config show MarketPrices", every)
        check(
            f"{label}: every process runs it, the deployed config object unchanged",
            len(
                [
                    r
                    for r in replies
                    if f"MarketPrices = {PRICES}, set live; deployed: Ore/Ice=0.01" in r
                ]
            )
            == every,
            replies,
        )

    def shared() -> str:
        files = list((cluster.config.root / "shared").rglob("live.cfg"))
        return files[0].read_text() if files else ""

    cluster.kill()
    cluster.prepare(wipe=True)
    for stale in (cluster.config.root / "shared").rglob("live.cfg"):
        stale.unlink()
    for process in processes:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    served = wa.wait_until(
        lambda: "Serving the station markets on the World Authority" in log("wa-1"),
        180,
        "the market on the WA",
    )
    check("the WA serves the market", served)

    # The stations' partitions are shared out among the nodes at the start
    _, before = until(lowered, 300)
    check(f"the Stores trade {ITEM} under {HIGH} SC before", lowered(before), before)

    reply = " ".join(everywhere("@node-2 !oct config set MarketPrice 5", 1))
    check("an unknown option is refused", "No option MarketPrice" in reply, reply)
    reply = " ".join(everywhere("@node-2 !oct config set MarketTaxPercent -5", 1))
    check("a value out of range is refused", "out of its range" in reply, reply)
    reply = " ".join(everywhere("@node-2 !oct config set MarketTaxPercent lots", 1))
    check("a value of the wrong type is refused", "not a Int32" in reply, reply)
    check(
        "nothing written for the refused values",
        shared().count("<Market") == 0,
        shared(),
    )

    reply = " ".join(everywhere(f"@node-2 !oct config set MarketPrices {PRICES}", 1))
    check(
        "node-2 sets MarketPrices live", "on every process within 15 s" in reply, reply
    )
    took, shown = until(raised, LIMIT)
    check(
        f"the Stores' prices are {HIGH}+ SC within {LIMIT} s, no restart",
        took is not None,
        f"{took and round(took)} s: {shown}",
    )
    run_live("the WA, both nodes and the hot spare")
    text = shared()
    check(
        "live.cfg in the shared storage has it",
        f"<MarketPrices>{PRICES}</MarketPrices>" in text,
        text,
    )
    bench.record("live-cfg", text)

    # The set and the reset run on node-2 only, so node-1 and the WA never run them again
    applied = log("node-1").count("Live config: MarketPrices = " + PRICES)
    cluster.restart_node("node-1")
    cluster.wait_ready(nodes=["node-1"])
    check(
        "node-1 applied it from live.cfg at its start",
        log("node-1").count("Live config: MarketPrices = " + PRICES) > applied,
    )
    took, shown = until(raised, 240)
    check(
        "after a node-1 kill, the Stores still have its prices", took is not None, shown
    )

    cluster.restart_node("wa-1")
    cluster.wait_ready(nodes=["wa-1"])
    took, shown = until(raised, 240)
    check("after a WA kill, the Stores still have its prices", took is not None, shown)
    run_live("after the node-1 and WA kills")

    # The whole cluster down and up, the plan emptied meanwhile: no process sets it again
    check("graceful stop", cluster.stop(grace=240))
    wa.lc.PLAN.write_text("")
    for old in wa.lc.OUT.parent.glob(wa.lc.OUT.name + ".*"):
        old.unlink()
    probe.step = 0
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    took, shown = until(raised, 300)
    check(
        "after a cluster restart, the Stores still have its prices",
        took is not None,
        shown,
    )
    run_live("after a cluster restart")

    reply = " ".join(everywhere("@node-2 !oct config reset MarketPrices", 1))
    check("node-2 resets it", "goes back to" in reply, reply)
    took, shown = until(lowered, LIMIT)
    check(
        f"the Stores' prices are back under {HIGH} SC within {LIMIT} s",
        took is not None,
        f"{took and round(took)} s: {shown}",
    )
    replies = everywhere("!oct config show MarketPrices", every)
    check(
        "every process runs the deployed value again",
        len([r for r in replies if r.strip() == "MarketPrices = Ore/Ice=0.01"])
        == every,
        replies,
    )
    check("live.cfg is empty", "<Market" not in shared(), shared())
    check("graceful stop at the end", cluster.stop(grace=240))


run.spare_nodes = 1


def main() -> int:
    run.world_template = wa.prepare()
    CONFIG.write_text(
        (PLUGIN.parent / "config/octahedron.cfg").read_text(encoding="utf-8"),
        encoding="utf-8",
    )
    # The probe on every process, its commands as the console of each
    wa.os.environ["CONSOLEPROBE_NODE"] = "*"
    for stale in wa.lc.OUT.parent.glob(wa.lc.OUT.name + ".*"):
        stale.unlink()
    wa.os.environ["CLUSTER_EXTRA_PLUGINS"] = wa.os.environ[
        "CLUSTER_EXTRA_PLUGINS"
    ].replace(
        f"wa:{wa.lc.PLUGINS / 'ConsoleProbe'}", str(wa.lc.PLUGINS / "ConsoleProbe")
    )
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-live-config"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-LIVE-CONFIG",
        "SE-1474: market settings changed live on every process of a cluster",
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
