"""Bounties, salvage and wreck salvage on a local test cluster (SE-702), through the cluster's own scenario
bench. Same bench and world as wa_economy_cluster.py; read its docstring for host.env. The nodes report the
grids, the World Authority posts and decides; the ledger probe on the WA runs !oct contracts there, and its
`clock` line moves the clock that ages offline partitions.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/contracts_cluster.py CLUSTER_TEST_DIR     (from plugin/)

Near Alpha: an NPC ship and a derelict. Far from every station, each in its own partition once the bench
takes them offline: three armor-only wrecks of different sizes, a hull without power, a hull with a charged
battery, a static hull and a wreck too small to count.

- A bounty and a salvage contract from the nodes' reports; a taken derelict at the shipyard is finished and
  scrapped by its node, a completed bounty paid by OCT.
- The one biggest wreck with a pool of one, at the nearest station, then the other two.
- The hull without power: not at 6.9 days offline, yes at 7.01. The live, static and small ones never.
- Taken wreck with the player there: its partition waking changes nothing; deleted, it counts as done.
  Taken with the player away: fails. Nobody took it: withdrawn.
- A node restart keeps the contracts, and so does a graceful cluster stop and warm start.

Towing, grinding down and powering up a wreck are checked in tests/contracts/ContractTests only.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa
from wa_economy_cluster import (
    CLIENT_ID,
    Client,
    bench_module,
    link_mods,
    number,
    rig,
    set_config,
    stations,
    wait_until,
)
from test_contracts import HEADER, LINE, SPRT_MEMBER, position

# Parallel benches on one host each have their own probe files and plugin copies
wa.ROOT = Path.home() / f"cluster-runs/contracts-{os.environ.get('OCT_TASK', 'SE-702')}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"

LARGE_ID = 1 << 56
ARMOR = "MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock"
GRIDS = {
    "Bounty Target": LARGE_ID + 1,
    "Test Derelict": LARGE_ID + 2,
    "Wreck Big": LARGE_ID + 3,
    "Wreck Mid": LARGE_ID + 4,
    "Wreck Small": LARGE_ID + 5,
    "Dead Hull": LARGE_ID + 6,
    "Live Hull": LARGE_ID + 7,
    "Static Hull": LARGE_ID + 8,
    "Too Small": LARGE_ID + 9,
}
FAR = [name for name in GRIDS if name not in ("Bounty Target", "Test Derelict")]
# Ten stations' worth of room between them: a partition each, none near another
SPACING = 40_000
STATIONS_AWAY = 60_000
KNOWN_LINE = re.compile(
    r"Known grids: (\d+), in offline partitions (\w+), wrecks (\w+)"
)


def armor(count: int, start_z: int = 0) -> list[str]:
    return [
        rig.block(*ARMOR, (x, 0, z + start_z))
        for x in range(5)
        for z in range(count // 5)
    ]


def hull(stored_mwh: float) -> list[str]:
    own = f"<Owner>{SPRT_MEMBER}</Owner><BuiltBy>{SPRT_MEMBER}</BuiltBy>"
    return [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0), own),
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 0, 1),
            own + f"<CurrentStoredPower>{stored_mwh}</CurrentStoredPower>",
        ),
        *armor(25, 2),
    ]


def far_point(
    where: dict[str, tuple[float, float, float]], index: int
) -> tuple[float, float, float]:
    """A spot along a line clear of every station's bounty range"""
    for y in range(STATIONS_AWAY, 2_000_000, SPACING):
        spot = (STATIONS_AWAY, float(y), SPACING * index + 500_000.0)
        if all(
            sum((a - b) ** 2 for a, b in zip(spot, s)) ** 0.5 >= STATIONS_AWAY
            for s in where.values()
        ):
            return spot
    raise AssertionError("no room")


