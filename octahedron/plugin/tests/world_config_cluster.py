"""The features the world's octahedron.cfg turns on, together on a cluster (SE-783): money, market,
contracts, starter ships and station refills, with the file as it is. Same bench and world as
wa_economy_cluster.py, whose run covers ship orders under the projection limits.

Run on polaris on 2026-10-31: 15/15 checks in 6 minutes.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/world_config_cluster.py CLUSTER_TEST_DIR     (from plugin/)
"""

from __future__ import annotations

import json
import re
import sys
import time
from datetime import datetime, timezone

import wa_economy_cluster as wa
from wa_economy_cluster import (
    CLIENT_ID,
    Client,
    CONFIG,
    PLUGIN,
    bench_module,
    link_mods,
    pl,
    rig,
    stations,
    tc,
)

SUPPLIES = {"OxygenBottle", "HydrogenBottle", "Powerkit"}
TOOLS = ("Welder", "AngleGrinder", "HandDrill")
BINOCULARS = "BinocularsItem"
# The Free Tools rule names the binoculars, so the bench's world needs these on top of the rig's mods
BINOCULARS_MODS = {2754014019: "Scope Framework", 2777644246: "Binoculars"}
SOURCE = PLUGIN.parent / "config/octahedron.cfg"
# Seconds from the nodes' start
LOOT_LIMIT = 540


