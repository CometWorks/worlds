"""SE-1527: does a jetpacking character snap back in a station hangar? A headless client with the
snap probe flies a fixed pattern over a station's hangar deck (its connector layer) and the probe's
per-frame log is summed up: position jumps the velocity doesn't explain, frames without client
prediction, parent changes.

    OCT_TASK=SE-1527 python3 snapback.py run LABEL [MINUTES] [STATION]   server + client, one flight
    python3 snapback.py sum CSV                                          the summary of a probe log

Env: SNAP_SERVER_PLUGINS=perf adds the Performance plugin (OCT_PERF_REPO) to the server.
"""

from __future__ import annotations

import math
import os
import shutil
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROBE = ("snap-probe", HERE / "snap-probe", "SnapProbe.xml", "snap-probe")
RESULTS = Path(os.environ.get("SNAP_RESULTS", HERE / "snap-results"))
# (unused) One round of the pattern: (keys held, seconds). Dampeners stay on, so each leg stops on its own.
PATTERN = [
    (["W"], 2.0), ([], 0.5), (["S"], 2.0), ([], 0.5),
    (["A"], 1.5), ([], 0.5), (["D"], 3.0), ([], 0.5), (["A"], 1.5),
    (["Space"], 1.0), ([], 0.5), (["C"], 1.0), ([], 0.5),
    (["W", "D"], 1.5), (["S", "A"], 1.5), ([], 1.0),
]


