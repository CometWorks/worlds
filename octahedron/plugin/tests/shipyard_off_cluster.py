"""A shipyard's job when its player leaves the node, on a local test cluster (SE-887), through the
cluster's scenario bench (CometWorks/cluster Test/). The station's partition has nobody near and its
grids are static, so the cluster would take it offline. It stays online for a paid build, the rest of
its turn and a repair slot, and goes offline once the shipyard is free. The shipyard starts off, and is
off again after each job.

    OCT_TASK=SE-887C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/shipyard_off_cluster.py <Test>     (from plugin/)

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

os.environ.setdefault("OCT_TASK", "SE-887C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    bench_module,
    rig,
    tc,
    wait_until,
)

from scenarios_offline import _put_config  # noqa: E402
import shipyard_turn_cluster as turn  # noqa: E402
import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402

STATION = "Alpha"
OCT = "Octahedron_Authority"
PLATE = "Component/SteelPlate"
WINDOW = 2
GOLD = "Ingot/Gold"
SHIP = "Off Test"
COLUMNS = 8 * 32
LONG = rig.grid(
    SHIP,
    777000887000300,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(8)
        for z in range(32)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PLATES = COLUMNS * 25
REPAIR_BLOCKS = 60
NODES = ("node-1", "node-2")
# Nobody can see the station from here
AWAY_METERS = 25_000

wa.ROOT = Path.home() / f"cluster-runs/off-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


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

    def other(node: str) -> str:
        return NODES[1 - NODES.index(node)]

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

    def yard(api) -> dict[str, str]:
        return dict(re.findall(r"(\w+)=(\S+)", probe(api, f"yard {STATION}")))

    def theirs(state: dict) -> bool:
        return all(
            state.get(k, "").endswith("/Faction") and not state[k].startswith(OCT)
            for k in ("cargo", "welders", "connectors")
        )

    def off(state: dict) -> bool:
        return (
            all(
                state.get(k, "").startswith(OCT + "/")
                for k in ("cargo", "welders", "connectors")
            )
            and state.get("welderson") == "0"
            and state.get("connectorson") == "0"
        )

    def on_for_refill(state: dict, what: str) -> None:
        have, total = state.get("refill", "0/0").split("/")
        check(
            f"{what}: refill stations work", have == total != "0", state.get("refill")
        )
        have, total = state.get("reactorson", "0/0").split("/")
        check(f"{what}: reactors on", have == total != "0", state.get("reactorson"))

    def wait_state(api, what, timeout: float = 90) -> bool:
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            try:
                if what(yard(api)):
                    return True
            except Exception as error:  # noqa: BLE001 -- the node may be coming back
                print(f"no yard yet: {error}", flush=True)
            time.sleep(5)
        return False

    def press(api) -> str:
        reply = ""
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order on {where()} -> {reply}", flush=True)
            if not any(
                busy in reply
                for busy in ("shipyard is busy", "has no shipyard", "did not answer")
            ):
                break
            time.sleep(10)
        return reply

    def shipyard_id(api) -> int:
        return next(
            g["entityId"]
            for g in api.list_grids()
            if g.get("name") == f"{STATION} Station Shipyard"
        )

    def offline_with(member: int) -> bool:
        return any(
            str(member) in [str(x) for x in row.get("Members") or []]
            for row in cluster.get("/registry/partitions/offline").get("Partitions", [])
        )

    def logged(text: str) -> bool:
        return any(
            text in p.read_text(errors="replace")
            for node in NODES
            for p in (bench.run_root / f"{node}-data").rglob("*.log")
            if p.is_file()
        )

    def leave(api, home: str) -> None:
        """Far enough that the station's partition has nobody near, and on the other node"""
        x, y, z = sa.station_tables()[STATION]
        api.character_teleport(x + AWAY_METERS, y, z)
        # The client's own partition first: it splits off the station's after the teleport
        wait_until(
            lambda: any(
                CLIENT_ID in (p.get("clients") or [])
                and len(p.get("members") or p.get("entities") or []) <= 2
                for p in wa._partitions(bench, where() or home)
            ),
            240,
            "the client's own partition",
        )
        to_node(other(home))

    def settle_offline() -> None:
        """Now the station's partition goes offline as soon as nothing holds it"""
        for node in NODES:
            bench.record(
                f"offline-{node}",
                _put_config(
                    bench,
                    node,
                    {
                        "offline.enabled": "true",
                        "offline.minOnlineSeconds": "0",
                        "offline.graceSeconds": "0",
                    },
                ),
            )

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    home = wait_until(where, 120, "the client's partition")

    # --- off from the start, on a node of the cluster ---
    check("off at the start", wait_state(api, off, 60), yard(api))
    on_for_refill(yard(api), "at the start")
    yard_id = shipyard_id(api)
    settle_offline()

    # --- a paid build: the player leaves during the build ---
    probe(api, "balance 300000")
    mark = tp.latest(api)
    tr.load(api, LONG)
    tr.settled(api, "table", "paid")
    reply = press(api)
    if "Your quote for" in reply:
        reply = press(api)
    check("the paid order is in the queue", "is in the queue" in reply, reply)
    told = tp.wait_chat(api, "It is your turn", 60, mark)
    check("the player is told it is their turn", told, told)
    check("on for the slot", wait_state(api, theirs, 30), yard(api))
    state = yard(api)
    check(
        "the welders and connectors are on",
        state["welderson"] == "24" and state["connectorson"] == "8",
        state,
    )
    print(probe(api, f"yardload {STATION} {PLATE} {PLATES}"), flush=True)
    # What is left keeps the turn going until its deadline
    print(probe(api, f"yardload {STATION} {GOLD} 5"), flush=True)
    mark = tp.latest(api)
    reply = press(api)
    check("the press starts it", "builds" in reply or "bought" in reply, reply)
    check(
        "it is placed",
        until(lambda: [g for g in api.list_grids() if g.get("name") == SHIP], 40),
    )
    left_at = time.monotonic()
    leave(api, home)
    node = where()
    check("the client is on the other node", node == other(home), node)
    built = until(lambda: logged("built or broken down"), 600, 5)
    check(
        "the build ends with the player on the other node",
        built,
        f"{time.monotonic() - left_at:.0f} s after leaving",
    )
    check(
        "their partition stayed online",
        not offline_with(yard_id),
        "the station's partition went offline",
    )
    told = tp.wait_chat(api, "Take what is in", 30, mark)
    check("the player is told on the other node", told, told)
    gone = until(
        lambda: logged(f"at {STATION} Station Shipyard ended:"), WINDOW * 60 + 90, 5
    )
    check("the rest of the turn ends by its deadline", gone)
    check(
        "the partition goes offline once the shipyard is free",
        until(lambda: offline_with(yard_id), 180, 5),
    )

    # --- back at the station, which the approach wakes: off, the ship there and whole ---
    to_table(api)
    check("off again", wait_state(api, off, 60), yard(api))
    on_for_refill(yard(api), "after the build")
    ship = next((g for g in api.list_grids() if g.get("name") == SHIP), None)
    check("the ship is there", ship, api.list_grids())
    if ship:
        info = dict(re.findall(r"(\w+)=(\S+)", probe(api, f"info {ship['entityId']}")))
        check("the ship is whole", info.get("blocks") == str(COLUMNS), info)
    print(probe(api, f"removegrid {SHIP.replace(' ', '_')}"), flush=True)
    rig.chat(api, "!oct table remove", 5)
    home = wait_until(where, 120, "the client's partition")

    # --- a repair slot: the player leaves while the welders work ---
    reply = press(api)
    check("a repair slot", "Repair slot" in reply, reply)
    check("on for the slot", wait_state(api, theirs, 40), yard(api))
    docked = re.search(r"SHIP (\d+)", probe(api, f"yardship {STATION}"))
    check("a ship with the components", docked, docked)
    check("it docks", st.wait_docked(api, docked[1] if docked else "0"))
    target = re.search(
        r"TARGET (\d+)", probe(api, f"yardtarget {STATION} {REPAIR_BLOCKS}")
    )
    check("a ship to repair", target, target)
    print(probe(api, f"yardload {STATION} {GOLD} 5"), flush=True)
    mark = tp.latest(api)
    leave(api, home)
    node = where()
    check("the client is on the other node", node == other(home), node)
    ended = until(lambda: logged("no building or welding for"), 600, 5)
    check("the slot ends with the player on the other node", ended)
    check("its partition stayed online", not offline_with(yard_id))
    told = tp.wait_chat(api, "no building or welding", 30, mark)
    check("the player is told on the other node", told, told)
    check(
        "the partition goes offline once the slot's turn is over",
        until(lambda: offline_with(yard_id), WINDOW * 60 + 240, 5),
    )
    to_table(api)
    check("off after the slot", wait_state(api, off, 60), yard(api))
    info = dict(
        re.findall(r"(\w+)=(\S+)", probe(api, f"info {target[1] if target else 0}"))
    )
    check("the repair is done", info.get("blocks") == str(3 + REPAIR_BLOCKS), info)
    print(probe(api, "undock"), flush=True)
    print(probe(api, "removegrid Repair_Target"), flush=True)


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTurnMinutes=WINDOW,
        ShipyardRepairIdleMinutes=1,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=0,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        MoneyOctCap=1_000_000_000_000,
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-off")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD-OFF",
        "SE-887: a shipyard's job without its player",
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
