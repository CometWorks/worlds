"""A shipyard order on a local test cluster, through the cluster's scenario bench (CometWorks/cluster Test/):
the player's client, with Multigrid Projector, loads a blueprint into the hologram table and orders it. The
delivered ship's cockpit toolbar must point at the ship's own blocks (SE-772).

    OCT_WORLD_SOURCE=<world from build_world.py> OCT_MGP_REPO=<MGP working copy> \\
        <Test>/.venv/bin/python tests/shipyard_cluster.py <Test> bp.sbc     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.

`python tests/mech_blueprint.py bp.sbc big` writes a ship whose order doesn't fit one plugin message: before SE-805
it got the answer "Invalid" here, as the ship "Polaris Mk2 RC8 Recolored" did on the live cluster.
"""

from __future__ import annotations

import json
import re
import sys
import time
import xml.etree.ElementTree as ET
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
import se_autopilot as ap  # noqa: E402

BLUEPRINT = Path(sys.argv[2]).read_text(encoding="utf-8")
MGP = (
    wa.pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)
NODES = ("node-1", "node-2")
pin_source_age = clients.pin_source_age


def client_with_mgp(profile: Path) -> None:
    """The bench copies the client's profile before every start, this comes right after"""
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


clients.pin_source_age = client_with_mgp


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
    target = user_data / "Blueprints/local/Loaded"
    target.mkdir(parents=True, exist_ok=True)
    (target / "bp.sbc").write_text(BLUEPRINT, encoding="utf-8")
    (target / "bp.sbcB5").unlink(missing_ok=True)  # the game's binary copy would win
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
    print(api.load_projection(block["entityId"], name="Loaded"), flush=True)
    time.sleep(10)
    print(api.get_block(table["gridId"], tuple(table["min"]))["projector"], flush=True)
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
        lambda: re.search(r".*off the arm, pasted anew as (\d+).*", node_log()),
        1500,
        "the re-paste",
    )
    check("the ship is re-pasted", line, line[0])
    ship = int(line[1])
    time.sleep(20)
    cockpit = ap.find_cockpit(api, ship)
    block = api.get_block(ship, tuple(cockpit["min"]))
    builder = ET.fromstring(api.get_object_builder(block["entityId"]))
    slots = [d for d in builder.iterfind("Toolbar/Slots/Slot/Data")]
    refs = {d.findtext("BlockEntityId") for d in slots} - {None}
    ids = set()
    offset = 0
    while True:
        page = api.list_blocks(ship, offset=offset, limit=50)
        for item in page["blocks"]:
            ids.add(str(api.get_block(ship, tuple(item["min"]))["entityId"]))
        offset += 50
        if offset >= page["total"]:
            break
    # A slot may be on a subgrid's block: the ship's terminal system has their groups
    groups = api.list_block_groups(ship)
    ids |= {str(member) for members in groups.values() for member in members}
    check(
        "the cockpit's toolbar points at the ship's blocks",
        refs and refs <= ids,
        f"cockpit {block['entityId']}: slots on {sorted(refs)}, not on the ship: {sorted(refs - ids)}",
    )
    print(
        f"groups: { {name: len(members) for name, members in groups.items()} }",
        flush=True,
    )


run.administrators = [CLIENT_ID]


def press(api) -> str:
    """Presses the table's button once. The answer comes after the chat's first line, once a big
    blueprint has gone to the World Authority; another press would cancel that order."""
    latest = api.get_chat_messages().get("latest", 0)
    reply = " ".join(rig.chat(api, "!oct order", 15))
    deadline = time.monotonic() + 60
    while reply.strip().endswith("answers in chat.") and time.monotonic() < deadline:
        time.sleep(2)
        got = api.get_chat_messages(after=latest)
        reply = " ".join(
            m.get("Text", "")
            for m in got.get("messages", [])
            if m.get("Text") != "!oct order"
        )
    print(f"!oct order -> {reply}", flush=True)
    return reply


def main() -> int:
    run.world_template = wa.prepare()
    wa.set_config(FreeBuildPcuLimit=100000)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-shipyard")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-SHIPYARD",
        "SE-772: a shipyard ship's toolbar on a cluster",
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
