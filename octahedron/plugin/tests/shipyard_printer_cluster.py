"""test_shipyard_printer.py on a local test cluster, through the cluster's scenario bench (CometWorks/cluster
Test/), SE-1122: a player's printer stands by the Moon Shipyard where Viktor's missile printer stood, and the player
orders a blueprint free at Moon Station. The order must end with the ship complete and the printer's projection
unbuilt.

    OCT_TASK=SE-1122C OCT_WORLD_SOURCE=<world from build_world.py> OCT_MGP_REPO=<MGP working copy> \\
        <Test>/.venv/bin/python tests/shipyard_printer_cluster.py <Test> bp.sbc     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless admin client,
with the station probe on the nodes. OCT_BLUEPRINT_SECONDS (2400) limits the build.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1122C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    bench_module,
    rig,
    tc,
)

import shipyard_cluster as sc  # noqa: E402
import shipyard_turn_cluster as stc  # noqa: E402
import test_shipyard_printer as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402

SECONDS = int(os.environ.get("OCT_BLUEPRINT_SECONDS", 2400))
NODES = ("node-1", "node-2")

wa.ROOT = Path.home() / f"cluster-runs/printer-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1500])

    def node_log() -> str:
        return "".join(
            p.read_text(errors="replace")
            for node in NODES
            for p in (bench.run_root / f"{node}-data").rglob("*.log")
            if p.is_file()
        )

    def probe(api, args: str) -> str:
        # A busy test machine can take a while to answer
        return " ".join(rig.chat(api, f"!sprobe {args}", 60))

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *NODES):
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(["wa-1", *NODES])
    cluster.wait_ready(nodes=["wa-1", *NODES])

    user_data = Client(
        bench.run_root,
        CLIENT_ID,
        bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
        cluster.config.gateway_listen,
    ).user_data
    wa.link_mods(user_data / "Mods")
    target = user_data / "Blueprints/local/Loaded"
    target.mkdir(parents=True, exist_ok=True)
    (target / "bp.sbc").write_text(sc.BLUEPRINT, encoding="utf-8")
    (target / "bp.sbcB5").unlink(missing_ok=True)  # the game's binary copy would win
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    # The player is an admin (run.administrators), this makes any ship a free build for them
    api.set_admin_flag("ignorePcu", True)
    api.set_admin_flag("invulnerable", True)
    time.sleep(3)  # the server gets the flag with the admin menu's event

    for attempt in range(24):
        if attempt % 6 == 0:
            api.character_teleport(*sa.station_tables()[tp.STATION])
        time.sleep(5)
        tables = tc.tables(api)
        if tables:
            try:
                tc.stand_by(api, min(tables, key=lambda b: b["distance"]))
                break
            except Exception as error:  # noqa: BLE001 -- a grid already gone
                print(f"no stand by a table yet: {error}", flush=True)
    reply = probe(
        api, f"yardprinter {tp.STATION} {' '.join(map(str, tp.PRINTER))} {tp.BATTERIES}"
    )
    found = re.search(r"TARGET (\d+)", reply)
    if not check("a printer by the shipyard", found, reply):
        return
    printer = found[1]

    def info() -> dict[str, str]:
        return dict(re.findall(r"(\w+)=(\S+)", probe(api, f"info {printer}")))

    before = info()
    print(f"printer {before}", flush=True)

    table = min(tc.tables(api), key=lambda b: b["distance"])
    block = api.get_block(table["gridId"], tuple(table["min"]))
    # The world's blueprint off the table first: the server refuses a player's removal of it
    # from an OCT table (SE-936), and the client's load would keep it
    print(" ".join(rig.chat(api, "!oct table remove", 5)), flush=True)
    print(api.load_projection(block["entityId"], name="Loaded"), flush=True)
    time.sleep(10)
    reply = ""
    for _ in range(12):
        reply = sc.press(api)
        if "Your quote for" in reply:
            reply = sc.press(api)
        if "is in the queue" in reply or "builds" in reply:
            break
        time.sleep(10)
    if not check(
        "the ship is ordered", "is in the queue" in reply or "builds" in reply, reply
    ):
        return

    # The node that builds logs how the order ended, with the chat message; the headless client's
    # chat list may not have it
    ending = re.compile(r"Shipyard order \S+ '.*' of .* ended: (.*)")
    started = time.monotonic()
    ended = None
    while time.monotonic() - started < SECONDS and ended is None:
        found = ending.search(node_log())
        ended = found[1] if found else None
        time.sleep(15)
    print(f"the build took {time.monotonic() - started:.0f} s", flush=True)
    check("the build ended", ended, ended)
    check("the ship is complete", " is ready in " in str(ended), ended)
    after = info()
    print(f"printer {after}", flush=True)
    check(
        "no printer block built",
        after.get("blocks") == before.get("blocks"),
        f"{before.get('blocks')} -> {after.get('blocks')} blocks",
    )
    lines = [
        line
        for line in node_log().splitlines()
        if "Shipyard order" in line and (" stalled" in line or " ended:" in line)
    ]
    for line in lines:
        print(line, flush=True)
    check(
        "the order ended on a node without a stall",
        lines and not any(" stalled" in line for line in lines),
        lines[-3:],
    )


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = stc.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        FreeBuildPcuLimit=1_000_000,
        GroupPcuLimit=1_000_000,
        FreeBuildBlockedBlocks="",
        ShipyardTableMinutes=600,
        ShipyardOrderMinutes=max(60, SECONDS // 60),
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-printer")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD-PRINTER",
        "SE-1122: an order's welders build only its ship on a cluster",
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
