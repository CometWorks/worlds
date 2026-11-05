"""Blueprints with subgrids at a cluster shipyard's hologram table without and with Multigrid Projector on the
client (SE-1082), through the cluster's scenario bench (CometWorks/cluster Test/). The game's client damages a
blueprint with subgrids on its way to the table, so the shipyard refuses it and asks for the blueprint's Workshop
link (!oct workshop), which the node downloads and loads into the table itself.

    OCT_WORLD_SOURCE=<world from build_world.py> [OCT_MGP_REPO=<MGP working copy>] [OCT_STEAMCMD=<steamcmd>] \\
        <Test>/.venv/bin/python tests/shipyard_workshop_cluster.py <Test> <mode> <single bp.sbc> <multi bp.sbc> <item>

The two blueprints are the Workshop item's bp.sbc (a ship with subgrids) and any ship without subgrids. Modes, one
build each:

- single: the client without MGP loads the ship without subgrids and orders it.
- vanilla: the client without MGP loads the ship with subgrids: the server tells the player in chat and on the
  table's screen, a press doesn't order it. Then !oct workshop <item's link> loads it, and it is ordered and built.
- mgp: the client with MGP (OCT_MGP_REPO) loads the ship with subgrids: no prompt, ordered and built.

A built ship must have the blueprint's grids and blocks, the grids next to each other. The setup is
tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (
    bench_module,
    rig,
    tc,
    Client,
    CLIENT_ID,
    wait_until,
)  # noqa: E402

import clients  # noqa: E402

MODE = sys.argv[2]
SINGLE, MULTI = (Path(a).read_text(encoding="utf-8") for a in sys.argv[3:5])
ITEM = sys.argv[5]
NODES = ("node-1", "node-2")
MGP = (
    wa.pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)
HINT = "!oct workshop"
pin_source_age = clients.pin_source_age


def client_with_mgp(profile: Path) -> None:
    """As tests/shipyard_cluster.py: the bench copies the client's profile before every start"""
    pin_source_age(profile)
    current = profile / "Profiles/Current.xml"
    text = current.read_text(encoding="utf-8")
    folder = f"<LocalFolderConfig><Id>{MGP[2]}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
    text = text.replace("<DevFolder />", "<DevFolder></DevFolder>").replace(
        "</DevFolder>", f"{folder}</DevFolder>"
    )
    assert MGP[2] in text
    current.write_text(text, encoding="utf-8")
    rig.register_source(
        profile / "Sources/sources.xml", "multigrid-projector", MGP[0], MGP[1]
    )


if MODE == "mgp":
    clients.pin_source_age = client_with_mgp


