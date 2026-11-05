"""SE-1095: checks the filaments' per-frame trace (FilamentTrace lines of the rig's script).

    python3 trace_check.py SpaceEngineers.log
"""

import re
import sys

import numpy as np

WELDER, DECON = "OctahedronFilamentWelder", "OctahedronFilamentDeconstructor"


def analyze(log, check):
    """Extend and retract times, and the drawn end's biggest step per frame, from the
    trace; returns the numbers"""
    result = {}
    rows = {}
    for m in re.finditer(r"FilamentTrace (\S+) (\d+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+)", log):
        t, tool, subtype, e, *xyz = m.groups()
        rows.setdefault((subtype, tool), []).append([float(t), float(e), *map(float, xyz)])
    check(any(s == WELDER for s, _ in rows) and any(s == DECON for s, _ in rows), f"trace of {len(rows)} tools")
    stats = {WELDER: {"extend": [], "retract": [], "interrupted": 0, "end_step": 0.0, "target_step": 0.0, "dt": []},
             DECON: {"extend": [], "retract": [], "interrupted": 0, "end_step": 0.0, "target_step": 0.0, "dt": []}}
    for (subtype, _), r in rows.items():
        a = np.array(r)
        s = stats[subtype]
        # Runs: the trace has a line per frame while the threads are out, so a missing
        # frame means they were all in (and a new run starts at the new aim)
        breaks = np.where(np.diff(a[:, 0]) > 0.05)[0] + 1
        for run in np.split(a, breaks):
            t, e = run[:, 0], run[:, 1]
            dt = np.diff(t)
            if len(dt):
                s["dt"].append(float(np.median(dt)))
            full = np.where(e >= 1)[0]
            if not len(full):
                continue
            step = np.median(dt) if len(dt) else 0
            # From the first frame out (one frame of growth before it) to fully out
            if e[0] < 0.2:
                s["extend"].append(float(t[full[0]] - t[0] + step))
            # From the last frame fully out to the last frame drawn; a retract that a
            # new block interrupts (out again partway) is counted apart
            if e[-1] < 0.2 and full[-1] < len(e) - 1:
                if np.all(np.diff(e[full[-1] :]) <= 0):
                    s["retract"].append(float(t[-1] - t[full[-1]] + step))
                else:
                    s["interrupted"] += 1
            ends, targets = run[:, 2:5], run[:, 5:8]
            if len(run) > 1:
                s["end_step"] = max(s["end_step"], float(np.linalg.norm(np.diff(ends, axis=0), axis=1).max()))
                s["target_step"] = max(
                    s["target_step"], float(np.linalg.norm(np.diff(targets, axis=0), axis=1).max())
                )
    for subtype, s in stats.items():
        name = "welder" if subtype == WELDER else "deconstructor"
        fps = 1 / np.median(s["dt"]) if s["dt"] else 0
        ext, ret = s["extend"], s["retract"]
        summary = {
            "fps": round(float(fps), 1),
            "extends": len(ext),
            "extend_s": [round(min(ext), 2), round(float(np.median(ext)), 2), round(max(ext), 2)] if ext else None,
            "retracts": len(ret),
            "interrupted_retracts": s["interrupted"],
            "retract_s": [round(min(ret), 2), round(float(np.median(ret)), 2), round(max(ret), 2)] if ret else None,
            "max_end_step_m": round(s["end_step"], 3),
            "max_target_step_m": round(s["target_step"], 3),
        }
        result[name + "_trace"] = summary
        print(name, summary, flush=True)
        check(ext and 0.8 <= np.median(ext) <= 1.3, f"{name}: extends in about 1 s ({summary['extend_s']})")
        check(ret and 0.8 <= np.median(ret) <= 1.3, f"{name}: retracts in about 1 s ({summary['retract_s']})")
        # The aim jumps from block to block; the drawn end moves by a small part of a
        # jump per frame
        check(
            s["target_step"] > 2 and s["end_step"] < 0.3 * s["target_step"],
            f"{name}: glides, biggest step {s['end_step']:.2f} m per frame for aim jumps up to {s['target_step']:.2f} m",
        )
    return result


if __name__ == "__main__":
    failed = []

    def check(ok, what):
        failed.append(not ok)
        print(("PASS " if ok else "FAIL ") + what, flush=True)

    analyze(open(sys.argv[1], errors="replace").read(), check)
    sys.exit(1 if any(failed) else 0)
