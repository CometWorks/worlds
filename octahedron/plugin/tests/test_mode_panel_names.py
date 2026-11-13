"""The shipyard mode panel keeps its button names (SE-1409), which say what each button books.

The plugin gives every empty button of the panel a toolbar item so that clients let players press
it, and the game names a button after the item put on it ("Button Panel - Book Shipyard - Toggle
block On"). On the live world that took the names Build, Repair, Hack and Grind away, and every
press answered "This button books nothing".

On a world built from this working copy, server only:
- the four stations left as built: their panels are armed and keep their names;
- Alpha, Beta and North as the live world has them since 2026-11-13: the game's names on all four
  buttons, and the ledger without the 2026-11-13-mode-panel-names-<station> patch, which builds
  the panel again with its names.

    OCT_TASK=SE-1409 uv run --no-project --with httpx python tests/test_mode_panel_names.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. A few minutes on top
of the world build.
"""

from __future__ import annotations

import os
import re
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-1409")
import rig  # noqa: E402

PANEL_NAME = "Button Panel - Book Shipyard"
MODES = ["Build", "Repair", "Hack", "Grind"]
GAME_NAME = f"{PANEL_NAME} - Toggle block On"
BROKEN = ["Alpha Station", "Beta Station", "North Station"]
STATIONS = BROKEN + ["Delta Station", "Gamma Station", "Moon Station", "South Station"]
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"

failures = 0


def check(ok: bool, what: str) -> None:
    global failures
    failures += not ok
    print(f"{'PASS' if ok else 'FAIL'} {what}", flush=True)


def break_names(sector) -> None:
    """Alpha, Beta and North as the live world has them: the game's names on the buttons and no
    names patch in the ledger"""
    text = sector.read_text(encoding="utf-8")
    parts = re.split(r"(?=<MyObjectBuilder_EntityBase )", text)
    done = 0
    for i, part in enumerate(parts):
        name = re.search(r"<DisplayName>([^<]*)</DisplayName>", part)
        if not name or name[1] not in BROKEN or "MyObjectBuilder_CubeGrid" not in part[:200]:
            continue
        panel = part.index(f"<CustomName>{PANEL_NAME}</CustomName>")
        end = part.index("</CustomButtonNames>", panel)
        names, count = re.subn(
            r"<Value>(?:Build|Repair|Hack|Grind)</Value>",
            f"<Value>{GAME_NAME}</Value>",
            part[panel:end],
        )
        assert count == 4, (name[1], count)
        part = part[:panel] + names + part[end:]
        part, count = re.subn(r",2026-11-13-mode-panel-names-\w+", "", part)
        assert count == 1, (name[1], count)
        parts[i] = part
        done += 1
    assert done == len(BROKEN), done
    sector.write_text("".join(parts), encoding="utf-8")


def panels(sector) -> dict[str, tuple[list[str], int]]:
    """Each station's mode panel: its button names and how many buttons have a toolbar item"""
    found = {}
    for entity in (
        ET.parse(sector).getroot().iterfind("SectorObjects/MyObjectBuilder_EntityBase")
    ):
        name = entity.findtext("DisplayName")
        if entity.get(XSI_TYPE) != "MyObjectBuilder_CubeGrid" or name not in STATIONS:
            continue
        for b in entity.iterfind("CubeBlocks/MyObjectBuilder_CubeBlock"):
            if b.findtext("CustomName") == PANEL_NAME:
                names = {
                    int(item.findtext("Key")): item.findtext("Value")
                    for item in b.iterfind("CustomButtonNames/dictionary/item")
                }
                armed = len(b.findall("Toolbar/Slots/Slot"))
                found[name] = ([names.get(k) for k in range(4)], armed)
    return found


def main() -> int:
    rig.prepare_world()
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    break_names(sector)
    before = panels(sector)
    for name in BROKEN:
        check(before[name][0] == [GAME_NAME] * 4, f"{name}: starts with the game's names")
    rig.write_plugin_config(rig.CONFIG_SOURCE.read_text(encoding="utf-8"))
    rig.prepare_server()
    rig.start_server()
    try:
        # The plugin arms the panels as it refreshes the stations, every few seconds
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            log = rig.server_game_log()
            if (
                len(re.findall(r"Shipyard button \d+ armed", log)) >= len(STATIONS)
                and log.count("APPLIED 2026-11-13-mode-panel-names-") >= len(BROKEN)
            ):
                break
            time.sleep(3)
        time.sleep(10)
    finally:
        rig.stop_server()
    log = rig.server_game_log()
    for name in BROKEN:
        patch = f"2026-11-13-mode-panel-names-{name.split()[0].lower()}"
        check(f"APPLIED {patch} on {name}" in log, f"{name}: {patch} applied")
    check(
        "books nothing" not in log and "Station patch FAILED" not in log,
        "no failed patch or empty press in the log",
    )
    after = panels(sector)
    for name in STATIONS:
        names, armed = after.get(name, ([], 0))
        check(armed == 4, f"{name}: 4 buttons armed ({armed})")
        check(names == MODES, f"{name}: buttons named {MODES} ({names})")
    print(f"{'PASSED' if failures == 0 else f'FAILED {failures}'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
