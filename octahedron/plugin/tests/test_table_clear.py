"""Checks that a station hologram table's blueprint is cleared (SE-773), on the test
server with the headless client. Every table starts with "Order Test" loaded;
!oct table hold makes the player the one who loaded a table's blueprint, as a load
from the client would.

- "left": the player holds a table's blueprint and teleports 5 km away: cleared.
- "queued": the player orders the ship at a table: cleared once it is in the queue.
- "stay": the player holds a table's blueprint and stays: not cleared within 25 s.
- "logoff": the player holds a table's blueprint and the client quits, as the player
  leaves a cluster node in a handover: cleared.
- "timeout": with ShipyardTableMinutes 1, a held table is cleared a minute after the
  hold, and the tables no one held are cleared too.

    uv run --no-project --with httpx python tests/test_table_clear.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
Not covered: a handover on a real cluster.
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-773")
import rig  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
from test_client import log, ship_reply, with_config  # noqa: E402

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def hold(remote, station: str) -> str:
    sa.at_table(remote, station)
    reply = sa.admin(remote, "!oct table hold")
    table = re.search(r"hologram table (\d+)", reply)
    check(
        table is not None, f"{station}: the player holds the table's blueprint: {reply}"
    )
    return table[1] if table else "none"


def cleared(table: str, timeout: float) -> str | None:
    return sa.wait_log(rf"Hologram table {table}: blueprint cleared", timeout)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(so.SMALL, 60)
    rig.start_server()
    remote = rig.start_client()
    rig.ensure_character(remote)
    stations = sorted(sa.station_tables())
    log(f"stations {stations}")
    try:
        left, queued, stay, logoff, timeout = stations[:5]

        table = hold(remote, left)
        x, y, z = sa.station_tables()[left]
        remote.character_teleport(x + 5000, y, z)
        line = cleared(table, 30)
        check(line is not None and "left the station" in line, f"left: {line}")

        table = hold(remote, queued)
        assert "Your quote for" in ship_reply(remote, "!oct order")
        reply = ship_reply(remote, "!oct order")
        check("is in the queue" in reply, f"queued: ordered: {reply}")
        line = cleared(table, 20)
        check(line is not None and "is in the queue" in line, f"queued: {line}")

        table = hold(remote, stay)
        time.sleep(25)
        line = cleared(table, 0)
        check(line is None, f"stay: kept while the player stays: {line}")

        table = hold(remote, logoff)
        rig.stop_client()
        line = cleared(table, 90)
        check(line is not None and "left the station" in line, f"logoff: {line}")

        remote = rig.start_client()
        rig.ensure_character(remote)
        table = hold(remote, timeout)
        held = time.monotonic()
        with_config(remote, ShipyardTableMinutes=1)
        line = cleared(table, 100)
        waited = time.monotonic() - held
        check(
            line is not None and "1 min ago" in line and waited >= 55,
            f"timeout: after {waited:.0f} s: {line}",
        )
        others = [
            line
            for line in rig.server_game_log().splitlines()
            if "blueprint cleared, loaded 1 min ago" in line
        ]
        check(
            len(others) >= len(stations) - 4,
            f"timeout: the tables no one held are cleared: {len(others)} lines",
        )
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
    print(f"{len(checks) - len(failed)}/{len(checks)} table checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