def summarize(csv: Path) -> dict:
    """Teleports (a jump over 20 m) and the 12 s after them don't count: a teleport switches
    client prediction off for 10 s by itself."""
    rows = [f for f in (line.split(",") for line in csv.read_text().splitlines()) if f[0] != "RX"]
    quiet_until = -1.0
    snaps: list[float] = []
    frames = predicted = forced = sec = flags = parents = teleports = 0
    reenable_snaps = 0
    last_flag_on = -1.0
    for f in rows:
        t = float(f[1])
        if f[0] == "SNAP" and float(f[3]) > 20:
            teleports += 1
            quiet_until = t + 12
            continue
        if t < quiet_until:
            continue
        if f[0] == "SEC":
            sec += 1
            frames += int(f[3])
            predicted += int(f[4])
            forced += int(f[5])
        elif f[0] == "SNAP":
            snaps.append(float(f[3]))
            if t - last_flag_on < 0.2:
                reenable_snaps += 1
        elif f[0] == "FLAG":
            flags += 1
            if f[3] == "1":
                last_flag_on = t
        elif f[0] == "PARENT":
            parents += 1
    snaps.sort()
    return {
        "seconds": sec,
        "teleports": teleports,
        "snaps_over_0.1m": len(snaps),
        "snaps_over_0.25m": sum(e > 0.25 for e in snaps),
        "snaps_over_1m": sum(e > 1 for e in snaps),
        "snaps_at_prediction_on": reenable_snaps,
        "snap_median_m": round(snaps[len(snaps) // 2], 2) if snaps else 0,
        "snap_max_m": round(snaps[-1], 2) if snaps else 0,
        "unpredicted_pct": round(100 * (frames - predicted) / frames, 1) if frames else None,
        "force_disabled_pct": round(100 * forced / frames, 1) if frames else None,
        "prediction_flag_changes": flags,
        "parent_changes": parents,
    }


def vec(a, b, s=1.0):
    return [a[i] + s * b[i] for i in range(3)]


def dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def goto(remote, target, timeout: float = 20.0) -> bool:
    """Holds the jetpack keys toward target along the character's own axes until within 1.5 m,
    as a player flying by keyboard would"""
    end = time.monotonic() + timeout
    try:
        while time.monotonic() < end:
            c = remote.get_character()
            pos, fwd, up = c["position"], c["forward"], c["up"]
            right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
            d = vec(target, pos, -1)
            if dot(d, d) < 1.5**2:
                return True
            keys = []
            for value, plus, minus in ((dot(d, fwd), "W", "S"), (dot(d, right), "D", "A"), (dot(d, up), "Space", "C")):
                if value > 0.7:
                    keys.append(plus)
                elif value < -0.7:
                    keys.append(minus)
            remote.set_input_state(keys=keys)
            time.sleep(0.25)
        return False
    finally:
        remote.clear_input_state()


def waypoints(center, points):
    """The deck center, then halfway to each connector and back"""
    out = []
    for p in points:
        out += [[(center[i] + p[i]) / 2 for i in range(3)], center]
    return out


def fly_waypoints(remote, center, points, minutes: float) -> None:
    mark = time.monotonic()
    legs = done = 0
    route = waypoints(center, points)
    while time.monotonic() - mark < minutes * 60:
        for w in route:
            legs += 1
            done += goto(remote, w, 15)
            if time.monotonic() - mark >= minutes * 60:
                break
    print(f"{legs} legs, {done} reached", flush=True)


def deck(station: str):
    """The center of the station's connectors in the world, and the station's up"""
    import rig

    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    grid = next(
        e for e in root.iter("MyObjectBuilder_EntityBase")
        if e.findtext("DisplayName") == f"{station} Station"
    )  # fmt: skip
    po = grid.find("PositionAndOrientation")
    pos, fwd, up = ([float(po.find(t).get(a)) for a in "xyz"] for t in ("Position", "Forward", "Up"))
    right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
    cells = [
        [int(b.find("Min").get(a)) * 2.5 for a in "xyz"]
        for b in grid.iter("MyObjectBuilder_CubeBlock")
        if b.findtext("SubtypeName") == "Connector"
    ]
    points = [[pos[i] + c[0] * right[i] + c[1] * up[i] - c[2] * fwd[i] for i in range(3)] for c in cells]
    center = [sum(p[i] for p in points) / len(points) for i in range(3)]
    return center, up, points


def fly_jiggle(remote, minutes: float, tap: float = 0.4, rest: float = 0.8) -> None:
    """Short taps along each of the character's axes and back, in place: a few m/s, a meter or two
    of travel, no walls. Dampeners stop each tap. Back to the start when it drifted over 1.5 m."""
    c = remote.get_character()
    home = vec(c["position"], c["up"], 1.0)
    goto(remote, home, 6)
    mark = time.monotonic()
    rounds = 0
    while time.monotonic() - mark < minutes * 60:
        for key in ("W", "S", "A", "D", "Space", "C"):
            remote.set_input_state(keys=[key])
            time.sleep(tap)
            remote.clear_input_state()
            time.sleep(rest)
        rounds += 1
        goto(remote, home, 6)
    print(f"{rounds} rounds", flush=True)


def fly_wander(remote, minutes: float, radius: float = 8.0) -> None:
    """Legs out from where the character is and back, 1 m above it, in 8 directions on its own
    floor level. A leg that a wall blocks ends after 6 s."""
    c = remote.get_character()
    pos, fwd, up = c["position"], c["forward"], c["up"]
    right = [fwd[1] * up[2] - fwd[2] * up[1], fwd[2] * up[0] - fwd[0] * up[2], fwd[0] * up[1] - fwd[1] * up[0]]
    home = vec(pos, up, 1.0)
    route = []
    for k in range(8):
        a = k * math.pi / 4
        route += [vec(vec(home, fwd, radius * math.cos(a)), right, radius * math.sin(a)), home]
    mark = time.monotonic()
    legs = done = 0
    while time.monotonic() - mark < minutes * 60:
        for w in route:
            legs += 1
            done += goto(remote, w, 6)
            if time.monotonic() - mark >= minutes * 60:
                break
    print(f"{legs} legs, {done} reached", flush=True)


def fly(remote, start, center, points, minutes: float) -> None:
    if not remote.get_character().get("jetpack"):
        remote.key("X")
        time.sleep(1)
    remote.character_teleport(*start)
    time.sleep(13)  # the teleport's own 10 s without prediction
    if os.environ.get("SNAP_MODE") == "waypoints":
        fly_waypoints(remote, center, points, minutes)
    elif os.environ.get("SNAP_MODE") == "wander":
        fly_wander(remote, minutes)
    else:
        fly_jiggle(remote, minutes)


def run(label: str, minutes: float, station: str) -> int:
    os.environ.setdefault("OCT_TASK", "SE-1527")
    import rig

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    extra = []
    if os.environ.get("SNAP_SERVER_PLUGINS") == "perf":
        extra.append(("performance", Path(os.environ["OCT_PERF_REPO"]), "PerformanceServer.xml",
                      "9F22D24B-ABA3-4C13-A9E5-A1FB31E6E2A6"))  # fmt: skip
    rig.prepare_server(extra_dev_folders=extra)
    probe_csv = rig.APPDATA / "snap-probe.csv"
    probe_csv.unlink(missing_ok=True)
    center, up, points = deck(station)
    start = [center[i] + 2.0 * up[i] for i in range(3)]
    center = start
    points = [[p[i] + 2.0 * up[i] for i in range(3)] for p in points]
    print(f"{station} deck: {len(points)} connectors, start {[round(v, 1) for v in start]}", flush=True)
    try:
        rig.start_server()
        remote = rig.start_client(extra_dev_folders=[PROBE])
        rig.ensure_character(remote)
        fly(remote, start, center, points, minutes)
    finally:
        rig.stop_client()
        rig.stop_server()
    out = RESULTS / label
    out.mkdir(parents=True, exist_ok=True)
    shutil.copy(probe_csv, out / "snap-probe.csv")
    result = summarize(out / "snap-probe.csv")
    (out / "summary.txt").write_text(f"{result}\n")
    print(f"RESULT {label} {result}", flush=True)
    return 0


if __name__ == "__main__":
    if sys.argv[1] == "sum":
        print(summarize(Path(sys.argv[2])))
    else:
        sys.exit(run(sys.argv[2], float(sys.argv[3]) if len(sys.argv) > 3 else 10,
                     sys.argv[4] if len(sys.argv) > 4 else "Gamma"))  # fmt: skip
