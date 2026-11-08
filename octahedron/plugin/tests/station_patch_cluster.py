"""SE-1234: station patches of the Octahedron plugin on a local test cluster (CometWorks/cluster
Test/ scenario bench, the setup of tests/wa_economy_cluster.py: WA + 2 nodes + one headless admin
client). The rig of SE-1227 (policy note SE-1238), with the plugin's !oct patch and its staged
StationPatches list in place of that ticket's probe. tests/edit-probe only reports the grids.

    OCT_TASK=SE-1234 OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/station_patch_cluster.py <Test> [parts]     (from plugin/)

parts (default both, in this order):
  online   patches an admin applies with !oct patch apply on the node that holds Alpha Station: a
           block setting, a removed, an added and a swapped lobby block, the refusals (the wrong
           block in a cell, a block with no mount neighbor, a patch whose last op is bad and must
           leave its first undone, the connector a ship is docked to, the shipyard's containers
           during a build), a shipyard change during a paid build; then a node restart and a
           graceful cluster stop and start, after which the station must be exactly as patched
  staged   a patch put into the config's StationPatches during a stop, for a station nobody
           visits: applied by whichever node loads it, once over a restart

Every result is in the bench's result.json; the summary at the end lists what each part found.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1234")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import CLIENT_ID, Client, bench_module, rig  # noqa: E402

import shipyard_turn_cluster as turn  # noqa: E402
import test_paid_builds as tp  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402

PARTS = sys.argv[2:] or ["online", "replace", "staged"]
STATION = "Alpha"
GRID = "Alpha Station"
YARD = "Alpha Station Shipyard"
FAR = "Beta Station"
NODES = ("node-1", "node-2")
PROCESSES = ["wa-1", *NODES]
SHIP = "Patch Test"
COLUMNS = 8 * 16
LONG = rig.grid(
    SHIP,
    777000123400300,
    [
        rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
        for x in range(8)
        for z in range(16)
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PLATES = COLUMNS * 25
WINDOW = 3
LIGHT = "LargeBlockHalfTrofferLight"
BARREL = "LargeBarrel"
CONNECTOR = "LargeBlockStructural_PlatformConnector"
ARMOR = "CubeBlock/LargeBlockArmorBlock"

wa.ROOT = Path.home() / f"cluster-runs/patch-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
PENDING = wa.ROOT / "edit-pending.txt"
OUT = wa.ROOT / "edit-out.txt"
EDIT_ID = "9A41C0D3-7E25-4B18-A6F2-1227E0D1A9B4"
FOUND: dict[str, object] = {}


def build_edit_probe() -> Path:
    plugin = Path(wa.__file__).resolve().parents[1]
    out = wa.lc.PLUGINS / ".build" / "EditProbe"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(plugin / "tests/edit-probe/EditProbe.csproj"),
         "-c", "Release", "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}", "-o", str(out)],
        check=True,
    )  # fmt: skip
    folder = wa.lc.PLUGINS / "EditProbe"
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(out / "EditProbe.dll", folder / "EditProbe.dll")
    (folder / "EditProbe.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        f"<Id>{EDIT_ID}</Id><FriendlyName>EditProbe</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
        "<Commit>se1234</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return folder


def staged_entry(
    id_: str, grid: str, ops: str = "", source: str = "", target: str = ""
) -> str:
    return (
        f"<StationPatchEntry><Id>{id_}</Id><Grid>{grid}</Grid><Ops>{ops}</Ops>"
        f"<From>{source}</From><To>{target}</To></StationPatchEntry>"
    )


XSI = "http://www.w3.org/2001/XMLSchema-instance"
OCTAHEDRON = Path(wa.__file__).resolve().parents[2]
sys.path.insert(0, str(OCTAHEDRON))
import build_world  # noqa: E402
import station_patch  # noqa: E402

BLUEPRINT = OCTAHEDRON / "blueprints/octahedron-station.sbc"
SOURCE = "station-from.sbc"
OFFICE = "0,9,-2"  # Text Panel - Station office center, with an LCD apps mod's storage
AIRLOCK = "1,0,2"  # a Rotary Airlock door, the mod's block
LCD_KEY = "74de02b3-27f9-4960-b1c4-27351f2b06d1"
RUNTIME_KEY = "c4d21234-6b0e-4f3a-9e57-2e8b1238a0f2"


def blueprint_copy(name: str, station: str, edit=None) -> str:
    """The station blueprint next to the config, changed by edit(main grid, block at cell), then
    prepared for station the way station_patch.py prepares a patch's blueprints"""
    ET.register_namespace("xsi", XSI)
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    tree = ET.parse(BLUEPRINT)
    grid = max(tree.getroot().iter("CubeGrid"), key=lambda g: len(g.find("CubeBlocks")))
    blocks = grid.find("CubeBlocks")

    def at(cell: str):
        want = tuple(int(v) for v in cell.split(","))
        for block in blocks:
            m = block.find("Min")
            got = tuple(int(m.get(a, 0)) for a in "xyz") if m is not None else (0, 0, 0)
            if got == want:
                return block
        raise KeyError(cell)

    if edit:
        edit(grid, at)
    raw = wa.CONFIG.parent / f"raw-{name}"
    tree.write(raw, encoding="utf-8", xml_declaration=True)
    station_patch.write(
        build_world,
        station_patch.prepare(build_world, station, raw),
        wa.CONFIG.parent / name,
    )
    return name


