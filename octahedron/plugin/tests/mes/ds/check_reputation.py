"""Judges a run of run.py with plan-reputation.txt (SE-1294): one line per check, PASS/FAIL, exit code 0
when all pass.

    python3 tests/mes/ds/check_reputation.py fix|attacker|off [ROOT]     (from plugin/)

A and B are in OCT, C and D in TST. A rams a THIE Atlas, then hits it with 30 bullets 100 ms apart from
another grid; then C does the same. THIE's trigger takes 5 reputation per firing, from the attacker's
whole faction.
fix (reputation.cfg, the Octahedron defaults): nothing A does changes anybody's reputation; C's ram
     changes nothing either, and C's bullets cost C and D alike, at most once per 500 ms cooldown.
attacker (reputation-attacker.cfg, MesReputationOctExempt off): as fix, but A's bullets cost A alone,
     with the cooldown.
off (reputation-off.cfg): the old behavior, which the test must catch: A's ram costs A and B, and A's
     bullets cost A and B alike, more often than the cooldown allows.
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

HITS, AMOUNT, COOLDOWN_S, SPACING_S = 30, 5, 0.5, 0.1
# Firings within the 30 hits at the cooldown, plus one for timing slack
MAX_FIRINGS = int(HITS * SPACING_S / COOLDOWN_S) + 2
LABELS = ("start", "rammed-A", "shot-A", "rammed-C", "shot-C")
NOBODY = dict(A=0, B=0, C=0, D=0)


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
        | {"Deformation": int((re.search(r"Deformation:(\d+)", m[3]) or [0, 0])[1])}
        for m in re.finditer(r"^rep (\S+) (.*?) damage=(\S*)$", text, re.M)
    }
    results = []

    def check(name: str, ok: bool, detail: object = "") -> None:
        results.append(ok)
        print(f"{'PASS' if ok else 'FAIL'} {name} {detail}")

    check("no probe errors", "error" not in text)
    check("all reports", all(k in reps for k in LABELS))
    if not results[-1]:
        return 1

    def delta(a: str, b: str) -> dict[str, int]:
        return {p: reps[b][p] - reps[a][p] for p in "ABCD"}

    def cooled(change: int) -> bool:
        return change % AMOUNT == 0 and 1 <= -change / AMOUNT <= MAX_FIRINGS

    ram_a, shot_a = delta("start", "rammed-A"), delta("rammed-A", "shot-A")
    ram_c, shot_c = delta("shot-A", "rammed-C"), delta("rammed-C", "shot-C")
    for ram, before, after in (("A", "start", "rammed-A"), ("C", "shot-A", "rammed-C")):
        hits = reps[after]["Deformation"] - reps[before]["Deformation"]
        check(f"{ram}'s ram hit the Atlas", hits > 0, f"{hits} Deformation events")

    if mode in ("fix", "attacker"):
        check("A's ram changes nobody", ram_a == NOBODY, ram_a)
        if mode == "fix":
            check("A's bullets change nobody", shot_a == NOBODY, shot_a)
        else:
            check(
                f"A's bullets cost A 1..{MAX_FIRINGS} firings",
                cooled(shot_a["A"]),
                shot_a,
            )
            check(
                "A's bullets cost nobody else",
                shot_a["B"] == shot_a["C"] == shot_a["D"] == 0,
                shot_a,
            )
        check("C's ram changes nobody", ram_c == NOBODY, ram_c)
        check(
            f"C's bullets cost C 1..{MAX_FIRINGS} firings", cooled(shot_c["C"]), shot_c
        )
        check("C's faction TST pays alike", shot_c["D"] == shot_c["C"], shot_c)
        check("C's bullets cost OCT nothing", shot_c["A"] == shot_c["B"] == 0, shot_c)
    else:
        check("A's ram costs A", ram_a["A"] < 0, ram_a)
        check("A's ram costs B as well", ram_a["B"] == ram_a["A"], ram_a)
        check(
            f"A's bullets cost A and B alike, more than {MAX_FIRINGS} firings",
            shot_a["A"] == shot_a["B"] < -MAX_FIRINGS * AMOUNT,
            shot_a,
        )

    print("PASS" if all(results) else "FAIL")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
