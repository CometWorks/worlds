"""The faction leaderboard on a local test cluster, run through the cluster's own scenario bench
(CometWorks/cluster Test/): node-1 counts its grids, gets the prices from the World Authority and
the ranked factions back, and writes the station panels; the World Authority only ranks.

    OCT_TASK=SE-698 python3 tests/leaderboard/leaderboard_cluster.py CLUSTER_TEST_DIR   (from plugin/)

CLUSTER_TEST_DIR is the Test/ folder of a CometWorks/cluster checkout with its host settings
(CLUSTER_PACKAGE, MAGNETAR_HOME, test root, port base, from the environment or host.env) and
`uv sync` done; run this with that folder's virtualenv python. One regular node + WA, no client.
The world is the Octahedron world as tests/rig.py prepares it. The Octahedron plugin, built from
this working copy against MAGNETAR_HOME's PluginSdk, runs on both; the probe
(tests/leaderboard/probe) on node-1 makes two factions of fresh identities, LBC and LBD, ships of
LBC's near Alpha Station, and runs the admin commands as the server console. LBD sells iron to OCT
for some cash.

Checks: the WA serves the leaderboard and counts no grids; both factions ranked with their
accounts, the NPC factions and OCT not; the ship's value and PCU as worked out from the game's
block definitions and OCT's prices (test_leaderboard.py); money held for a market order counted;
Alpha's 8 panels written on node-1 in Monospace; a second ship, and the panel refreshes with both.
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
os.environ.setdefault("OCT_TASK", "SE-698")

import hostenv  # noqa: E402,F401  (loads host.env: MAGNETAR_HOME for the build)
import rig  # noqa: E402  (the world copy; claims the task's slot, starts nothing)
import test_leaderboard as lb  # noqa: E402

ROOT = Path.home() / "cluster-runs/leaderboard-1573"
PLUGINS = ROOT / "plugins"
PLAN = ROOT / "probe-plan.txt"
OUT = ROOT / "probe-out.txt"
CONFIG = ROOT / "Octahedron.cfg"
NODE = "node-1"
PROBE_ID = "6D2F4A18-9B53-4E07-8C61-2E5B1F1573AA"
INTERVAL = 10


def build() -> None:
    """Octahedron and the probe as local plugins (DLL + manifest per folder) for CLUSTER_EXTRA_PLUGINS"""
    magnetar = os.environ["MAGNETAR_HOME"]
    commit = subprocess.check_output(
        ["git", "-C", str(PLUGIN), "rev-parse", "HEAD"], text=True
    ).strip()
    for project, name, plugin_id in (
        ("ServerPlugin/ServerPlugin.csproj", "Octahedron", rig.PLUGIN_ID),
        (
            "tests/leaderboard/probe/LeaderboardProbe.csproj",
            "LeaderboardProbe",
            PROBE_ID,
        ),
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
    CONFIG.write_text(
        rig.CONFIG_SOURCE.read_text(encoding="utf-8").replace(
            "</OctahedronConfig>",
            f"  <LeaderboardIntervalSeconds>{INTERVAL}</LeaderboardIntervalSeconds>\n"
            "</OctahedronConfig>",
        ),
        encoding="utf-8",
    )
    extra = [
        e for e in os.environ.get("CLUSTER_EXTRA_PLUGINS", "").split(",") if e.strip()
    ]
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(
        extra + [str(PLUGINS / "Octahedron"), f"node:{PLUGINS / 'LeaderboardProbe'}"]
    )
    os.environ.update(
        LBPROBE_PLAN=str(PLAN),
        LBPROBE_OUT=str(OUT),
        LBPROBE_NODE=NODE,
        OCTAHEDRON_CONFIG=str(CONFIG),
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

    def run(self, line: str, timeout: float = 120, settle: float = 3) -> str:
        if line.startswith("wait "):
            # The probe waits without a step of its own
            with PLAN.open("a") as plan:
                plan.write(line + "\n")
            time.sleep(float(line.split()[1]) + 2)
            return ""
        step = self.step
        self.step += 1
        with PLAN.open("a") as plan:
            plan.write(line + "\n")
        deadline = time.monotonic() + timeout
        while not self.replies(step):
            if time.monotonic() > deadline:
                return ""
            time.sleep(1)
        time.sleep(settle)
        return " | ".join(self.replies(step))

    def ask(self, line: str, settle: float = 3) -> str:
        """run, again when the World Authority turned the request away for a moment (Fenced)"""
        for _ in range(4):
            reply = self.run(line, settle=settle)
            if "did not answer" not in reply:
                return reply
            self.run("wait 10")
        return reply


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

    def board() -> dict[str, dict]:
        reply = probe.ask("!oct leaderboard", settle=4)
        rows = lb.rows_of(reply)
        bench.record(f"board-{probe.step}", reply)
        return rows

    processes = [NODE, "wa-1"]
    bench.start_cluster(processes)
    check(
        "the WA ranks the leaderboard",
        "Ranking the leaderboard on the World Authority" in log("wa-1"),
    )

    # A node asks the WA for a new faction; it comes back a moment later
    for tag in ("LBC", "LBD"):
        probe.run(f"!lbprobe faction {tag}")
    deadline = time.monotonic() + 120
    rows = board()
    while set(rows) != {"LBC", "LBD"} and time.monotonic() < deadline:
        probe.run("wait 5")
        rows = board()
    check("only the players' factions", set(rows) == {"LBC", "LBD"}, rows)
    lbc, lbd = rows.get("LBC", {}), rows.get("LBD", {})
    check("new factions have 0 SC", lbc.get("cash") == lbd.get("cash") == 0, rows)
    check("no grids yet", lbc.get("grids") == 0 and lbc.get("pcu") == 0, lbc)
    # LBD sells iron to OCT for some cash, through the WA
    probe.run(f"!oct ledger deposit Alpha faction:LBD {lb.IRON} 500")
    probe.run(f"!oct market do sell Alpha faction:LBD {lb.IRON} 500")
    reply = probe.ask("!oct market show Alpha faction:LBD")
    m = re.search(r"balance ([\d,]+) SC", reply)
    cash = lb.number(m[1]) if m else -1
    rows = board()
    check(
        "LBD's cash is the account the WA keeps",
        cash > 0 and rows.get("LBD", {}).get("cash") == cash,
        f"{cash} {rows}",
    )

    reply = probe.run(f"!lbprobe ship LBC Alpha {lb.IRON}=1000")
    check("the probe built LBC's ship near Alpha", "SHIP " in reply, reply)
    names = [s.strip() for s in probe.run("!oct ledger stations").split(",")]
    check("7 stations", len(names) == 7, names)
    price = lb.prices(
        lambda station: probe.ask(f'!oct market show "{station}" faction:LBC all'),
        names,
    )
    value, pcu = lb.expected(price, iron=1000)
    rows = board()
    lbc, lbd = rows.get("LBC", {}), rows.get("LBD", {})
    check("the ship's value", lbc.get("grids") == value, f"{lbc} vs {value}")
    check("the ship's PCU", lbc.get("pcu") == pcu, f"{lbc} vs {pcu}")
    first = "LBC" if value > cash else "LBD"
    check(f"{first} ranks first", rows.get(first, {}).get("rank") == 1, rows)

    reply = probe.run("!oct market do order Alpha faction:LBD Ingot/Gold 1000 1")
    after = re.search(
        r"balance ([\d,]+) SC", probe.ask("!oct market show Alpha faction:LBD")
    )
    rows = board()
    check(
        "money held for an order still counts",
        after
        and lb.number(after[1]) == cash - 1000
        and rows.get("LBD", {}).get("cash") == cash,
        f"{reply} {after and after[1]} -> {rows}",
    )

    reply = probe.run("!lbprobe panel Alpha")
    check(
        "Alpha's 8 panels in Monospace",
        re.match(r"PANEL 8 TEXT_AND_IMAGE \S*Monospace ", reply) is not None,
        reply,
    )
    rank = rows.get("LBC", {}).get("rank", 0)
    line = next((l for l in reply.split(" | ") if " LBC " in l), "")
    check(
        "LBC's row on the panel",
        line.startswith(f"{rank:>2}  LBC") and line.endswith(f"{value:,} {pcu:>7,}"),
        reply,
    )

    # A second ship: the panel refreshes on its own with both
    both, both_pcu = lb.expected(price, iron=1500, ships=2)
    bench.record("grids-before-second", probe.run("!lbprobe grids LBC"))
    probe.run(f"!lbprobe ship LBC Alpha {lb.IRON}=500 -200")
    ending = f"{both:,} {both_pcu:>7,}"
    deadline = time.monotonic() + 6 * INTERVAL
    line = ""
    while not line.endswith(ending) and time.monotonic() < deadline:
        probe.run("wait 3")
        reply = probe.run("!lbprobe panel Alpha")
        line = next((l for l in reply.split(" | ") if " LBC " in l), "")
    check(
        "the panel refreshed with both ships",
        line.endswith(ending),
        f"{line!r} vs {ending}; {probe.run('!lbprobe grids LBC')}",
    )

    wa, node = log("wa-1"), log(NODE)
    counted = [tuple(map(int, c)) for c in lb.COUNTED.findall(node)]
    check("node-1 counts its grids", counted and counted[-1][0] > 0, counted[-3:])
    check("the WA counts none", "Leaderboard: counted" not in wa)
    written = re.findall(r"factions on (\d+) of (\d+) panels", node)
    check(
        "every panel node-1 has written",
        written and all(int(a) == int(b) > 0 for a, b in written),
        written[-3:],
    )
    # Before the WA is up a node's first count finds no prices and retries
    check("the leaderboard never failed", "leaderboard failed" not in wa + node)
    bench.record(
        "leaderboard-lines",
        "\n".join(l for l in (wa + node).splitlines() if "eaderboard" in l)[-20000:],
    )


run.regular_nodes = 1


def main() -> int:
    world = prepare()
    import bench as bench_module

    run.world_template = world
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-leaderboard")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-LEADERBOARD",
        "SE-698: faction leaderboard counted on the nodes, ranked by the World Authority",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    passed = sum(1 for c in outcome.checks if c.passed)
    print(f"{passed}/{len(outcome.checks)} leaderboard cluster checks passed")
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
