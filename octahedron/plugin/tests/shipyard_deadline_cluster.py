"""A ship an order left in a shipyard's build space past ShipyardClearMinutes (SE-929), on a local
test cluster through the cluster's scenario bench (CometWorks/cluster Test/), with Hangar 1.2.0 or
later (OCT_HANGAR_REPO) on the nodes at its default of two slots. The node that simulates the shipyard
stores the ship in Hangar's shared storage; another node loads it, for free; it is counted once in the
PCU; a node restart in the wait neither loses the ship nor stores it twice. With the slots taken the
World Authority refunds it, once, also over a restart in the wait and a graceful stop afterwards.

    OCT_TASK=SE-929C OCT_WORLD_SOURCE=<world from build_world.py> OCT_HANGAR_REPO=<hangar> \\
        <Test>/.venv/bin/python tests/shipyard_deadline_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe and Hangar on the nodes. About 30 minutes.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-929C")
import shipyard_turn_cluster as turn  # noqa: E402  (takes the bench's folder from argv)
from shipyard_turn_cluster import (  # noqa: E402
    CLIENT_ID,
    NODES,
    PAID,
    SHIP,
    STATION,
    Client,
    ClusterError,
    bench_module,
    rig,
    sa,
    tc,
    tp,
    tr,
    wa,
    wait_until,
)

CLEAR = 1
WINDOW = 3
SLOTS = 2
NEXT = turn.PAID.replace(SHIP, "Next Up")

wa.ROOT = Path.home() / f"cluster-runs/deadline-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def build_hangar() -> Path:
    """Hangar as a local plugin, next to what wa.prepare built"""
    repo = Path(os.environ["OCT_HANGAR_REPO"])
    out = wa.lc.PLUGINS / ".build" / "Hangar"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(repo / "ServerPlugin/ServerPlugin.csproj"),
         "-c", "Release", "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "Hangar"
    folder.mkdir(parents=True, exist_ok=True)
    for dll in out.glob("*Hangar*.dll"):
        shutil.copy2(dll, folder / dll.name)
    (folder / "Hangar.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        "<Id>17C928CE-4645-4E1B-9365-C3C46BFE061D</Id><FriendlyName>Hangar</FriendlyName>"
        "<RepoId>CometWorks/hangar</RepoId><Commit>local</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return folder


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

    def back(api):
        """The client after its node restarted: a new one if this one can't go on"""
        try:
            bench.wait_spawned(CLIENT_ID, timeout=120)
            to_table(api)
            return api
        except Exception as error:  # noqa: BLE001
            print(f"the client comes back: {error}", flush=True)
            bench.kill_client(CLIENT_ID)
            return join()

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

    def pcu(api) -> int:
        reply = " ".join(rig.chat(api, "!oct ship", 10))
        found = re.search(r"You have ([\d,]+) PCU", reply)
        assert found, reply
        return int(found[1].replace(",", ""))

    def here(api, name: str = SHIP) -> bool:
        return any(g.get("name") == name for g in api.list_grids())

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

    def leave_ship(api) -> int:
        """A paid ship is built and its turn ended; returns what it took"""
        before = money(api)
        mark = tp.latest(api)
        check("the order is in the queue", "is in the queue" in order(api, PAID))
        told = tp.wait_chat(api, "It is your turn", 60, mark)
        check("its turn", told, told)
        # Most of it loaded: the Store's stock and the account are for the rest
        print(
            probe(api, f"yardload {STATION} {turn.PLATE} {turn.PLATES - 20}"),
            flush=True,
        )
        mark = tp.latest(api)
        press(api)
        done = tp.wait_chat(api, "Take what is in", 400, mark)
        check("the ship is built", done, done)
        until(lambda: money(api) < before)
        paid = before - money(api)
        check("it cost something", paid > 0, paid)
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
        return paid

    def behind(api) -> tuple:
        """An order behind the ship; returns the warning and the chat before it"""
        mark = tp.latest(api)
        check("an order behind it", "is in the queue" in order(api, NEXT))
        return tp.wait_chat(api, "blocks the next order", 60, mark), mark

    def next_ends(api) -> None:
        """The order behind it gets its turn, which ends here"""
        check(
            "the waiting order goes on",
            until(
                lambda: "Your turn ends now"
                in " ".join(rig.chat(api, "!oct shipyard done", 10)),
                90,
                5,
            ),
        )
        until(
            lambda: "No ships in the queue"
            in tr.settled(api, "queue", "No ships in the queue"),
            60,
        )
        rig.chat(api, "!oct table remove", 5)

    def restart_here(api):
        """A restart of the node simulating the shipyard, the ship in its partition's save"""
        node = where()
        partition = wa._client_partition(wa._partitions(bench, node))

        def newest():
            rows = cluster.get(f"/registry/saves?partition={partition}") or []
            return max(
                ((r["SaveId"]["Generation"], r["SaveId"]["Sequence"]) for r in rows),
                default=None,
            )

        before = newest()
        cluster.node(node, f"/save-capture?partition={partition}", "POST")
        wait_until(lambda: newest() != before or None, 180, "a fresh save")
        cluster.restart_node(node)
        cluster.wait_ready(nodes=[node])
        return back(api)

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
        # As on the live cluster; the rest is Hangar's defaults
        (bench.run_root / f"{process}-data" / "Hangar.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?><PluginConfig>'
            "<AdminSafeZonesOnly>true</AdminSafeZonesOnly></PluginConfig>",
            encoding="utf-8",
        )
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    home = wait_until(where, 120, "the client's partition")
    away = NODES[1 - NODES.index(home)]

    # A reused run folder keeps the shared storage
    until(lambda: "empty" in hangar(api, "remove 1"), 30, 1)

    # --- stored by the node, over a node restart in the wait, counted once ---
    paid = leave_ship(api)
    owned = until(lambda: pcu(api))
    balance = money(api)
    told, mark = behind(api)
    check(
        "the warning says what will happen",
        told and "goes into your Hangar" in told and f"{paid:,} SC" in told,
        told,
    )
    api = restart_here(api)
    # The client can take longer to come back than the wait that starts over on the node
    check(
        "the ship isn't lost in the restart",
        until(lambda: here(api) or SHIP in hangar(api), 60),
    )
    check("stored by the node", until(lambda: SHIP in hangar(api), CLEAR * 60 + 120, 5))
    check("gone from the world", until(lambda: not here(api), 30))
    check("one entry", hangar(api).count(SHIP) == 1)
    check(
        "it counts as stored at the station",
        f"zone: {STATION}" in hangar(api, f"info {SHIP}"),
    )
    check("nothing is paid", money(api) == balance, money(api))
    check(
        "counted once in the PCU",
        until(lambda: pcu(api) == owned, 60, 5),
        f"{owned} -> {pcu(api)}",
    )
    next_ends(api)

    # --- loaded from another node, free ---
    to_node(away)
    reply = hangar(api, f"load {SHIP}")
    check("another node loads it", f"Loaded {SHIP}" in reply, reply)
    check("it is back", until(lambda: here(api), 30))
    check("free at the station", money(api) == balance, money(api))
    check(
        "still counted once",
        until(lambda: pcu(api) == owned, 60, 5),
        f"{owned} -> {pcu(api)}",
    )
    print(probe(api, f"removegrid {SHIP.replace(' ', '_')}"), flush=True)
    # A ship of bare armor can't be saved by hand: a grid with a cockpit
    print(probe(api, f"yardblocker {STATION}"), flush=True)
    reply = hangar(api, "save Blocker")
    check("no cool-down was started", "Saved" in reply, reply)

    # --- the second slot, while the player's own cool-down runs ---
    leave_ship(api)
    told, mark = behind(api)
    check("told again", told, told)
    check(
        "into the second slot",
        until(lambda: SHIP in hangar(api), CLEAR * 60 + 60, 5),
    )
    next_ends(api)

    # --- no slot: the World Authority refunds it, over a restart in the wait ---
    paid = leave_ship(api)
    balance = money(api)
    told, mark = behind(api)
    check("the warning has the amount", told and f"{paid:,} SC" in told, told)
    api = restart_here(api)
    check(
        "refunded",
        until(lambda: money(api) == balance + paid, CLEAR * 60 + 120, 5),
        f"{balance} + {paid} = {money(api)}",
    )
    check("the ship is gone", until(lambda: not here(api), 30))
    check("the Hangar is as it was", len(hangar(api).split("[")) - 1 == SLOTS)
    next_ends(api)

    # --- a graceful stop: all of it stays as it is ---
    balance = money(api)
    owned = until(lambda: pcu(api))
    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    check("no second refund", until(lambda: money(api) == balance, 60, 5), money(api))
    check("no ship back", not here(api))
    check("both entries kept", len(hangar(api).split("[")) - 1 == SLOTS)
    check(
        "the PCU is the same",
        until(lambda: pcu(api) == owned, 90, 5),
        f"{owned} -> {pcu(api)}",
    )


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = turn.build_probe()
    hangar_folder = build_hangar()
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
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-deadline")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD-DEADLINE",
        "SE-929: a ship past the deadline",
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
