"""A new ship's hydrogen on a local test cluster (SE-922), through the cluster's scenario bench
(CometWorks/cluster Test/). A free ship's H2 tank is filled, a docked ship giving what it can. The next ship is paid,
because of the free build's cooldown: a ship docked at the station gives what is over half its tank
and its ice, and the rest comes from the Store's default stock, on the WA.

    OCT_TASK=SE-922C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/shipyard_hydrogen_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe on the nodes. About 10 minutes.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-922C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    bench_module,
    rig,
    tc,
)

import shipyard_turn_cluster as turn  # noqa: E402
import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402

STATION = "Alpha"
PLATE = "Component/SteelPlate"
ICE = "Ore/Ice"
SHIP = "TankTest"
FILLED = 500_000
TANKED = rig.grid(
    SHIP,
    777000922000100,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0)),
        rig.block("MyObjectBuilder_OxygenTank", "LargeHydrogenTankSmall", (0, 0, 1)),
    ],
    tag="MyObjectBuilder_CubeGrid",
)
NODES = ("node-1", "node-2")

wa.ROOT = Path.home() / f"cluster-runs/hydrogen-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1000])

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

    def probe(api, args: str) -> str:
        reply = " ".join(rig.chat(api, f"!sprobe {args}", 6))
        print(f"!sprobe {args} -> {reply}", flush=True)
        return reply

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

    def liters(api, grid: str) -> int:
        found = re.search(r"GAS \d+ (\d+)", probe(api, f"gas {grid}"))
        return int(found[1]) if found else -1

    def ice_in(api, grid: str) -> int:
        found = re.search(r"ITEM \d+ (\d+)", probe(api, f"griditem {grid} {ICE}"))
        return int(found[1]) if found else -1

    def store_ice(api) -> float:
        return st.stock(api, STATION, ICE)[0]

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
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
    probe(api, "balance 300000")

    # --- a free ship ---
    found = re.search(
        r"SHIP (\d+)", probe(api, f"dock {STATION} mine H2=0.55,{ICE}=1000")
    )
    check("a ship docks at the station for the free one", found, found)
    first = found[1] if found else "0"
    time.sleep(5)
    to_table(api)
    mark = tp.latest(api)
    tr.load(api, TANKED)
    tr.settled(api, "table", "Press the button")
    reply = press(api)
    if "Your quote for" in reply:
        reply = press(api)
    check("the free order is in the queue", "is in the queue" in reply, reply)
    ready = tp.wait_chat(api, "is ready", 400, mark)
    check(
        "the free ship's message names the hydrogen",
        ready and f"{FILLED:,} L" in ready,
        ready,
    )
    got = liters(api, SHIP)
    check("the free ship's H2 tank is filled", got == FILLED, got)
    gas = liters(api, first)
    check("the docked ship gave what is over half", gas == FILLED, gas)
    ice = ice_in(api, first)
    check("the docked ship's ice went for the free one too", ice == 0, ice)
    probe(api, f"removegrid {SHIP}")

    # --- a paid ship: the cooldown of the free one ---
    stock = store_ice(api)
    check("the Store has 100 t of ice", stock == 100_000, stock)
    found = re.search(
        r"SHIP (\d+)", probe(api, f"dock {STATION} mine H2=0.55,{ICE}=2500")
    )
    check("a ship docks at the station", found, found)
    docked = found[1] if found else "0"
    time.sleep(5)
    to_table(api)
    mark = tp.latest(api)
    tr.load(api, TANKED)
    tr.settled(api, "table", "paid")
    reply = press(api)
    if "Your quote for" in reply:
        reply = press(api)
    check("the paid order is in the queue", "is in the queue" in reply, reply)
    told = tp.wait_chat(api, "It is your turn", 60, mark)
    check("the player is told it is their turn", told, told)
    probe(api, f"yardload {STATION} {PLATE} 200")
    mark = tp.latest(api)
    press(api)
    ready = tp.wait_chat(api, "Take what is in", 400, mark)
    check(
        "the paid ship's message names the hydrogen",
        ready and f"{FILLED:,} L" in ready,
        ready,
    )
    got = liters(api, SHIP)
    check("the paid ship's H2 tank is filled", got == FILLED, got)
    gas = liters(api, docked)
    check("the docked ship keeps half its tank", gas == FILLED, gas)
    ice = ice_in(api, docked)
    check("the docked ship's ice went", ice == 0, ice)
    now = store_ice(api)
    # A tank's fill is a float: a fraction of a kg off
    check(
        "the rest came from the Store",
        abs(now - stock + 20_000) < 1,
        f"{stock} -> {now}",
    )
    rig.chat(api, "!oct shipyard done", 4)


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=600,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        MoneyOctCap=1_000_000_000_000,
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-hydrogen")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD-HYDROGEN",
        "SE-922: a new ship's hydrogen on a cluster",
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
