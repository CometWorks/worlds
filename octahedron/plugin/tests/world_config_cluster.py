"""The features the world's octahedron.cfg turns on, together on a cluster (SE-783): money, market,
contracts, starter ships and station refills, with the file as it is, and a client buying and
selling at a station's Store block, with the store probe (tests/store-probe) in its plugins. Same
bench and world as wa_economy_cluster.py, whose run covers ship orders under the projection limits.

Run on polaris on 2026-10-31: 23/23 checks in 7 minutes.

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

import clients  # noqa: E402
import station_tests as st  # noqa: E402

pin_source_age = clients.pin_source_age


def client_with_store_probe(profile) -> None:
    """The bench copies the client's profile before every start, this comes right after"""
    pin_source_age(profile)
    current = profile / "Profiles/Current.xml"
    text = current.read_text(encoding="utf-8")
    folder = f"<LocalFolderConfig><Id>{st.STORE_PROBE[3]}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
    text = text.replace("<DevFolder />", "<DevFolder></DevFolder>").replace(
        "</DevFolder>", f"{folder}</DevFolder>"
    )
    current.write_text(text, encoding="utf-8")
    rig.register_source(
        profile / "Sources/sources.xml",
        st.STORE_PROBE[0],
        st.STORE_PROBE[1],
        st.STORE_PROBE[2],
    )


clients.pin_source_age = client_with_store_probe

SUPPLIES = {"OxygenBottle", "HydrogenBottle", "Powerkit"}
TOOLS = ("Welder", "AngleGrinder", "HandDrill")
BINOCULARS = "BinocularsItem"
# The Free Tools rule names the binoculars, so the bench's world needs these on top of the rig's mods
BINOCULARS_MODS = {2754014019: "Scope Framework", 2777644246: "Binoculars"}
SOURCE = PLUGIN.parent / "config/octahedron.cfg"
# Seconds from the nodes' start
LOOT_LIMIT = 540
# The Free Tools rule tops up every minute
REFILL_LIMIT = 150


def store_trade(api, probe, check) -> None:
    """Buys and sells at Alpha's Store block on the node the client is on; the stock is the World
    Authority's, and the console's process, wherever it runs, sees the change."""
    plate = "Component/SteelPlate"

    def book() -> tuple[float, int, int]:
        reply = " ".join(probe.run(f"!oct market show Alpha {plate}", settle=3))
        m = re.search(
            r"OCT buys ([\d,.]+) SC up to \S+, sells ([\d,.]+) SC, stock ([\d,.]+)",
            reply,
        )
        check("store: the book answers", m, reply)
        return (
            float(m[3].replace(",", "")),
            int(m[2].replace(",", "")),
            int(m[1].replace(",", "")),
        )

    def account() -> int:
        reply = " ".join(st.chat(api, "!oct money"))
        return int(re.search(r"You: ([\d,]+) SC", reply)[1].replace(",", ""))

    api.character_teleport(*stations()["Alpha Station"])
    stores = st.wait_until(
        lambda: api.find_nearby_blocks(subtype="StoreBlock", max_distance=400, limit=5),
        60,
        every=2,
    )
    check("store: a client sees Alpha's Store block", stores)
    tc.stand_by(api, min(stores, key=lambda b: b["distance"]))
    # The stores refresh every 15 s; this one has had that since the node started
    stock, ask, bid = book()
    money = account()
    shown = " ".join(st.chat(api, f"/store list {plate}"))
    check(
        "store: the client sees OCT's offer",
        f"OFFER {plate} {int(stock)} {ask}" in shown,
        shown,
    )
    reply = " ".join(st.chat(api, f"/store buy {plate} 10"))
    check("store: the client buys on a node", "BUY Success" in reply, reply)
    check("store: its account paid", account() == money - 10 * ask)
    check("store: the WA's stock went down", book()[0] == stock - 10)
    reply = " ".join(st.chat(api, f"/store sell {plate} 10"))
    check("store: the client sells on a node", "SELL Success" in reply, reply)
    check("store: its account got it", account() == money - 10 * ask + 10 * bid)
    check("store: the WA's stock is back", book()[0] == stock)


