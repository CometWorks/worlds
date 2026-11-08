"""Judges a run of run.py with plan-reputation.txt (SE-1294): one line per check, PASS/FAIL, exit code 0
when all pass.

    python3 tests/mes/ds/check_reputation.py fix|off [ROOT]     (from plugin/)

A and B are in OCT, C and D in TST. A rams a THIE Atlas, then A and C each hit it with 30 bullets 100 ms
apart. THIE's trigger takes 5 reputation per firing.
fix (reputation.cfg): the ram changes nobody's reputation; A's bullets cost A alone, at most once per
     500 ms cooldown; C's cost C and D alike (other factions keep MES's faction-wide rule).
off (reputation-off.cfg): the old behavior, which the test must catch: the ram costs A and B, and A's
     bullets cost A and B alike, more often than the cooldown allows (each bullet, unless the -1500 floor
     stops it).
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

HITS, AMOUNT, COOLDOWN_S, SPACING_S = 30, 5, 0.5, 0.1
# Firings within the 30 hits at the cooldown, plus one for timing slack
MAX_FIRINGS = int(HITS * SPACING_S / COOLDOWN_S) + 2


def main() -> int:
    mode = sys.argv[1]
    root = Path(
        sys.argv[2]
        if len(sys.argv) > 2
        else Path.home() / ".se-test" / f"{os.environ.get('MES_TASK', '1509-mes')}-ds0"
    )
    text = (root / "probe.txt").read_text()
    print(*[l for l in text.splitlines() if l.startswith(("rep", "error"))], sep="\n")
    reps = {
        m[1]: {k: int(v) for k, v in re.findall(r"(\w)=(-?\d+)", m[2])}
        | {"damage": m[3]}
        for m in re.finditer(r"^rep (\S+) (.*?) damage=(\S*)$", text, re.M)
    }
    results = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'} {name} {detail}")

    check("no probe errors", "error" not in text)
    check(
        "all reports", all(k in reps for k in ("start", "rammed", "shot-A", "shot-C"))
    )
    if not results[-1]:
        return 1

    def delta(a: str, b: str) -> dict[str, int]:
        return {p: reps[b][p] - reps[a][p] for p in "ABCD"}

    ram, shot_a, shot_c = (
        delta("start", "rammed"),
        delta("rammed", "shot-A"),
        delta("shot-A", "shot-C"),
    )
    deformation = re.search(r"Deformation:(\d+)", reps["rammed"]["damage"])
    check("the ram hit the Atlas", bool(deformation), reps["rammed"]["damage"])

    if mode == "fix":
        check("ram changes nobody", ram == dict(A=0, B=0, C=0, D=0), str(ram))
        firings = -shot_a["A"] / AMOUNT
        check(
            f"A's bullets cost A 1..{MAX_FIRINGS} firings",
            shot_a["A"] % AMOUNT == 0 and 1 <= firings <= MAX_FIRINGS,
            str(shot_a),
        )
        check(
            "A's bullets cost nobody else",
            shot_a["B"] == shot_a["C"] == shot_a["D"] == 0,
            str(shot_a),
        )
        firings = -shot_c["C"] / AMOUNT
        check(
            f"C's bullets cost C 1..{MAX_FIRINGS} firings",
            shot_c["C"] % AMOUNT == 0 and 1 <= firings <= MAX_FIRINGS,
            str(shot_c),
        )
        check("C's faction TST pays alike", shot_c["D"] == shot_c["C"], str(shot_c))
        check(
            "C's bullets cost OCT nothing", shot_c["A"] == shot_c["B"] == 0, str(shot_c)
        )
    else:
        check("ram costs A", ram["A"] < 0, str(ram))
        check("ram costs B as well", ram["B"] == ram["A"], str(ram))
        check(
            f"A's bullets cost A and B alike, more than {MAX_FIRINGS} firings",
            shot_a["A"] == shot_a["B"] < -MAX_FIRINGS * AMOUNT,
            str(shot_a),
        )

    print("PASS" if all(results) else "FAIL")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
