"""The world's config/hangar.cfg on a local test cluster (SE-930), through the cluster's scenario bench
(CometWorks/cluster Test/), with Hangar (OCT_HANGAR_REPO) on the nodes. A ship saved out
in space and loaded from another node comes back at its saved spot. With another grid's bounding
sphere there, and that grid on another node, it comes back at the nearest clear spot. A ship the
shipyard's deadline stored (SE-929) comes back clear of the station. All of it free.

    OCT_TASK=SE-930C OCT_WORLD_SOURCE=<world from build_world.py> OCT_HANGAR_REPO=<hangar> \\
        <Test>/.venv/bin/python tests/hangar_where_saved_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe and Hangar on the nodes. About 20 minutes.
"""

from __future__ import annotations

import json
import math
import os
import re
import shutil
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-930C")
import shipyard_deadline_cluster as deadline  # noqa: E402  (takes the bench's folder from argv)
from shipyard_deadline_cluster import (  # noqa: E402
    CLEAR,
    CLIENT_ID,
    NEXT,
    NODES,
    PAID,
    SHIP,
    STATION,
    WINDOW,
    Client,
    ClusterError,
    bench_module,
    rig,
    sa,
    tc,
    tp,
    tr,
    turn,
    wa,
    wait_until,
)

wa.ROOT = Path.home() / f"cluster-runs/where-saved-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"