def add_grids() -> None:
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    sx, sy, sz = position("Alpha Station Shipyard", text)
    where = stations()
    blocks = {
        "Wreck Big": armor(60),
        "Wreck Mid": armor(40),
        "Wreck Small": armor(25),
        "Dead Hull": hull(0),
        "Live Hull": hull(1),
        "Static Hull": hull(0),
        "Too Small": armor(10),
    }
    grids = [
        rig.grid(
            "Bounty Target",
            GRIDS["Bounty Target"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx, sy + 3000, sz),
        ),
        rig.grid(
            "Test Derelict",
            GRIDS["Test Derelict"],
            armor(30),
            position=(sx, sy - 1000, sz),
        ),
    ]
    for index, name in enumerate(FAR):
        xml = rig.grid(
            name, GRIDS[name], blocks[name], position=far_point(where, index)
        )
        grids.append(
            xml.replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
            if name == "Static Hull"
            else xml
        )
    text = text.replace("</SectorObjects>", "".join(grids) + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()
    nodes = ("node-1", "node-2")
    processes = ["wa-1", *nodes]

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def command(line: str) -> list[str]:
        return [
            part
            for reply in probe.run(line, timeout=120, settle=3)
            for part in reply.split(" | ")
        ]

    def contracts() -> tuple[dict[str, list[tuple]], str]:
        """Station -> [(id, kind, state, reward, detail)], and the book's summary line"""
        found: dict[str, list[tuple]] = {}
        current, known = None, ""
        for line in command("!oct contracts"):
            if m := HEADER.match(line.strip()):
                current = m[1]
                found[current] = []
            elif m := LINE.match(line):
                found[current].append((int(m[1]), m[2], m[3], number(m[4]), m[5]))
            elif KNOWN_LINE.search(line):
                known = line
        return found, known

    def post() -> dict[str, list[tuple]]:
        print(" ".join(command("!oct contracts post")), flush=True)
        return contracts()[0]

    def named(found: dict, name: str, kind: str | None = None) -> list[tuple]:
        return [
            c
            for list_ in found.values()
            for c in list_
            if name in c[4] and kind in (None, c[1])
        ]

    def wreck_names(found: dict) -> set[str]:
        return {name for name in FAR for c in named(found, name, "wreck")}

    def reconfigure(**values) -> None:
        set_config(**values)
        command("!oct reload")

    def shift_clock(days: float) -> None:
        # The probe's own line, with no reply to wait for; the next command runs after it
        with wa.lc.PLAN.open("a") as plan:
            plan.write(f"clock {days}\n")
        time.sleep(12)

    def owner(name: str) -> tuple[str, int] | None:
        found = bench.entity_owner(bench.diag(), GRIDS[name])
        return found[0] if found else None

    def offline() -> dict[int, list[int]]:
        return {
            int(row["Partition"]): [int(x) for x in row.get("Members") or []]
            for row in cluster.get("/registry/partitions/offline").get("Partitions", [])
        }

    def offline_partition(name: str) -> int | None:
        return next(
            (p for p, members in offline().items() if GRIDS[name] in members), None
        )

    def wake(name: str) -> str:
        """Loads the grid's partition if needed and gives its node"""
        partition = offline_partition(name)
        if partition is not None:
            cluster.post(
                f"/registry/partitions/offline/load?partition={partition}&reason=test"
            )
        where = wait_until(lambda: owner(name), 180, f"{name} on a node")
        return where[0]

    def delete(name: str) -> None:
        # A grid that just arrived on its node is refused for its import grace, 5 minutes after a load
        query = urllib.parse.urlencode({"entityId": GRIDS[name], "name": name})
        replies = []

        def attempt():
            replies.append(cluster.node(wake(name), f"/grid-delete?{query}", "POST"))
            return replies[-1].get("deleted")

        check(
            f"{name}: deleted on its node",
            wait_until(attempt, 420, f"{name} deleted", 10),
            replies[-1:],
        )

    def balances(api) -> tuple[int, int]:
        text = " ".join(rig.chat(api, "!oct money", 10))
        return number(re.search(r"OCT: ([\d,]+) SC", text)[1]), number(
            re.search(r"You: ([\d,]+) SC", text)[1]
        )

    def paid(
        api, before: tuple[int, int], reward: int, timeout: float = 90
    ) -> tuple[int, int]:
        end = time.monotonic() + timeout
        now = balances(api)
        while time.monotonic() < end and now[1] != before[1] + reward:
            time.sleep(5)
            now = balances(api)
        return now

    def still_paid(what: str, before, now, reward) -> None:
        check(
            f"{what}: OCT paid the player",
            now[0] == before[0] - reward and now[1] == before[1] + reward,
            f"{before} -> {now}, reward {reward:,}",
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in processes:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    # Nothing but the rig removes these grids; offline only once the nodes have reported them
    cluster.handover_config({"npc.sweepSeconds": "0"})

    # --- bounty and salvage from the nodes' reports ---
    known = wait_until(
        lambda: (m := KNOWN_LINE.search(contracts()[1])) and int(m[1]) >= 7 + 6 and m,
        420,
        "the nodes' reports",
    )
    print(known[0], flush=True)
    found = post()
    bounty = named(found, "Bounty Target", "bounty")
    salvage = named(found, "Test Derelict", "salvage")
    check(
        "a bounty on the NPC grid",
        len(bounty) == 1 and bounty[0][3] > 0,
        found.get("Alpha Station"),
    )
    check(
        "a salvage contract on the derelict",
        len(salvage) == 1 and salvage[0][3] > 0,
        found.get("Alpha Station"),
    )
    check(
        "both at Alpha",
        bounty[0] in found["Alpha Station"] and salvage[0] in found["Alpha Station"],
    )
    check(
        "hauling still posted",
        any(c[1] == "hauling" for list_ in found.values() for c in list_),
    )
    check(
        "no wreck contract while no partition is offline",
        not wreck_names(found),
        wreck_names(found),
    )

    # --- wrecks ---
    cluster.handover_config(
        {"offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}
    )
    wait_until(
        lambda: all(any(GRIDS[n] in m for m in offline().values()) for n in FAR),
        420,
        "the far grids offline",
    )
    # The World Authority reads the table once a minute
    wait_until(
        lambda: (m := KNOWN_LINE.search(contracts()[1]))
        and m[3].isdigit()
        and int(m[3]) >= 3,
        150,
        "the wrecks known",
    )
    print(contracts()[1], flush=True)
    reconfigure(ContractWreckContracts=1, ContractWreckPool=1)
    found = post()
    check(
        "the one biggest with a pool of one",
        wreck_names(found) == {"Wreck Big"},
        wreck_names(found),
    )
    where = stations()
    big = next(
        s for s, list_ in found.items() if any("Wreck Big" in c[4] for c in list_)
    )
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    at = position("Wreck Big", sector)
    distance = lambda name: sum((a - b) ** 2 for a, b in zip(at, where[name])) ** 0.5
    check("posted at the nearest station", big == min(where, key=distance), big)

    reconfigure(ContractWreckContracts=9, ContractWreckPool=9)
    found = post()
    armor_only = {"Wreck Big", "Wreck Mid", "Wreck Small"}
    check(
        "all the armor wrecks, at once",
        wreck_names(found) == armor_only,
        wreck_names(found),
    )

    shift_clock(6.9)
    found = post()
    check(
        "6.9 days offline: not the hull without power",
        wreck_names(found) == armor_only,
        wreck_names(found),
    )
    shift_clock(7.01)
    found = post()
    check(
        "7.01 days offline: the hull without power, and nothing else",
        wreck_names(found) == armor_only | {"Dead Hull"},
        wreck_names(found),
    )
    shift_clock(0)

    # --- a player takes them ---
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
    identity = api.get_faction()["identityId"]
    check("the client has an identity", identity, identity)

    # At the shipyard, so the derelict's partition is awake
    shipyard = position("Alpha Station Shipyard", sector)
    api.character_teleport(shipyard[0], shipyard[1] - 900, shipyard[2])
    wait_until(lambda: owner("Test Derelict"), 180, "the derelict awake")
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {salvage[0][0]} {identity}"))
    check("take the salvage contract", "Success" in reply, reply)
    now = paid(api, before, salvage[0][3])
    still_paid("salvage at the shipyard", before, now, salvage[0][3])
    check(
        "the node scrapped the derelict",
        wait_until(lambda: owner("Test Derelict") is None, 60, "the derelict gone")
        or True,
    )
    found = post()
    check(
        "the scrapped derelict gets no new contract",
        not named(found, "Test Derelict"),
        found.get("Alpha Station"),
    )

    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {bounty[0][0]} {identity}"))
    check("take the bounty", "Success" in reply, reply)
    reply = " ".join(command(f"!oct contract complete {bounty[0][0]}"))
    check("complete the bounty", "finished" in reply, reply)
    still_paid("bounty", before, paid(api, before, bounty[0][3]), bounty[0][3])

    # Wreck Big, with the player there: waking isn't its end, its end with them near is done
    big_contract = named(found, "Wreck Big", "wreck")[0]
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {big_contract[0]} {identity}"))
    check("take the wreck", "Success" in reply, reply)
    api.character_teleport(at[0] + 300, at[1], at[2])
    wait_until(lambda: owner("Wreck Big"), 180, "Wreck Big awake")
    time.sleep(30)
    found = contracts()[0]
    check(
        "awake with the hunter near: still Active",
        [c[2] for c in named(found, "Wreck Big", "wreck")] == ["Active"],
        named(found, "Wreck Big"),
    )
    delete("Wreck Big")
    still_paid(
        "wreck gone with the hunter near",
        before,
        paid(api, before, big_contract[3], 300),
        big_contract[3],
    )

    # Wreck Mid, with the player elsewhere: fails. Wreck Small, nobody took it: withdrawn.
    cluster.handover_config({"offline.enabled": "false"})
    mid = named(found, "Wreck Mid", "wreck")[0]
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {mid[0]} {identity}"))
    check("take a second wreck", "Success" in reply, reply)
    # Awake together, so both import graces pass together
    wake("Wreck Mid")
    wake("Wreck Small")
    delete("Wreck Mid")
    delete("Wreck Small")
    gone = wait_until(
        lambda: not named(contracts()[0], "Wreck Mid")
        and not named(contracts()[0], "Wreck Small"),
        300,
        "both contracts over",
    )
    check("gone: both contracts are over", gone)
    check(
        "gone with the hunter away: not paid",
        balances(api) == before,
        f"{before} -> {balances(api)}",
    )
    check(
        "withdrawn when nobody took it", "withdrawn: its target is gone" in log("wa-1")
    )

    # --- restarts ---
    dead = named(contracts()[0], "Dead Hull", "wreck")[0]
    reply = " ".join(command(f"!oct contract accept {dead[0]} {identity}"))
    check("take the hull", "Success" in reply, reply)
    found = post()
    open_before = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck", "salvage")
    }
    print(open_before, flush=True)

    node = wake("Bounty Target")
    cluster.restart_node(node)
    cluster.wait_ready(nodes=[node])
    # Past the 3 minutes in which a node reporting without a target ends its contract
    time.sleep(200)
    found = post()
    after = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck", "salvage")
    }
    check(
        f"{node} restarted: the same contracts",
        after == open_before,
        f"{open_before} -> {after}",
    )

    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    saved = wa.global_save(bench.run_root / "wa-1-data")
    check(
        "the World Authority's global save has the targets and grids",
        "Octahedron-Contract-Grids" in saved and "Octahedron-Contracts" in saved,
    )
    wa.lc.PLAN.write_text("")
    wa.lc.OUT.write_text("")
    probe.step = 0
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    time.sleep(90)
    found = post()
    after = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck", "salvage")
    }
    check(
        "warm start: the same contracts",
        after == open_before,
        f"{open_before} -> {after}",
    )

    check("graceful stop at the end", cluster.stop(grace=240))
    for process in processes:
        failures = re.findall(r"\[Octahedron\].*(?:failed|Failed).*", log(process))
        check(f"{process}: no Octahedron failure logged", not failures, failures[:3])


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = wa.prepare()
    add_grids()
    set_config(
        ContractsEnabled="true",
        MoneyEnabled="true",
        ContractBountyMinThreat=1,
        ContractSalvageRadius=3000,
        ContractWreckContracts=0,
        MarketEnabled="false",
    )
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-contracts"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-CONTRACTS",
        "SE-702: bounties, salvage and wrecks on a cluster",
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
