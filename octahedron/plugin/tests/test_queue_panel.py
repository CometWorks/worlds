"""The shipyard queue panel's layout at a real station (SE-935): an entry wraps to two lines
with the second indented, no line is wider than the 46 columns, and the font is the size those
columns give (0.57) instead of shrinking to fit an entry on one line. tests/panel-text has the
layout of a full queue without the game.

    OCT_TASK=SE-935 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_queue_panel.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 5 minutes.
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "SE-935")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log, with_config  # noqa: E402
from test_table_rules import (  # noqa: E402
    CONFIG,
    STATION,
    check,
    checks,
    load,
    press,
    remove,
    settled,
)

COLUMNS = 46
QUEUE_PANEL = "Text Panel - Station ShipyardQueue"


def queue_panel(remote) -> dict:
    panels = remote.find_nearby_blocks(
        subtype="LargeTextPanel", max_distance=100, limit=50
    )
    return next(p for p in panels if p.get("customName") == QUEUE_PANEL)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    folder = rig.APPDATA / "Mods"
    folder.mkdir(parents=True, exist_ok=True)
    for id_ in pl.MODS:
        link = folder / f"{id_}.sbm"
        link.unlink(missing_ok=True)
        link.symlink_to(pl.WORKSHOP / str(id_))
    try:
        rig.start_server()
        a = rig.start_client()
        rig.ensure_character(a)
        a.set_admin_flag("invulnerable", True)
        sa.at_table(a, STATION)
        with_config(a, **CONFIG)
        log(remove(a))
        settled(a, "table", "then press")
        load(a)
        settled(a, "table", "Press the button")
        check("is in the queue" in press(a), "the order is in the queue")
        text = settled(a, "queue", "for OctTester")
        log(repr(text))
        lines = text.rstrip().split("\n")
        first = next((i for i, l in enumerate(lines) if l.startswith("1. ")), None)
        check(first is not None, "the queue panel shows the order")
        check(
            first is not None
            and first + 1 < len(lines)
            and lines[first + 1].startswith("   ")
            and "for OctTester" in " ".join(lines[first : first + 2]),
            "the entry wraps to a second, indented line",
        )
        check(
            all(len(l) <= COLUMNS for l in lines),
            f"no line is wider than {COLUMNS}: {max(len(l) for l in lines)}",
        )
        panel = queue_panel(a)
        grid, at = panel["gridId"], tuple(panel["min"])
        shown = a.get_surface(grid, at)["text"]
        check(shown.rstrip() == text.rstrip(), f"the client shows the same: {shown!r}")
        size = a.get_property(grid, at, "FontSize")
        log(f"FontSize {size}")
        value = float(size.get("value") if isinstance(size, dict) else size)
        check(0.55 < value < 0.6, f"the font size is the 46 columns' 0.57: {value:.3f}")
    finally:
        rig.stop_client()
        rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} queue panel checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
