"""A shipyard's job over a cluster stop with no player near, on a local test cluster (SE-904), through
the cluster's scenario bench (CometWorks/cluster Test/). A stop takes the idle partitions offline, and
nothing would load them until someone comes by. The station's partition has a build running, so it
comes back with the start: the build and the rest of the turn end with nobody connected, and the
ship is whole when a player does come.

    OCT_TASK=SE-904C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/shipyard_stop_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe on the nodes. About 15 minutes.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-904C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    bench_module,
    rig,
    tc,
    wait_until,
)

from scenarios_offline import _put_config  # noqa: E402
import shipyard_turn_cluster as turn  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402

STATION = "Alpha"
PLATE = "Component/SteelPlate"
GOLD = "Ingot/Gold"
WINDOW = 2
SHIP = "Stop Test"
# Big enough that a stop right after the press finds it unfinished
BLOCKS = 8 * 32 * 3
LONG = rig.grid(
    SHIP,
    777000904000300,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, y, z))
        for x in range(8)
        for y in range(3)
        for z in range(32)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PLATES = BLOCKS * 25
NODES = ("node-1", "node-2")
# How long the start has to bring the job back. The sweep's own grace is 4 minutes by default.
BACK_SECONDS = 240

wa.ROOT = Path.home() / f"cluster-runs/stop-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1000])

    def until(what, timeout: float = 30, every: float = 1):
        end = time.monotonic() + timeout
        while True:
            value = what()
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

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
        api.set_admin_flag("invulnerable", True)
        to_table(api)
        return api

    def probe(api, args: str) -> str:
        return " ".join(rig.chat(api, f"!sprobe {args}", 6))

    def press(api) -> str:
        reply = ""
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order -> {reply}", flush=True)
            if not any(
                busy in reply
                for busy in ("shipyard is busy", "has no shipyard", "did not answer")
            ):
                break
            time.sleep(10)
        return reply

    def offline_rows() -> list[dict]:
        return cluster.get("/registry/partitions/offline").get("Partitions", [])

    def offline_with(member: int) -> bool:
        return any(
            str(member) in [str(x) for x in row.get("Members") or []]
            for row in offline_rows()
        )

    # The world's lane computers have " Station" in their names too, and some are in the yard's partition
    others = {
        name: at
        for name, at in wa.stations().items()
        if re.fullmatch(r"\w+ Station", name) and not name.startswith(STATION)
    }

    def idle_stations_offline() -> list[str]:
        """The other stations, which have nothing to do, in the offline table"""
        rows = offline_rows()
        return [
            name
            for name, at in others.items()
            if any(
                sum((row[axis] - at[i]) ** 2 for i, axis in enumerate("XYZ")) ** 0.5
                <= row["Radius"] + 500
                for row in rows
            )
        ]

    def log_text() -> str:
        return "\n".join(
            p.read_text(errors="replace")
            for node in NODES
            for p in (bench.run_root / f"{node}-data").rglob("*.log")
            if p.is_file()
        )

    def since(text: str, mark: int) -> str | None:
        """The first new line with this text, after <mark> of them in the logs"""
        lines = [line for line in log_text().splitlines() if text in line]
        return lines[mark] if len(lines) > mark else None

    def seen(text: str) -> int:
        return sum(text in line for line in log_text().splitlines())

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()

    # --- a paid build, and then nobody ---
    yard_id = next(
        g["entityId"]
        for g in api.list_grids()
        if g.get("name") == f"{STATION} Station Shipyard"
    )
    probe(api, "balance 300000")
    # The world's blueprint off the table first: the server refuses a player's removal of it
    # from an OCT table (SE-936), and the client's load would keep it
    tr.remove(api)
    mark = tp.latest(api)
    tr.load(api, LONG)
    tr.settled(api, "table", "paid")
    reply = press(api)
    if "Your quote for" in reply:
        reply = press(api)
    check("the paid order is in the queue", "is in the queue" in reply, reply)
    told = tp.wait_chat(api, "It is your turn", 60, mark)
    check("the player is told it is their turn", told, told)
    print(probe(api, f"yardload {STATION} {PLATE} {PLATES}"), flush=True)
    # What is left keeps the turn going until its deadline
    print(probe(api, f"yardload {STATION} {GOLD} 5"), flush=True)
    reply = press(api)
    check("the press starts it", "builds" in reply or "bought" in reply, reply)
    check(
        "it is placed",
        until(lambda: [g for g in api.list_grids() if g.get("name") == SHIP], 40),
    )
    bench.kill_client(CLIENT_ID)

    # --- the cluster stops in the middle of it ---
    built_mark = seen("built or broken down")
    check("unfinished at the stop", built_mark == 0, f"{built_mark} ended")
    pickup_mark = seen("picked up at")
    started = time.monotonic()
    graceful = cluster.stop(grace=240)
    stop_seconds = time.monotonic() - started
    check("graceful stop", graceful, f"{stop_seconds:.0f} s")
    bench.record("stop", {"graceful": graceful, "seconds": round(stop_seconds, 1)})
    print(f"STOP took {stop_seconds:.1f} s", flush=True)

    # --- and starts, with nobody connected ---
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    idle = idle_stations_offline()
    check("the idle stations stayed offline", len(idle) == len(others), idle)
    picked = until(lambda: since("picked up at", pickup_mark), BACK_SECONDS, 3)
    check(
        "the start brings the job back without a visit",
        picked,
        f"offline: {offline_with(yard_id)}, {len(offline_rows())} rows",
    )
    if not picked:
        # What the ticket says: the job waits for a visit. A visit shows it, the ship isn't lost.
        print(f"yard offline after the start: {offline_with(yard_id)}", flush=True)
        api = join()
        woken = until(lambda: since("picked up at", pickup_mark), 120, 3)
        print(f"after a visit: {woken}", flush=True)
        return
    check("its partition stays online", not offline_with(yard_id))
    # Now the partition goes offline as soon as nothing holds it
    for node in NODES:
        bench.record(
            f"offline-{node}",
            _put_config(
                bench,
                node,
                {"offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"},
            ),
        )
    built = until(lambda: since("built or broken down", built_mark), 600, 5)
    check("the build ends with nobody connected", built, built)
    check("its partition stayed online", not offline_with(yard_id))
    gone = until(
        lambda: since(f"at {STATION} Station Shipyard ended:", 0), WINDOW * 60 + 90, 5
    )
    check("the rest of the turn ends by its deadline", gone, gone)
    check(
        "the partition goes offline once the shipyard is free",
        until(lambda: offline_with(yard_id), 180, 5),
    )

    # --- a player comes: the ship is there and whole ---
    api = join()
    ship = until(lambda: [g for g in api.list_grids() if g.get("name") == SHIP], 90, 3)
    check("the ship is there", ship, api.list_grids())
    if ship:
        info = dict(
            re.findall(r"(\w+)=(\S+)", probe(api, f"info {ship[0]['entityId']}"))
        )
        check("the ship is whole", info.get("blocks") == str(BLOCKS), info)


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTurnMinutes=WINDOW,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=0,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        MoneyOctCap=1_000_000_000_000,
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-stop")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD-STOP",
        "SE-904: a shipyard's job over a cluster stop with no player near",
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
