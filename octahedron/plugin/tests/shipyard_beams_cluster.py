"""The shipyard welders' beams on a local test cluster, through the cluster's scenario bench (CometWorks/cluster
Test/), as tests/test_shipyard_beams.py checks them on one server (SE-782): the client orders a long build at a
station, flies to another station on the other node and back, and all of the shipyard's welders must shoot on the
client after that, and some must hit. Then the probe switches the welders off on the client alone, and the plugin's
switch, sent every 30 s, must turn them on again (SE-963). The client runs the beam-probe test plugin.

    OCT_TASK=SE-782 OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/shipyard_beams_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one headless client.
OCT_CLUSTER_ROOT is a folder of its own for the plugins this builds, for a host with other benches running.
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
    lc,
)  # noqa: E402

import clients  # noqa: E402
import test_shipyard_beams as beams  # noqa: E402
import test_shipyard_order as so  # noqa: E402

NODES = ("node-1", "node-2")
pin_source_age = clients.pin_source_age


def client_with_probe(profile: Path) -> None:
    """The bench copies the client's profile before every start, this comes right after"""
    pin_source_age(profile)
    current = profile / "Profiles/Current.xml"
    text = current.read_text(encoding="utf-8")
    folder = f"<LocalFolderConfig><Id>{beams.PROBE[3]}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
    text = text.replace("<DevFolder />", "<DevFolder></DevFolder>").replace(
        "</DevFolder>", f"{folder}</DevFolder>"
    )
    current.write_text(text, encoding="utf-8")
    rig.register_source(
        profile / "Sources/sources.xml", beams.PROBE[0], beams.PROBE[1], beams.PROBE[2]
    )


clients.pin_source_age = client_with_probe


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:1500])

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
    probe_log = user_data / "beam-probe.log"
    probe_log.unlink(missing_ok=True)

    positions = wa.stations()
    name = next(iter(positions))
    other = next(n for n in positions if n != name)
    os.environ["BEAM_PROBE_YARD"] = f"{name} Shipyard"
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)

    def go_to_table(position) -> None:
        for attempt in range(24):
            if attempt == 0:
                api.character_teleport(*position)
            time.sleep(5)
            tables = tc.tables(api)
            if tables:
                try:
                    tc.stand_by(api, min(tables, key=lambda b: b["distance"]))
                    return
                except Exception as error:  # noqa: BLE001 -- a grid already gone
                    print(f"no stand by a table yet: {error}", flush=True)

    go_to_table(positions[name])
    reply = ""
    for _ in range(12):
        reply = " ".join(rig.chat(api, "!oct order", 15))
        print(f"!oct order -> {reply}", flush=True)
        if "Your quote for" in reply:
            reply = " ".join(rig.chat(api, "!oct order", 15))
            print(f"!oct order -> {reply}", flush=True)
        if "is in the queue" in reply or "builds" in reply:
            break
        time.sleep(10)
    if not check(
        "the ship is ordered", "is in the queue" in reply or "builds" in reply, reply
    ):
        return

    def state(since: float) -> list[dict]:
        out = [s for s in beams.samples(probe_log) if s["t"] >= since]
        for s in out:
            for key in ("tools", "activated", "hitting", "unfinished", "ship"):
                s[key] = int(s[key])
        return out

    def shooting(label: str, since: float) -> None:
        got = [s for s in state(since) if s["unfinished"] > 0]
        off = [s for s in got if s["activated"] < s["tools"]]
        hit = max((s["hitting"] for s in got), default=0)
        last = got[-1] if got else {}
        check(
            label,
            got and not off and hit > 0,
            f"{len(got)} samples, {len(off)} with welders off, at most {hit} hitting, last {last}",
        )

    time.sleep(60)
    shooting("the welders shoot before the trip", time.time() - 30)
    print(f"to {other}", flush=True)
    go_to_table(positions[other])
    time.sleep(40)
    print(f"back to {name}", flush=True)
    go_to_table(positions[name])
    # The next 10 s check sends the switch again, 10 s more if a tool's packet came before the zone's
    time.sleep(25)
    back = time.time()
    time.sleep(60)
    shooting("the welders shoot after the trip to the other node", back)

    off = time.time()
    (user_data / "beam-probe.off").touch()
    time.sleep(beams.RESEND)
    again = time.time()
    time.sleep(30)
    picked = state(off)
    recovered = beams.recovered(picked)
    check(
        "the welders go off on the client and on again",
        recovered.startswith("on again"),
        recovered,
    )
    shooting("the welders shoot after they went off on the client", again)
    print(beams.samples(probe_log)[-1], flush=True)


run.administrators = [CLIENT_ID]


def main() -> int:
    root = os.environ.get("OCT_CLUSTER_ROOT")
    if root:
        wa.ROOT = Path(root)
        lc.PLUGINS, lc.PLAN, lc.OUT = (
            wa.ROOT / "plugins",
            wa.ROOT / "probe-plan.txt",
            wa.ROOT / "probe-out.txt",
        )
    grid, _ = beams.ship()
    so.SMALL = grid
    run.world_template = wa.prepare()
    for file in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / file
        text, count = re.subn(
            r"<WelderSpeedMultiplier>[^<]*<",
            f"<WelderSpeedMultiplier>{os.environ.get('OCT_BEAM_WELD', '0.5')}<",
            path.read_text(encoding="utf-8"),
        )
        assert count, file
        path.write_text(text, encoding="utf-8")
    wa.set_config(FreeBuildPcuLimit=100000)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-beams")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-BEAMS",
        "SE-782: a shipyard's beams after a trip to another node",
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