def run(bench) -> None:
    cluster = bench.cluster
    nodes = ("node-1", "node-2")
    probe = wa.lc.Probe()

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *nodes):
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    processes = ["wa-1", *nodes]
    cluster.start(processes)
    started = time.time()
    cluster.wait_ready(nodes=processes)

    start = wa.wait_until(
        lambda: re.search(r"Money rules start: .*", log("wa-1")),
        180,
        "the WA's first money run",
    )
    check("money: the WA runs the money rules", start, start[0])
    market = wa.wait_until(
        lambda: "Serving the station markets on the World Authority" in log("wa-1"),
        180,
        "the market on the WA",
    )
    check("market: served on the WA", market)

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
    check(
        "starter ship: built from the table", "waits outside the station" in ship, ship
    )

    probe.run("!oct contracts post", timeout=120, settle=3)
    reply = "\n".join(probe.run("!oct contracts", timeout=120, settle=3))
    print(reply, flush=True)
    posted = re.findall(r"^\s*.+ Station: ([1-9]\d*) contracts", reply, re.M)
    check("contracts: posted at the stations", posted, reply)

    reply = " ".join(rig.chat(api, "!oct market show Alpha", 15))
    check(
        "market: Alpha's market reaches the client from the WA",
        "SteelPlate" in reply or "Iron" in reply,
        reply,
    )

    store_trade(api, probe, check)

    # The Free Tools rule tops up every minute, and a client sees a container's inventory when near it
    api.character_teleport(*station)

    def nearby_containers() -> list[dict]:
        return api.find_nearby_blocks(
            block_type="CargoContainer", max_distance=400, limit=100
        )

    containers = nearby_containers()

    def stock(name: str) -> tuple[int, dict[str, float]]:
        named = [
            b
            for b in containers
            if name in (b.get("customName") or b.get("name") or "")
        ]
        items: dict[str, float] = {}
        for block in named:
            for item in api.get_inventory(block["gridId"], tuple(block["min"]))[
                "items"
            ]:
                items[item["subtypeId"]] = (
                    items.get(item["subtypeId"], 0) + item["amountRaw"] / 1_000_000
                )
        print(f"{name}: {len(named)} blocks, {items}", flush=True)
        return len(named), items

    def topped_up(count: int, items: dict[str, float]) -> bool:
        return (
            count
            and SUPPLIES <= set(items)
            and items.get(BINOCULARS) == count
            and all(
                sum(items.get(f"{tool}{tier}Item", 0) for tier in ("", "2", "3", "4"))
                == 3 * count
                for tool in TOOLS
            )
        )

    end = time.time() + REFILL_LIMIT
    count, items = stock("Free Tools")
    while not topped_up(count, items) and time.time() < end:
        time.sleep(5)
        containers = nearby_containers()
        count, items = stock("Free Tools")
    check(
        "refill: Free Tools has the supplies",
        count
        and SUPPLIES <= set(items)
        and min(items[t] for t in SUPPLIES) >= 3 * count,
        f"{count} containers, {items}",
    )
    for tool in TOOLS:
        tiers = {
            name: items.get(f"{tool}{name}Item", 0) for name in ("", "2", "3", "4")
        }
        # 12 draws per tool at 3 per container: all on one tier is a 1 in 2048 chance
        check(
            f"refill: Free Tools has {tool} tiers 2 and 3 at random, 3 per container",
            count
            and tiers["2"]
            and tiers["3"]
            and tiers["2"] + tiers["3"] == 3 * count
            and not tiers[""] + tiers["4"],
            f"{count} containers, {tiers}",
        )
    check(
        "refill: Free Tools has one pair of binoculars per container",
        count and items.get(BINOCULARS) == count,
        f"{count} containers, {items}",
    )
    # Topped up only when short: a pair taken from a container comes back, a spare one stays
    source, target = [
        b
        for b in containers
        if "Free Tools" in (b.get("customName") or b.get("name") or "")
    ][:2]

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

    def binocular_counts() -> list[int]:
        return [
            sum(item["amountRaw"] for item in binoculars(b)) // 1_000_000
            for b in (source, target)
        ]

    end = time.time() + REFILL_LIMIT
    amounts = binocular_counts()
    while amounts != [1, 2] and time.time() < end:
        time.sleep(5)
        amounts = binocular_counts()
    check(
        "refill: taken binoculars come back, spare ones stay",
        amounts == [1, 2],
        amounts,
    )
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
        arrivals = re.findall(
            r"Refilling station .* as it came to this node", log(node)
        )
        print(f"{node}: {len(arrivals)} stations refilled on arrival", flush=True)
    check("graceful stop", cluster.stop(grace=240))
    for process in ("wa-1", *nodes):
        check(
            f"{process}: no Octahedron failure logged",
            not re.search(r"\[Octahedron\].*(failed|Failed)", log(process)),
        )
        compat = re.findall(
            r"cluster_mod_compat mod=(\d+) ",
            (bench.run_root / f"{process}.log").read_text(errors="replace"),
        )
        check(
            f"{process}: both binocular mods loaded",
            set(map(str, BINOCULARS_MODS)) <= set(compat),
            compat,
        )
        check(f"{process}: every refill item known", "unknown item" not in log(process))


run.administrators = [CLIENT_ID]


def main() -> int:
    pl.MODS.update(BINOCULARS_MODS)
    pl.MOD_ITEMS = pl.mod_items()
    run.world_template = wa.prepare()
    CONFIG.write_text(SOURCE.read_text(encoding="utf-8"), encoding="utf-8")
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-world-config"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-WORLD-CONFIG",
        "SE-783: the features of the world's octahedron.cfg on a cluster",
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
