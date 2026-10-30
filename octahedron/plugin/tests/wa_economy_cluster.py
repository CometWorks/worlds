"""The money rules (SE-695) and the free-build cool-down (SE-696) on a local test cluster, run through
the cluster's own scenario bench (CometWorks/cluster Test/): both keep their state on the World Authority.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/wa_economy_cluster.py CLUSTER_TEST_DIR     (from plugin/)

CLUSTER_TEST_DIR is the Test/ folder of a CometWorks/cluster checkout with its host.env (CLUSTER_PACKAGE,
test root, port base) and `uv sync` done; run this with that folder's virtualenv python. host.env needs
MAGNETAR_HOME at a Magnetar with ServerCommands.Execute (2.4.3.8 works) and, when that differs from what
other benches on the host use, a MAGNETAR_GITHUB_CACHE of its own: Magnetars of different versions recompile
each other's plugins in a shared cache, and a node can then fail at start. The zstd command reads the
global save. WA + 2 regular
nodes + one headless admin client, the Octahedron world as tests/test_shipyard_order.py prepares it (no
asteroids, ToolCore and OmniArc staged, a 10 block blueprint on every table). Octahedron runs on all
three, Multigrid Projector (the hub's cached build) on the nodes, and the ledger probe
(tests/ledgers/probe) on the WA, because !oct payday and !oct taxday only run there.

Money: the WA cuts OCT's 1e9, a new account has 25,000 SC, a node refuses !oct payday, the tax and the
payout on the WA count the client through the merged online list and reach its balance, nodes log nothing
from Money, the state is in the WA's save and comes back.

Cool-down: a free ship ordered and completed on one node starts it. It then refuses on the other (the live
broadcast), on the other restarted (the WA's list), after a graceful cluster stop and warm start (the global
save), and on the other restarted once more after a WA kill. It runs at 60 minutes so that it lasts through
the restarts. Last, FreeBuildCooldownMinutes is lowered so
that about 2 minutes are left: refused, then free right after they pass. So only 2 minutes are waited.

Run on polaris on 2026-10-30: 25/25 checks in 16 minutes.
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
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[1]
TEST_DIR = Path(sys.argv[1]).resolve()
sys.path[:0] = [
    str(TEST_DIR),
    str(TEST_DIR.parent / "Cli"),
    str(PLUGIN / "tests"),
    str(PLUGIN / "tests/ledgers"),
]
os.environ.setdefault("OCT_TASK", "SE-695")

import bench as bench_module  # noqa: E402  (loads host.env, sets MAGNETAR_HOME)
from clients import Client  # noqa: E402
from cluster import ClusterError, wait_until  # noqa: E402
from scenarios_core import (  # noqa: E402
    CLIENT_ID,
    _client_partition,
    _handover_url,
    _partitions,
)

import rig  # noqa: E402
import ledger_cluster as lc  # noqa: E402  (the probe's build and its plan file protocol)
import test_client as tc  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402

ROOT = Path.home() / "cluster-runs/wa-economy"
lc.PLUGINS = ROOT / "plugins"
lc.PLAN = ROOT / "probe-plan.txt"
lc.OUT = ROOT / "probe-out.txt"
CONFIG = rig.SERVER_DATA / "Octahedron.cfg"
MGP_CACHE = Path.home() / ".cache/Magnetar/Legacy/GitHub"
SHIP = "Order Test"
COOLDOWN = "next free ship is in"
MONEY_LOG = re.compile(r"Money( rules| hour| tax| day|:)")
TAX = re.compile(r"Money tax \S+: ([\d,]+) SC from (\d+) players")
HOUR = re.compile(r"Money hour .*? to (\d+) active players at ([\d,]+) SC each")
STATE = re.compile(r"day=\d{4}-\d\d-\d\d;hour=[^;]*;held=(\d+);.*?;seen=\d+:\d+")


def number(text: str) -> int:
    return int(text.replace(",", ""))


def set_config(**values) -> None:
    text = CONFIG.read_text(encoding="utf-8")
    for key, value in values.items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        if not count:
            text = text.replace(
                "</OctahedronConfig>", f"  <{key}>{value}</{key}>\n</OctahedronConfig>"
            )
    CONFIG.write_text(text, encoding="utf-8")


def link_mods(folder: Path) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        if not link.exists():
            link.symlink_to(pl.WORKSHOP / str(id_))


def prepare() -> Path:
    ROOT.mkdir(parents=True, exist_ok=True)
    lc.build()
    # The hub's build, as on the live cluster
    cached = next(MGP_CACHE.glob("Multigrid Projector-*/Bin/plugin.dll"))
    mgp = lc.PLUGINS / "MultigridProjector"
    mgp.mkdir(parents=True, exist_ok=True)
    shutil.copy2(cached, mgp / "MultigridProjector.dll")
    (mgp / "MultigridProjector.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{pl.MGP[3]}</Id><FriendlyName>Multigrid Projector</FriendlyName>"
        "<RepoId>viktor-ferenczi/se-multigrid-projector</RepoId>"
        "<Commit>hub</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    # Everything of the shipyard test's world but its Magnetar folder: the bench makes its own
    prepare_server, rig.prepare_server = rig.prepare_server, lambda **_: None
    so.prepare(so.SMALL, 60)
    rig.prepare_server = prepare_server
    # An hour's cool-down: the default of 10 minutes could run out during the node restarts
    set_config(
        EncountersEnabled="false",
        MoneyPlayerTaxThreshold=10_000,
        FreeBuildCooldownMinutes=60,
    )
    lc.PLAN.write_text("")
    lc.OUT.write_text("")
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(
        [
            str(lc.PLUGINS / "Octahedron"),
            f"node:{mgp}",
            f"wa:{lc.PLUGINS / 'LedgerProbe'}",
        ]
    )
    os.environ.update(
        LEDGERPROBE_PLAN=str(lc.PLAN),
        LEDGERPROBE_OUT=str(lc.OUT),
        LEDGERPROBE_NODE="wa-1",
        OCTAHEDRON_CONFIG=str(CONFIG),
        OCTAHEDRON_ENCOUNTERS=str(PLUGIN / "Encounters"),
    )
    return rig.WORLD


def stations() -> dict[str, tuple[float, float, float]]:
    """The stations' positions from the world file"""
    found = {}
    for grid in (
        ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot().find("SectorObjects")
    ):
        name = grid.findtext("DisplayName") or ""
        if name.endswith(" Station"):
            at = grid.find("PositionAndOrientation/Position")
            found[name] = tuple(float(at.get(axis)) for axis in "xyz")
    return found


