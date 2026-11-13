"""SE-1417: the cluster node route /cluster/zone-evacuate clears Moon Station's safe zone while the
cluster runs, and the station patches a docked ship held back apply on their next retry. On a local
test cluster (WA + 2 nodes + one headless admin client), the setup of station_extenders_cluster.py.

The world is built from a commit before the extenders (the live world's). Checks:
- two ships docked at Moon Station, one on the connector (-13,0,0) the extender patch removes, and the
  client on foot in the zone with its helmet open
- with Moon Station's patches staged, the extender patch is refused: a ship is docked
- a dry run lists both ships, both connectors and the client, and changes nothing
- the run undocks both ships and moves them and the client out of the zone, the client with its
  helmet closed; the client's own game sees itself outside
- the extender, mode panel and how-to patches apply on their next retry
- a second run finds nothing left to move

    OCT_TASK=SE-1417 OCT_WORLD_SOURCE=<world from build_world.py before the extenders> \\
        <Test>/.venv/bin/python tests/zone_evacuate_cluster.py <Test>     (from plugin/)
"""

from __future__ import annotations

import json
import math
import os
import re
import shutil
import sys
import time
import urllib.parse
from datetime import datetime, timezone

os.environ.setdefault("OCT_TASK", "SE-1417")
import station_extenders_cluster as sx  # noqa: E402  (takes the bench's folder from argv)
import station_patch_cluster as spc  # noqa: E402
import wa_economy_cluster as wa  # noqa: E402
from wa_economy_cluster import CLIENT_ID, ClusterError, bench_module, rig  # noqa: E402

import shipyard_turn_cluster as turn  # noqa: E402
import test_support_arm as sa  # noqa: E402

STATION = "Moon"
GRID = "Moon Station"
NODES = sx.NODES
PROCESSES = sx.PROCESSES
DOCK_CELL = "-13;0;0"
WAITED = ("station-extenders", "mode-panel", "howto-entries")
FOUND: dict[str, object] = {}


