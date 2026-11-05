"""Blueprints with subgrids at a cluster shipyard's hologram table without and with Multigrid Projector on the
client (SE-1082), through the cluster's scenario bench (CometWorks/cluster Test/). The game's client damages a
blueprint with subgrids on its way to the table, so the shipyard takes any blueprint with subgrids that doesn't
show it came from MGP off the table and asks for the blueprint's Workshop link (!bp), which the node downloads and
loads into the table itself.

    OCT_WORLD_SOURCE=<world from build_world.py> [OCT_MGP_REPO=<MGP working copy>] [OCT_STEAMCMD=<steamcmd>] \\
        <Test>/.venv/bin/python tests/shipyard_workshop_cluster.py <Test> <mode> <single bp.sbc> <multi bp.sbc> <item>

The two blueprints are the Workshop item's bp.sbc (a ship with subgrids) and any ship without subgrids. Modes, one
build each:

- single: the client without MGP loads the ship without subgrids and orders it.
- vanilla: !bp away from a table is refused. The client without MGP loads the ship with subgrids, then a copy of it without the links of its rotors,
  pistons and hinges: the server takes each off the table and names !bp in chat. Then !bp <item's link> loads it,
  the client's copy on the table has every grid in place, again by its bare id from the cache, and it is ordered
  and built.
- mgp: the client with MGP (OCT_MGP_REPO) loads the ship with subgrids: no prompt, ordered and built.
- dlc: the player owns no DLC (!sprobe nodlc, a node plugin built from tests/station-probe) and loads the
  item, a ship with DLC blocks, with !bp: the quote and the order name the blocks left out, and the ship is
  built without them.

Blocks of mods the world doesn't load, by OCT_DS64's content (2414780476 has some on its hinges' grids), are left
out and named by the order (SE-1086). A built ship must have the blueprint's grids and blocks, the grids next to
each other, and the order ends with it ready. The setup is
tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.
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
import station_tests as st  # noqa: E402
from se_remote import CallOp  # noqa: E402

MODE = sys.argv[2]
SINGLE, MULTI = (Path(a).read_text(encoding="utf-8") for a in sys.argv[3:5])
ITEM = sys.argv[5]
NODES = ("node-1", "node-2")
MGP = (
    wa.pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)
HINT = "!bp"
CACHE = wa.ROOT / "workshop-cache"
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


def unknown(blueprint: str) -> int:
    """The bp.sbc's blocks that the game's own content doesn't define, of mods the world doesn't load"""
    data = Path(os.environ["OCT_DS64"]).parent / "Content" / "Data"
    known = set()
    for f in data.rglob("*.sbc"):
        known.update(re.findall(r"<SubtypeId>([^<]*)<", f.read_text(errors="ignore")))
    blocks = blueprint.split("<MyObjectBuilder_CubeBlock")[1:]
    subtypes = (re.search(r"<SubtypeName>([^<]*)<|<SubtypeName />", b) for b in blocks)
    return sum(bool(m and m[1] and m[1] not in known) for m in subtypes)


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
    # The links of the bases to their heads gone: nothing shows it came from MGP
    unlinked = re.sub(r"<TopBlockId>-?\d+</TopBlockId>", "", MULTI)
    for name, blueprint in (
        ("Single", SINGLE),
        ("Multi", MULTI),
        ("Unlinked", unlinked),
    ):
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

    if MODE == "vanilla":
        # Far from any table, where the player spawned: no download
        far = " ".join(rig.chat(api, f"{HINT} {ITEM}", 5))
        check(
            "!bp away from a hologram table is refused",
            "Stand by a station's hologram table." in far,
            far,
        )
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
        """The table's screens on one line, their text wraps; within 2 min the one with the words,
        the time the shipyard may take to plan a support arm"""
        text = ""
        for _ in range(40):
            text = " ".join(" ".join(rig.chat(api, "!oct table show", 3)).split())
            if all(w in text for w in words):
                break
        return text

    def workshop(command: str, seconds: float) -> str:
        """Sends a !bp command, returns the chat until the shipyard says the blueprint is on the
        table: the answer comes once SteamCMD is done"""
        before = api.get_chat_messages()
        api.send_chat(command)
        reply = ""
        deadline = time.monotonic() + seconds
        while "hologram table" not in reply and time.monotonic() < deadline:
            time.sleep(2)
            reply = " ".join(rig.new_chat_lines(api, before, command))
        print(f"{command} -> {reply}", flush=True)
        return reply

    ship, grids, blocks = counts(SINGLE if MODE == "single" else MULTI)
    left_out = 0
    if MODE == "single":
        said = load("Single")
        check("no prompt for a ship without subgrids", HINT not in said, said)
    elif MODE == "dlc":
        reply = st.probe(api, "nodlc")
        check("the server takes the player for owning no DLC", "NODLC" in reply, reply)
        reply = workshop(f"{HINT} {ITEM}", 180)
        check(
            "the Workshop blueprint is on the table",
            "is on the hologram table" in reply,
            reply,
        )
        ship = re.search(r"'(.+)' is on the hologram table", reply)[1]
        shown = screen("Left out")
        left_out = sum(
            int(n)
            for n in re.findall(r"(\d+) of ", shown.split("Left out", 1)[-1][:200])
        )
        check(
            "the quote names the DLC blocks it leaves out",
            "you don't own their DLCs" in shown and left_out > 0,
            shown,
        )
    elif MODE == "mgp":
        said = load("Multi")
        check("no prompt with Multigrid Projector", HINT not in said, said)
    else:
        for name in ("Multi", "Unlinked"):
            said = load(name)
            check(
                f"{name}: taken off the table, the player is told !bp in chat",
                HINT in said and "off the table" in said,
                said,
            )
            shown = screen("BLUEPRINT False")
            check(f"{name}: the table is empty", "BLUEPRINT False" in shown, shown)
        reply = workshop(
            f"{HINT} https://steamcommunity.com/sharedfiles/filedetails/?id={ITEM}", 180
        )
        check(
            "the Workshop blueprint is on the table",
            "is on the hologram table" in reply,
            reply,
        )
        # The client's own copy, the hologram it shows: every grid, in place
        time.sleep(5)
        xml = api.call([CallOp.fat_block_method(block["entityId"], "GetObjectBuilder")])
        shown, expected = layout(xml.call(0)["xml"], "ProjectedGrids"), layout(MULTI)
        check(
            "the client's hologram has the blueprint's grids in place",
            len(shown) == len(expected)
            and all(
                a[0] == b[0] and abs(a[1] - b[1]) < 0.1 for a, b in zip(shown, expected)
            ),
            f"{shown} {expected}",
        )
        # Again, by the bare id: the blueprint now comes from the cache the nodes share (CACHE)
        again = workshop(f"{HINT} {ITEM}", 60)
        log = node_log()
        downloads = re.findall(
            rf"Workshop blueprint {ITEM} version \S+ .* (from the cache|downloaded)",
            log,
        )
        check(
            "a second load comes from the cache, one download",
            "is on the hologram table" in again
            and downloads == ["downloaded", "from the cache"]
            and list(CACHE.glob(f"{ITEM}-*/bp.sbc")),
            f"{again} {downloads} {list(CACHE.iterdir())}",
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
    expected = unknown(SINGLE if MODE == "single" else MULTI)
    named = re.search(r"doesn't have their mods: ([^.]*)\.", reply)
    modded = sum(int(n) for n in re.findall(r"(\d+) ", named[1])) if named else 0
    check(
        f"the order names the {expected} blocks of mods the server doesn't have",
        (
            modded == expected
            and all(re.fullmatch(r"\d+ \S.*", n.strip()) for n in named[1].split(","))
            if named
            else expected == 0
        ),
        reply,
    )
    left_out += modded
    line = wait_until(
        lambda: re.search(
            rf"Shipyard order \S+ '{re.escape(ship)}' of .* ended: (.*)", node_log()
        ),
        1800,
        "the order's end",
    )
    print(line[0], flush=True)
    check("the order ends with the ship ready", "is ready" in line[1], line[1])
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
        f"the ship has the blueprint's {grids} grids and {blocks} blocks"
        + (f" but the {left_out} left out" if left_out else ""),
        len(found) == grids and sum(found.values()) == blocks - left_out,
        f"{found}",
    )
    check("its grids are within 100 m of each other", spread < 100, f"{spread:.1f} m")


run.administrators = [CLIENT_ID]


def layout(xml: str, under: str = "CubeGrids") -> list[tuple[int, float]]:
    """Each grid's blocks and its distance from the main grid, the same however the copy is turned"""
    import xml.etree.ElementTree as ET

    grids = [
        (len(g.find("CubeBlocks")), g.find("PositionAndOrientation/Position").attrib)
        for parent in ET.fromstring(xml).iter(under)
        for g in parent
    ]
    origin = [float(v) for v in grids[0][1].values()] if grids else []
    return [
        (n, sum((float(v) - o) ** 2 for v, o in zip(p.values(), origin)) ** 0.5)
        for n, p in grids
    ]


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


def build_probe() -> Path:
    """The station probe as a local plugin, as tests/shipyard_turn_cluster.py builds it"""
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


def main() -> int:
    run.world_template = wa.prepare()
    if MODE == "dlc":
        os.environ["CLUSTER_EXTRA_PLUGINS"] += f",node:{build_probe()}"
    wa.set_config(FreeBuildPcuLimit=100000, ShipyardWorkshopCache=CACHE)
    shutil.rmtree(CACHE, ignore_errors=True)
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
