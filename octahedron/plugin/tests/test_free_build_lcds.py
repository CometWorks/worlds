"""The stations' rules LCDs of a kept world say 30,000 PCU after the station patches (SE-1393).

A world built from OCT_OLD_WORLDS (the commit the live world was built from, whose LCDs say
"first 10,000 PCU is free!") gets the config of this working copy on a plain server. The server
applies its staged station patches, the 2026-11-12-free-build-30k pair of every station among
them, and saves when it stops. The saved world then has no 10,000 PCU text left and the new text
on every rules LCD, four per station.

    OCT_TASK=SE-1393l [OCT_OLD_WORLDS=70fcefb] uv run --no-project --with httpx python tests/test_free_build_lcds.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 5 minutes.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-1393l")
import rig  # noqa: E402
from test_client import log  # noqa: E402
from test_world_patch import old_world  # noqa: E402

PATCH = "2026-11-12-free-build-30k"
STATIONS = ("North", "South", "Alpha", "Beta", "Gamma", "Delta", "Moon")
OLD_TEXT = "first 10,000 PCU is free!"
NEW_TEXT = "first 30,000 PCU is free!"
LCDS = 4 * len(STATIONS)

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def counts() -> tuple[int, int]:
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    return text.count(OLD_TEXT), text.count(NEW_TEXT)


def patched(text: str, station: str) -> bool:
    return bool(
        re.search(
            rf"Station patch STATUS {station} Station .*ledger=.*{PATCH}-{station.lower()}",
            text,
        )
    )


def main() -> int:
    rig.stop_server()
    rig.write_plugin_config()
    os.environ["OCT_WORLD_SOURCE"] = str(old_world())
    rig._world_source = None
    rig.prepare_world()
    old, new = counts()
    check(
        old == LCDS and new == 0,
        f"the old world's LCDs say 10,000 PCU: {old} old, {new} new texts",
    )
    rig.prepare_server()
    rig.start_server()
    try:
        rig.wait_until(
            lambda: all(patched(rig.server_game_log(), s) for s in STATIONS)
            or "Station patch staged" in rig.server_game_log(),
            300,
            "every station's ledger lists its free-build patch",
        )
        text = rig.server_game_log()
    finally:
        rig.stop_server()
    for station in STATIONS:
        check(patched(text, station), f"{station} Station applied {PATCH}")
    errors = [
        line
        for line in text.splitlines()
        if "Station patch" in line
        and ("staged:" in line or "failed" in line or "left out" in line)
    ]
    check(not errors, f"no refused or failed station patches: {errors[:3]}")
    old, new = counts()
    check(
        old == 0 and new == LCDS,
        f"the saved world's LCDs say 30,000 PCU: {old} old, {new} new texts",
    )

    failed = [what for ok, what in checks if not ok]
    print(f"{len(checks) - len(failed)}/{len(checks)} checks passed", flush=True)
    for what in failed:
        print("FAILED", what, flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