def moon_entries():
    return [e for e in sx.entries() if e.findtext("Grid") == GRID]


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        print(
            f"{'PASS' if passed else 'FAIL'}: {label} {str(detail)[:300]}", flush=True
        )
        return bench.check(label, bool(passed), str(detail)[:1000])

    def note(name, data) -> None:
        print(f"NOTE {name}: {str(data)[:600]}", flush=True)
        FOUND[name] = data
        bench.record(name, data)

    def until(fn, seconds: float, every: float = 3.0):
        end = time.monotonic() + seconds
        while True:
            value = fn()
            if value or time.monotonic() > end:
                return value
            time.sleep(every)

    def logs() -> str:
        return "\n".join(
            p.read_text(errors="replace")
            for process in PROCESSES
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def chat(api, text: str, wait: float = 8) -> str:
        reply = " ".join(rig.chat(api, text, wait))
        print(f"{text} -> {reply[:300]}", flush=True)
        return reply

    def probe(api, args: str) -> str:
        return chat(api, f"!sprobe {args}", 6)

    def evacuate(node: str, dry: bool) -> dict:
        query = urllib.parse.urlencode({"name": GRID, "dryRun": "1" if dry else "0"})
        return cluster.node(node, f"/zone-evacuate?{query}", "POST")

    def holder() -> str | None:
        for node in NODES:
            try:
                if evacuate(node, True).get("dryRun"):
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def me(node: str) -> dict | None:
        """The client's character on the node, as the node's /characters sees it"""
        rows = cluster.node(node, "/characters")
        rows = rows.get("characters", rows) if isinstance(rows, dict) else rows
        return next((r for r in rows if r.get("controllingSteamId") == CLIENT_ID), None)

    def ship_at(ship: str) -> tuple[float, ...] | None:
        m = re.search(r" at (\S+) (\S+) (\S+)", probe(api, f"docked {ship}"))
        return tuple(float(v) for v in m.groups()) if m else None

    # ============================== setup ==============================
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    spc.PENDING.write_text("")
    spc.OUT.write_text("")
    cluster.start(PROCESSES)
    cluster.wait_ready(nodes=PROCESSES)
    api = sx_join(bench, cluster)
    api.character_teleport(*sa.station_tables()[STATION])
    time.sleep(10)

    # ============================== two docked ships, the client on foot ==============================
    ships = []
    for cell in (DOCK_CELL, None):
        at = f",AT={cell}" if cell else ""
        reply = probe(api, f"dock {STATION} mine BAT=0{at}")
        ship = re.search(r"SHIP (\d+)", reply)
        check(
            f"a ship docks at Moon Station {cell or '(any free connector)'}",
            ship,
            reply,
        )
        if ship and until(
            lambda: f"DOCKED {ship[1]} yes" in probe(api, f"docked {ship[1]}"), 60
        ):
            ships.append(ship[1])
    check("both ships are docked", len(ships) == 2, ships)
    node = until(holder, 120)
    check("a node holds Moon Station", node, node)
    if not node:
        return
    for _ in range(3):
        if me(node) and me(node).get("helmet"):
            api.key("J")
            time.sleep(2)
    check(
        "the client is on foot with its helmet open",
        me(node) and me(node).get("helmet") is False,
        me(node),
    )

    # ============================== the patches are refused ==============================
    wa.set_config(
        StationPatches="".join(
            f"<StationPatchEntry><Id>{e.findtext('Id')}</Id><Grid>{GRID}</Grid>"
            f"<From>{e.findtext('From')}</From><To>{e.findtext('To')}</To></StationPatchEntry>"
            for e in moon_entries()
        )
    )
    chat(api, "!oct reload")
    extenders = next(
        e.findtext("Id")
        for e in moon_entries()
        if "station-extenders" in e.findtext("Id")
    )
    refused = until(
        lambda: re.search(rf"REFUSED {re.escape(extenders)}.*a ship is docked", logs()),
        120,
    )
    check(
        "the extender patch is refused: a ship is docked",
        refused,
        refused and refused[0],
    )

    # ============================== dry run ==============================
    dry = evacuate(node, True)
    note("dry-run", dry)
    planned = {str(s["entityId"]) for s in dry["moved"]["ships"]}
    check("the dry run lists both ships", set(ships) <= planned, planned)
    check(
        "and both connectors",
        len([r for r in dry["released"] if r["kind"] == "connector"]) == 2,
        dry["released"],
    )
    check(
        "and the client",
        any(c["entityId"] == me(node)["entityId"] for c in dry["moved"]["characters"]),
        dry["moved"]["characters"],
    )
    check(
        "and changes nothing",
        all(f"DOCKED {s} yes" in probe(api, f"docked {s}") for s in ships),
    )

    # ============================== the evacuation ==============================
    done = evacuate(node, False)
    note("evacuation", done)
    center, radius = done["zoneCenter"], done["zoneRadius"]
    moved = {str(s["entityId"]): s for s in done["moved"]["ships"]}
    check(
        "both ships are moved", all(moved.get(s, {}).get("moved") for s in ships), moved
    )
    check(
        "both undocked",
        all(f"DOCKED {s} no" in probe(api, f"docked {s}") for s in ships),
    )
    where = {s: ship_at(s) for s in ships}
    note("ships-at", where)
    check(
        "both ships are outside the zone",
        all(p and math.dist(p, center) > radius for p in where.values()),
        {s: p and round(math.dist(p, center)) for s, p in where.items()},
    )
    time.sleep(5)
    mine = me(node) or me(NODES[1 - NODES.index(node)])
    note("client-server", mine)
    check(
        "the client is outside the zone, as the server sees it",
        mine and math.dist(mine["position"], center) > radius,
        mine and round(math.dist(mine["position"], center)),
    )
    check(
        "with its helmet closed",
        mine and mine.get("helmet") is True,
        mine and mine.get("helmet"),
    )
    seen = api.get_character()
    note("client-own", seen)
    check(
        "and the client's own game sees itself outside the zone",
        math.dist(seen["position"], center) > radius,
        round(math.dist(seen["position"], center)),
    )

    # ============================== the patches apply ==============================
    for name in WAITED:
        id_ = next(e.findtext("Id") for e in moon_entries() if name in e.findtext("Id"))
        applied = until(
            lambda: f"Station patch APPLIED {id_} on {GRID} (staged)" in logs(), 150, 5
        )
        check(f"the {name} patch applies on its retry", applied, id_)

    again = evacuate(node, False)
    note("second-run", again)
    check(
        "a second run finds nothing to move",
        not again.get("released")
        and not again["moved"]["ships"]
        and not again["moved"]["characters"],
        again,
    )


def sx_join(bench, cluster):
    """station_extenders_cluster's join, without its teleport to Alpha"""
    wa.link_mods(
        wa.Client(
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
    return api


def main() -> int:
    run.world_template = wa.prepare()
    folders = [turn.build_probe(), spc.build_edit_probe()]
    os.environ["CLUSTER_EXTRA_PLUGINS"] += "".join(f",node:{f}" for f in folders)
    os.environ.update(EDITPROBE_PENDING=str(spc.PENDING), EDITPROBE_OUT=str(spc.OUT))
    for e in moon_entries():
        for name in (e.findtext("From"), e.findtext("To")):
            shutil.copy2(sx.STORAGE / name, wa.CONFIG.parent / name)
    wa.set_config(StationPatches="")
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-zone-evacuate")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-ZONE-EVACUATE",
        "SE-1417: Moon Station's safe zone cleared for its patches",
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
