"""Wreck contracts from the grids of offline partitions that no node ever reported (SE-821), read from the
gateway's GET /registry/partitions/offline/grids, on the same bench and world as contracts_cluster.py.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/contracts_offline_grids.py CLUSTER_TEST_DIR     (from plugin/)

The contracts are off while the far grids go offline at rest and everything else on a graceful stop, so
no report names any of them. They are on after the warm start, with only Alpha's shipyard woken. So these
partitions went offline before the plugin ran and never woke since.

- The rows: at rest and on a drain offer, the blocks' mass of movable and static grids, the group of a grid on a
  rotor. A row read back from a Final save has only what a save can tell.
- The armor wrecks at once. The hulls without power, and the group on a rotor as one contract, at 7.01 days.
  Rewards by the rows' mass. Never the live hull, a lane computer, a station or the row from a Final.
- A graceful stop and warm start: the same contracts, nothing posted twice.
- The zone a row can't tell: its node reports it once it wakes, and the contract is withdrawn.
- A taken wreck: the player near it wakes it, it stays taken, gone with them near is done and paid. Nobody
  took it: withdrawn.

OCT_CLUSTER_WITHOUT_ROWS=1 on a cluster older than the route (0.1.25): no wreck, one line in the log, no error.

Run on polaris on 2026-11-01: 43/43 checks. Most of the time is the 5 minute import grace on grid deletes.
"""

from __future__ import annotations

import json
import math
import os
import re
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import contracts_cluster as base
from contracts_cluster import (
    FAR,
    GRIDS,
    KNOWN_LINE,
    LARGE_ID,
    armor,
)
import wa_economy_cluster as wa
from wa_economy_cluster import (
    CLIENT_ID,
    Client,
    bench_module,
    link_mods,
    number,
    rig,
    set_config,
    wait_until,
)
from test_contracts import HEADER, LINE, position

WITHOUT_ROWS = os.environ.get("OCT_CLUSTER_WITHOUT_ROWS") == "1"
PER_TONNE = 100
STATOR, ROTOR = LARGE_ID + 22, LARGE_ID + 23
GRIDS |= {
    "Rotor Hull": LARGE_ID + 20,
    "Rotor Arm": LARGE_ID + 21,
    "Final Wreck": LARGE_ID + 24,
}
ARMOR_ONLY = {"Wreck Big", "Wreck Mid", "Wreck Small", "Zone Wreck"}
WITHOUT_POWER = {"Dead Hull", "Static Hull", "Rotor Hull"}
NAMES = [*FAR, "Rotor Hull", "Final Wreck"]
SHIPYARD = "Alpha Station Shipyard"


