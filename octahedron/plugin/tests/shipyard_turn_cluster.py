"""A player's turn at a shipyard on a local test cluster, through the cluster's scenario bench
(CometWorks/cluster Test/), SE-840: the queue and the sale are the World Authority's, the blocks and
the containers the node's that simulates the shipyard. A paid build's turn, a move of the station's
partition while the player loads and again in the last part of the turn, the deadline sale; a node
restart in a window nobody uses; a graceful stop and warm start in the last part of a turn; a towed
grid's breakdown, with and without room, and the chat command.

    OCT_TASK=SE-840C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/shipyard_turn_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, with the station probe on the nodes. About 40 minutes.
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

os.environ.setdefault("OCT_TASK", "SE-840C")
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

import station_tests as st  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402

STATION = "Alpha"
OCT = "Octahedron_Authority"
PLATE = "Component/SteelPlate"
GOLD = "Ingot/Gold"
WINDOW = 4
SHIP = "Turn Test"
BLOCKS = 8
PAID = rig.grid(
    SHIP,
    777000840000200,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(2)
        for z in range(BLOCKS // 2)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PLATES = BLOCKS * 25
NODES = ("node-1", "node-2")

wa.ROOT = Path.home() / f"cluster-runs/turn-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def build_probe() -> Path:
    """The station probe as a local plugin, next to what wa.prepare built"""
    plugin = Path(wa.__file__).resolve().parents[1]
    out = wa.lc.PLUGINS / ".build" / "StationProbe"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(plugin / "tests/station-probe/StationProbe.csproj"),
         "-c", "Release", "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "StationProbe"
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(out / "StationProbe.dll", folder / "StationProbe.dll")
    commit = subprocess.check_output(
        ["git", "-C", str(plugin), "rev-parse", "HEAD"], text=True
    ).strip()
    (folder / "StationProbe.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{st.PROBE[3]}</Id><FriendlyName>StationProbe</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
        f"<Commit>{commit}</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
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

    def until(what, timeout: float = 30, every: float = 1):
        end = time.monotonic() + timeout
        while True:
            value = what()
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

    def other(node: str) -> str:
        return NODES[1 - NODES.index(node)]

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

    def probe(api, args: str) -> str:
        return " ".join(rig.chat(api, f"!sprobe {args}", 6))

    def yard(api) -> dict[str, str]:
        return dict(re.findall(r"(\w+)=(\S+)", probe(api, f"yard {STATION}")))

    # The player's, whatever the share: no share in OCT, the faction's otherwise (SE-975, tested in
    # the plain tests). The bench player doesn't join OCT on a cluster (SE-1012).
    def theirs(state: dict) -> bool:
        return all(
            state.get(k, "").split("/")[0] not in ("", OCT)
            for k in ("cargo", "welders", "connectors")
        )

    def octs(state: dict) -> bool:
        return all(
            state.get(k, "").startswith(OCT + "/")
            for k in ("cargo", "welders", "connectors")
        )

    def stock(api) -> dict[str, float]:
        reply = probe(api, f"yardstock {STATION}")
        return {k: float(v) for k, v in re.findall(r"(\w+/\w+)=([\d.]+)", reply)}

    def money(api) -> int:
        lines = " ".join(rig.chat(api, "!oct money", 10))
        found = re.search(r"You: ([\d,]+) SC", lines)
        assert found, lines
        return int(found[1].replace(",", ""))

    def market(api, item: str) -> tuple[float, float, float]:
        have, ask, bid, _ = st.stock(api, STATION, item)
        assert have >= 0, f"{item} is not in the Store"
        return bid, ask, have

    def press(api) -> str:
        reply = ""
        # A shipyard joins its station's partition a little after a start, and a node just
        # restarted can be fenced from the World Authority for a moment
        for _ in range(12):
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order on {where()} -> {reply}", flush=True)
            # A character waiting in space runs out of oxygen in about 15 minutes
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

    def order(api) -> None:
        probe(api, "air")
        # The world's blueprint off the table first: the server refuses a player's removal of it
        # from an OCT table (SE-936), and the client's load would keep it
        tr.remove(api)
        mark = tp.latest(api)
        tr.load(api, PAID)
        tr.settled(api, "table", "paid")
        reply = press(api)
        if "Your quote for" in reply:
            reply = press(api)
        check("the order is in the queue", "is in the queue" in reply, reply)
        told = tp.wait_chat(api, "It is your turn", 60, mark)
        check("the player is told it is their turn", told, told)

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

    def free_again(api, what: str) -> None:
        check(f"{what}: OCT's blocks", wait_state(api, octs), yard(api))
        state = yard(api)
        check(f"{what}: nothing left", state.get("items") == "0", state.get("items"))
        check(
            f"{what}: unlocked", state.get("connected") == "0", state.get("connected")
        )
        rig.chat(api, "!oct table remove", 5)

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    home = wait_until(where, 120, "the client's partition")

    # --- a paid build, moved between nodes in the window and in the last part of the turn ---
    plate = market(api, PLATE)
    gold = market(api, GOLD)
    before = money(api)
    uranium = float(yard(api).get("uranium", -1))
    order(api)
    t_turn = time.monotonic()
    check("nothing is paid at the queue", money(api) == before, money(api))
    check("the blocks are the player's", wait_state(api, theirs), yard(api))
    state = yard(api)
    check(
        "the reactors stay OCT's",
        state["reactors"].startswith(OCT + "/"),
        state["reactors"],
    )
    check("the connectors don't trade", state["trading"] == "0", state["trading"])
    print(probe(api, f"yardload {STATION} {PLATE} {PLATES // 2}"), flush=True)
    print(probe(api, f"yardload {STATION} {GOLD} 20"), flush=True)
    to_node(other(home))
    check(f"moved to {where()}: still the player's", wait_state(api, theirs), yard(api))
    left = stock(api)
    check(
        "the load moved along",
        left.get(PLATE) == PLATES // 2 and left.get(GOLD) == 20,
        left,
    )
    mark = tp.latest(api)
    reply = press(api)
    check("the press starts it", "builds" in reply or "bought" in reply, reply)
    until(lambda: money(api) < before)
    paid = before - money(api)
    check(
        "the press paid for what is missing",
        paid >= (PLATES // 2) * plate[1],
        f"{paid}, {PLATES // 2} plates at {plate[1]}",
    )
    now = market(api, PLATE)
    check(
        "they came out of OCT's stock on the World Authority",
        now[2] == plate[2] - PLATES // 2,
        f"{plate[2]} -> {now[2]}",
    )
    done = tp.wait_chat(api, "Take what is in", 400, mark)
    check("it is built", done, done)
    state = yard(api)
    check(
        "the uranium stays in the reactors",
        float(state["uranium"]) >= uranium - 20 and state["elsewhere"] == "0",
        state,
    )
    check("still the player's for the taking", theirs(state), state)
    t_after = time.monotonic()
    to_node(home)
    check(
        f"moved back to {where()}: still the player's",
        wait_state(api, theirs),
        yard(api),
    )
    check("what is left moved along", stock(api).get(GOLD) == 20, stock(api))
    money2 = money(api)
    mark = tp.latest(api)
    told = tp.wait_chat(api, "are over", WINDOW * 60 + 60, mark)
    took = time.monotonic() - t_after
    check("the deadline ends the turn", told, told)
    check(
        "the move didn't start the deadline over",
        took <= WINDOW * 60 + 40,
        f"{took:.0f} s of {WINDOW * 60}",
    )
    until(lambda: money(api) != money2)
    want = 20 * gold[0]
    got = money(api) - money2
    check(
        "sold at OCT's bid, tax in it",
        got == want,
        f"{got}, want {want:.0f}",
    )
    now = market(api, GOLD)
    check("in OCT's stock", now[2] == gold[2] + 20, f"{gold[2]} -> {now[2]}")
    free_again(api, "deadline")
    print(probe(api, f"removegrid {SHIP.replace(' ', '_')}"), flush=True)

    # --- a node restart in a window nobody uses: the old deadline holds ---
    order(api)
    t_turn = time.monotonic()
    node = where()
    plate = market(api, PLATE)
    before = money(api)
    cluster.restart_node(node)
    cluster.wait_ready(nodes=[node])
    # The partition moved or the client lost its node: a new client if this one can't go on
    try:
        bench.wait_spawned(CLIENT_ID, timeout=120)
        to_table(api)
    except Exception as error:  # noqa: BLE001
        print(f"the client comes back: {error}", flush=True)
        bench.kill_client(CLIENT_ID)
        api = join()
    if time.monotonic() - t_turn < WINDOW * 60:
        check(
            "the restarted node keeps the player's blocks",
            wait_state(api, theirs, 30),
            yard(api),
        )
    # After the kill: a load since the last save dies with the node
    print(probe(api, f"yardload {STATION} {PLATE} 10"), flush=True)
    limit = t_turn + WINDOW * 60 + 60
    check(
        "the window ends at its old time",
        wait_state(api, octs, max(limit - time.monotonic(), 5)),
        yard(api),
    )
    until(lambda: money(api) > before)
    check("the load is sold", money(api) > before, f"{before} -> {money(api)}")
    now = market(api, PLATE)
    check("into OCT's stock", now[2] == plate[2] + 10, f"{plate[2]} -> {now[2]}")
    free_again(api, "window after a node restart")

    # --- a graceful stop and warm start in the last part of a turn ---
    order(api)
    print(probe(api, f"yardload {STATION} {PLATE} {PLATES}"), flush=True)
    print(probe(api, f"yardload {STATION} {GOLD} 5"), flush=True)
    mark = tp.latest(api)
    press(api)
    done = tp.wait_chat(api, "Take what is in", 400, mark)
    check("built for the restart", done, done)
    before = money(api)
    gold = market(api, GOLD)
    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    # The deadline may have passed while it was down: the turn ends at the first check then
    check(
        "after the warm start the turn ends by itself",
        wait_state(api, octs, WINDOW * 60 + 90),
        yard(api),
    )
    until(lambda: money(api) > before)
    check("what was left is sold", money(api) > before, f"{before} -> {money(api)}")
    now = market(api, GOLD)
    check(
        "and in OCT's stock after the start",
        now[2] == gold[2] + 5,
        f"{gold[2]} -> {now[2]}",
    )
    free_again(api, "warm start")
    print(probe(api, f"removegrid {SHIP.replace(' ', '_')}"), flush=True)

    # --- a towed grid's breakdown: at once, into the free shipyard ---
    wreck = re.search(r"WRECK (\d+)", probe(api, f"yardwreck {STATION}"))
    check("a wreck by the shipyard", wreck, wreck)
    mark = tp.latest(api)
    rig.chat(api, f"!oct shipyard tow {STATION} {wreck[1]}", 10)
    told = tp.wait_chat(api, "broke it down", 30, mark)
    check("at once, and the player is told", told, told)
    left = stock(api)
    check("its cargo came along", left.get(GOLD) == 100, left)
    check("the half block yields what it has", 50 < left.get(PLATE, 0) < 75, left)
    check("the player's turn", theirs(yard(api)), yard(api))
    before = money(api)
    reply = " ".join(rig.chat(api, "!oct shipyard done", 10))
    check("the chat command", "Your turn ends now" in reply, reply)
    until(lambda: money(api) > before)
    check("the rest is sold", money(api) > before, f"{before} -> {money(api)}")
    free_again(api, "breakdown")

    wreck = re.search(
        r"WRECK (\d+)",
        probe(api, f"yardwreck {STATION} Ingot/Gold=100,Component/Motor=10 over"),
    )
    before = money(api)
    mark = tp.latest(api)
    rig.chat(api, f"!oct shipyard tow {STATION} {wreck[1]}", 10)
    told = tp.wait_chat(api, "broke it down", 30, mark)
    check("no room: broken down", told, told)
    until(lambda: money(api) > before)
    check("what doesn't fit is sold at once", money(api) > before, money(api))
    check("and said so", "sold" in (told or ""), told)
    left = stock(api)
    check("the gold, dearer than the stone, stays", left.get(GOLD) == 100, left)
    check("as much stone as fits", left.get("Ore/Stone", 0) > 0, left)

    # --- busy: the player's own turn holds the shipyard, a tow is sold whole ---
    wreck = re.search(r"WRECK (\d+)", probe(api, f"yardwreck {STATION}"))
    before = money(api)
    mark = tp.latest(api)
    rig.chat(api, f"!oct shipyard tow {STATION} {wreck[1]}", 10)
    told = tp.wait_chat(api, "is busy", 30, mark)
    check("busy: all of it sold at once", told, told)
    until(lambda: money(api) > before)
    check("the money is there", money(api) > before, f"{before} -> {money(api)}")
    # Not this much stone on sale: past 1,000,000 SC a client with no Steam crashes
    probe(api, f"yardtake {STATION}")
    rig.chat(api, "!oct shipyard done", 10)
    free_again(api, "breakdown with no room")


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    probe_folder = build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe_folder}"
    wa.set_config(
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTurnMinutes=WINDOW,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=0,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        # OCT's excess would come back as an hourly payout, the only player being the test's
        MoneyOctCap=1_000_000_000_000,
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard-turn")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run, "P2-OCT-SHIPYARD-TURN", "SE-840: a turn at a shipyard", "3", results
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
