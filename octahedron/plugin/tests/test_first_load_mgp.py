"""A player's first blueprint load after joining, from Multigrid Projector, is taken (SE-1248).

The shipyard refuses a blueprint with subgrids whose rotor, piston and hinge bases don't point at
its blocks: the game's client damages those without Multigrid Projector. It checked in a
SetNewBlueprint postfix, while the clipboard activation that SetNewBlueprint starts remapped the
same blueprint's ids on a worker thread, so it could see half of them remapped. Cold code on the
main thread made that likely on a player's first load and rare after. The tests/remap-probe plugin
on the server holds the worker after it remapped the rotor's base until the postfixes ran, so
every run hits that interleaving.

    uv run --no-project --with httpx python tests/test_first_load_mgp.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1248")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import remove, screens, settled, table  # noqa: E402

STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
SETTLE = 5
TESTS = Path(__file__).resolve().parent
PROBE = ("remap-probe", TESTS / "remap-probe", "RemapProbe.xml", "remap-probe")
MGP = (
    pl.MGP[0],
    pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)
PROBE_LOG = rig.SERVER_DATA / "remap-probe.log"
REFUSED = re.compile(r"Hologram table \d+: 'Mech Ship' of .* refused")
checks: list[tuple[bool, str]] = []


def check(ok, what: str) -> None:
    checks.append((bool(ok), what))
    print(("PASS " if ok else "FAIL ") + what, flush=True)


def rotor_blueprint() -> str:
    """bp.sbc of a ship with a rotor, so its head is a subgrid"""
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "bp.sbc"
        subprocess.run(
            [sys.executable, str(TESTS / "mech_blueprint.py"), str(path)],
            check=True,
            cwd=TESTS,
        )
        return path.read_text(encoding="utf-8")


def probe_said() -> str:
    """HELD or NOT REACHED once the probe answered for the load, else empty"""
    text = PROBE_LOG.read_text(encoding="utf-8") if PROBE_LOG.exists() else ""
    found = re.findall(r" (HELD|NOT REACHED)$", text, re.M)
    return found[-1] if found else ""


def main() -> int:
    blueprint = rotor_blueprint()
    rig.stop_client()
    rig.stop_server()
    PROBE_LOG.unlink(missing_ok=True)
    so.prepare(so.SMALL, 60, plugins=[PROBE])
    try:
        rig.start_server()
        a = rig.start_client(extra_dev_folders=[MGP])
        a.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        with_config(a, ShipyardTableSettleSeconds=SETTLE, ShipyardTableMinutes=600)
        log(remove(a))
        settled(a, "table", "then press")
        t = table(a)
        entity_id = a.get_block(t["gridId"], tuple(t["min"]))["entityId"]

        # The player's first load since joining
        log(str(a.load_projection(entity_id, xml=blueprint)))
        deadline = time.monotonic() + 30
        while not probe_said() and time.monotonic() < deadline:
            time.sleep(1)
        said = probe_said()
        check(
            said == "HELD",
            f"the probe held the remap mid-way while the server checked the blueprint: {said or 'no answer'}",
        )
        # A refused blueprint leaves the table SETTLE s after it arrived
        time.sleep(SETTLE + 5)
        refusals = REFUSED.findall(rig.server_game_log())
        check(not refusals, f"the shipyard took the first load: {refusals}")
        shown = screens(a)
        check(
            shown["blueprint"] == "True",
            f"the server's table holds it after {SETTLE + 5} s: blueprint {shown['blueprint']}, holder {shown['holder']}",
        )
    finally:
        rig.stop_client()
        rig.stop_server()
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} first load checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