def add_more_grids() -> None:
    """A hull with an arm on a rotor, each with an empty battery, and a wreck for the row from a Final"""
    base.add_grids()
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    # far_point's spots crowd together this far along its line: the game pulls a grid back to 500 km from
    # the center, and these two have to keep their own partitions. Well away from everything else.
    hull_at = 40_000.0, 40_000.0, -480_000.0
    final_at = -40_000.0, -40_000.0, -480_000.0
    empty = "<CurrentStoredPower>0</CurrentStoredPower>"
    grids = [
        rig.grid(
            "Rotor Hull",
            GRIDS["Rotor Hull"],
            [
                rig.block(
                    "MyObjectBuilder_MotorAdvancedStator",
                    "LargeAdvancedStator",
                    (2, 1, 2),
                    f"<EntityId>{STATOR}</EntityId><TopBlockId>{ROTOR}</TopBlockId>",
                ),
                *armor(30),
            ],
            position=hull_at,
        ),
        rig.grid(
            "Rotor Arm",
            GRIDS["Rotor Arm"],
            [
                rig.block(
                    "MyObjectBuilder_MotorAdvancedRotor",
                    "LargeAdvancedRotor",
                    (0, 0, 0),
                    f"<EntityId>{ROTOR}</EntityId><ParentEntityId>{STATOR}</ParentEntityId>",
                ),
                rig.block(
                    "MyObjectBuilder_BatteryBlock",
                    "LargeBlockBatteryBlock",
                    (0, 1, 0),
                    empty,
                ),
            ],
            position=(hull_at[0] + 5, hull_at[1] + 5, hull_at[2] + 5),
        ),
        rig.grid(
            "Final Wreck",
            GRIDS["Final Wreck"],
            armor(35),
            position=final_at,
        ),
    ]
    text = text.replace("</SectorObjects>", "".join(grids) + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()
    nodes = ("node-1", "node-2")
    processes = ["wa-1", *nodes]
    config = {"npc.sweepSeconds": "0"}

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def configure(**values) -> None:
        config.update({k.replace("_", "."): v for k, v in values.items()})
        cluster.handover_config(config)

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
        return {name for name in NAMES if named(found, name, "wreck")}

    def wrecks(found: dict) -> dict[int, str]:
        return {
            c[0]: c[2] for list_ in found.values() for c in list_ if c[1] == "wreck"
        }

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

    def rows() -> dict[str, dict]:
        """The gateway's grid rows by grid name"""
        answer = cluster.get("/registry/partitions/offline/grids")
        return {r["grid"]["n"]: r["grid"] for r in answer["grids"]}

    def wake_id(grid: int) -> None:
        for partition, members in offline().items():
            if grid in members:
                print(
                    cluster.post(
                        f"/registry/partitions/offline/load?partition={partition}&reason=test"
                    ),
                    flush=True,
                )

    def wake(name: str) -> str:
        # A node just started isn't serving yet, and a load asked for then is lost
        def attempt():
            wake_id(GRIDS[name])
            return owner(name)

        return wait_until(attempt, 180, f"{name} on a node", 10)[0]

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

    def warm_start() -> None:
        check("graceful stop", cluster.stop(grace=240))
        wa.lc.PLAN.write_text("")
        wa.lc.OUT.write_text("")
        probe.step = 0
        cluster.start(processes)
        cluster.wait_ready(nodes=processes)
        configure(offline_enabled="false")

    def reward(*names: str) -> int:
        have = rows()
        return math.ceil(sum(have[n]["m"] for n in names) / 1000 * PER_TONNE)

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in processes:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)

    # --- the plugin's contracts are off: partitions go offline, no report names them ---
    configure(offline_minOnlineSeconds="0", offline_graceSeconds="0")
    wait_until(
        lambda: all(any(GRIDS[n] in m for m in offline().values()) for n in NAMES),
        420,
        "the far grids offline",
    )
    check("no contract report ran", "Contracts:" not in log("wa-1"), "")

    if not WITHOUT_ROWS:
        have = wait_until(
            lambda: (r := rows()) and all(n in r for n in NAMES) and r,
            120,
            "the far grids' rows",
        )
        big, live, static = have["Wreck Big"], have["Live Hull"], have["Static Hull"]
        check(
            "at rest: an armor wreck's facts",
            big["m"] == 60 * 500
            and big["g"] == big["id"]
            and big["o"] == []
            and big["f"] is False
            and big["p"] is False
            and big["t"] is False,
            big,
        )
        check("a charged battery: powered", live["p"] is True, live)
        check(
            "the same blocks, the same mass, movable or static",
            live["m"] == static["m"] > 0 and live["s"] != static["s"],
            (live["m"], static["m"]),
        )
        check(
            "a grid on a rotor: one physical group",
            have["Rotor Hull"]["g"] == have["Rotor Arm"]["g"] == GRIDS["Rotor Hull"],
            (have["Rotor Hull"], have["Rotor Arm"]),
        )

        # Out and back without its grids in the offer, as from an older node: read from its Final
        configure(offline_offerGrids="false")
        wake("Final Wreck")
        old = wait_until(
            lambda: (r := rows().get("Final Wreck")) and "f" not in r and r,
            300,
            "the row read from its Final",
        )
        check(
            "a row from a Final: what a save can tell, the rest absent",
            old["b"] > 0 and not {"m", "g", "f", "p", "t"} & set(old),
            old,
        )
        configure(offline_offerGrids="true")

    # --- a cluster stop takes all the rest offline on drain offers; the contracts come on with the start ---
    check("graceful stop", cluster.stop(grace=240))
    set_config(ContractsEnabled="true")
    wa.lc.PLAN.write_text("")
    wa.lc.OUT.write_text("")
    probe.step = 0
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    configure(offline_enabled="false")

    if not WITHOUT_ROWS:
        have = rows()
        drained = {
            n: have.get(n)
            for n in ("Bounty Target", "Test Derelict", "Derelict Arm", SHIPYARD)
        }
        check(
            "on a drain offer: the blocks' mass of movable and static grids",
            all(r and r["m"] > 0 for r in drained.values()),
            drained,
        )
        check(
            "on a drain offer: the group of a grid on a rotor",
            drained["Test Derelict"]["g"] == drained["Derelict Arm"]["g"],
            drained,
        )
        check(
            "lane computers are among the rows, for the checks below",
            sum(n.startswith("TL ") for n in have) >= 20,
            len(have),
        )

    # Only the partition of the shipyard and the derelict at it wakes: its report gives the contracts a
    # station to post at
    wake("Yard Derelict")

    if WITHOUT_ROWS:
        known = wait_until(
            lambda: (m := KNOWN_LINE.search(contracts()[1])) and int(m[1]) >= 1 and m,
            300,
            "a report",
        )
        print(known[0], flush=True)
        wait_until(lambda: "no offline grid rows" in log("wa-1"), 150, "the log line")
        found = post()
        check(
            "an older cluster: no wreck from unreported grids",
            not wreck_names(found),
            found,
        )
        check(
            "the table is still read: partitions known",
            KNOWN_LINE.search(contracts()[1])[2].isdigit(),
            contracts()[1],
        )
        check(
            "one log line, no error",
            log("wa-1").count("no offline grid rows") == 1
            and "offline partitions could not be read" not in log("wa-1"),
            "",
        )
        check("graceful stop at the end", cluster.stop(grace=240))
        return

    # --- at once: the armor wrecks ---
    found = wait_until(
        lambda: (f := post()) and ARMOR_ONLY <= wreck_names(f) and f,
        300,
        "the armor wrecks",
        15,
    )
    print(contracts()[1], flush=True)
    check(
        "a wreck contract for each armor wreck no report named",
        wreck_names(found) == ARMOR_ONLY,
        wreck_names(found),
    )
    for name in ARMOR_ONLY:
        check(
            f"{name}: pays by the row's mass",
            [c[3] for c in named(found, name, "wreck")] == [reward(name)],
            (named(found, name), reward(name)),
        )

    shift_clock(6.9)
    found = post()
    check(
        "6.9 days offline: not the hulls without power",
        wreck_names(found) == ARMOR_ONLY,
        wreck_names(found),
    )
    shift_clock(7.01)
    found = post()
    check(
        "7.01 days: the hulls without power, static too, and no other of the far grids",
        wreck_names(found) == ARMOR_ONLY | WITHOUT_POWER,
        wreck_names(found),
    )
    check(
        "the group on a rotor: one contract, on its bigger grid, paying for both",
        [c[3] for c in named(found, "Rotor Hull", "wreck")]
        == [reward("Rotor Hull", "Rotor Arm")]
        and not named(found, "Rotor Arm"),
        (named(found, "Rotor"), reward("Rotor Hull", "Rotor Arm")),
    )
    check(
        "the static hull pays by its blocks' mass",
        [c[3] for c in named(found, "Static Hull", "wreck")] == [reward("Static Hull")],
        named(found, "Static Hull"),
    )
    check("no lane computer", not named(found, "TL "), named(found, "TL "))
    check("no station", not named(found, "Station Shipyard"), "")
    check("not the live hull", not named(found, "Live Hull"), "")
    check("not the row from a Final", not named(found, "Final Wreck"), "")
    posted = wrecks(found)
    print(posted, flush=True)
    check(
        "nothing twice",
        all(len(named(found, n, "wreck")) == 1 for n in ARMOR_ONLY | WITHOUT_POWER),
        posted,
    )
    found = post()
    check("posted again: the same contracts", wrecks(found) == posted, wrecks(found))

    # --- a restart ---
    warm_start()
    shift_clock(7.01)
    found = wait_until(
        lambda: (f := post()) and wrecks(f) and f, 240, "the contracts back", 15
    )
    check(
        "warm start: the same contracts, nothing posted twice",
        wrecks(found) == posted,
        f"{posted} -> {wrecks(found)}",
    )
    shift_clock(0)

    # --- awake: what the row couldn't tell ---
    wake("Zone Wreck")
    check(
        "a zone a row can't tell: withdrawn once its node reports it",
        wait_until(
            lambda: not named(contracts()[0], "Zone Wreck"),
            300,
            "the zone's report",
            15,
        ),
        named(contracts()[0], "Zone Wreck"),
    )

    # --- a player takes one ---
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
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    at = position("Wreck Big", sector)
    big = named(contracts()[0], "Wreck Big", "wreck")[0]
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {big[0]} {identity}"))
    check("take the wreck", "Success" in reply, reply)
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(at[0] + 300, at[1], at[2])
    wait_until(lambda: owner("Wreck Big"), 180, "Wreck Big awake")
    wake("Wreck Small")
    wait_until(
        lambda: int(KNOWN_LINE.search(contracts()[1])[1]) >= 1,
        120,
        "a report after the wake",
    )
    # Past a report and a check after it woke
    time.sleep(70)
    found = contracts()[0]
    check(
        "awake with the hunter near: still Active",
        [c[2] for c in named(found, "Wreck Big", "wreck")] == ["Active"],
        named(found, "Wreck Big"),
    )
    check(
        "awake with nobody on it: still open",
        [c[2] for c in named(found, "Wreck Small", "wreck")] == ["Inactive"],
        named(found, "Wreck Small"),
    )
    delete("Wreck Big")
    delete("Wreck Small")
    end = time.monotonic() + 360
    now = balances(api)
    while time.monotonic() < end and now[1] != before[1] + big[3]:
        time.sleep(5)
        now = balances(api)
    check(
        "gone with the hunter near: OCT paid the player",
        now[0] == before[0] - big[3] and now[1] == before[1] + big[3],
        f"{before} -> {now}, reward {big[3]:,}",
    )
    check(
        "nobody took it: withdrawn",
        wait_until(
            lambda: not named(contracts()[0], "Wreck Small"), 240, "the withdrawal", 15
        ),
    )

    bench.kill_client(CLIENT_ID)
    check("graceful stop at the end", cluster.stop(grace=240))
    for process in processes:
        failures = re.findall(r"\[Octahedron\].*(?:failed|Failed).*", log(process))
        check(f"{process}: no Octahedron failure logged", not failures, failures[:3])
        check(
            f"{process}: the offline rows were read without an error",
            "offline partitions could not be read" not in log(process),
        )


run.administrators = [CLIENT_ID]


def main() -> int:
    # Its computers are the static, ownerless grids the wreck rule has to leave alone
    wa.pl.MODS[3816175430] = "Trade Lanes MP"
    wa.pl.MOD_ITEMS += (
        '<ModItem FriendlyName="Trade Lanes MP"><Name>3816175430.sbm</Name>'
        "<PublishedFileId>3816175430</PublishedFileId>"
        "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    run.world_template = wa.prepare()
    add_more_grids()
    set_config(
        ContractsEnabled="false",
        MoneyEnabled="true",
        ContractSalvageMinBlocks=1,
        ContractSalvageRewardPerTonne=PER_TONNE,
        # The hourly split would change the balances the test compares
        MoneySubsidyPerActivePlayer=0,
        ContractWreckContracts=30,
        ContractWreckPool=30,
        ContractBountiesPerStation=0,
        ContractSalvagePerStation=0,
        MarketEnabled="false",
    )
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-offline-grids"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-OFFLINE-GRIDS",
        "SE-821: wreck contracts from the rows of offline partitions",
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
