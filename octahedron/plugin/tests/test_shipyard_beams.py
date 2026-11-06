"""Checks the shipyard welders' beams as a client sees them while a ship is built (SE-782,
SE-441): the client and the server each run the beam-probe test plugin (tests/beam-probe),
which logs once a second what ToolCore says about the shipyard's tools. Prints a table of
the build and a pass/fail summary, exit code 0 = all passed. On a world built by
build_world.py with the final shipyard.

The steps, with a long build running: OCT_BEAM_STEPS=stream goes beyond sync distance and
comes back, =rejoin leaves the game and joins again; the shipyard is streamed in again each
time; =off switches the welders off on the client alone, with nothing streamed, which only the
plugin's switch sent every 30 s turns on again (SE-963); =none stays in range, to count the
packets; =handover empties the server's client lists of the shipyard's tools before the order,
as a cluster node has them after a handover that brought the shipyard and the client along,
which keeps its tools and so never asks the new node for them again (SE-1115). All of its welders must shoot on the client after that, and some must hit, also once
all blocks are placed and only their integrity is left. With the OctahedronFilaments tools
the client must draw their filaments too.

    OCT_TASK=SE-782 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_shipyard_beams.py

OCT_BEAM_OFFLINE=1 compares a game without a server: the server stops mid-build and saves,
then the client loads that save offline, the zones allowing welding as the plugin isn't
there. ToolCore runs the same loop with the real inventories then.

With OCT_SHOTS=<folder> and OCT_BEAM_RENDER=1 the client renders and saves screenshots from
among the welders after the steps.

OCT_BEAM_SHIP (8,8,24), OCT_BEAM_WELD (0.5, the world has 3) and OCT_BEAM_WAIT set the ship,
the welder speed and the seconds per step. OCT_BEAM_ZONE=allow is the control: the zones
allow welding. Needs what tests/test_support_arm.py needs.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-782")
import rig  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_shipyard_order as so  # noqa: E402
from test_client import log  # noqa: E402

PROBE = (
    "beam-probe",
    Path(__file__).resolve().parent / "beam-probe",
    "BeamProbe.xml",
    "beam-probe",
)
SHIP = "ArmBeams"
# OCT_SHIPYARD_STATION=Moon runs the shipyard checks at the Moon Shipyard (SE-1069)
STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
ENTITY = 777000782000100
STEPS = os.environ.get("OCT_BEAM_STEPS", "stream,rejoin,off").split(",")
# The plugin sends the switch to all clients in range every 30 s, at one of its 10 s checks
RESEND = 45
OFF = "the welders go off on the client alone"
SIZE = tuple(int(x) for x in os.environ.get("OCT_BEAM_SHIP", "8,8,24").split(","))


# OCT_BEAM_MIX=1: every other block functional, whose many components take the welders long
MIXED = [
    ("MyObjectBuilder_Reactor", "LargeBlockSmallGenerator"),
    ("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock"),
    ("MyObjectBuilder_CargoContainer", "LargeBlockSmallContainer"),
]


def ship() -> tuple[str, int]:
    special = {}
    if os.environ.get("OCT_BEAM_MIX"):
        for x in range(SIZE[0]):
            for y in range(SIZE[1]):
                for z in range(SIZE[2]):
                    if (x + y + z) % 2:
                        tag, subtype = MIXED[(x + y + z) // 2 % len(MIXED)]
                        special[(x, y, z)] = rig.block(tag, subtype, (x, y, z))
    blocks = sa.hull(special, SIZE, "LargeHeavyBlockArmorBlock")
    blocks.append(
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (1, SIZE[1], 0))
    )
    return rig.grid(SHIP, ENTITY, blocks, tag="MyObjectBuilder_CubeGrid"), len(blocks)


marks: list[tuple[float, str]] = []


def mark(text: str) -> None:
    log(text)
    marks.append((time.time(), text))


def scenario(remote):
    """Away beyond sync distance and back, then out of the game and back in, with the
    build running. Returns the client. Every step waits so the table shows its effect"""
    wait = float(os.environ.get("OCT_BEAM_WAIT", "60"))
    time.sleep(wait)
    if "stream" in STEPS:
        x, y, z = remote.get_character()["position"]
        mark("the client goes beyond sync distance")
        remote.character_teleport(x + 20_000, y, z)
        time.sleep(40)
        mark("the client comes back")
        remote.character_teleport(x, y, z)
        time.sleep(wait)
    if "rejoin" in STEPS:
        mark("the client leaves the game")
        rig.stop_client()
        time.sleep(20)
        remote = rig.start_client(
            render=bool(os.environ.get("OCT_BEAM_RENDER")), extra_dev_folders=[PROBE]
        )
        mark("the client is back in the game")
        time.sleep(wait)
    if "off" in STEPS:
        switch_off(rig.APPDATA)
        time.sleep(RESEND + 30)
    return remote


def forget(remote) -> None:
    """The server forgets the clients of the shipyard's tools once the client has them"""
    sa.at_table(remote, STATION)
    server = rig.SERVER_DATA / "beam-probe.log"
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline and not any(
        int(s["replicated"]) for s in samples(server)[-1:]
    ):
        time.sleep(1)
    mark("the server forgets the clients of the shipyard's tools")
    (rig.SERVER_DATA / "beam-probe.forget").touch()
    while time.monotonic() < deadline and "FORGET" not in server.read_text(
        encoding="utf-8"
    ):
        time.sleep(1)
    log(
        next(
            x for x in server.read_text(encoding="utf-8").splitlines() if "FORGET" in x
        )
    )


