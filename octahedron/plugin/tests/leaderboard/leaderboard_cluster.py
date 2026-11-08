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

SE-1270, offline partitions: LBD gets a ship far from Alpha, in a partition of its own. Then every
partition goes offline (offline grace 0), LBC's ships at Alpha and LBD's far ship with them. Both
factions keep their grids' value and PCU on the board, counts after the partitions went offline,
and across a graceful stop and warm start of the cluster (the World Authority keeps the grids in
its world). LBD's partition wakes on node-1: its ship counts once. Deleted, it drops off the board.
Every board is printed as a BOARD line, so a run on the plugin before the fix shows the zeros.
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

ROOT = Path.home() / f"cluster-runs/leaderboard-{os.environ['OCT_TASK']}"
PLUGINS = ROOT / "plugins"
PLAN = ROOT / "probe-plan.txt"
OUT = ROOT / "probe-out.txt"
CONFIG = ROOT / "Octahedron.cfg"
NODE = "node-1"
PROBE_ID = "6D2F4A18-9B53-4E07-8C61-2E5B1F1573AA"
INTERVAL = 10
# LBD's ship, meters to the right of Alpha: far past the partition split distance
FAR = 40_000
# How long the World Authority counts a grid its node reports without: two intervals and two
# reads of the gateway's offline table
GONE = 2 * INTERVAL + 120


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
    cluster = bench.cluster
    cluster.kill()
    cluster.prepare(wipe=True)
    # The world's station mod, which the servers can't download
    for process in processes:
        rig.link_mods(bench.run_root / f"{process}-data" / "Mods", rig.STATION_MODS)
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
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
    # LBD has some cash, in the account the WA keeps
    cash = 12_345
    probe.run(f"!lbprobe pay LBD {cash}")
    rows = board()
    check(
        "LBD's cash is the account the WA keeps",
        cash > 0 and rows.get("LBD", {}).get("cash") == cash,
        f"{cash} {rows}",
    )

    reply = probe.run(f"!lbprobe ship LBC Alpha {lb.IRON}=1000")
    check("the probe built LBC's ship near Alpha", "SHIP " in reply, reply)
    ships = [int(m) for m in re.findall(r"SHIP (\d+)", reply)]
    names = [s.strip() for s in probe.run("!oct market stations").split(",")]
    check("7 stations", len(names) == 7, names)
    price = lb.prices(
        lambda station: probe.ask(f'!oct market show "{station}" all'),
        names,
    )
    value, pcu = lb.expected(price, iron=1000)
    rows = board()
    lbc, lbd = rows.get("LBC", {}), rows.get("LBD", {})
    check("the ship's value", lbc.get("grids") == value, f"{lbc} vs {value}")
    check("the ship's PCU", lbc.get("pcu") == pcu, f"{lbc} vs {pcu}")
    first = "LBC" if value > cash else "LBD"
    check(f"{first} ranks first", rows.get(first, {}).get("rank") == 1, rows)

    reply = probe.run("!lbprobe panel Alpha")
    check(
        "Alpha's panel in Monospace",
        re.match(r"PANEL 1 TEXT_AND_IMAGE \S*Monospace ", reply) is not None,
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
    ships += [
        int(m)
        for m in re.findall(
            r"SHIP (\d+)", probe.run(f"!lbprobe ship LBC Alpha {lb.IRON}=500 -200")
        )
    ]
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
    offline_partitions(bench, probe, check, log, ships, cash)


def offline_partitions(bench, probe, check, log, lbc_ships, cash) -> None:
    """SE-1270: the grids of offline partitions stay on the board"""
    cluster = bench.cluster
    processes = [NODE, "wa-1"]

    def board(label: str) -> dict[str, dict]:
        reply = probe.ask("!oct leaderboard", settle=4)
        print(f"BOARD {label}: {reply}", flush=True)
        bench.record(f"board-{label}", reply)
        return lb.rows_of(reply)

    def offline() -> dict[int, list[int]]:
        return {
            int(row["Partition"]): [int(x) for x in row.get("Members") or []]
            for row in cluster.get("/registry/partitions/offline").get("Partitions", [])
        }

    def wait(seconds: float) -> None:
        # Counts, on the node's own timer, while the test waits
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            probe.run("wait 5")

    reply = probe.run(f"!lbprobe ship LBD Alpha {lb.IRON}=2000 {FAR}")
    check("the probe built LBD's ship far from Alpha", "SHIP " in reply, reply)
    far = int(re.search(r"SHIP (\d+)", reply)[1]) if "SHIP " in reply else 0
    names = [s.strip() for s in probe.run("!oct market stations").split(",")]

    def expected() -> dict[str, tuple[int, int]]:
        """The ships at the market's prices now: they drift while the test runs"""
        price = lb.prices(
            lambda station: probe.ask(f'!oct market show "{station}" all'), names
        )
        return {
            "LBC": lb.expected(price, iron=1500, ships=2),
            "LBD": lb.expected(price, iron=2000),
        }

    def near(rows: dict[str, dict], want, tags=("LBC", "LBD")) -> bool:
        return all(
            rows.get(t, {}).get("pcu") == want[t][1]
            and abs(rows.get(t, {}).get("grids", 0) - want[t][0]) <= want[t][0] // 20
            for t in tags
        )

    deadline = time.monotonic() + 6 * INTERVAL
    rows = board("online")
    while not rows.get("LBD", {}).get("grids") and time.monotonic() < deadline:
        probe.run("wait 3")
        rows = board("online")
    want = expected()
    check(
        "online: both factions' grids counted, within 5% of the prices now",
        near(rows, want),
        f"{rows} vs {want}",
    )
    online = rows
    # From here on the grids count as last counted: as on this board
    want = {t: (r["grids"], r["pcu"]) for t, r in online.items()}

    def same(rows: dict[str, dict], tags=("LBC", "LBD")) -> bool:
        return all(
            (rows.get(t, {}).get("grids"), rows.get(t, {}).get("pcu")) == want[t]
            for t in tags
        )

    # Every partition goes offline: nobody is near any of them
    cluster.handover_config(
        {
            "offline.enabled": "true",
            "offline.minOnlineSeconds": "0",
            "offline.graceSeconds": "0",
        }
    )
    grids = [far, *lbc_ships]
    deadline = time.monotonic() + 420
    members: set[int] = set()
    while time.monotonic() < deadline:
        members = {m for ms in offline().values() for m in ms}
        if all(g in members for g in grids):
            break
        probe.run("wait 5")
    check(
        "the ships' partitions went offline",
        all(g in members for g in grids),
        f"{grids} in {sorted(members)[:40]}",
    )
    on_node = probe.run("!lbprobe grids LBD") + " | " + probe.run("!lbprobe grids LBC")
    check("node-1 has none of the ships", "GRID " not in on_node, on_node)
    # Past the World Authority's grace for grids a node stopped reporting
    wait(GONE + 15)
    rows = board("offline")
    check(
        "offline: LBC's ships still counted",
        same(rows, ["LBC"]),
        f"{rows.get('LBC')} vs {want['LBC']}",
    )
    check(
        "offline: LBD's ship still counted",
        same(rows, ["LBD"]),
        f"{rows.get('LBD')} vs {want['LBD']}",
    )
    check(
        "offline: the same ranks and totals as online",
        {t: (r["rank"], r["total"]) for t, r in rows.items()}
        == {t: (r["rank"], r["total"]) for t, r in online.items()},
        f"{rows} vs {online}",
    )
    ranked = re.findall(
        r"ranking (\d+) grids, (\d+) in offline partitions", log("wa-1")
    )
    check(
        "the WA ranks the offline grids",
        ranked and int(ranked[-1][1]) >= len(grids),
        ranked[-3:],
    )

    # A graceful stop and warm start: the World Authority loads the grids from its world
    check("graceful stop", cluster.stop(grace=240))
    PLAN.write_text("")
    OUT.write_text("")
    probe.step = 0
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    deadline = time.monotonic() + 180
    rows = board("warm-start")
    while not same(rows) and time.monotonic() < deadline:
        probe.run("wait 5")
        rows = board("warm-start")
    check("after a warm start: both factions' grids counted", same(rows), f"{rows}")
    # The game writes its log in a while
    loaded = re.compile(r"Leaderboard: (\d+) grids loaded from the world")
    deadline = time.monotonic() + 60
    while not loaded.search(log("wa-1")) and time.monotonic() < deadline:
        probe.run("wait 5")
    found = loaded.search(log("wa-1"))
    check(
        "the WA loaded the grids from its world",
        found and int(found[1]) >= len(grids),
        found and found[0],
    )
    wait(GONE + 15)
    rows = board("warm-start-later")
    check("and still, counts later", same(rows), f"{rows}")
    check("cash unchanged", rows.get("LBD", {}).get("cash") == cash, rows.get("LBD"))

    # LBD's partition wakes on node-1 and stays: the ship counts once, as node-1 counts it at
    # the market's prices now
    cluster.handover_config({"offline.enabled": "false"})

    def woken() -> bool:
        for partition, ms in offline().items():
            if far in ms:
                print(
                    cluster.post(
                        f"/registry/partitions/offline/load?partition={partition}&reason=test"
                    ),
                    flush=True,
                )
        return f"GRID {far} " in probe.run("!lbprobe grids LBD")

    deadline = time.monotonic() + 240
    while not woken() and time.monotonic() < deadline:
        probe.run("wait 5")
    check(
        "LBD's partition woke on node-1",
        f"GRID {far} " in probe.run("!lbprobe grids LBD"),
    )
    wait(GONE + 15)
    rows = board("woken")
    now = expected()
    check(
        "woken: LBD's ship counted once, within 5% of the prices now",
        near(rows, now, ["LBD"]),
        f"{rows.get('LBD')} vs {now['LBD']}",
    )

    # Deleted on its node: gone from the board once the node has reported without it
    reply = probe.run(f"!lbprobe delete {far}")
    check("LBD's ship deleted", f"DELETED {far}" in reply, reply)
    wait(GONE + 15)
    rows = board("deleted")
    check(
        "deleted: LBD has no grids",
        rows.get("LBD", {}).get("grids") == 0 and rows.get("LBD", {}).get("pcu") == 0,
        rows.get("LBD"),
    )
    check("deleted: LBC unchanged", same(rows, ["LBC"]), rows.get("LBC"))


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
