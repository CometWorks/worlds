"""Top speeds on a local test cluster (SE-807, SE-869): a small ship, a large ship and a character on
jetpack, each flown by a client holding W until the speed stops rising, under the world's Relative Top
Speed config and the Trade Lanes mod. Same bench and world as wa_economy_cluster.py, see there for host.env.

    OCT_WORLD_SOURCE=<world built by build_world.py> OCT_TRADE_LANES=<mod folder> \
        TOP_SPEED_BLUEPRINTS=<folder> python3 tests/top_speed_cluster.py CLUSTER_TEST_DIR     (from plugin/)

OCT_TRADE_LANES: the Trade Lanes mod to run, by default the Workshop's. TOP_SPEED_BLUEPRINTS: a folder holding
copies of Viktor's two blueprints, which aren't in this repo. Their tanks and batteries are filled in the copy
that gets spawned, the blocks stay as they are.
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
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    _partitions,
    bench_module,
    link_mods,
    pl,
    rig,
    wait_until,
)
from se_autopilot import enter_cockpit  # noqa: E402

wa.ROOT = Path.home() / f"cluster-runs/top-speed-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"
NODES = ("node-1", "node-2")
PROCESSES = ("wa-1", *NODES)
RTS, TRADE_LANES = 1359618037, 3816175430
RTS_CONFIG = rig.PLUGIN.parent / "config/relative-top-speed.xml"
RTS_STORAGE = f"Storage/{RTS}.sbm_RelativeTopSpeed"
BLUEPRINTS = Path(os.environ["TOP_SPEED_BLUEPRINTS"])
# In empty space, far enough from the 450 km braking sphere for 60 km of flight
SHIPS = (
    (
        "small ship",
        "Small Hydrogen and Ion Welder + Repair",
        "SmallGrid",
        (0, -300_000, 200_000),
    ),
    (
        "large ship",
        "Black Warship With Ext Cockpit",
        "LargeGrid",
        (100_000, -300_000, 0),
    ),
)
CHARACTER_AT = (-100_000, -300_000, 0)
# Above this the speed can only come from Relative Top Speed's own values: its defaults stop at 140
FASTER_THAN = 140
BATTERY_MWH = {"SmallBlockSmallBatteryBlock": 0.05, "LargeBlockBatteryBlock": 3}


def filled_blueprint(name: str, out: Path) -> Path:
    """A copy of the blueprint with full tanks and batteries"""
    text = (BLUEPRINTS / name / "bp.sbc").read_text(encoding="utf-8")
    text = re.sub(r"<FilledRatio>[^<]*", "<FilledRatio>1", text)

    def battery(match: re.Match) -> str:
        return re.sub(
            r"<CurrentStoredPower>[^<]*",
            f"<CurrentStoredPower>{BATTERY_MWH[match[1]]}",
            match[0],
        )

    text = re.sub(
        r'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_BatteryBlock">\s*'
        r"<SubtypeName>(\w+)</SubtypeName>.*?</MyObjectBuilder_CubeBlock>",
        battery,
        text,
        flags=re.S,
    )
    path = out / f"{re.sub(r'[^A-Za-z]', '', name)}.sbc"
    path.write_text(text, encoding="utf-8")
    return path


def cruise(section: str, mass: float) -> tuple[float, float, float]:
    """(cruise speed at this mass, boost limit, resistance multiplier) from the world's config"""
    node = ET.parse(RTS_CONFIG).getroot().find(section)
    points = sorted(
        (float(p.get("Mass")), float(p.get("Speed"))) for p in node.find("CruiseCurve")
    )
    speed = points[-1][1]
    if mass <= points[0][0]:
        speed = points[0][1]
    else:
        for (m0, s0), (m1, s1) in zip(points, points[1:]):
            if m0 <= mass <= m1:
                speed = s0 + (s1 - s0) * (mass - m0) / (m1 - m0)
    return (
        speed,
        float(node.findtext("MaxBoostSpeed")),
        float(node.findtext("ResistanceMultiplier")),
    )


def fly(read, seconds: float) -> list[tuple[float, float]]:
    """(time, speed) samples until the speed stops rising: nothing gained for 8 s, after at least 15 s"""
    samples: list[tuple[float, float]] = []
    start = time.monotonic()
    while (now := time.monotonic() - start) < seconds:
        samples.append((now, read()))
        recent = [s for t, s in samples if t > now - 8]
        older = [s for t, s in samples if t <= now - 8]
        if now > 15 and older and max(recent) <= max(older) + 0.2:
            break
        time.sleep(0.25)
    return samples


def acceleration(samples: list[tuple[float, float]]) -> float:
    """m/s2 from standstill to the first 60 m/s, before any limit acts"""
    moving = [(t, s) for t, s in samples if 2 < s < 60]
    if len(moving) < 2:
        return 0.0
    return (moving[-1][1] - moving[0][1]) / max(moving[-1][0] - moving[0][0], 0.01)


def server_speed(bench) -> float | None:
    """The fastest member of any partition, as the nodes report it"""
    speeds = []
    for node in NODES:
        try:
            speeds += [row.get("maxSpeed") or 0 for row in _partitions(bench, node)]
        except (ClusterError, OSError):
            pass
    return max(speeds) if speeds else None