def switch_off(user_data: Path) -> None:
    """The probe switches the client's welders off at its next sample"""
    mark(OFF)
    (user_data / "beam-probe.off").touch()


def recovered(picked: list[dict]) -> str:
    """How long the welders stayed off after the switch-off, from the samples after it"""
    off = next((s for s in picked if int(s["activated"]) < int(s["tools"])), None)
    if off is None:
        return "never off"
    on = next(
        (
            s
            for s in picked
            if s["t"] > off["t"] and int(s["activated"]) == int(s["tools"])
        ),
        None,
    )
    return f"on again after {on['t'] - off['t']:.0f} s" if on else "still off"


def samples(path: Path) -> list[dict]:
    out = []
    if not path.exists():
        return out
    for line in path.read_text(encoding="utf-8").splitlines():
        if " TOOLS " in line:
            fields = dict(re.findall(r"(\w+)=(\S+)", line))
            fields["t"] = float(line.split()[0])
            out.append(fields)
    return out


def zone_events(path: Path) -> list[str]:
    if not path.exists():
        return []
    return [x for x in path.read_text(encoding="utf-8").splitlines() if " ZONE " in x]


def report() -> None:
    client = samples(rig.APPDATA / "beam-probe.log")
    server = samples(rig.SERVER_DATA / "beam-probe.log")
    if not client:
        log("no client samples")
        return
    on = [s for s in server if int(s["activated"]) > 0]
    if on:
        log(
            f"ToolCore packets to the clients during the build: "
            f"{int(on[-1]['sent']) - int(on[0]['sent'])} in {on[-1]['t'] - on[0]['t']:.0f} s, "
            f"entries in the tools' client lists at the end: {on[-1]['replicated']}"
        )
    start = client[0]["t"]
    for at, text in marks:
        log(f"mark {at - start:.0f}: {text}")
    log(
        "time  | client: tools act pow tgt hit stock zone fil drawn | server: act hit stock | ship unfinished"
    )
    step = int(os.environ.get("OCT_BEAM_STEP", "20"))
    for at in range(0, int(client[-1]["t"] - start) + 1, step):
        c = next((s for s in client if s["t"] >= start + at), None)
        s = next((s for s in server if s["t"] >= start + at), None)
        if c is None:
            continue
        srv = (
            f"{s['activated']:>3} {s['hitting']:>3} {s['stocked']:>3}"
            if s
            else "  -   -   -"
        )
        print(
            f"{at:5d} | {c['tools']:>3} {c['activated']:>3} {c['powered']:>3} {c['target']:>3} "
            f"{c['hitting']:>3} {c['stocked']:>3} {c['zone_allows']:>5} {c.get('filament_hits', '-'):>3} "
            f"{c.get('drawn', '-'):>5} | {srv} | {c['ship']:>4} {c['unfinished']:>4}",
            flush=True,
        )
    for name, path in (("client", rig.APPDATA), ("server", rig.SERVER_DATA)):
        events = zone_events(path / "beam-probe.log")
        counts: dict[str, int] = {}
        for e in events:
            counts[e.split()[2]] = counts.get(e.split()[2], 0) + 1
        log(f"{name} zone events: {counts}")