def run(bench) -> None:
    cluster = bench.cluster
    nodes = ("node-1", "node-2")
    probe = wa.lc.Probe()

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace") for p in (bench.run_root / f"{process}-data").rglob("*.log") if p.is_file()
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *nodes):
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    processes = ["wa-1", *nodes]
    cluster.start(processes)
    started = time.time()
    cluster.wait_ready(nodes=processes)

    start = wa.wait_until(lambda: re.search(r"Money rules start: .*", log("wa-1")), 180, "the WA's first money run")
    check("money: the WA runs the money rules", start, start[0])
    market = wa.wait_until(
        lambda: "Serving the station markets on the World Authority" in log("wa-1"), 180, "the market on the WA"
    )
    check("market: served on the WA", market)

    link_mods(
        Client(
            bench.run_root, CLIENT_ID, bench.remote_port(bench_module.LEGACY_REMOTE_PORT), cluster.config.gateway_listen
        ).user_data
        / "Mods"
    )
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)

    reply = " ".join(rig.chat(api, "!oct money", 10))
    check("money: a new account has 25,000 SC", "You: 25,000 SC" in reply, reply)

    ship = ""
    for station in stations().values():
        api.character_teleport(*station)
        for _ in range(12):
            time.sleep(5)
            tables = tc.tables(api)
            if tables:
                break
        tc.stand_by(api, min(tables, key=lambda b: b["distance"]))
        ship = " ".join(rig.chat(api, "!oct ship", 10))
        if "waits outside the station" in ship:
            break
    check("starter ship: built from the table", "waits outside the station" in ship, ship)

    probe.run("!oct contracts post", timeout=120, settle=3)
    reply = "\n".join(probe.run("!oct contracts", timeout=120, settle=3))
    print(reply, flush=True)
    posted = re.findall(r"^\s*.+ Station: ([1-9]\d*) contracts", reply, re.M)
    check("contracts: posted at the stations", posted, reply)

    reply = " ".join(rig.chat(api, "!oct market show Alpha", 15))
    check("market: Alpha's market reaches the client from the WA", "SteelPlate" in reply or "Iron" in reply, reply)

    # The Free Tools rule tops up every minute, and a client sees a container's inventory when near it
    api.character_teleport(*station)
    time.sleep(90)
    containers = api.find_nearby_blocks(block_type="CargoContainer", max_distance=400, limit=100)

    def stock(name: str) -> tuple[int, dict[str, float]]:
        named = [b for b in containers if name in (b.get("customName") or b.get("name") or "")]
        items: dict[str, float] = {}
        for block in named:
            for item in api.get_inventory(block["gridId"], tuple(block["min"]))["items"]:
                items[item["subtypeId"]] = items.get(item["subtypeId"], 0) + item["amountRaw"] / 1_000_000
        print(f"{name}: {len(named)} blocks, {items}", flush=True)
        return len(named), items

    count, items = stock("Free Tools")
    check(
        "refill: Free Tools has the supplies",
        count and SUPPLIES <= set(items) and min(items[t] for t in SUPPLIES) >= 3 * count,
        f"{count} containers, {items}",
    )
    for tool in TOOLS:
        tiers = {name: items.get(f"{tool}{name}Item", 0) for name in ("", "2", "3", "4")}
        # 12 draws per tool at 3 per container: all on one tier is a 1 in 2048 chance
        check(
            f"refill: Free Tools has {tool} tiers 2 and 3 at random, 3 per container",
            count and tiers["2"] and tiers["3"] and tiers["2"] + tiers["3"] == 3 * count and not tiers[""] + tiers["4"],
            f"{count} containers, {tiers}",
        )
    check(
        "refill: Free Tools has one pair of binoculars per container",
        count and items.get(BINOCULARS) == count,
        f"{count} containers, {items}",
    )
    # Topped up only when short: a pair taken from a container comes back, a spare one stays
    source, target = [b for b in containers if "Free Tools" in (b.get("customName") or b.get("name") or "")][:2]

    def binoculars(block) -> list[dict]:
        items = api.get_inventory(block["gridId"], tuple(block["min"]))["items"]
        return [item for item in items if item["subtypeId"] == BINOCULARS]

    api.transfer_inventory(
        source["gridId"],
        tuple(source["min"]),
        target["gridId"],
        tuple(target["min"]),
        binoculars(source)[0]["itemId"],
        1_000_000,
    )
    time.sleep(90)
    amounts = [sum(item["amountRaw"] for item in binoculars(b)) // 1_000_000 for b in (source, target)]
    check("refill: taken binoculars come back, spare ones stay", amounts == [1, 2], amounts)
    # A node refills a station as it comes to the node, not at the first economy tick (600 s here)
    end = started + LOOT_LIMIT
    count, items = stock("Station Loot")
    while count and not items and time.time() < end:
        time.sleep(15)
        count, items = stock("Station Loot")
    elapsed = time.time() - started
    check(
        "refill: Station Loot has loot well before the first economy tick",
        count and items,
        f"{count} containers after {elapsed:.0f} s, {items}",
    )
    for node in nodes:
        arrivals = re.findall(r"Refilling station .* as it came to this node", log(node))
        print(f"{node}: {len(arrivals)} stations refilled on arrival", flush=True)
    check("graceful stop", cluster.stop(grace=240))
    for process in ("wa-1", *nodes):
        check(
            f"{process}: no Octahedron failure logged", not re.search(r"\[Octahedron\].*(failed|Failed)", log(process))
        )
        compat = re.findall(
            r"cluster_mod_compat mod=(\d+) ", (bench.run_root / f"{process}.log").read_text(errors="replace")
        )
        check(f"{process}: both binocular mods loaded", set(map(str, BINOCULARS_MODS)) <= set(compat), compat)
        check(f"{process}: every refill item known", "unknown item" not in log(process))


run.administrators = [CLIENT_ID]


def main() -> int:
    pl.MODS.update(BINOCULARS_MODS)
    pl.MOD_ITEMS = pl.mod_items()
    run.world_template = wa.prepare()
    CONFIG.write_text(SOURCE.read_text(encoding="utf-8"), encoding="utf-8")
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-world-config")
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run, "P2-OCT-WORLD-CONFIG", "SE-783: the features of the world's octahedron.cfg on a cluster", "3", results
    )
    print(json.dumps({"passed": outcome.passed, "error": outcome.error, "results": str(results)}))
    print(f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed")
    for item in outcome.checks:
        if not item.passed:
            print("FAILED: " + item.label + (f" - {item.detail}" if item.detail else ""))
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