ROAMER = "Roamer"
KEEPER = "Keeper"
# Out in space from the station's table, past the cluster's splitDistance of 12 km
AWAY_FROM_TABLE = (0.0, 20000.0, 0.0)
# The two probe ships are 2.5 x 2.5 x 5 m: their spheres of about 3 m meet, their boxes don't
KEEPER_ABOVE = 4.0


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

    def at_spot(api) -> bool:
        return math.dist(api.get_character()["position"], SPOT) < 50

    def to_spot(api) -> None:
        api.character_teleport(*SPOT)
        wait_until(lambda: at_spot(api), 30, "the client at the spot")

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

    def money(api) -> int:
        lines = " ".join(rig.chat(api, "!oct money", 10))
        found = re.search(r"You: ([\d,]+) SC", lines)
        assert found, lines
        return int(found[1].replace(",", ""))

    def hangar(api, args: str = "list") -> str:
        reply = " ".join(rig.chat(api, f"!hangar {args}", 10))
        print(f"!hangar {args} on {where()} -> {reply}", flush=True)
        return reply

    def sphere(api, name: str) -> tuple | None:
        """The grid's sphere on the client's node and what it meets, None when it isn't there"""
        reply = probe(api, f"sphere {name.replace(' ', '_')}")
        print(f"sphere {name} on {where()} -> {reply}", flush=True)
        found = re.search(r"SPHERE (\S+) (\S+) (\S+) (\S+) meets=(\S*)", reply)
        if not found:
            return None
        return (
            tuple(float(found[i]) for i in (1, 2, 3)),
            float(found[4]),
            [n for n in found[5].split(",") if n],
        )

    def spot_ship(api, name: str, y: float = 0.0) -> int:
        reply = probe(api, f"spotship {name} {SPOT[0]} {SPOT[1] + y} {SPOT[2]}")
        print(reply, flush=True)
        return int(re.search(r"SPOTSHIP (\d+)", reply)[1])

    def owner(grid: int) -> tuple | None:
        """The grid's (node, partition), when it has one owner"""
        found = bench.entity_owner(bench.diag("entities"), grid)
        return found[0] if len(found) == 1 else None

    def press(api) -> str:
        reply = ""
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order on {where()} -> {reply}", flush=True)
            if "Spawn first" in reply:
                bench.wait_spawned(CLIENT_ID)
                to_table(api)
                continue
            if not any(
                busy in reply
                for busy in ("shipyard is busy", "has no shipyard", "did not answer")
            ):
                break
            time.sleep(10)
        return reply

    def order(api, blueprint: str) -> str:
        probe(api, "air")
        tr.load(api, blueprint)
        tr.settled(api, "table", "paid")
        reply = press(api)
        if "Your quote for" in reply:
            reply = press(api)
        return reply

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
        # The world's config/hangar.cfg, and saving more than once an hour
        (bench.run_root / f"{process}-data" / "Hangar.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?><PluginConfig>'
            "<DefaultLoadMode>OriginalPosition</DefaultLoadMode>"
            "<PlayersMayChooseLoadPlace>false</PlayersMayChooseLoadPlace>"
            "<LoadClearOfGridSpheres>true</LoadClearOfGridSpheres>"
            "<AdminSafeZonesOnly>false</AdminSafeZonesOnly>"
            "<SaveCooldownMinutes>0</SaveCooldownMinutes></PluginConfig>",
            encoding="utf-8",
        )
    # A reused run folder keeps Hangar's entries and cool-downs in the shared storage
    for stale in (bench.run_root / "shared" / "plugins").glob("17C928CE-*"):
        shutil.rmtree(stale)
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    home = wait_until(where, 120, "the client's partition")
    away = NODES[1 - NODES.index(home)]
    table = sa.station_tables()[STATION]
    SPOT = tuple(t + d for t, d in zip(table, AWAY_FROM_TABLE))

    balance = money(api)

    # --- saved out in space, loaded far from it on another node ---
    to_spot(api)
    probe(api, "air")
    spot_ship(api, ROAMER)
    saved = until(lambda: sphere(api, ROAMER))
    check("the ship is at the spot", saved, saved)
    reply = hangar(api, f"save {ROAMER}")
    check("saved out in space", "Saved" in reply, reply)
    to_table(api)
    to_node(away)
    reply = hangar(api, f"load {ROAMER} true")
    check("a player can't choose", "can't choose" in reply, reply)
    reply = hangar(api, f"load {ROAMER}")
    check(
        "loaded on another node", reply.endswith(f"Loaded {ROAMER} from hangar."), reply
    )
    back = until(lambda: sphere(api, ROAMER))
    check("back at its saved spot", back and math.dist(back[0], saved[0]) < 1, back)
    check("nothing to pay", money(api) == balance, money(api))

    # --- another grid's sphere meets it there, and that grid is on another node ---
    to_spot(api)
    probe(api, "air")
    until(lambda: sphere(api, ROAMER), 120, 5)
    reply = hangar(api, f"save {ROAMER}")
    check("saved again", "Saved" in reply, reply)
    keeper_id = spot_ship(api, KEEPER, KEEPER_ABOVE)
    keeper = until(lambda: sphere(api, KEEPER))
    check("the other grid is at the spot", keeper, keeper)
    check(
        "the spheres would meet",
        math.dist(keeper[0], saved[0]) < keeper[1] + saved[1],
        keeper,
    )
    to_table(api)
    # The client's partition lets go of what it left behind
    keeper_at = until(
        lambda: (place := owner(keeper_id))
        and place[1] != wa._client_partition(wa._partitions(bench, where()))
        and place,
        180,
        5,
    )
    check("the other grid has its own partition", keeper_at, keeper_at)
    to_node(NODES[1 - NODES.index(keeper_at[0])])
    check(
        "the other grid is on another node",
        until(lambda: sphere(api, KEEPER) is None, 60),
    )
    reply = hangar(api, f"load {ROAMER}")
    moved = re.search(r"from hangar, (\d+) m from where it was saved", reply)
    check("moved, and it says so", moved, reply)
    to_spot(api)
    probe(api, "air")
    back = until(
        lambda: (s := sphere(api, ROAMER)) and sphere(api, KEEPER) and s, 120, 5
    )
    check("it is at the spot's side", back, back)
    gap = math.dist(back[0], keeper[0]) - back[1] - keeper[1]
    check("clear of the other grid's sphere, and near", 0 < gap < 2, gap)
    check("clear of every sphere", back[2] == [], back)
    check("the other grid stayed", math.dist(sphere(api, KEEPER)[0], keeper[0]) < 5)
    check("nothing to pay", money(api) == balance, money(api))
    print(probe(api, f"removegrid {ROAMER}"), flush=True)
    print(probe(api, f"removegrid {KEEPER}"), flush=True)

    # --- stored by the shipyard's deadline, loaded from another node ---
    to_table(api)
    probe(api, "air")
    before = money(api)
    mark = tp.latest(api)
    check("the order is in the queue", "is in the queue" in order(api, PAID))
    told = tp.wait_chat(api, "It is your turn", 60, mark)
    check("its turn", told, told)
    print(probe(api, f"yardload {STATION} {turn.PLATE} {turn.PLATES - 20}"), flush=True)
    mark = tp.latest(api)
    press(api)
    done = tp.wait_chat(api, "Take what is in", 400, mark)
    check("the ship is built", done, done)
    until(lambda: money(api) < before)
    rig.chat(api, "!oct shipyard done", 10)
    check(
        "the turn ends",
        until(
            lambda: "No ships in the queue"
            in tr.settled(api, "queue", "No ships in the queue"),
            60,
        ),
    )
    rig.chat(api, "!oct table remove", 5)
    in_yard = sphere(api, SHIP)
    check("in the station's sphere", in_yard and in_yard[2], in_yard)
    balance = money(api)
    check("an order behind it", "is in the queue" in order(api, NEXT))
    check("stored by the node", until(lambda: SHIP in hangar(api), CLEAR * 60 + 120, 5))
    to_node(NODES[1 - NODES.index(wait_until(where, 120, "the client's partition"))])
    reply = hangar(api, f"load {SHIP}")
    moved = re.search(r"from hangar, (\d+) m from where it was saved", reply)
    check("moved out of the station's sphere, and it says so", moved, reply)
    back = until(lambda: sphere(api, SHIP))
    check("it is back", back, back)
    check("clear of every sphere", back and back[2] == [], back)
    check(
        "the distance it says",
        back and abs(math.dist(back[0], in_yard[0]) - int(moved[1])) < 5,
        f"{back} {in_yard}",
    )
    check("free", money(api) == balance, money(api))


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    hangar_folder = deadline.build_hangar()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder},node:{hangar_folder}"
    wa.set_config(
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTurnMinutes=WINDOW,
        ShipyardClearMinutes=CLEAR,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=0,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        MoneyOctCap=1_000_000_000_000,
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-hangar-where-saved")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-HANGAR-WHERE-SAVED",
        "SE-930: a ship back where it was saved",
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
