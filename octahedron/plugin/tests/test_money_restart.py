"""Checks on the test server, without a client, that a restart neither doubles nor multiplies
the hourly payout: an hour already paid isn't paid again, and a server that missed one or
more tops of the hour makes one payout. The missed hours are made by moving the saved
state's payout hour back. Prints what it saw; the asserts stop it at the first rule broken.

    OCT_TASK=SE-808 uv run --no-project --with httpx python tests/test_money_restart.py

OCT_WORLD_SOURCE=<folder> runs it on a world built by build_world.py instead of the
committed one.
"""

from __future__ import annotations

import re
import time
from datetime import datetime, timedelta, timezone

import rig
from test_client import log
from test_money import START, wait_log

PAID = re.compile(r"Money hour (\S+ \d\d):00")
SAVED = re.compile(r"(;hour=)(\d{4}-\d\d-\d\dT\d\d)(;)")
# Two minutes of game: the rules run at once at the start and then every minute
SETTLE = 130


def now() -> datetime:
    return datetime.now(timezone.utc)


def hour_label(time_: datetime) -> str:
    return time_.strftime("%Y-%m-%d %H")


def game_logs() -> dict:
    return {
        p: p.read_bytes() for p in rig.SERVER_DATA.glob("SpaceEngineersDedicated*.log")
    }


def paid_since(before: dict) -> list[str]:
    """The hours paid since, from the game log only: the other logs repeat its lines"""
    paid = []
    for path, data in game_logs().items():
        old = before.get(path, b"")
        new = data[len(old) :] if data.startswith(old) else data
        paid += PAID.findall(new.decode(errors="replace"))
    return sorted(paid)


def saved_hour() -> str:
    found = SAVED.search((rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8"))
    assert found, "the money state in the saved world"
    return found[2].replace("T", " ")


def move_saved_hour(hours: int) -> str:
    path = rig.WORLD / "Sandbox.sbc"
    hour = (now() - timedelta(hours=hours)).strftime("%Y-%m-%dT%H")
    text, count = SAVED.subn(rf"\g<1>{hour}\g<3>", path.read_text(encoding="utf-8"))
    assert count == 1, "the money state in the saved world"
    path.write_text(text, encoding="utf-8")
    # The game prefers the binary copy of the checkpoint
    (rig.WORLD / "Sandbox.sbcB5").unlink(missing_ok=True)
    return hour


def run_server() -> tuple[list[str], str, str]:
    """Starts and stops the server: the hours it paid and the hours it ran from and to"""
    before = game_logs()
    start = hour_label(now())
    rig.start_server()
    time.sleep(SETTLE)
    rig.stop_server()
    return paid_since(before), start, hour_label(now())


def hours_between(start: str, end: str) -> int:
    first, last = (datetime.strptime(t, "%Y-%m-%d %H") for t in (start, end))
    return int((last - first) / timedelta(hours=1))


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    wait_log(START)
    rig.stop_server()
    first = saved_hour()
    log(f"first run, saved payout hour {first}")

    # The hour is paid: a restart pays only a top of the hour it really passes
    paid, _, end = run_server()
    log(f"restart in the same hour: paid {paid}")
    assert all(hour > first for hour in paid), f"paid again after {first}: {paid}"
    assert len(paid) == hours_between(first, end), f"from {first} to {end}: {paid}"
    assert saved_hour() == end, f"saved {saved_hour()}, now {end}"

    for missed in (1, 3):
        moved = move_saved_hour(missed)
        paid, start, end = run_server()
        log(f"{missed} tops of the hour missed (saved hour {moved}): paid {paid}")
        assert paid and paid[0] >= start, f"no payout for this hour: {paid}"
        assert len(paid) == len(set(paid)), f"an hour paid twice: {paid}"
        assert len(paid) == 1 + hours_between(
            paid[0], end
        ), f"one payout an hour: {paid}"
        assert saved_hour() == end, f"saved {saved_hour()}, now {end}"
    log("PASS")


if __name__ == "__main__":
    main()
