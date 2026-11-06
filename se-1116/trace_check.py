"""SE-1095/SE-1116: checks the filaments' per-frame trace (FilamentTrace lines of the rig's script).

    python3 trace_check.py SpaceEngineers.log [linger seconds]

SE-1116 adds the tool's working state (1/0, -1 for main's mod, which doesn't trace it):
- stops: the tool stops working while its threads are out;
- relinks: stops after which it works again before the threads retract (they glide on);
- re-extends: runs after a tool's first, each one a retract and a new extend;
- linger: from a tool's last working frame to the start of the retract.
"""

import re
import sys

import numpy as np

WELDER, DECON = "OctahedronFilamentWelder", "OctahedronFilamentDeconstructor"


def analyze(log, check, linger=None):
    """Extend and retract times, and the drawn end's biggest step per frame, from the
    trace; returns the numbers"""
    result = {}
    rows = {}
    for m in re.finditer(r"FilamentTrace (\S+) (\d+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+) (\S+)(?: (-?\d))?", log):
        t, tool, subtype, e, *xyz, w = m.groups()
        rows.setdefault((subtype, tool), []).append([float(t), float(e), *map(float, xyz), float(w or -1)])
    check(any(s == WELDER for s, _ in rows) and any(s == DECON for s, _ in rows), f"trace of {len(rows)} tools")
    stats = {
        k: {"extend": [], "retract": [], "interrupted": 0, "end_step": 0.0, "target_step": 0.0, "dt": [],
            "runs": 0, "tools": 0, "stops": 0, "relinks": 0, "linger": [], "relink_glide": 0}
        for k in (WELDER, DECON)
    }
    for (subtype, _), r in rows.items():
        a = np.array(r)
        s = stats[subtype]
        s["tools"] += 1
        # Runs: the trace has a line per frame while the threads are out, so a missing
        # frame means they were all in (and a new run starts at the new aim)
        breaks = np.where(np.diff(a[:, 0]) > 0.05)[0] + 1
        for run in np.split(a, breaks):
            t, e = run[:, 0], run[:, 1]
            s["runs"] += 1
            w = run[:, 8]
            if w[0] >= 0:
                # Working state changes within the run: each 1->0 is a stop, a 0->1 after
                # it a relink (the threads were still out)
                changes = np.diff(w)
                s["stops"] += int((changes < 0).sum())
                s["relinks"] += int((changes > 0).sum())
                for i in np.where(changes > 0)[0]:
                    # Did the aim move on relinking? Then the end glides to it
                    later = run[i + 1 :, 5:8]
                    if len(later) and np.linalg.norm(later - run[i, 5:8], axis=1).max() > 0.5:
                        s["relink_glide"] += 1
                last = np.where(w > 0)[0]
                down = np.where((np.arange(len(e)) > (last[-1] if len(last) else -1)) & (e < 1 - 1e-6))[0]
                if len(last) and len(down) and e[last[-1]] >= 1:
                    s["linger"].append(float(t[down[0]] - t[last[-1]]))
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
    # Every stop of a tool that works again later, with how long it was off: from the last
    # working frame to the next, within a run or across a retract (the trace has the
    # working state while the threads are out, and the stop is never longer than linger
    # there, so a longer one shows as a run break)
    gaps = {WELDER: [], DECON: []}
    for (subtype, _), r in rows.items():
        a = np.array(r)
        if a[0, 8] < 0:
            continue
        on = a[a[:, 8] > 0, 0]
        d = np.diff(on)
        gaps[subtype] += [float(g) for g in d if g > 0.1]
    for subtype, s in stats.items():
        g = np.array(gaps[subtype])
        s["gaps"] = g
        if len(g):
            # With a linger time L, a stop shorter than L keeps the threads out (glide),
            # one up to L + 1 s retracts them partway, a longer one fully and extends again
            s["by_linger"] = {
                str(L): {"glide": int((g < L).sum()), "partway": int(((g >= L) & (g < L + 1)).sum()), "re_extend": int((g >= L + 1).sum())}
                for L in (0.4, 2, 3)
            }
            s["gap_s"] = [round(float(np.percentile(g, q)), 2) for q in (0, 50, 90, 100)]
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
            "tools": s["tools"],
            "runs": s["runs"],
            "re_extends": s["runs"] - s["tools"],
            "stops": s["stops"],
            "relinks": s["relinks"],
            "relinks_to_new_aim": s["relink_glide"],
            "gap_s_min_med_p90_max": s.get("gap_s"),
            "stops_by_linger": s.get("by_linger"),
            "linger_s": [round(min(s["linger"]), 2), round(float(np.median(s["linger"])), 2), round(max(s["linger"]), 2)]
            if s["linger"] else None,
        }
        result[name + "_trace"] = summary
        print(name, summary, flush=True)
        check(ext and 0.8 <= np.median(ext) <= 1.3, f"{name}: extends in about 1 s ({summary['extend_s']})")
        check(ret and 0.8 <= np.median(ret) <= 1.3, f"{name}: retracts in about 1 s ({summary['retract_s']})")
        if linger is not None:
            # The aim updates every 10 frames, so a stop is seen up to 1/6 s late
            check(
                s["linger"] and abs(np.median(s["linger"]) - linger) < 0.3,
                f"{name}: stays {linger:g} s after the tool stops ({summary['linger_s']})",
            )
            check(s["relinks"] > 0, f"{name}: works again before retracting {s['relinks']} times, the threads stay out")
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

    analyze(open(sys.argv[1], errors="replace").read(), check, float(sys.argv[2]) if len(sys.argv) > 2 else None)
    sys.exit(1 if any(failed) else 0)