def run(bench) -> None:
    cluster = bench.cluster
    results: dict[str, dict] = {}

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in PROCESSES:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
        # The bench's world copy leaves Storage behind. A file in the mod's local storage counts
        # when the world has none, and the mod then writes it to the world
        storage = bench.run_root / f"{process}-data" / RTS_STORAGE
        storage.mkdir(parents=True, exist_ok=True)
        shutil.copy(RTS_CONFIG, storage / "RelativeTopSpeed.cfg")
    cluster.start(list(PROCESSES))
    cluster.wait_ready(nodes=list(PROCESSES))
    for process in PROCESSES:
        configs = list(
            (bench.run_root / f"{process}-data" / "Saves").glob(
                f"*/{RTS_STORAGE}/RelativeTopSpeed.cfg"
            )
        )
        check(
            f"{process} has the world's Relative Top Speed config",
            configs and "<SpeedLimit>360<" in configs[0].read_text(encoding="utf-8"),
            configs,
        )
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
    bench.ensure_gameplay_focus(CLIENT_ID)
    bench.record("rts-config", rig.chat(api, "/rts config", 5))

    for label, blueprint, section, at in SHIPS:
        path = filled_blueprint(blueprint, wa.ROOT)
        query = urllib.parse.urlencode(
            {"path": str(path), "x": at[0], "y": at[1], "z": at[2], "name": label}
        )
        spawn = cluster.node(
            "node-1", f"/blueprint-test-spawn?{query}", method="POST", timeout=120
        )
        grid = spawn["grids"][0]
        api.character_teleport(at[0], at[1] + 60, at[2])

        def on_client():
            try:
                return api.get_grid(grid)
            except Exception as error:  # noqa: BLE001 -- not streamed to the client yet
                print(f"{label} not on the client yet: {error}", flush=True)
                return None

        wait_until(on_client, 120, f"the {label} on the client")
        seat = enter_cockpit(api, grid)
        check(f"the client sits in the {label}", seat.get("state") == "sitting", seat)
        bench.ensure_gameplay_focus(CLIENT_ID)
        state = api.get_grid(grid)
        bench.record(f"state-{section}", state)

        api.set_input_state(keys=["W"])
        fastest_on_server = [0.0]

        def read() -> float:
            if int(time.monotonic() * 4) % 8 == 0:
                seen = server_speed(bench)
                if seen is not None:
                    fastest_on_server[0] = max(fastest_on_server[0], seen)
            return api.get_grid(grid)["speed"]

        samples = fly(read, 180)
        api.clear_input_state()
        top = max(s for _, s in samples)
        mass = state["mass"]
        accel = acceleration(samples)
        cruising, boost, resistance = cruise(section, mass)
        results[label] = {
            "top": round(top, 1),
            "server": round(fastest_on_server[0], 1),
            "mass_kg": round(mass),
            "accel": round(accel, 1),
            "100_plus_accel": round(100 + accel, 1),
            "rts_expected": round(min(boost, cruising + accel / resistance), 1),
            "seconds": round(samples[-1][0], 1),
        }
        print(f"{label}: {json.dumps(results[label])}", flush=True)
        bench.record(f"samples-{section}", samples[::4])
        check(
            f"the {label} flies faster than {FASTER_THAN} m/s",
            top > FASTER_THAN,
            results[label],
        )
        check(
            f"the {label} gets close to what Relative Top Speed gives it",
            abs(top - results[label]["rts_expected"])
            < 0.1 * results[label]["rts_expected"],
            results[label],
        )
        api.character_use()
        wait_until(
            lambda: api.get_character().get("state") != "sitting",
            30,
            "the client out of the cockpit",
        )

    api.character_teleport(*CHARACTER_AT)
    time.sleep(3)
    if not api.get_character().get("jetpack"):
        api.key("X")
        wait_until(lambda: api.get_character().get("jetpack"), 15, "the jetpack on")
    bench.ensure_gameplay_focus(CLIENT_ID)
    last = [time.monotonic(), api.get_character()["position"], 0.0]
    fastest_on_server = [0.0]

    def character_speed() -> float:
        # The route has no velocity: from positions at least half a second apart
        now = time.monotonic()
        if now - last[0] >= 0.5:
            position = api.get_character()["position"]
            last[2] = math.dist(position, last[1]) / (now - last[0])
            last[0], last[1] = now, position
            seen = server_speed(bench)
            if seen is not None:
                fastest_on_server[0] = max(fastest_on_server[0], seen)
        return last[2]

    api.set_input_state(keys=["W"])
    samples = fly(character_speed, 180)
    api.clear_input_state()
    top = max(s for _, s in samples)
    results["character"] = {
        "top": round(top, 1),
        "server": round(fastest_on_server[0], 1),
        "accel": round(acceleration(samples), 1),
        "seconds": round(samples[-1][0], 1),
    }
    print(f"character: {json.dumps(results['character'])}", flush=True)
    bench.record("samples-character", samples[::4])
    check(
        f"the character on jetpack flies faster than {FASTER_THAN} m/s",
        top > FASTER_THAN,
        results["character"],
    )
    bench.record("top-speeds", results)
    print("TOP SPEEDS " + json.dumps(results), flush=True)


def main() -> int:
    pl.MODS.update({RTS: "Relative Top Speed", TRADE_LANES: "Trade Lanes MP"})
    pl.MOD_ITEMS = pl.mod_items()
    # The bench links mods from one folder, with the Trade Lanes under test in it
    stage = wa.ROOT / "mods"
    stage.mkdir(parents=True, exist_ok=True)
    trade_lanes = os.environ.get("OCT_TRADE_LANES")
    for id_ in pl.MODS:
        link = stage / str(id_)
        link.unlink(missing_ok=True)
        link.symlink_to(
            trade_lanes
            if trade_lanes and id_ == TRADE_LANES
            else pl.WORKSHOP / str(id_)
        )
    pl.WORKSHOP = stage
    run.world_template = wa.prepare()
    run.administrators = [CLIENT_ID]
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-top-speed")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-TOP-SPEED",
        "SE-807: top speeds of a small ship, a large ship and a character on jetpack",
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