def counts(blueprint: str) -> tuple[str, int, int]:
    """The main grid's name, the grids and the blocks of a bp.sbc"""
    grids = blueprint.count("<CubeGrid>")
    blocks = blueprint.count("<MyObjectBuilder_CubeBlock")
    name = re.search(r"<CubeGrid>.*?<DisplayName>([^<]*)<", blueprint, re.S)[1]
    return name, grids, blocks


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1500])

    def node_log() -> str:
        return "".join(
            p.read_text(errors="replace")
            for node in NODES
            for p in (bench.run_root / f"{node}-data").rglob("*.log")
            if p.is_file()
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in ("wa-1", *NODES):
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(["wa-1", *NODES])
    cluster.wait_ready(nodes=["wa-1", *NODES])

    user_data = Client(
        bench.run_root,
        CLIENT_ID,
        bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
        cluster.config.gateway_listen,
    ).user_data
    wa.link_mods(user_data / "Mods")
    for name, blueprint in (("Single", SINGLE), ("Multi", MULTI)):
        target = user_data / "Blueprints/local" / name
        target.mkdir(parents=True, exist_ok=True)
        (target / "bp.sbc").write_text(blueprint, encoding="utf-8")
        (target / "bp.sbcB5").unlink(
            missing_ok=True
        )  # the game's binary copy would win
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    # The player is an admin (run.administrators), this makes any ship a free build for them
    api.set_admin_flag("ignorePcu", True)
    time.sleep(3)  # the server gets the flag with the admin menu's event

    station = next(iter(wa.stations().values()))
    for attempt in range(24):
        if attempt == 0:
            api.character_teleport(*station)
        time.sleep(5)
        tables = tc.tables(api)
        if tables:
            try:
                tc.stand_by(api, min(tables, key=lambda b: b["distance"]))
                break
            except Exception as error:  # noqa: BLE001 -- a grid already gone
                print(f"no stand by a table yet: {error}", flush=True)
    table = min(tc.tables(api), key=lambda b: b["distance"])
    block = api.get_block(table["gridId"], tuple(table["min"]))
    # The world's blueprint off the table first: the server refuses a player's removal of it
    # from an OCT table (SE-936), and the client's load would keep it
    print(" ".join(rig.chat(api, "!oct table remove", 5)), flush=True)

    def load(name: str) -> str:
        """Loads a local blueprint into the table, returns the chat lines that came in 20 s"""
        before = api.get_chat_messages()
        print(api.load_projection(block["entityId"], name=name), flush=True)
        time.sleep(20)
        return " ".join(rig.new_chat_lines(api, before, ""))

    def screen(*words: str) -> str:
        """The table's screens on one line, their text wraps; within 30 s the one with the words"""
        text = ""
        for _ in range(10):
            text = " ".join(" ".join(rig.chat(api, "!oct table show", 3)).split())
            if all(w in text for w in words):
                break
        return text

    ship, grids, blocks = counts(SINGLE if MODE == "single" else MULTI)
    if MODE == "single":
        said = load("Single")
        check("no prompt for a ship without subgrids", HINT not in said, said)
    elif MODE == "mgp":
        said = load("Multi")
        check("no prompt with Multigrid Projector", HINT not in said, said)
    else:
        said = load("Multi")
        check("the player is told in chat", HINT in said and "damage" in said, said)
        shown = screen("sent damaged", HINT)
        check(
            "the table's screen tells why",
            "sent damaged" in shown and HINT in shown,
            shown,
        )
        reply = press(api)
        check(
            "a press doesn't order the damaged copy",
            "is in the queue" not in reply and HINT in reply,
            reply,
        )
        # The answer comes in chat once SteamCMD is done
        command = (
            f"{HINT} https://steamcommunity.com/sharedfiles/filedetails/?id={ITEM}"
        )
        before = api.get_chat_messages()
        api.send_chat(command)
        reply = ""
        deadline = time.monotonic() + 180
        while "hologram table" not in reply and time.monotonic() < deadline:
            time.sleep(2)
            reply = " ".join(rig.new_chat_lines(api, before, command))
        print(f"{command} -> {reply}", flush=True)
        check(
            "the Workshop blueprint is on the table",
            "is on the hologram table" in reply,
            reply,
        )
        # The quote and the details name the Workshop item's title
        ship = (
            re.search(r"'(.+)' is on the hologram table", reply)[1]
            if "'" in reply
            else ship
        )
        shown = screen("A free build.")
        check("the quote is a free build", "A free build." in shown, shown)

    owned = {g["entityId"] for g in api.list_grids()}
    reply = ""
    for _ in range(12):
        reply = press(api)
        if "Your quote for" in reply:
            reply = press(api)
        if "is in the queue" in reply or "builds" in reply:
            break
        time.sleep(10)
    if not check(
        "the ship is ordered", "is in the queue" in reply or "builds" in reply, reply
    ):
        return
    line = wait_until(
        lambda: re.search(
            rf"Shipyard order \S+ '{re.escape(ship)}' of .* ended: (.*)", node_log()
        ),
        1800,
        "the order's end",
    )
    print(line[0], flush=True)
    time.sleep(20)
    new = [
        api.get_grid(g["entityId"])
        for g in api.list_grids()
        if g["entityId"] not in owned
    ]
    # Grids of the player's the client has no blocks of yet, far away, aren't the ship
    cubes = {g["entityId"]: api.list_cubes(g["entityId"])["total"] for g in new}
    print(
        f"new grids: {[(g['entityId'], cubes[g['entityId']], g['position']) for g in new]}"
    )
    found = {id_: n for id_, n in cubes.items() if n}
    positions = [g["position"] for g in new if cubes[g["entityId"]]]
    spread = max(
        (
            sum((a - b) ** 2 for a, b in zip(p, q)) ** 0.5
            for p in positions
            for q in positions
        ),
        default=0,
    )
    check(
        f"the ship has the blueprint's {grids} grids and {blocks} blocks",
        len(found) == grids and sum(found.values()) == blocks,
        f"{found}",
    )
    check("its grids are within 100 m of each other", spread < 100, f"{spread:.1f} m")


run.administrators = [CLIENT_ID]


def press(api) -> str:
    """As tests/shipyard_cluster.py: presses the table's button once"""
    before = api.get_chat_messages()
    reply = " ".join(rig.chat(api, "!oct order", 15))
    deadline = time.monotonic() + 60
    while (
        not reply.strip() or reply.strip().endswith("answers in chat.")
    ) and time.monotonic() < deadline:
        time.sleep(2)
        reply = " ".join(rig.new_chat_lines(api, before, "!oct order"))
    print(f"!oct order -> {reply}", flush=True)
    return reply


def main() -> int:
    run.world_template = wa.prepare()
    wa.set_config(FreeBuildPcuLimit=100000)
    if os.environ.get("OCT_STEAMCMD"):
        wa.set_config(ShipyardSteamCmd=os.environ["OCT_STEAMCMD"])
    label = datetime.now(timezone.utc).strftime(f"%Y%m%d-%H%M%SZ-workshop-{MODE}")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD",
        f"SE-1082: a blueprint with subgrids at a cluster shipyard, {MODE}",
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
