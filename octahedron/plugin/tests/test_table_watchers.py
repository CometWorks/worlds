"""What the other players at a station's hologram table see when one loads a blueprint (SE-1195,
SE-1196), with three clients that all have Multigrid Projector: A loads the way the blueprint
screen does, B watches, C joins later.

A blueprint the shipyard refuses (two grids that only connectors join, which doesn't prove it
came from Multigrid Projector) must not make B say "Blueprint can't be found", throw, or keep a
stale hologram, and once the shipyard cleared it nobody shows it. The shipyard used to clear it
at once, so the removal reached the clients while they still built its hologram on a worker
thread. Loaded several times, as the race doesn't hit every load. Then a blueprint the
shipyard takes must show on A and B, and on C, who arrives after the load.

The table-probe test plugin (tests/table-probe) on the server and the clients logs each change
to the table's clipboard, with OVERLAP and the stack of one made while the clipboard's
activation still runs.

    OCT_TASK=SE-1195 OCT_BLUEPRINT=<bp.sbc of two grids joined by connectors> \\
        uv run --no-project --with httpx python tests/test_table_watchers.py

OCT_LOADS (3) sets how often the refused blueprint is loaded.
Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1195")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (  # noqa: E402
    Second,
    blueprint_xml,
    both_admins,
    remove,
    screens,
    settled,
    table,
)

STATION = os.environ.get("OCT_SHIPYARD_STATION", "Alpha")
BLUEPRINT = Path(os.environ["OCT_BLUEPRINT"])
LOADS = int(os.environ.get("OCT_LOADS", "3"))
SETTLE = 5
PROBE = (
    "table-probe",
    Path(__file__).resolve().parent / "table-probe",
    "TableProbe.xml",
    "table-probe",
)
MGP = (
    pl.MGP[0],
    pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


class First:
    """rig's own client globals: the first client"""

    def __enter__(self):
        pass

    def __exit__(self, *_):
        pass


class Third(Second):
    """rig's client globals point at a third client inside the with"""

    values = {
        **Second.values,
        "PULSAR": rig.TEST_ROOT / f"{rig.TASK}-c2",
        "LAUNCHER": rig.TEST_ROOT / f"{rig.TASK}-c2" / "Interim.bin",
        "APPDATA": rig.TEST_ROOT / f"{rig.TASK}-c2-data",
        "CLIENT_PORT": rig.CLIENT_PORT + 2,
        "CLIENT_ID": rig.CLIENT_ID + 2,
        "CLIENT_NAME": "OctLate",
        "CLIENT_PID": rig.TEST_ROOT / f"{rig.TASK}-c2-data" / "game.pid",
        "CLIENT_LOG": rig.TEST_ROOT / f"{rig.TASK}-c2-data" / "launch.log",
    }


def data(client) -> Path:
    with client():
        return rig.APPDATA


def probe_log(folder: Path) -> str:
    path = folder / "table-probe.log"
    return path.read_text(encoding="utf-8", errors="replace") if path.exists() else ""


def game_log(folder: Path) -> str:
    logs = sorted(folder.glob("SpaceEngineers*.log"), key=lambda p: p.stat().st_mtime)
    return logs[-1].read_text(encoding="utf-8", errors="replace") if logs else ""


def clipboard_exceptions(text: str) -> list[str]:
    found = re.findall(r".*Exception: .*(?:\n   at .*){0,4}", text)
    return [e for e in found if "Clipboard" in e or "Projector" in e]


def projector(remote) -> dict:
    t = table(remote)
    return remote.get_block(t["gridId"], tuple(t["min"]))["projector"] or {}


def message_boxes(remote) -> list:
    return [
        s for s in remote.list_screens() if s.get("type") == "MyGuiScreenMessageBox"
    ]


def wait_for(what, timeout: float):
    """what's first true value, or its last value at the timeout"""
    deadline = time.monotonic() + timeout
    while True:
        value = what()
        if value or time.monotonic() > deadline:
            return value
        time.sleep(1)


def invulnerable(remote) -> bool:
    try:
        remote.set_admin_flag("invulnerable", True)
        return True
    except Exception as e:  # noqa: BLE001 -- retried by the caller
        log(f"admin flag: {e}")
        return False


def folders() -> dict[str, Path]:
    return {"server": rig.SERVER_DATA, "A": data(First), "B": data(Second)}


def marks() -> dict[str, int]:
    return {name: len(probe_log(folder)) for name, folder in folders().items()}


