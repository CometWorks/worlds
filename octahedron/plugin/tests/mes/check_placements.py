"""Checks MES Cluster's decisions in Dedicated Server logs, e.g. of every node of a cluster.

    python3 -I tests/mes/check_placements.py [--cell M] [--gap M] [--jitter J] [--seed S] LOG...

Counts the placed and refused spawns per log and checks that every placed spawn start lies in a cell
core, recomputed independently of the plugin (default pattern: 40 km cells, 12 km gap, jitter 0.5,
seed 1321, the Octahedron defaults). Also reports whether the plugin patched MES and which census it
used. Prints PASS or FAIL; exit code 0 on PASS.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "ds"))
import check  # noqa: E402  (the pattern port)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cell", type=float, default=check.CELL)
    parser.add_argument("--gap", type=float, default=check.GAP)
    parser.add_argument("--jitter", type=float, default=check.JITTER)
    parser.add_argument("--seed", type=int, default=check.SEED)
    parser.add_argument("logs", nargs="+", type=Path)
    args = parser.parse_args()
    check.CELL, check.GAP, check.JITTER, check.SEED = (
        args.cell,
        args.gap,
        args.jitter,
        args.seed,
    )

    failed = 0
    for log in args.logs:
        text = log.read_text(errors="replace")
        placed = [
            tuple(map(float, m))
            for m in re.findall(r"spawn placed: .* start (-?\d+),(-?\d+),(-?\d+)", text)
        ]
        refused = re.findall(r"spawn refused: type \S+, reason (\w+)", text)
        reasons = {r: refused.count(r) for r in sorted(set(refused))}
        gated = re.findall(r"MES spawn placements gated: .*census (.+)$", text, re.M)
        critical = re.findall(r"(UNPATCHED|UNGATED).*", text)
        outside = [p for p in placed if check.core_depth(p)[1] < 0]
        ok = not outside and not critical
        failed += not ok
        print(
            f"{'PASS' if ok else 'FAIL'} {log}: gated={bool(gated)} census={gated[-1] if gated else '-'} "
            f"placed={len(placed)} outside-core={len(outside)} refused={reasons}"
            + (f" CRITICAL={critical[0][:80]}" if critical else "")
        )
    print("PASS" if not failed else f"FAIL: {failed} logs")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