def global_save(process_data: Path) -> str:
    """The newest global save of a process: an MPS1 file with zstd frames"""
    newest = max(
        (process_data / "cluster/saves/GLOBAL").glob("*.mps"), key=lambda p: p.name
    )
    data = newest.read_bytes()
    text, at = [], 0
    while (at := data.find(b"\x28\xb5\x2f\xfd", at)) >= 0:
        unpacked = subprocess.run(
            ["zstd", "-dcq"], input=data[at:], capture_output=True
        )
        text.append(unpacked.stdout.decode(errors="replace"))
        at += 4
    return "".join(text)


def run(bench) -> None:
    cluster = bench.cluster
    probe = lc.Probe()
    nodes = ("node-1", "node-2")

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
        for node in nodes:
            try:
                if _client_partition(_partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def to_node(target: str) -> None:
        """Hands the client's partition over to the target unless it is there already"""
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
        time.sleep(10)

    def to_table(api, position=None) -> None:
        if position:
            api.character_teleport(*position)
        # The client drops the grids it left and gets the new ones a little after a teleport
        for attempt in range(24):
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

    def press(api) -> str:
        """The table's button. The reply may need a trip to the World Authority"""
        reply = ""
        # A shipyard joins its station's partition by proximity, a little after a start
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order on {where()} -> {reply}", flush=True)
            if "shipyard is busy" not in reply and "has no shipyard" not in reply:
                break
            time.sleep(10)
        return reply

    unvisited = list(stations().values())

    def to_station_on(api, node: str):
        """Teleports to a station whose partition is on the node; a handover if there is none"""
        for station in unvisited:
            to_table(api, station)
            time.sleep(10)
            if where() == node:
                return
            print(f"the station at {station} is on {where()}", flush=True)
        to_node(node)

    def refused(api, label: str, node: str) -> None:
        to_station_on(api, node)
        reply = press(api)
        check(
            f"{label}: refused on {node}", COOLDOWN in reply and where() == node, reply
        )

    def money(api) -> dict[str, int]:
        lines = " ".join(rig.chat(api, "!oct money", 10))
        found = {k: number(v) for k, v in re.findall(r"(OCT|You): ([\d,]+) SC", lines)}
        print(f"!oct money on the client: {found}", flush=True)
        return found

    def wa_command(line: str) -> list[str]:
        replies = probe.run(line, timeout=120, settle=3)
        print(f"WA {line} -> {replies}", flush=True)
        return replies

    def wait_wa_log(pattern: re.Pattern, what: str, timeout: float = 120) -> re.Match:
        return wait_until(lambda: pattern.search(log("wa-1")), timeout, what)

    def reset_probe() -> None:
        lc.PLAN.write_text("")
        lc.OUT.write_text("")
        probe.step = 0

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *nodes):
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    # Both nodes from the start: the world's partitions are assigned among them, and a shipyard on a
    # node that is down looks like a station without one
    processes = ["wa-1", *nodes]
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)

    # --- SE-695: the money rules ---
    start = wait_wa_log(
        re.compile(
            r"Money rules start: OCT's balance cut from 1,000,000,000 SC to its cap [\d,]+ SC"
        ),
        "the WA's first money run",
        180,
    )
    check("the WA cut OCT's 1e9 to its cap", start, start[0])
    api = join()
    before = money(api)
    check(
        "a new account has 25,000 SC on the client", before.get("You") == 25_000, before
    )
    check(
        "OCT at its cap on the client's node", before.get("OCT") == 20_000_000, before
    )
    reply = " ".join(rig.chat(api, "!oct payday", 5))
    check(
        "a node's !oct payday points at the WA",
        "run on the World Authority" in reply,
        reply,
    )

    wa_command("!oct taxday")
    tax = wait_wa_log(TAX, "the tax line")
    check(
        "the WA taxes the client's forwarded account",
        tax[1] == "150" and tax[2] == "1",
        tax[0],
    )
    wa_command("!oct payday")
    paid = wait_wa_log(HOUR, "the payout line")
    check("the hourly payout counts the client as active", paid[1] == "1", paid[0])

    def balance_expected() -> int:
        wa = log("wa-1")
        return (
            25_000
            - sum(number(m[1]) for m in TAX.finditer(wa))
            + sum(number(m[2]) for m in HOUR.finditer(wa))
        )

    try:
        wait_until(
            lambda: money(api).get("You") == balance_expected(), 60, "the balance"
        )
    except ClusterError:
        pass
    now = money(api)
    check(
        "tax and payout reach the client's balance",
        now.get("You") == balance_expected() and now.get("You") != 25_000,
        f"{now} expected You {balance_expected()}",
    )
    bench.record(
        "wa-money-lines", [l for l in log("wa-1").splitlines() if MONEY_LOG.search(l)]
    )
    described = wa_command("!oct money")
    bench.record("wa-money-before", described)

    # --- SE-696: the cool-down ---
    to_table(api)
    reply = press(api)
    node_a = wait_until(where, 120, "the client's partition")
    node_b = "node-2" if node_a == "node-1" else "node-1"
    check(f"a free quote on {node_a}", "a free build" in reply, reply)
    reply = press(api)
    check("the free order is queued", "is in the queue" in reply, reply)
    so.wait_chat(api, f"builds '{SHIP}' for you now", 120)
    so.wait_chat(api, "is ready", 900)
    ended = time.time()
    here = api.get_character()["position"]
    if isinstance(here, dict):
        here = (here["x"], here["y"], here["z"])
    # This table's blueprint is cleared once queued; the other stations' tables still have theirs
    unvisited[:] = [p for p in unvisited if math.dist(p, here) > 5_000]
    refused(api, "the live broadcast", node_b)

    to_station_on(api, node_a)
    cluster.restart_node(node_b)
    cluster.wait_ready(nodes=[node_b])
    refused(api, f"{node_b} restarted, the WA's list", node_b)
    warnings = re.findall(
        r"Free builds: (?:no cool-down list|sending cool-downs failed).*", log(node_b)
    )
    bench.record("list-warnings", warnings)
    # At most one for each of the node's two starts, however long it was fenced
    check("a failed list request is logged once", len(warnings) <= 2, warnings)

    # A handover keeps a table's blueprint, in both directions
    for node in (node_a, node_b):
        to_node(node)
        reply = press(api)
        check(
            f"a handover to {node}: the table keeps its blueprint",
            COOLDOWN in reply,
            reply,
        )

    # --- both: the global save ---
    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    saved = {
        process: "".join(
            p.read_text(encoding="utf-8", errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("Sandbox.sbc")
        )
        for process in nodes
    }
    saved["wa-1"] = global_save(bench.run_root / "wa-1-data")
    state = STATE.search(saved["wa-1"])
    check("the money state is in the WA's global save", state, len(saved["wa-1"]))
    check(
        "the WA's global save has the cool-down",
        "Octahedron-FreePrints" in saved["wa-1"],
    )
    bench.record("saved-money-state", state[0] if state else "")
    check(
        "the nodes saved neither",
        not any(
            n in saved[p]
            for p in nodes
            for n in ("Octahedron-Money", "Octahedron-FreePrints")
        ),
    )
    money_lines = {
        p: [l for l in log(p).splitlines() if MONEY_LOG.search(l)] for p in nodes
    }
    check(
        "the nodes logged nothing from Money",
        not any(money_lines.values()),
        money_lines,
    )

    reset_probe()
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    refused(api, "after the warm start", node_b)
    after = wa_command("!oct money")
    bench.record("wa-money-after", after)

    def held(lines: list[str]) -> str | None:
        found = re.search(r"tax held: [\d,]+ SC", " ".join(lines))
        return found[0] if found else None

    check(
        "the WA read its money state back",
        held(described) and held(described) == held(after),
        f"{described} | {after}",
    )
    check(
        "the OCT cut did not run again",
        log("wa-1").count("Money rules start") <= 1,
        log("wa-1").count("Money rules start"),
    )
    expected = now["You"]
    later = money(api)
    # A new hour may have paid out meanwhile, the log has it
    check(
        "the client's balance came back",
        later.get("You") in (expected, balance_expected()),
        f"{later}, before {expected}",
    )

    # --- the WA alone ---
    to_station_on(api, node_a)
    reset_probe()
    cluster.restart_node("wa-1")
    cluster.wait_ready(nodes=["wa-1"])
    cluster.restart_node(node_b)
    cluster.wait_ready(nodes=[node_b])
    refused(api, f"the WA killed, {node_b} restarted", node_b)

    # --- the cool-down ends: only about 2 minutes left, not the hour ---
    minutes = math.ceil((time.time() - ended) / 60) + 2
    set_config(FreeBuildCooldownMinutes=minutes)
    print(f"cool-down {minutes} min: {rig.chat(api, '!oct reload', 5)}", flush=True)
    reply = press(api)
    check(
        f"with {minutes} min, still refused",
        re.search(COOLDOWN + r" [123] min", reply),
        reply,
    )
    time.sleep(max(0, ended + minutes * 60 + 5 - time.time()))
    reply = press(api)
    check(
        "free once the time has passed",
        COOLDOWN not in reply
        and ("a free build" in reply or "is in the queue" in reply),
        reply,
    )


run.regular_nodes = 2
run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = prepare()
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-wa-economy")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-WA-ECONOMY",
        "SE-695, SE-696: money rules and free-build cool-down on the World Authority",
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
