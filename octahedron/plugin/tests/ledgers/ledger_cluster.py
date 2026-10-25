"""The station ledgers on a local test cluster, run through the cluster's own scenario bench
(CometWorks/cluster Test/): the World Authority keeps them, a regular node changes and reads them
through it, and they come back after a cluster restart.

    OCT_TASK=SE-682 python3 tests/ledgers/ledger_cluster.py CLUSTER_TEST_DIR     (from plugin/)

CLUSTER_TEST_DIR is the Test/ folder of a CometWorks/cluster checkout at the build under test, with
its host.env (CLUSTER_PACKAGE, MAGNETAR_HOME, test root, port base) and `uv sync` done; run this with
that folder's virtualenv python. One regular node + WA, no client. The world is the Octahedron world as
tests/rig.py prepares it (no mods, no asteroids). The Octahedron plugin, built from this working copy
against MAGNETAR_HOME's PluginSdk, runs on both; the probe (tests/ledgers/probe) on node-1 runs the
admin commands as the server console. The owners are the world's NPC identity "Octahedron Authority"
and the Space Pirates faction (SPRT): a cluster without a client has no player.

Checks: node-1's changes are applied on the WA only (its log, not node-1's); refusals (limit,
whole units, too much) come back from the WA; reads on node-1 show what the WA holds; after a
graceful stop and a warm start the WA's global save brings the ledgers back.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[2]
TEST_DIR = Path(sys.argv[1]).resolve()
sys.path[:0] = [str(TEST_DIR), str(TEST_DIR.parent / "Cli"), str(PLUGIN / "tests")]

import rig  # noqa: E402  (the world copy; claims the task's slot, starts nothing)

ROOT = Path.home() / "cluster-runs/ledgers-1553"
PLUGINS = ROOT / "plugins"
PLAN = ROOT / "probe-plan.txt"
OUT = ROOT / "probe-out.txt"
NODE = "node-1"
OWNER = '"Octahedron Authority"'
PROBE_ID = "3B8E1F52-6C47-4D0A-9E21-5A7C0D1553AA"


def build() -> None:
    """Octahedron and the probe as local plugins (DLL + manifest per folder) for CLUSTER_EXTRA_PLUGINS"""
    magnetar = os.environ["MAGNETAR_HOME"]
    commit = subprocess.check_output(
        ["git", "-C", str(PLUGIN), "rev-parse", "HEAD"], text=True
    ).strip()
    for project, name, plugin_id in (
        ("ServerPlugin/ServerPlugin.csproj", "Octahedron", rig.PLUGIN_ID),
        ("tests/ledgers/probe/LedgerProbe.csproj", "LedgerProbe", PROBE_ID),
    ):
        out = PLUGINS / ".build" / name
        subprocess.run(
            ["nice", "-n", "19", "dotnet", "build", str(PLUGIN / project), "-c", "Release",
             "--nologo", "-v", "q", f"-p:Magnetar={magnetar}", "-o", str(out)],
            check=True,
        )  # fmt: skip
        folder = PLUGINS / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / f"{name}.dll").write_bytes((out / f"{name}.dll").read_bytes())
        (folder / f"{name}.xml").write_text(
            '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
            f"<Id>{plugin_id}</Id><FriendlyName>{name}</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
            f"<Commit>{commit}</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
            encoding="utf-8",
        )


def prepare() -> Path:
    ROOT.mkdir(parents=True, exist_ok=True)
    build()
    rig.prepare_world()
    PLAN.write_text("")
    OUT.write_text("")
    extra = [
        e for e in os.environ.get("CLUSTER_EXTRA_PLUGINS", "").split(",") if e.strip()
    ]
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(
        extra + [str(PLUGINS / "Octahedron"), f"node:{PLUGINS / 'LedgerProbe'}"]
    )
    os.environ.update(
        LEDGERPROBE_PLAN=str(PLAN),
        LEDGERPROBE_OUT=str(OUT),
        LEDGERPROBE_NODE=NODE,
        OCTAHEDRON_CONFIG=str(rig.CONFIG_SOURCE),
        OCTAHEDRON_ENCOUNTERS=str(PLUGIN / "Encounters"),
    )
    return rig.WORLD


class Probe:
    """Sends one command line at a time to the probe and waits for its replies"""

    def __init__(self) -> None:
        self.step = 0

    def replies(self, step: int) -> list[str]:
        lines = OUT.read_text(errors="replace").splitlines()
        return [l.split("\t", 1)[1] for l in lines if l.split("\t", 1)[0] == str(step)]

    def run(self, line: str, timeout: float = 120, settle: float = 2) -> list[str]:
        step = self.step
        self.step += 1
        with PLAN.open("a") as plan:
            plan.write(line + "\n")
        deadline = time.monotonic() + timeout
        while not self.replies(step):
            if time.monotonic() > deadline:
                return []
            time.sleep(1)
        time.sleep(settle)
        return self.replies(step)


def run(bench) -> None:
    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in bench.run_root.glob(f"{process}*.log")
            if p.is_file()
        ) + "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    probe = Probe()

    def expect(line: str, needle: str, label: str) -> None:
        replies = probe.run(line)
        check(
            label, any(needle in r for r in replies), " | ".join(replies) or "no reply"
        )

    def shows(line: str, needles: list[str], label: str) -> None:
        replies = probe.run(line, settle=4)
        text = "\n".join(replies)
        missing = [n for n in needles if n not in text]
        check(label, not missing, f"missing {missing} in: {text}" if missing else "")

    def final(when: str) -> None:
        shows(
            f"!oct ledger show Alpha {OWNER}",
            ["Octahedron Authority (P", ": 1.3 t", "Ingot/Iron 700", "Component/SteelPlate 30",
             "reserved build-1: Component/SteelPlate 10"],
            f"{when}: the NPC's Alpha ledger on node-1",
        )  # fmt: skip
        shows(
            "!oct ledger show Alpha faction:SPRT",
            ["SPRT (F", "Ingot/Iron 300", "Component/SteelPlate 10"],
            f"{when}: the faction's Alpha ledger",
        )
        shows(f"!oct ledger show Beta {OWNER}", ["Ingot/Iron 5"], f"{when}: Beta")

    processes = [NODE, "wa-1"]
    bench.start_cluster(processes)
    check(
        "the WA serves the ledgers",
        "Serving the station ledgers on the World Authority" in log("wa-1"),
    )

    owner = OWNER
    expect("!oct ledger stations", "Alpha Station", "node-1 lists the stations")
    expect(
        f"!oct ledger deposit Alpha {owner} Ingot/Iron 1000",
        "done.",
        "deposit through the WA",
    )
    expect(
        f"!oct ledger deposit Alpha {owner} Component/SteelPlate 1.5",
        "whole units",
        "half a plate refused",
    )
    expect(
        f"!oct ledger deposit Alpha {owner} Ingot/Iron 99000.5",
        "over the limit of 100 t",
        "the limit",
    )
    expect(
        f"!oct ledger withdraw Alpha {owner} Ingot/Iron 2000",
        "Only 1,000",
        "withdraw too much",
    )
    expect(
        f"!oct ledger deposit Alpha {owner} Component/SteelPlate 50", "done.", "plates"
    )
    expect(
        f"!oct ledger transfer Alpha {owner} faction:SPRT Component/SteelPlate 10",
        "done.",
        "to the faction",
    )
    expect(
        f"!oct ledger reserve Alpha {owner} listing-1 Ingot/Iron 400",
        "done.",
        "list iron",
    )
    expect(
        f"!oct ledger release Alpha {owner} listing-1 Ingot/Iron 100",
        "done.",
        "release part",
    )
    expect(
        f"!oct ledger give Alpha {owner} listing-1 faction:SPRT",
        "done.",
        "sell the rest",
    )
    expect(
        f"!oct ledger fill Alpha {owner} build-1 Component/SteelPlate 10000",
        "done.",
        "build plates",
    )
    expect(
        f"!oct ledger take Alpha {owner} build-1 Component/SteelPlate 9990",
        "done.",
        "build uses them",
    )
    expect(
        f"!oct ledger withdraw Alpha {owner} Component/SteelPlate 10",
        "done.",
        "plates out",
    )
    expect(f"!oct ledger deposit Beta {owner} Ingot/Iron 5", "done.", "iron at Beta")
    final("before the restart")

    wa, node = log("wa-1"), log(NODE)
    applied_wa = len(
        re.findall(
            r"Ledger (Deposit|Withdraw|Transfer|Reserve|Fill|Release|Give|Take) at", wa
        )
    )
    applied_node = len(
        re.findall(
            r"Ledger (Deposit|Withdraw|Transfer|Reserve|Fill|Release|Give|Take) at",
            node,
        )
    )
    check("the WA applied the 10 changes", applied_wa == 10, f"WA {applied_wa}")
    check("node-1 applied none itself", applied_node == 0, f"node-1 {applied_node}")
    bench.record(
        "wa-ledger-lines", "\n".join(l for l in wa.splitlines() if "Ledger" in l)
    )

    stopped = bench.cluster.stop(grace=240)
    check("graceful stop", stopped)
    PLAN.write_text("")
    OUT.write_text("")
    probe.step = 0
    bench.cluster.start(processes)
    bench.cluster.wait_ready(nodes=processes)
    final("after the restart")
    expect(
        f"!oct ledger release Alpha {owner} build-1",
        "done.",
        "leftovers back after the restart",
    )
    shows(
        f"!oct ledger show Alpha {owner}",
        ["Component/SteelPlate 40"],
        "leftovers in the free stock",
    )


run.regular_nodes = 1


def main() -> int:
    world = prepare()
    import bench as bench_module

    run.world_template = world
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-ledgers")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-LEDGERS",
        "SE-682: station ledgers kept by the World Authority",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    passed = sum(1 for c in outcome.checks if c.passed)
    print(f"{passed}/{len(outcome.checks)} ledger cluster checks passed")
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
