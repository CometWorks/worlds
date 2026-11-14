"""SE-1527: the snap probe on the LIVE cluster (Viktor's OK, 2026-11-14). A headless client joins
through the Gateway's DirectTransport port as a new player, spawns where the cluster puts it, finds
the station's hangar deck (its Connector blocks) and flies between waypoints over it with the
jetpack, like a player would. Nothing is changed on the cluster beyond one more player.

    OCT_TASK=SE-1527-live python3 snapback_live.py LABEL [MINUTES]

Env: LIVE_GATEWAY (192.168.1.80:27116), LIVE_MODS (a file of Workshop ids), LIVE_WORKSHOP (their
folder), LIVE_NAME (the player name).
"""

from __future__ import annotations

import os
import shutil
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1527-live")

import rig  # noqa: E402
from snapback import RESULTS, client_plugins, fly_bounce, fly_jiggle, fly_wander, summarize  # noqa: E402

GATEWAY = os.environ.get("LIVE_GATEWAY", "192.168.1.80:27116")
WORKSHOP = Path(os.environ.get("LIVE_WORKSHOP", "/mnt/data/workshop/content/244850"))
NAME = os.environ.get("LIVE_NAME", "SnapProbe")


def link_mods() -> None:
    folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for i in Path(os.environ["LIVE_MODS"]).read_text().split():
        link = folder / f"{i}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(WORKSHOP / i)


def main(label: str, minutes: float) -> int:
    rig.stop_client()
    probe_csv = rig.APPDATA / "snap-probe.csv"
    probe_csv.unlink(missing_ok=True)
    rig.prepare_client(client_plugins())
    link_mods()
    rig._start(
        [
            str(rig.LAUNCHER),
            "-multiInstance", "-lazySteam", "-noprompt", "-noupdate", "-nosplash", "-stablelogs",
            "-bin64", str(rig.BIN64), "--no-steam", "--client-id", str(rig.CLIENT_ID), "--client-name", NAME,
            "-appdata", str(rig.APPDATA), "--quality", "minimal", "--no-audio", "--headless",
            "--resolution", "640x480", "--no-render", "--connect", GATEWAY,
        ],
        rig.CLIENT_GIB + 3, rig.PULSAR, rig.CLIENT_LOG, rig.CLIENT_PID, {"PULSAR_NO_RENDER": "true"},
    )  # fmt: skip
    remote = rig.api()
    try:
        rig.wait_until(lambda: _ping(remote), 900, "the client's Remote API")
        rig.wait_world(remote, 1800)
        c = rig.ensure_character(remote, 600)
        print(f"spawned at {[round(v, 1) for v in c['position']]} state {c.get('state')}", flush=True)
        {"wander": fly_wander, "bounce": fly_bounce}.get(os.environ.get("SNAP_MODE"), fly_jiggle)(remote, minutes)
    finally:
        try:
            remote.exit_game(force=True)
        except Exception:  # noqa: BLE001
            pass
        time.sleep(10)
        rig.stop_client()
    out = RESULTS / label
    out.mkdir(parents=True, exist_ok=True)
    shutil.copy(probe_csv, out / "snap-probe.csv")
    shutil.copy(rig.CLIENT_LOG, out / "launch.log")
    result = summarize(out / "snap-probe.csv")
    (out / "summary.txt").write_text(f"{result}\n")
    print(f"RESULT {label} {result}", flush=True)
    return 0


def _ping(remote) -> bool:
    if not rig.client_pid():
        raise RuntimeError(f"The client exited, see {rig.CLIENT_LOG}")
    try:
        remote.ping()
        return True
    except Exception:  # noqa: BLE001
        return False


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 10))