def offline(remote) -> None:
    time.sleep(float(os.environ.get("OCT_BEAM_WAIT", "60")))
    rig.stop_client()
    rig.stop_server()
    allow_welding()
    # Anything else starts a lobby, which fails without Steam
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = re.sub(
            r"<OnlineMode>[^<]*<",
            "<OnlineMode>OFFLINE<",
            path.read_text(encoding="utf-8"),
        )
        path.write_text(text, encoding="utf-8")
    (rig.APPDATA / "beam-probe.log").unlink(missing_ok=True)
    remote = rig.start_client(extra_dev_folders=[PROBE], connect=False)
    remote.load(str(rig.WORLD))
    rig.wait_world(remote)
    rig.ensure_character(remote)
    mark("offline: the save is loaded")
    time.sleep(30)
    x, y, z = remote.get_character()["position"]
    mark("offline: the client goes beyond sync distance")
    remote.character_teleport(x + 20_000, y, z)
    time.sleep(40)
    mark("offline: the client comes back")
    remote.character_teleport(x, y, z)
    deadline = time.monotonic() + 900
    while time.monotonic() < deadline:
        time.sleep(20)
        last = samples(rig.APPDATA / "beam-probe.log")[-1]
        if int(last["unfinished"]) == 0:
            break
    time.sleep(10)
    rig.stop_client()


def allow_welding() -> None:
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    # A saved world lists them in its own order
    text, count = re.subn(
        r"<AllowedActions(?: />|>([^<]*)</AllowedActions>)",
        r"<AllowedActions>\1 Welding BuildingProjections</AllowedActions>",
        sector.read_text(encoding="utf-8"),
    )
    assert count, "no zones"
    sector.write_text(text, encoding="utf-8")
    # The binary copy a save has would win
    (rig.WORLD / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)


