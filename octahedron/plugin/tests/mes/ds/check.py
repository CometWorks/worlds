"""Judges a run of run.py: prints one line per check and PASS/FAIL, exit code 0 when all pass.

    python3 tests/mes/ds/check.py MODE [ROOT]     (from plugin/)

MODE guard: the plugin found MES, logged its hash and refused to patch (Critical), MES spawned anyway.
MODE gate-new-version: gate, and the plugin warned that this MES version isn't listed but patched it
            for its accepted hash (run with new-version.cfg).
MODE gate:  the plugin patched MES; every placed spawn start is in a cell core (recomputed here,
            independently of the plugin's C#); every NPC grid lies in a core or within EDGE m of one;
            the probe's view of the published InGap slot agrees with this script; density refusals
            happened where the plan piles spawns up, every placement saw fewer NPC grids within the
            density radius than the cap (counting reservations), and none of the placed starts ends up
            with more than twice the cap around it.
"""

from __future__ import annotations

import math
import re
import sys
from pathlib import Path

MASK = (1 << 64) - 1
CELL, GAP, JITTER, SEED = (
    40000.0,
    12000.0,
    0.5,
    1321,
)  # plugin defaults, sync distance 3000 m
DENSITY_RADIUS, DENSITY_CAP = 15000.0, 12
EDGE = 1500.0  # prefab offsets around the spawn point


def mix(v: int) -> int:
    v = (v + 0x9E3779B97F4A7C15) & MASK
    v = ((v ^ (v >> 30)) * 0xBF58476D1CE4E5B9) & MASK
    v = ((v ^ (v >> 27)) * 0x94D049BB133111EB) & MASK
    return v ^ (v >> 31)


def unit(h: int) -> float:
    return (h >> 11) * (1.0 / (1 << 53))


def site(i: int, j: int, k: int):
    h = mix((SEED & MASK) ^ mix((i & MASK) ^ mix((j & MASK) ^ mix(k & MASK))))
    x = (i + 0.5 + JITTER * (unit(h) - 0.5)) * CELL
    h = mix(h)
    y = (j + 0.5 + JITTER * (unit(h) - 0.5)) * CELL
    h = mix(h)
    z = (k + 0.5 + JITTER * (unit(h) - 0.5)) * CELL
    return x, y, z


def core_depth(p):
    """(cell, depth): depth = min distance to a bisector minus half the gap; >= 0 means in the core"""
    reach = math.ceil(math.sqrt(3) + GAP / CELL)
    c = [math.floor(v / CELL) for v in p]
    sites = {
        (i, j, k): site(i, j, k)
        for i in range(c[0] - reach, c[0] + reach + 1)
        for j in range(c[1] - reach, c[1] + reach + 1)
        for k in range(c[2] - reach, c[2] + reach + 1)
    }
    best = min(sites, key=lambda key: math.dist(p, sites[key]))
    b = sites[best]
    db = math.dist(p, b) ** 2
    depth = min(
        (math.dist(p, s) ** 2 - db) / (2 * math.dist(s, b))
        for key, s in sites.items()
        if key != best
    )
    return best, depth - GAP / 2


def main() -> int:
    mode = sys.argv[1]
    root = (
        Path(sys.argv[2])
        if len(sys.argv) > 2
        else Path.home() / ".se-test/1509-mes-ds0"
    )
    logs = sorted((root / "data").glob("SpaceEngineersDedicated*.log"))
    log = "\n".join(p.read_text(errors="replace") for p in logs) + (
        root / "server.log"
    ).read_text(errors="replace")
    probe = (
        (root / "probe.txt").read_text(errors="replace")
        if (root / "probe.txt").exists()
        else ""
    )
    results: list[tuple[bool, str]] = []

    def check(ok: bool, what: str) -> None:
        results.append((ok, what))

    hashes = re.findall(r"MES (\S+) found, instruction hash ([0-9a-f]{16})", log)
    check(bool(hashes), f"MES found and hashed: {hashes[:1]}")
    check("error " not in probe, "probe ran its plan without errors")
    requests = re.findall(
        r"^request (\S+) (\S+) (\S+) (\S+) (True|False)$", probe, re.M
    )
    census = [line.split() for line in probe.splitlines() if line.startswith("census ")]
    grids = [(float(c[3]), float(c[4]), float(c[5]), c[6]) for c in census]
    print(
        f"info: {len(requests)} spawn requests, {sum(r[4] == 'True' for r in requests)} accepted by MES, "
        f"{len(grids)} NPC grids in the census lines"
    )

    if mode == "guard":
        check(
            re.search(r"was not checked against this plugin", log) is not None,
            "Critical: unchecked MES hash",
        )
        check(
            "spawn placed" not in log and "spawn refused" not in log,
            "no gating happened",
        )
        check(
            any(r[4] == "True" for r in requests), "MES still spawned (runs unpatched)"
        )
    elif mode in ("gate", "gate-new-version"):
        if mode == "gate-new-version":
            check(
                "is not in MesAcceptedVersions" in log,
                "Warning: unlisted MES version, accepted hash",
            )
        check("MES spawn placements gated" in log, "patch installed")
        check("UNPATCHED" not in log and "UNGATED" not in log, "no Critical fallback")
        placed = [
            tuple(map(float, m))
            for m in re.findall(r"spawn placed: .* start (-?\d+),(-?\d+),(-?\d+)", log)
        ]
        refused = re.findall(r"spawn refused: type \S+, reason (\w+)", log)
        print(
            f"info: {len(placed)} placed, refusals {dict((r, refused.count(r)) for r in set(refused))}"
        )
        check(len(placed) > 0, "some spawns were placed")
        bad = [p for p in placed if core_depth(p)[1] < 0]
        check(
            not bad,
            f"every placed start is in a core ({len(placed) - len(bad)}/{len(placed)})",
        )
        depths = [core_depth(g[:3])[1] for g in grids]
        deep = [d for d in depths if d < -EDGE]
        check(
            not deep,
            f"every NPC grid is in a core or within {EDGE:.0f} m of one (worst {min(depths, default=0):.0f} m)",
        )
        disagree = [
            g
            for g, d in zip(grids, depths)
            if g[3] != "na" and (g[3] == "gap") != (d < 0)
        ]
        check(
            not disagree,
            f"InGap slot agrees with this script on {len(grids) - len(disagree)}/{len(grids)} grids",
        )
        check(refused.count("Density") > 0, "density refusals happened")
        nears = [
            int(n) for n in re.findall(r"spawn placed: .*, near (-?\d+)$", log, re.M)
        ]
        check(
            len(nears) == len(placed) and all(0 <= n < DENSITY_CAP for n in nears),
            f"every placement saw fewer than {DENSITY_CAP} NPC grids within {DENSITY_RADIUS:.0f} m (max {max(nears, default=-1)})",
        )
        crowd = max(
            (sum(math.dist(p, g[:3]) <= DENSITY_RADIUS for g in grids) for p in placed),
            default=0,
        )
        check(
            crowd <= 2 * DENSITY_CAP,
            f"final NPC grids within {DENSITY_RADIUS:.0f} m of a placed start: {crowd} (bound {2 * DENSITY_CAP})",
        )
    else:
        raise SystemExit(f"unknown mode {mode}")

    for ok, what in results:
        print(("PASS " if ok else "FAIL ") + what)
    failed = sum(not ok for ok, _ in results)
    print("PASS" if not failed else f"FAIL: {failed} checks")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