def overlaps(before: dict[str, int]) -> dict[str, int]:
    """New OVERLAP lines in each probe log since before"""
    return {
        name: probe_log(folder)[before[name] :].count("OVERLAP")
        for name, folder in folders().items()
    }


def refused_load(a, b, n: int, entity_id: int) -> None:
    before = marks()
    log(str(a.load_projection(entity_id, name="Loaded")))
    # B shows it until the shipyard clears it
    shown = wait_for(lambda: projector(b).get("isProjecting"), 20)
    check(shown, f"load {n}: B shows the blueprint first")
    gone = wait_for(
        lambda: not projector(a).get("isProjecting")
        and not projector(b).get("isProjecting"),
        SETTLE + 20,
    )
    check(
        gone, f"load {n}: then it is cleared on A and B: {projector(a)} {projector(b)}"
    )
    check(screens(a)["blueprint"] == "False", f"load {n}: the server's table is empty")
    boxes = message_boxes(b)
    check(not boxes, f'load {n}: no "Blueprint can\'t be found" box on B: {boxes}')
    for box in boxes:
        b.close_screen(box["index"])
    found = overlaps(before)
    check(
        not any(found.values()),
        f"load {n}: nothing changes the table's clipboard while it activates: {found}",
    )


def main() -> int:
    for client in (First, Second, Third):
        with client():
            rig.stop_client()
            (rig.APPDATA / "table-probe.log").unlink(missing_ok=True)
    rig.stop_server()
    (rig.SERVER_DATA / "table-probe.log").unlink(missing_ok=True)
    so.prepare(so.SMALL, 60, plugins=[PROBE])
    both_admins()
    loaded = rig.APPDATA / "Blueprints/local/Loaded"
    loaded.mkdir(parents=True, exist_ok=True)
    (loaded / "bp.sbc").write_bytes(BLUEPRINT.read_bytes())
    (loaded / "bp.sbcB5").unlink(missing_ok=True)  # the game's binary copy would win
    for client in (Second, Third):
        folder = data(client) / "Mods"
        folder.mkdir(parents=True, exist_ok=True)
        for id_ in pl.MODS:
            link = folder / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client(extra_dev_folders=[MGP, PROBE])
        with Second():
            b = rig.start_client(extra_dev_folders=[MGP, PROBE])
        # The run outlasts the suit's oxygen in an unpressurized station
        a.set_admin_flag("invulnerable", True)
        b.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        sa.at_table(b, STATION)
        # The loader holds the table for the whole test
        with_config(a, ShipyardTableSettleSeconds=SETTLE, ShipyardTableMinutes=600)
        log(remove(a))
        settled(a, "table", "then press")
        t = table(a)
        entity_id = a.get_block(t["gridId"], tuple(t["min"]))["entityId"]

        for n in range(LOADS):
            refused_load(a, b, n, entity_id)

        before = marks()
        log(str(a.load_projection(entity_id, xml=blueprint_xml(so.SMALL))))
        shown = wait_for(
            lambda: projector(a).get("isProjecting")
            and projector(b).get("isProjecting"),
            30,
        )
        total = projector(a).get("totalBlocks")
        check(
            shown and total == projector(b).get("totalBlocks"),
            f"a blueprint the shipyard takes shows on A and B: {projector(a)} {projector(b)}",
        )
        check(not message_boxes(b), "no message box on B")
        found = overlaps(before)
        check(
            not any(found.values()), f"no clipboard changes while it activates: {found}"
        )

        with Third():
            c = rig.start_client(extra_dev_folders=[MGP, PROBE])
        # Right after joining the client may answer 503 for a while
        wait_for(lambda: invulnerable(c), 60)
        sa.at_table(c, STATION)
        late = wait_for(lambda: projector(c).get("isProjecting"), 30)
        check(
            late and projector(c).get("totalBlocks") == total,
            f"C, who joined after the load, shows it: {projector(c)}",
        )
        check(not message_boxes(c), "no message box on C")
    finally:
        for client in (Third, Second, First):
            with client():
                rig.stop_client()
        rig.stop_server()
    logs = {"server": rig.server_game_log()}
    for name, client in (("A", First), ("B", Second), ("C", Third)):
        logs[name] = game_log(data(client))
    for name, text in logs.items():
        found = clipboard_exceptions(text)
        check(not found, f"no clipboard exceptions on {name}: {found[:2]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} table watcher checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
