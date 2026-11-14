"""SE-1527: keeps the live probe client running for exploring: start | stop"""
import os
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-1527-live")
import rig  # noqa: E402
import snapback_live as live  # noqa: E402
from snapback import PROBE  # noqa: E402

if sys.argv[1] == "start":
    rig.stop_client()
    rig.prepare_client([PROBE])
    live.link_mods()
    rig._start(
        [
            str(rig.LAUNCHER),
            "-multiInstance", "-lazySteam", "-noprompt", "-noupdate", "-nosplash", "-stablelogs",
            "-bin64", str(rig.BIN64), "--no-steam", "--client-id", str(rig.CLIENT_ID), "--client-name", live.NAME,
            "-appdata", str(rig.APPDATA), "--quality", "minimal", "--no-audio", "--headless",
            "--resolution", "640x480", "--no-render", "--connect", live.GATEWAY,
        ],
        rig.CLIENT_GIB + 3, rig.PULSAR, rig.CLIENT_LOG, rig.CLIENT_PID, {"PULSAR_NO_RENDER": "true"},
    )  # fmt: skip
    remote = rig.api()
    rig.wait_until(lambda: live._ping(remote), 900, "the client's Remote API")
    rig.wait_world(remote, 1800)
    print(rig.ensure_character(remote, 600))
else:
    try:
        rig.api().exit_game(force=True)
        time.sleep(8)
    except Exception as e:  # noqa: BLE001
        print(e)
    rig.stop_client()
