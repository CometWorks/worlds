"""SE-1358: world patches of the Octahedron plugin on a local test cluster (CometWorks/cluster Test/
scenario bench, the setup of tests/wa_economy_cluster.py: WA + 2 nodes + one headless admin client).

The world is one built before the space elevators (OCT_WORLD_SOURCE, a build of the commit the live
cluster was wiped at); the config is this working copy's, with the WorldPatches entry that adds the
elevators' four lane computers, and its blueprint staged into the world's Storage the way Deploy
Octahedron stages it. Every process and the client get Trade Lanes MP, whose blocks the computers are.

  1. At the first start the World Authority finds the patch missing from its ledger and has a
     regular node add the grids: both log APPLIED, the node with the grids' new entity ids, and the
     grids are in partitions of that node.
  2. After a graceful stop and start the ledger has the patch: nothing is added again.
  3. The admin client goes to each grid's place: a node loads it from its partition, so it was saved,
     and there is one grid of each name there.

    OCT_TASK=SE-1358c OCT_WORLD_SOURCE=<world of 70fcefb> \\
        <Test>/.venv/bin/python tests/world_patch_cluster.py <Test>     (from plugin/)

Every result is in the bench's result.json; the summary at the end lists what each check found.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

os.environ.setdefault("OCT_TASK", "SE-1358c")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import CLIENT_ID, Client, bench_module, rig  # noqa: E402

from test_elevator_entry import TRADE_LANES, with_trade_lanes  # noqa: E402

NODES = ("node-1", "node-2")
PROCESSES = ["wa-1", *NODES]
FOUND: dict = {}


def ledger_ids(lines: list[str]) -> set[str]:
    """The ids of the last LEDGER line: '?<id>' for a patch in doubt"""
    return (
        set(lines[-1].split(" ", 1)[1].split(",")) - {""}
        if lines and " " in lines[-1]
        else set()
    )


def link_mods(folder) -> None:
    """The bench's mods and Trade Lanes MP, whose blocks the lane computers are: without it the game
    drops their grids"""
    wa.link_mods(folder)
    rig.link_mods(folder, {TRADE_LANES: "Trade Lanes MP"})


def patches() -> list[tuple[str, dict[str, tuple[float, float, float]]]]:
    """(id, {grid name: position}) of each WorldPatches entry of the config"""
    config = ET.parse(rig.CONFIG_SOURCE).getroot()
    out = []
    for entry in config.iterfind("WorldPatches/WorldPatchEntry"):
        blueprint = ET.parse(
            rig.CONFIG_STORAGE / "Octahedron" / entry.findtext("Grids")
        ).getroot()
        grids = {}
        for grid in blueprint.iter("CubeGrid"):
            p = grid.find("PositionAndOrientation/Position")
            grids[grid.findtext("DisplayName")] = tuple(float(p.get(a)) for a in "xyz")
        out.append((entry.findtext("Id"), grids))
    return out


def run(bench) -> None:
    cluster = bench.cluster
    wanted = patches()

    def check(label, passed, detail="") -> bool:
        print(
            f"{'PASS' if passed else 'FAIL'}: {label} {str(detail)[:300]}", flush=True
        )
        return bench.check(label, bool(passed), str(detail)[:1000])

    def note(name, data) -> None:
        print(f"NOTE {name}: {str(data)[:400]}", flush=True)
        FOUND[name] = data
        bench.record(name, data)

    def until(fn, seconds: float, every: float = 2.0):
        end = time.monotonic() + seconds
        while True:
            value = fn()
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

    def lines(process: str) -> list[str]:
        out = []
        for p in sorted((bench.run_root / f"{process}-data").rglob("*.log")):
            out += [
                l.split("World patch ", 1)[1].strip()
                for l in p.read_text(errors="replace").splitlines()
                if "World patch " in l
            ]
        return out

    def owner_of(entity: int) -> tuple[str, int] | None:
        for node in NODES:
            try:
                got = cluster.node(node, f"/partition-of?entity={entity}")
            except Exception:  # noqa: BLE001 -- a node that is down or doesn't hold it
                continue
            if got and got.get("partition"):
                return node, int(got["partition"])
        return None

    def join(at):
        link_mods(
            Client(
                bench.run_root,
                CLIENT_ID,
                bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
                cluster.config.gateway_listen,
            ).user_data
            / "Mods"
        )
        for attempt in range(2):
            try:
                api = bench.start_client(CLIENT_ID)
                bench.wait_spawned(CLIENT_ID)
                break
            except (
                Exception
            ) as error:  # noqa: BLE001 -- the bench client's respawn flow fails now and then
                print(f"join attempt {attempt + 1} failed: {error}", flush=True)
                bench.kill_client(CLIENT_ID)
                if attempt:
                    raise
        api.set_admin_flag("invulnerable", True)
        api.character_teleport(*at)
        time.sleep(8)
        return api

    def start() -> None:
        cluster.start(PROCESSES)
        cluster.wait_ready(nodes=PROCESSES)

    # ============================== 1. the first start ==============================
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    start()
    ids = [i for i, _ in wanted]
    ledger = until(lambda: [l for l in lines("wa-1") if l.startswith("LEDGER")], 120)
    note("ledger-1", ledger)
    check(
        "the World Authority logs its ledger, without the patches",
        ledger and not ledger_ids(ledger) & ({*ids} | {"?" + i for i in ids}),
        ledger,
    )
    applied = until(
        lambda: (lambda wa_lines: wa_lines if len(wa_lines) == len(ids) else None)(
            [l for l in lines("wa-1") if l.startswith("APPLIED")]
        ),
        180,
        3,
    ) or [l for l in lines("wa-1") if l.startswith("APPLIED")]
    note("applied-wa", applied)
    note("lines-wa", lines("wa-1"))
    check(
        f"the World Authority applied {len(ids)} patches",
        len(applied) == len(ids),
        applied,
    )
    node_applied = {n: [l for l in lines(n) if l.startswith("APPLIED")] for n in NODES}
    note("applied-nodes", node_applied)
    check(
        "exactly one node added each patch's grids",
        sum(len(v) for v in node_applied.values()) == len(ids),
        node_applied,
    )
    made = {}
    for text in applied:
        for item in text.split(" grids: ", 1)[1].split("; ")[0].split(", "):
            name, entity = item.rsplit(" ", 1)
            made[name] = int(entity)
    names = {n for _, grids in wanted for n in grids}
    note("made", made)
    check(
        "the node created every grid of the patches", set(made) == names, sorted(made)
    )
    owners = {
        name: until(lambda e=entity: owner_of(e), 60, 3)
        for name, entity in made.items()
    }
    note("owners-1", owners)
    check("each new grid is in a partition", all(owners.values()), owners)

    # ============================== 2. stop and start ==============================
    graceful = cluster.stop(grace=240)
    check("the cluster stops gracefully", graceful)
    before = {p: len(lines(p)) for p in PROCESSES}
    start()
    ledger = until(
        lambda: [l for l in lines("wa-1")[before["wa-1"] :] if l.startswith("LEDGER")],
        120,
    )
    note("ledger-2", ledger)
    check(
        "after the restart the ledger has the patches, none in doubt",
        ledger_ids(ledger) == set(ids),
        ledger,
    )
    time.sleep(30)
    again = [
        l for p in PROCESSES for l in lines(p)[before[p] :] if l.startswith("APPLIED")
    ]
    check("nothing applied after the restart", not again, again)

    # ============================== 3. the grids are where they were put ==============================
    api = None
    for _, grids in wanted:
        for name, position in grids.items():
            if api is None:
                api = join(position)
            else:
                api.character_teleport(*position)
                time.sleep(8)
            seen = until(
                lambda n=name: [g for g in api.list_grids() if g.get("name") == n],
                120,
                5,
            )
            owner = until(lambda e=made.get(name, 0): owner_of(e), 60, 3)
            note(f"at {name}", {"client": seen, "owner": owner})
            check(
                f"{name}: one grid there, in a partition",
                len(seen or []) == 1 and owner,
                {"client": seen, "owner": owner},
            )
    bench.kill_client(CLIENT_ID)


def main() -> int:
    run.world_template = wa.prepare()
    with_trade_lanes()
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-world-patch")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-WORLD-PATCH",
        "SE-1358: world patches of the Octahedron plugin on a kept cluster world",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    print(json.dumps(FOUND, default=str)[:20000])
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        print(
            ("PASSED: " if item.passed else "FAILED: ")
            + item.label
            + (f" - {item.detail[:300]}" if item.detail else "")
        )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


run.administrators = [CLIENT_ID]

if __name__ == "__main__":
    sys.exit(main())
