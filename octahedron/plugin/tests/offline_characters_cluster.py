"""Offline characters on a local test cluster (SE-854), through the cluster's own scenario bench. Same bench and
world as wa_economy_cluster.py; read its docstring for host.env. The World Authority keeps the times and the
inventories, the node holding a body asks it what to do. The probe in tests/offline-characters/probe fills and
reads the client's character on its node, the console probe on the WA runs !oct commands there.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/offline_characters_cluster.py CLUSTER_TEST_DIR     (from plugin/)

- Away for less than the time: the same body.
- A body that went offline with its partition before its time was up: the WA loads the partition, the node
  removes the body. With that node killed the inventory comes back on the other one, and the space credits are
  in the bank once.
- Kept, then a graceful cluster stop and warm start: the inventory is in the global save and comes back.
- A node killed right after the WA kept the inventory, before the removal: the body comes back with the
  partition and is removed without a second deposit.
- The WA killed right after it kept the inventory: nothing is removed before a save holds it, so the save and
  the world agree. In the end one deposit and one inventory.
- Saves held before their registration (SE-865): kept but not removed; removed once they land. The WA killed in
  that hold: the body stays, and goes the way it would have, with one deposit and one inventory. Saves refused
  for good: the body stays.

Takes about 55 minutes. OCT_SAVES_ONLY=1 runs only the SE-865 part, about 20 minutes.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa
from test_offline_characters import (
    balance,
    inventory,
    probe as cprobe,
    teleport,
    to_edge,
    wait_inventory,
)
from wa_economy_cluster import (
    CLIENT_ID,
    Client,
    _client_partition,
    _partitions,
    bench_module,
    link_mods,
    rig,
    set_config,
    stations,
    wait_until,
    ClusterError,
    PLUGIN,
)

wa.ROOT = (
    Path.home()
    / f"cluster-runs/offline-characters-{os.environ.get('OCT_TASK', 'SE-854')}"
)
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"

PROBE_ID = "7A2D4E10-8F36-4C51-B7A9-1D3E5F1736AA"
NODES = ("node-1", "node-2")
CARRIED = {"Ingot/Iron": 321.0, "Component/SteelPlate": 17.0}
SC = "PhysicalObject/SpaceCredit"


def build_probe() -> str:
    magnetar = os.environ["MAGNETAR_HOME"]
    out = wa.lc.PLUGINS / ".build" / "CharacterProbe"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build",
         str(PLUGIN / "tests/offline-characters/probe/CharacterProbe.csproj"), "-c", "Release",
         "--nologo", "-v", "q", f"-p:Magnetar={magnetar}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "CharacterProbe"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "CharacterProbe.dll").write_bytes(
        (out / "CharacterProbe.dll").read_bytes()
    )
    (folder / "CharacterProbe.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{PROBE_ID}</Id><FriendlyName>CharacterProbe</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
        "<Commit>test</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return str(folder)


def prepare() -> Path:
    world = wa.prepare()
    probe = build_probe()
    os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{probe}"
    set_config(
        OfflineCharacterMinutes=5,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
    )
    return world


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()
    alpha = stations()["Alpha Station"]
    processes = ["wa-1", *NODES]

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in bench.run_root.glob(f"{process}*.log")
            if p.is_file()
        ) + "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def wa_command(line: str) -> list[str]:
        replies = probe.run(line, timeout=120, settle=3)
        print(f"WA {line} -> {replies}", flush=True)
        return replies

    def minutes(value: int) -> None:
        """The WA decides when a body's time is up, and only it needs to know"""
        set_config(OfflineCharacterMinutes=value)
        wa_command("!oct reload")

    def reset_probe() -> None:
        wa.lc.PLAN.write_text("")
        wa.lc.OUT.write_text("")
        probe.step = 0

    def join():
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
        return api

    before: dict[str, int] = {}

    def where() -> str | None:
        for node in NODES:
            try:
                if _client_partition(_partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def stock(api, credits: int) -> tuple[int, dict[str, float], int]:
        """To the station, with known items and space credits"""
        teleport(api, alpha[0] + 150, alpha[1], alpha[2])
        distance, radius = to_edge(api, -3)
        check(f"inside the sphere: {distance:.1f} of {radius:.1f} m", distance < radius)
        cprobe(api, "clear", "CLEAR")
        for item, amount in CARRIED.items():
            cprobe(api, f"give {item} {amount:.0f}", "GIVE True")
        cprobe(api, f"give {SC} {credits}", "GIVE True")
        body, items = inventory(api)
        before.clear()
        before.update({n: count(removed_line(body), n) for n in NODES})
        before["wa-1"] = count(kept_line(body), "wa-1")
        check(
            f"carries {credits} SC, body {body} on {where()}",
            items.get(SC) == credits,
            items,
        )
        return body, items, balance(api)

    def kept_line(body: int) -> re.Pattern:
        return re.compile(rf"Offline character of \d+: body {body} kept")

    def removed_line(body: int) -> re.Pattern:
        return re.compile(rf"Offline character of \d+: body {body} removed")

    def count(pattern: re.Pattern, process: str) -> int:
        return len(pattern.findall(log(process)))

    def removed_on(body: int) -> str | None:
        """A body can be removed more than once, when a node's death brings it back: since stock()"""
        return next(
            (n for n in NODES if count(removed_line(body), n) > before.get(n, 0)), None
        )

    def wait_kept(body: int, timeout: float) -> None:
        wait_until(
            lambda: count(kept_line(body), "wa-1") > before.get("wa-1", 0),
            timeout,
            f"{body} kept",
        )

    def wait_removed(body: int, timeout: float) -> str:
        return wait_until(lambda: removed_on(body), timeout, f"{body} removed")

    def nothing_kept() -> bool:
        return any("kept inventories: 0" in r for r in wa_command("!oct offline"))

    def came_back(
        api, label: str, body: int, bank: int, credits: int, same=False
    ) -> None:
        """The client's body: the items back once, the space credits in the bank once. The old body
        only where the node's death may bring it back: it is emptied before it gets them.
        """

        def has_them():
            found = inventory(api)
            return (
                found
                if all(found[1].get(i, 0) >= a for i, a in CARRIED.items())
                and SC not in found[1]
                else None
            )

        new_body, items = wait_until(has_them, 180, f"{label}: the items back")
        print(f"{label}: body {new_body} after {body}", flush=True)
        if not same:
            check(f"{label}: a new body", new_body != body, f"{new_body} after {body}")
        for item, amount in CARRIED.items():
            check(f"{label}: {item} back, once", items.get(item) == amount, items)
        now = balance(api)
        check(
            f"{label}: the bank has the SC once",
            now == bank + credits,
            f"{now}, {bank}",
        )
        check(
            f"{label}: nothing kept any more",
            wait_until(nothing_kept, 60, f"{label}: nothing kept"),
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in processes:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()

    # A killed client lingers up to 140 s and its time starts then, so a limit of 1 minute means
    # about 4 minutes from the kill to the removal, more with a save to wait for
    away = 420

    def hold_saves() -> None:
        cluster.node("wa-1", "/global-save-register-hold?ms=600000", "POST")

    def not_removed(body: int, label: str) -> None:
        """Kept and waiting: the node asks every 5 s, and the WA knows it isn't saved"""
        check(
            f"{label}: the WA says not saved yet",
            any("not saved yet" in r for r in wa_command("!oct offline")),
        )
        end = time.monotonic() + 45
        while time.monotonic() < end:
            gone = removed_on(body)
            if gone:
                break
            time.sleep(1)
        check(f"{label}: the body stays", not gone, gone)

    # --- saves held before their registration: no removal, then one ---
    minutes(1)
    body, carried, bank = stock(api, 16_000)
    hold_saves()
    bench.kill_client(CLIENT_ID)
    wait_kept(body, away)
    hold_saves()
    not_removed(body, "saves held")
    cluster.node("wa-1", "/global-save-register-hold?ms=1", "POST")
    wait_removed(body, 120)
    check(
        "saves held: the save holds the inventory at the removal",
        re.search(
            rf"P \d+ {body} 16000 ", wa.global_save(bench.run_root / "wa-1-data")
        ),
    )
    api = join()
    came_back(api, "after a held save", body, bank, 16_000)
    # A WA restored from a save before that hands them out again: SE-908
    wait_until(
        lambda: not re.search(
            rf"P \d+ {body} 16000 ", wa.global_save(bench.run_root / "wa-1-data")
        ),
        180,
        "a save without what was handed out",
    )

    # --- the WA killed in that hold: the body stays, and nothing is lost ---
    minutes(1)
    body, carried, bank = stock(api, 32_000)
    hold_saves()
    bench.kill_client(CLIENT_ID)
    wait_kept(body, away)
    hold_saves()
    not_removed(body, "WA kill in a held save")
    reset_probe()
    cluster.restart_node("wa-1")
    cluster.wait_ready(nodes=["wa-1"])
    check("WA killed in a held save: not removed yet", not removed_on(body))
    wait_removed(body, away)
    api = join()
    came_back(api, "after a WA kill in a held save", body, bank, 32_000)

    def refused_saves(api) -> None:
        """Last: a catalogue row ahead of the WA names no file, the cluster is done after it"""
        minutes(1)
        body, carried, bank = stock(api, 64_000)
        rows = cluster.get("/registry/global-saves")
        authority = next(
            row for row in cluster.get("/registry/nodes") if row["Node"] == "wa-1"
        )
        generation = max(row["SaveId"]["Generation"] for row in rows)

        def row_ahead(sequence: int) -> None:
            cluster.post(
                f"/registry/global-saves?node=wa-1&epoch={authority['Epoch']}&generation={generation}"
                f"&sequence={sequence}&kind=Periodic&size=1&checksum=forced&clusterTime=0&location=forced-ahead"
            )

        try:
            row_ahead(1_000_000)
        except ClusterError as error:
            # A cluster before SE-819 takes only the next sequence, and the WA is left refused all the same
            row_ahead(int(re.search(r"expected (\d+)", str(error)).group(1)))
        bench.kill_client(CLIENT_ID)
        wait_kept(body, away)
        not_removed(body, "saves refused")
        bench.kill_client(CLIENT_ID)

    if os.environ.get("OCT_SAVES_ONLY"):
        refused_saves(api)
        return

    # --- back in time ---
    body, carried, bank = stock(api, 1_000)
    minutes(30)
    bench.kill_client(CLIENT_ID)
    api = join()
    again, items = inventory(api)
    check("back in time: the same body", again == body, f"{again} after {body}")
    check("back in time: nothing taken", items == carried, items)
    check("back in time: the bank as it was", balance(api) == bank)
    minutes(5)

    # --- the partition goes offline with the body, the WA loads it when the time is up ---
    bench.kill_client(CLIENT_ID)
    start = time.time()

    def offline_with_body() -> bool:
        table = cluster.get("/registry/partitions/offline", timeout=30)
        home = cluster.get(f"/registry/character-homes?client={CLIENT_ID}", timeout=30)
        rows = [r for r in table["Partitions"] if r["Partition"] == home["Partition"]]
        if rows:
            print(
                f"offline: {home['Partition']}, members {rows[0]['Members']}",
                flush=True,
            )
        return bool(rows)

    # The time and the quiet partition both count from when the gateway drops the killed client,
    # up to about 140 s after the kill
    went_offline = False
    while time.time() - start < 450 and not went_offline and not removed_on(body):
        try:
            went_offline = offline_with_body()
        except (ClusterError, OSError) as error:
            print(f"offline table: {error}", flush=True)
        time.sleep(5)
    print(
        f"offline with its partition after {time.time() - start:.0f} s: {went_offline}"
    )
    check("the body went offline with its partition before its time", went_offline)
    remover = wait_removed(body, 480)
    check(
        "the WA asked for the partition's load",
        re.search(rf"load of partition \d+ for body {body}", log("wa-1")),
    )
    check(
        f"removed on {remover} at about the 5 minutes",
        time.time() - start >= 300,
        f"{time.time() - start:.0f} s",
    )

    # Only the other node is left to take the partition, so the player comes back there. It may
    # bring back the body from before the removal, which has to go again.
    other = NODES[1 - NODES.index(remover)]
    cluster.kill(remover)
    api = join()
    node = wait_until(where, 120, "the client's node")
    back, _ = inventory(api)
    if back == body:
        wait_until(
            lambda: SC not in inventory(api)[1],
            30,
            "the brought back body emptied",
        )
        check("the brought back body has no SC within 30 s", True)
    check(f"back on another node than {remover}: {node}", node == other)
    came_back(api, "after a partition load", body, bank, 1_000, same=True)
    cluster.restart_node(remover)
    cluster.wait_ready(nodes=[remover])

    # --- just outside the sphere: the WA can't tell and loads the partition, the body stays ---
    teleport(api, alpha[0] + 150, alpha[1], alpha[2])
    distance, radius = to_edge(api, 3)
    check(f"outside the sphere: {distance:.1f} of {radius:.1f} m", distance > radius)
    body, items = inventory(api)
    bank = balance(api)
    before.clear()
    before.update({n: count(removed_line(body), n) for n in NODES})
    before["wa-1"] = count(kept_line(body), "wa-1")
    bench.kill_client(CLIENT_ID)
    load = re.compile(rf"load of partition \d+ for body {body}")
    wait_until(lambda: load.search(log("wa-1")), 600, "the WA's load of the partition")

    def online_again() -> bool:
        return not offline_with_body()

    wait_until(online_again, 180, "the partition back online")
    # Two scans of the node holding it, and an answer to each
    quiet_until = time.monotonic() + 70
    while (
        time.monotonic() < quiet_until
        and not removed_on(body)
        and count(kept_line(body), "wa-1") == before["wa-1"]
    ):
        time.sleep(2)
    check(
        "outside the sphere: the WA kept nothing",
        count(kept_line(body), "wa-1") == before["wa-1"],
    )
    check("outside the sphere: the body stays", not removed_on(body))
    api = join()
    again, items_again = inventory(api)
    check("outside the sphere: the same body", again == body, f"{again} after {body}")
    check("outside the sphere: nothing taken", items_again == items, items_again)
    check("outside the sphere: the bank as it was", balance(api) == bank)

    # --- kept through a graceful stop and warm start ---
    minutes(1)
    body, carried, bank = stock(api, 2_000)
    bench.kill_client(CLIENT_ID)
    wait_removed(body, away)
    # A save holds it: the removal waited for that
    check("graceful stop", cluster.stop(grace=240))
    saved = wa.global_save(bench.run_root / "wa-1-data")
    check(
        "the inventory is in the WA's global save",
        re.search(rf"P \d+ {body} 2000 ", saved),
    )
    reset_probe()
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    api = join()
    came_back(api, "after a warm start", body, bank, 2_000)

    # --- a node killed between the WA's save and the removal ---
    minutes(1)
    body, carried, bank = stock(api, 4_000)
    holder = where()
    bench.kill_client(CLIENT_ID)
    wait_kept(body, away)
    gone_early = removed_on(body)
    cluster.kill(holder)
    print(f"killed {holder}; removed already: {gone_early}", flush=True)
    cluster.restart_node(holder)
    cluster.wait_ready(nodes=[holder])
    if not gone_early:
        # On the restarted node, or on the other one if it took the partition first
        print(f"removed on {wait_removed(body, away)}", flush=True)
    check(
        "the WA kept it once",
        count(kept_line(body), "wa-1") == before["wa-1"] + 1,
        count(kept_line(body), "wa-1"),
    )
    api = join()
    came_back(api, "after a node kill", body, bank, 4_000)

    # --- the WA killed right after it kept the inventory ---
    minutes(1)
    body, carried, bank = stock(api, 8_000)
    bench.kill_client(CLIENT_ID)
    wait_kept(body, away)
    reset_probe()
    cluster.restart_node("wa-1")
    cluster.wait_ready(nodes=["wa-1"])
    # The save held it or it did not: the body goes in both, the deposit once
    wait_removed(body, away)
    api = join()
    came_back(api, "after a WA kill", body, bank, 8_000)

    refused_saves(api)


run.administrators = [CLIENT_ID]


def main() -> int:
    run.world_template = prepare()
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-offline-characters")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-OFFLINE-CHARACTERS",
        "SE-854: offline characters on the World Authority",
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