def check_beams() -> None:
    """The welders shoot on the client while the server welds, some hit: from the start, after
    the shipyard came back to the client, and once only integrity is left"""
    client = samples(rig.APPDATA / "beam-probe.log")
    for s in client:
        for name in (
            "tools",
            "activated",
            "hitting",
            "ship",
            "unfinished",
            "filaments",
            "drawn",
        ):
            if name in s:
                s[name] = int(s[name])
    sa.check(len(client) > 0, "the client's probe logs")
    if not client:
        return
    full = max(s["ship"] for s in client)
    building = [s for s in client if s["unfinished"] > 0]
    ends = building[-1]["t"] if building else 0

    def shooting(what: str, picked: list[dict]) -> None:
        off = [s for s in picked if s["activated"] < s["tools"]]
        hit = max((s["hitting"] for s in picked), default=0)
        sa.check(
            len(picked) > 0 and not off and hit > 0,
            f"{what}: {len(picked)} samples, {len(off)} with welders off, at most {hit} hitting",
        )
        if any(s.get("filaments", 0) for s in picked):
            drawn = max(s.get("drawn", 0) for s in picked)
            sa.check(drawn > 0, f"{what}: filaments drawn by at most {drawn} tools")

    step = next(
        (at for at, text in marks if "goes" in text or "leaves" in text or text == OFF),
        None,
    )
    first = ends if step is None else step
    # The first blocks may come a moment before the switch, which comes after the welders are on.
    # A client the server forgot gets the switch at the next 10 s check of the build if a
    # tool's packet came before the zone's.
    settle = 25
    begins = (
        building[0]["t"] + (settle if "handover" in STEPS else 5) if building else 0
    )
    shooting("before any step", [s for s in building if begins <= s["t"] < first])
    # The next 10 s check of the build sends the switch again, 10 s more if a tool's packet
    # came before the zone's
    for i, (at, text) in enumerate(marks):
        if "back" in text:
            until = marks[i + 1][0] if i + 1 < len(marks) else ends
            shooting(
                f"after {text!r}",
                [s for s in building if at + settle < s["t"] < until],
            )
    for i, (at, text) in enumerate(marks):
        if text == OFF:
            until = marks[i + 1][0] if i + 1 < len(marks) else ends
            again = recovered([s for s in client if at <= s["t"] < until])
            sa.check(
                again.startswith("on again"), f"after {text!r}: the welders are {again}"
            )
            shooting(
                f"after {text!r}",
                [s for s in building if at + RESEND < s["t"] < until],
            )
    shooting(
        "only integrity left",
        [
            s
            for s in building
            if s["ship"] == full and (step is None or s["t"] > step + settle)
        ],
    )


def summary() -> int:
    failed = [what for ok, what in sa.checks if not ok]
    print(f"{len(sa.checks) - len(failed)}/{len(sa.checks)} checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


def main() -> int:
    grid, blocks = ship()
    tables = list(sa.TABLES)
    # sa.TABLES is in the world's order of the stations
    tables[
        ["North", "South", "Alpha", "Beta", "Gamma", "Delta", "Moon"].index(STATION)
    ] = grid
    sa.CASES["beams"] = (STATION, SHIP, blocks, False, False, "Large")
    rig.stop_client()
    rig.stop_server()
    for path in (rig.APPDATA, rig.SERVER_DATA):
        (path / "beam-probe.log").unlink(missing_ok=True)
    so.prepare(tables, 60)
    rig.prepare_server(extra_dev_folders=[so.pl.LOCAL_WORLD_MODS, so.pl.MGP, PROBE])
    if os.environ.get("OCT_BEAM_ZONE") == "allow":
        # The control: the zones don't refuse welding on the clients either
        allow_welding()
    weld = os.environ.get("OCT_BEAM_WELD", "0.5")
    if weld:
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            path = rig.WORLD / name
            text, count = re.subn(
                r"<WelderSpeedMultiplier>[^<]*<",
                f"<WelderSpeedMultiplier>{weld}<",
                path.read_text(encoding="utf-8"),
            )
            assert count, name
            path.write_text(text, encoding="utf-8")
    os.environ["BEAM_PROBE_YARD"] = f"{STATION} Station Shipyard"
    rig.start_server()
    try:
        remote = rig.start_client(
            render=bool(os.environ.get("OCT_BEAM_RENDER")), extra_dev_folders=[PROBE]
        )
        log("joined")
        if "handover" in STEPS:
            forget(remote)
        ordered = sa.order(remote, "beams")
        assert ordered is not None
        sa.STALL = 600.0
        if os.environ.get("OCT_BEAM_OFFLINE"):
            offline(remote)
            report()
            check_beams()
            return summary()
        remote = scenario(remote)
        if so.SHOTS:
            so.screenshots(remote, sa.shipyard_id(remote, STATION), "beams")
        sa.follow(remote, "beams", lambda _: False)
        log(f"build ended: {sa.wait_chat(remote, f'{SHIP!r} is ready', 60, ordered)}")
        time.sleep(10)
    finally:
        rig.stop_client()
        rig.stop_server()
    report()
    check_beams()
    return summary()


if __name__ == "__main__":
    sys.exit(main())