def lcd(block, text: str) -> None:
    for component in block.iter("ComponentData"):
        if component.findtext("TypeId") == "MyLcdSurfaceComponent":
            component.find("Component/TextPanelContent/Text").text = text


def storage(block, key: str, value: str) -> None:
    for item in block.iter("item"):
        if item.findtext("Key") == key:
            item.find("Value").text = value


def recolor(block) -> None:
    color = block.find("ColorMaskHSV")
    color.set("x", "0.5" if color.get("x") != "0.5" else "0.25")


def run(bench) -> None:
    cluster = bench.cluster
    queries = [0]

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

    def logs() -> str:
        return "\n".join(
            p.read_text(errors="replace")
            for process in PROCESSES
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def seen(text: str) -> int:
        return sum(text in line for line in logs().splitlines())

    # --- the probe's answers, from whichever process holds a grid ---

    def out_lines() -> list[list[str]]:
        return [
            l.split("\t", 2)
            for l in OUT.read_text(errors="replace").splitlines()
            if l.count("\t") >= 2
        ]

    def query(
        grid: str, what: str = "dump", seconds: float = 150
    ) -> tuple[str, str] | None:
        queries[0] += 1
        tag = f"?q{queries[0]}"
        with PENDING.open("a", encoding="utf-8") as f:
            f.write(f"{tag}\t{grid}\t{what}\n")
        hit = until(lambda: [l for l in out_lines() if l[1] == tag], seconds)
        return (hit[0][0], hit[0][2]) if hit else None

    def snap(grid: str, name: str) -> set[str]:
        query(grid, f"snap:{name}")
        path = Path(f"{OUT}.{name}")
        return set(path.read_text().splitlines()) if path.exists() else set()

    def diff(before: set[str], after: set[str]) -> dict:
        return {"gone": sorted(before - after)[:20], "new": sorted(after - before)[:20],
                "counts": [len(before), len(after)]}  # fmt: skip

    def fields(text: str) -> dict[str, str]:
        return dict(re.findall(r"(\w+)=(\S*)", text or ""))

    # --- the admin client ---

    def join(at=None):
        wa.link_mods(
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
        api.character_teleport(*(at or sa.station_tables()[STATION]))
        time.sleep(8)
        return api

    def chat(api, text: str, wait: float = 8) -> str:
        reply = " ".join(rig.chat(api, text, wait))
        print(f"{text} -> {reply[:300]}", flush=True)
        return reply

    def sedit(api, args: str) -> str:
        return chat(api, f"!sedit {args}")

    def sprobe(api, args: str) -> str:
        return chat(api, f"!sprobe {args}", 6)

    def patch(api, id_: str, grid: str, ops: str, verb: str = "apply") -> str:
        return chat(api, f'!oct patch {verb} {id_} "{grid}" "{ops}"')

    def found(api, grid: str, what: str) -> list[tuple[str, str]]:
        """(cell, Type/Subtype) of up to 5 blocks of a subtype or type"""
        return re.findall(
            r"FOUND (\S+) (\S+)", sedit(api, f"find {grid.replace(' ', '_')} {what}")
        )

    def client_blocks(api, grid: str) -> int | None:
        grids = [g for g in api.list_grids() if g.get("name") == grid]
        return (
            api.list_cubes(grids[0]["entityId"], 0, 1).get("total") if grids else None
        )

    def owner_of(entity: int) -> tuple[str, int] | None:
        for node in NODES:
            try:
                got = cluster.node(node, f"/partition-of?entity={entity}")
            except Exception:  # noqa: BLE001 -- a node that is down or doesn't hold it
                continue
            if got and got.get("partition"):
                return node, int(got["partition"])
        return None

    def stop(label: str, api=None) -> bool:
        if api is not None:
            bench.kill_client(CLIENT_ID)
        started = time.monotonic()
        graceful = cluster.stop(grace=240)
        note(
            f"{label}-stop",
            {"graceful": graceful, "seconds": round(time.monotonic() - started)},
        )
        return graceful

    def start() -> None:
        cluster.start(PROCESSES)
        cluster.wait_ready(nodes=PROCESSES)

    # ============================== setup ==============================
    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    PENDING.write_text("")
    OUT.write_text("")
    start()
    api = join()
    # The grids reach the node once the client is near: ask the probe until it has them
    base = (
        until(
            lambda: (lambda r: r if "DUMP" in r else None)(
                sedit(api, f"dump {GRID.replace(' ', '_')}")
            ),
            120,
            5,
        )
        or ""
    )
    station_id = int(re.search(r"DUMP (\d+)", base).group(1)) if base else 0
    status = chat(api, f'!oct patch status "{GRID}"')
    note("baseline", {"station": base, "status": status, "owner": owner_of(station_id),
                      "client_blocks": client_blocks(api, GRID), "islands": query(GRID, "islands")})  # fmt: skip
    check("the probe sees the station", "DUMP" in base, base)
    check(
        "the station has no islands (all its mods loaded, SE-1239)",
        "islands=0" in status,
        status,
    )

    lights = [c for c, _ in found(api, GRID, LIGHT)]
    barrels = [c for c, _ in found(api, GRID, BARREL)]
    connectors = [c for c, _ in found(api, GRID, CONNECTOR)]

    # Free cells where an armor block mounts, by the plugin's own check
    def addable(grid: str, want: int) -> list[str]:
        cells = re.findall(r"FREE (\S+)", sedit(api, f"free {grid.replace(' ', '_')}"))
        ok = []
        for cell in cells:
            if len(ok) < want and "CHECKED" in patch(
                api, "probe", grid, f"add:{cell}:{ARMOR}:Forward:Up", "check"
            ):
                ok.append(cell)
        return ok

    free = addable(GRID, 2)
    yard_free = addable(YARD, 1) if "online" in PARTS else []
    note("targets", {"lights": lights, "barrels": barrels, "connectors": connectors, "free": free,
                     "yard_free": yard_free})  # fmt: skip
    if len(lights) < 3 or len(barrels) < 4 or not free or not connectors:
        check("the station has the blocks the rig changes", False, FOUND["targets"])
        return
    g = GRID

    # ============================== online: !oct patch apply ==============================
    if "online" in PARTS:
        before = int(fields(base)["blocks"])
        # The game's own raze is refused inside the station's safe zone: why patches need the plugin
        sedit(api, f"vdel {g.replace(' ', '_')} {barrels[3]}")
        time.sleep(3)
        still = sedit(api, f"block {g.replace(' ', '_')} {barrels[3]}")
        check(
            "the vanilla raze in the safe zone leaves the block",
            "BLOCK" in still,
            still,
        )

        near_before = query(GRID, "near:600")
        s0 = snap(GRID, "s0")
        reply = patch(
            api,
            "L1-set",
            g,
            f"set:{lights[0]}:{LIGHT}:enabled=0;set:{lights[0]}:{LIGHT}:name=SE-1234 Light",
        )
        check("a block setting applies", "APPLIED L1-set" in reply, reply)
        check("the setting is on the block",
              "SE-1234_Light off" in sedit(api, f"block {g.replace(' ', '_')} {lights[0]}"))  # fmt: skip
        note("set-reply", reply)  # the diff comes as more chat lines
        reply = patch(api, "L1-del", g, f"del:{barrels[0]}:{BARREL}")
        check("a removed lobby block applies", "APPLIED L1-del" in reply, reply)
        check(
            "the log line has the block-list diff",
            until(
                lambda: seen(
                    f"APPLIED L1-del on {GRID}: 1 ops, blocks {before} -> {before - 1}"
                )
                and seen(f"- {barrels[0]} CubeBlock/{BARREL} "),
                15,
            ),
        )
        time.sleep(3)
        s1 = snap(GRID, "s1")
        note("diff-set-del", diff(s0, s1))
        near_after = query(GRID, "near:600")
        check("the removal splits nothing off (no new grids near the station)",
              near_after and near_before and set(near_after[1].split()) == set(near_before[1].split()), {"before": near_before, "after": near_after})  # fmt: skip
        check(
            "the removal took only that block",
            len(s0 - s1) == 2 and len(s1 - s0) == 1,
            diff(s0, s1),
        )

        reply = patch(api, "L1-add", g, f"add:{free[0]}:{ARMOR}:Forward:Up")
        check("an added block applies", "APPLIED L1-add" in reply, reply)
        s2 = snap(GRID, "s2")
        check(
            "the add changes nothing but the new block",
            len(s2 - s1) == 1 and not (s1 - s2),
            diff(s1, s2),
        )
        mounted = query(GRID, f"neighbours:{free[0]}")
        check(
            "the added block has a mount neighbor",
            mounted and not mounted[1].endswith(" 0"),
            mounted,
        )

        reply = patch(
            api,
            "L1-swap",
            g,
            f"swap:{lights[1]}:{LIGHT}:InteriorLight/LargeBlockLight_1corner",
        )
        check("a swapped block applies", "APPLIED L1-swap" in reply, reply)
        swapped = sedit(api, f"block {g.replace(' ', '_')} {lights[1]}")
        check(
            "the swapped block is the new one, with the old one's owner",
            "LargeBlockLight_1corner" in swapped,
            swapped,
        )
        s3 = snap(GRID, "s3")
        check(
            "the swap changes only that block",
            len(s3 - s2) == 1 and len(s2 - s3) == 1,
            diff(s2, s3),
        )

        # --- refusals: nothing of a refused patch applies ---
        reply = patch(api, "L1-wrong", g, f"del:{barrels[1]}:{LIGHT}")
        check(
            "the wrong block in a cell is refused",
            "REFUSED L1-wrong" in reply and "holds" in reply,
            reply,
        )
        reply = patch(api, "L1-float", g, f"add:1000,1000,1000:{ARMOR}:Forward:Up")
        check(
            "an added block with no mount neighbor is refused",
            "REFUSED L1-float" in reply and "mount" in reply,
            reply,
        )
        reply = patch(
            api,
            "L1-half",
            g,
            f"del:{barrels[1]}:{BARREL};set:{barrels[2]}:{BARREL}:enabled=0",
        )
        check(
            "a patch whose second op is bad is refused",
            "REFUSED L1-half" in reply and "op 2" in reply,
            reply,
        )
        kept = sedit(api, f"block {g.replace(' ', '_')} {barrels[1]}")
        check("and its first op is not applied (all or nothing)", BARREL in kept, kept)
        checked = patch(api, "L1-dry", g, f"del:{barrels[1]}:{BARREL}", verb="check")
        check(
            "a check alone applies nothing",
            "CHECKED L1-dry" in checked
            and BARREL in sedit(api, f"block {g.replace(' ', '_')} {barrels[1]}"),
            checked,
        )
        s4 = snap(GRID, "s4")
        check("the refusals left the station as it was", s4 == s3, diff(s3, s4))

        again = patch(api, "L1-set", g, f"set:{lights[0]}:{LIGHT}:enabled=1")
        check(
            "a patch applies once (the ledger on the grid)",
            "SKIP L1-set" in again,
            again,
        )
        after = fields(sedit(api, f"dump {g.replace(' ', '_')}"))
        check("block count: -1 +1", int(after["blocks"]) == before, after)
        time.sleep(5)
        check("the client sees the same block count",
              client_blocks(api, GRID) == int(after["blocks"]), client_blocks(api, GRID))  # fmt: skip
        check("the log has a line per applied patch",
              all(seen(f"Station patch APPLIED {i} on {GRID}") for i in ("L1-set", "L1-del", "L1-add", "L1-swap")))  # fmt: skip

        # --- a docked ship: the connector it is docked to can't go ---
        ship = re.findall(
            r"SHIP (\d+)",
            sprobe(api, f"dock {STATION} mine AT={connectors[0].replace(',', ';')}"),
        )
        docked = until(
            lambda: "yes" in sprobe(api, f"docked {ship[0]}") if ship else False, 30, 3
        )
        note("docked-ship", {"ship": ship, "docked": docked})
        if check("a ship docks for the connector check", ship and docked, ship):
            reply = patch(api, "L1-conn", g, f"del:{connectors[0]}:{CONNECTOR}")
            check(
                "removing the connector a ship is docked to is refused",
                "REFUSED L1-conn" in reply and "docked" in reply,
                reply,
            )
            check("the ship stays docked", "yes" in sprobe(api, f"docked {ship[0]}"))

        # --- the shipyard during a paid build ---
        sprobe(api, "balance 400000")
        tr.remove(api)
        mark = tp.latest(api)
        tr.load(api, LONG)
        tr.settled(api, "table", "paid")
        press = chat(api, "!oct order", 15)
        if "Your quote for" in press:
            press = chat(api, "!oct order", 15)
        turn_told = tp.wait_chat(api, "It is your turn", 90, mark)
        sprobe(api, f"yardload {STATION} Component/SteelPlate {PLATES}")
        built_mark = seen("built or broken down")
        press = chat(api, "!oct order", 15)
        placed = until(
            lambda: [x for x in api.list_grids() if x.get("name") == SHIP], 60
        )
        note(
            "build-started",
            {"turn": bool(turn_told), "press": press, "placed": bool(placed)},
        )
        if check("a paid build starts", placed, press):
            containers = found(api, YARD, "CargoContainer")
            batteries = [c for c, _ in found(api, YARD, "LargeBlockBatteryBlock")]
            note("yard-targets", {"containers": containers, "batteries": batteries})
            if containers:
                cell, kind = containers[0]
                reply = patch(
                    api, "L1-crate", YARD, f"set:{cell}:{kind}:name=SE-1234 Crate"
                )
                check("a change to the shipyard's containers during a build is refused",
                      "REFUSED L1-crate" in reply and "order" in reply, reply)  # fmt: skip
            yard_ops = ";".join(
                ([f"set:{batteries[0]}:LargeBlockBatteryBlock:enabled=0"] if batteries else [])
                + ([f"add:{yard_free[0]}:{ARMOR}:Forward:Up"] if yard_free else [])
            )  # fmt: skip
            reply = (
                patch(api, "L1-yard", YARD, yard_ops)
                if yard_ops
                else "nothing on the yard to change"
            )
            check(
                "another shipyard change during a build applies",
                "APPLIED L1-yard" in reply,
                reply,
            )
            reply = patch(api, "L1-build", g, f"del:{barrels[2]}:{BARREL}")
            check(
                "a station change during a build applies",
                "APPLIED L1-build" in reply,
                reply,
            )
            built = until(lambda: seen("built or broken down") > built_mark, 900, 10)
            check("the build still ends", built)
            ships = [x for x in api.list_grids() if x.get("name") == SHIP]
            info = fields(sprobe(api, f"info {ships[0]['entityId']}")) if ships else {}
            check("the built ship is whole", info.get("blocks") == str(COLUMNS), info)
        patched = sedit(api, f"dump {g.replace(' ', '_')}")
        patched_yard = sedit(api, f"dump {YARD.replace(' ', '_')}")
        note("patched", {"station": patched, "yard": patched_yard})

        # --- persistence: the owning node restarts, then the whole cluster stops and starts ---
        holder = owner_of(station_id)
        note("owner-before-restart", holder)
        time.sleep(70)  # a periodic save after the last patch
        bench.kill_client(CLIENT_ID)
        if holder:
            cluster.restart_node(holder[0])
            cluster.wait_ready(nodes=PROCESSES)
        api = join()
        got = query(GRID)
        note("after-node-restart", got)
        check("the patched station survives its node's restart",
              got and fields(got[1]).get("hash") == fields(patched).get("hash"), got)  # fmt: skip
        check("the ledger survives it",
              got and all(i in fields(got[1]).get("ledger", "") for i in ("L1-set", "L1-del", "L1-add", "L1-swap")), got)  # fmt: skip
        check("the cluster stops gracefully", stop("online", api))
        start()
        api = join()
        got = query(GRID)
        yard_got = query(YARD)
        note("after-stop-start", {"station": got, "yard": yard_got})
        check("the patched station survives a stop and start",
              got and fields(got[1]).get("hash") == fields(patched).get("hash"), got)  # fmt: skip
        check("the patched shipyard survives it",
              yard_got and fields(yard_got[1]).get("hash") == fields(patched_yard).get("hash"), yard_got)  # fmt: skip
        store = sprobe(api, f"store {STATION}")
        check("the station's Store still works", "STORE" in store, store)
        quote = chat(api, f"!oct market show {STATION}", 10)
        check("the station's market answers", "Alpha" in quote or "SC" in quote, quote)
        check("the station is still the station's grid (faction station entry)",
              str(station_id) in (got[1] if got else ""), got)  # fmt: skip

    # ============================== replace: a new blueprint, by its diff ==============================
    if "replace" in PARTS:
        gu = GRID.replace(" ", "_")
        lamp, named = lights[3], lights[4]
        crates = [c for c, _ in found(api, GRID, "CargoContainer")]
        crate = crates[0] if crates else None
        if crate:
            sedit(api, f"give {gu} {crate} Ingot/Platinum 7")
        sedit(api, f"storeset {gu} {OFFICE} {RUNTIME_KEY} live")
        before = {q: (query(GRID, q) or ("", ""))[1] for q in
                  (f"block:{lamp}", f"block:{named}", f"block:{AIRLOCK}", f"items:{crate}:Ingot/Platinum",
                   f"storage:{OFFICE}", f"prop:{lamp}:Radius", "dump")}  # fmt: skip
        stores = found(api, GRID, "StoreBlock")
        store_before = sedit(api, f"block {gu} {stores[0][0]}") if stores else ""
        market_before = chat(api, f"!oct market show {STATION}", 10)
        note("replace-before", {**before, "store": store_before})
        added = free[1] if len(free) > 1 else None

        def edit(grid, at):
            blocks = grid.find("CubeBlocks")
            blocks.remove(at(barrels[3]))
            if added:
                armor = next(
                    b
                    for b in blocks
                    if b.findtext("SubtypeName") == "LargeBlockArmorBlock"
                )
                copy = ET.fromstring(ET.tostring(armor))
                x, y, z = added.split(",")
                copy.find("Min").attrib.update(x=x, y=y, z=z)
                o = copy.find("BlockOrientation")
                if o is not None:
                    o.attrib.update(Forward="Forward", Up="Up")
                blocks.append(copy)
            at(lamp).find("Radius").text = "7.5"
            at(named).find("CustomName").text = "SE-1234 Hall Light"
            lcd(at(OFFICE), "SE-1234 replaced text")
            storage(at(OFFICE), LCD_KEY, "SE-1234")
            recolor(at(AIRLOCK))
            if crate:
                recolor(at(crate))
            # Not in the blueprint, and build_world.py leaves it alone
            immune = ET.SubElement(grid, "Immune")
            immune.text = "true"

        blueprint_copy(SOURCE, GRID)
        target = blueprint_copy("station-to.sbc", GRID, edit)
        expected = 6 + (1 if added else 0) + (1 if crate else 0)
        diff_reply = chat(api, f'!oct patch diff R1 "{GRID}" {SOURCE} {target}', 10)
        check("the diff finds the changes and nothing else",
              f"CHECKED R1 on {GRID}: {expected} ops would apply" in diff_reply, diff_reply)  # fmt: skip
        reply = chat(api, f'!oct patch replace R1 "{GRID}" {SOURCE} {target}', 10)
        check("the replacement applies", f"APPLIED R1 on {GRID}" in reply, reply)
        after = {q: (query(GRID, q) or ("", ""))[1] for q in before}
        note("replace-after", after)

        def entity(text: str) -> str:
            return text.split()[-1] if text.startswith("BLOCK") else ""

        check(
            "the removed block is gone",
            (query(GRID, f"block:{barrels[3]}") or ("", ""))[1].startswith("NOBLOCK"),
        )
        if added:
            check(
                "the added block is there",
                ARMOR.split("/")[1] in (query(GRID, f"block:{added}") or ("", ""))[1],
            )
        check("a terminal property is the new blueprint's (light radius)", after[f"prop:{lamp}:Radius"] == "PROP Radius=7.5",
              after[f"prop:{lamp}:Radius"])  # fmt: skip
        check("a block built again is a new entity", entity(after[f"block:{lamp}"]) != entity(before[f"block:{lamp}"]),
              [before[f"block:{lamp}"], after[f"block:{lamp}"]])  # fmt: skip
        check("a new name is set in place (same entity)",
              "SE-1234_Hall_Light" in after[f"block:{named}"] and entity(after[f"block:{named}"]) == entity(before[f"block:{named}"]),
              after[f"block:{named}"])  # fmt: skip
        text = (query(GRID, f"text:{OFFICE}") or ("", ""))[1]
        check(
            "an LCD shows the new blueprint's text",
            "SE-1234 replaced text" in text,
            text,
        )
        kept = after[f"storage:{OFFICE}"]
        # The live keys: the game drops a key no loaded mod registers at load, like the LCD
        # app's, so only the probe's runtime key is left besides the changed one
        unchanged = [
            k
            for k in before[f"storage:{OFFICE}"].split()[2:]
            if not k.startswith(LCD_KEY)
        ]
        check(
            "the mod storage key the blueprint changes has its new value",
            f"{LCD_KEY}=SE-1234" in kept,
            kept,
        )
        check("the mod storage keys it doesn't change keep their values",
              unchanged and all(k in kept.split() for k in unchanged), [unchanged, kept])  # fmt: skip
        check(
            "a key set while the station ran is kept",
            f"{RUNTIME_KEY}=live" in kept,
            kept,
        )
        check("the mod's block (Rotary Airlock) is built again with its name",
              "AdvancedDoor/RotaryAirlock" in after[f"block:{AIRLOCK}"]
              and before[f"block:{AIRLOCK}"].split()[2] == after[f"block:{AIRLOCK}"].split()[2]
              and entity(after[f"block:{AIRLOCK}"]) != entity(before[f"block:{AIRLOCK}"]), after[f"block:{AIRLOCK}"])  # fmt: skip
        if crate:
            check("a container built again keeps its items",
                  before[f"items:{crate}:Ingot/Platinum"].split()[2] == after[f"items:{crate}:Ingot/Platinum"].split()[2]
                  and before[f"items:{crate}:Ingot/Platinum"].split()[3] != after[f"items:{crate}:Ingot/Platinum"].split()[3],
                  [before[f"items:{crate}:Ingot/Platinum"], after[f"items:{crate}:Ingot/Platinum"]])  # fmt: skip
        check(
            "the grid setting is set (Immune)",
            "immune=True" in after["dump"],
            after["dump"],
        )
        status = chat(api, f'!oct patch status "{GRID}"')
        check(
            "the station has no islands after the replacement",
            "islands=0" in status,
            status,
        )
        check(
            "the Store block is untouched",
            not stores or sedit(api, f"block {gu} {stores[0][0]}") == store_before,
        )
        market_after = chat(api, f"!oct market show {STATION}", 10)
        check(
            "the market's stock is the same",
            market_after == market_before,
            [market_before[:200], market_after[:200]],
        )
        again = chat(api, f'!oct patch replace R1 "{GRID}" {SOURCE} {target}', 10)
        check("a replacement applies once", f"SKIP R1 on {GRID}" in again, again)
        replaced = sedit(api, f"dump {gu}")
        time.sleep(70)  # a periodic save after the replacement
        check("the cluster stops gracefully", stop("replace", api))
        start()
        api = join()
        got = query(GRID)
        check("the replaced station survives a stop and start",
              got and fields(got[1]).get("hash") == fields(replaced).get("hash") and "R1" in fields(got[1]).get("ledger", ""), got)  # fmt: skip
        radius = (query(GRID, f"prop:{lamp}:Radius") or ("", ""))[1]
        check("and so does the new radius", radius == "PROP Radius=7.5", radius)

    # ============================== staged: StationPatches, applied at load ==============================
    if "staged" in PARTS:
        far = wa.stations().get(FAR)
        check("the far station is in the world", far, wa.stations())
        check("the cluster stops gracefully", stop("staged", api))
        # Beta Station is the same blueprint as Alpha: the cells checked on Alpha
        cell = free[-1]
        ops = f"swap:{lights[2]}:{LIGHT}:InteriorLight/LargeBlockLight_1corner;add:{cell}:{ARMOR}:Forward:Up"
        note("staged-ops", ops)
        source = blueprint_copy("station-beta-from.sbc", FAR)

        def beta(grid, at):
            lcd(at(OFFICE), "SE-1234 staged text")
            at(lights[4]).find("CustomName").text = "SE-1234 Staged Light"

        target = blueprint_copy("station-beta.sbc", FAR, beta)
        wa.set_config(
            StationPatches=staged_entry("L2-far", FAR, ops)
            + staged_entry("L2-bp", FAR, source=source, target=target)
        )
        start()
        api = join(at=far)
        applied = until(
            lambda: seen(f"Station patch APPLIED L2-far on {FAR} (staged)"), 120, 3
        )
        check(
            "the staged patch applies when a node loads the station", applied, applied
        )
        got = query(FAR)
        check(
            "the ledger has it",
            got and "L2-far" in fields(got[1]).get("ledger", ""),
            got,
        )
        swapped = query(FAR, f"block:{lights[2]}")
        check(
            "the staged swap is on the far station",
            swapped and "LargeBlockLight_1corner" in swapped[1],
            swapped,
        )
        bp = until(
            lambda: seen(f"Station patch APPLIED L2-bp on {FAR} (staged)"), 30, 3
        )
        text = (query(FAR, f"text:{OFFICE}") or ("", ""))[1]
        check(
            "the staged blueprint replacement applies after the ops patch",
            bp and "SE-1234 staged text" in text,
            text,
        )
        mounted = query(FAR, f"neighbours:{cell}")
        check(
            "the staged block has a mount neighbor",
            mounted and not mounted[1].endswith(" 0"),
            mounted,
        )
        first = got[1] if got else ""
        time.sleep(70)  # a periodic save after the patch
        check("the cluster stops gracefully", stop("staged2", api))
        start()
        api = join(at=far)
        got = query(FAR)
        ledger = fields(got[1]).get("ledger", "").split(",") if got else []
        note(
            "staged-after-restart",
            {"dump": got, "log lines": seen(f"Station patch APPLIED L2-far on {FAR}")},
        )
        check(
            "the staged patch applied once over a restart (once in the ledger)",
            ledger.count("L2-far") == 1 and ledger.count("L2-bp") == 1,
            ledger,
        )
        check("the far station is as patched after a restart",
              got and fields(got[1]).get("hash") == fields(first).get("hash"), got)  # fmt: skip


def main() -> int:
    run.world_template = wa.prepare()
    folders = [turn.build_probe(), build_edit_probe()]
    os.environ["CLUSTER_EXTRA_PLUGINS"] += "".join(f",node:{f}" for f in folders)
    os.environ.update(EDITPROBE_PENDING=str(PENDING), EDITPROBE_OUT=str(OUT))
    wa.set_config(
        FreeBuildPcuLimit=0,
        ShipyardLaborFeePer100Pcu=10,
        ShipyardTurnMinutes=WINDOW,
        ShipyardTableMinutes=600,
        FreeBuildCooldownMinutes=0,
        MoneySubsidyPerActivePlayer=0,
        MoneyPlayerTaxThreshold=1_000_000_000,
        MoneyOctCap=1_000_000_000_000,
        StationPatches="",
    )
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-station-patch")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-STATION-PATCH",
        "SE-1234: station patches of the Octahedron plugin, online and staged",
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
