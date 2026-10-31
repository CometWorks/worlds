"""What the in-game tests of the stations share: the checks, the station probe (a test-only
server plugin of admin commands, tests/station-probe) and the store probe (a test-only client
plugin that sends the Store screen's requests, tests/store-probe)."""

from __future__ import annotations

import re
import time
from pathlib import Path

import rig
from test_client import log

TESTS = Path(__file__).resolve().parent
PROBE = (
    "station-probe",
    TESTS / "station-probe",
    "StationProbe.xml",
    "5C1A9E07-3D84-4B6F-A2E9-0E6C1D1554AA",
)
STORE_PROBE = ("store-probe", TESTS / "store-probe", "StoreProbe.xml", "store-probe")

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(f"{'PASS' if ok else 'FAIL'} {what}")


def probe(remote, args: str, wait: float = 3.0) -> str:
    reply = " ".join(rig.chat(remote, f"!sprobe {args}", wait))
    log(f"!sprobe {args} -> {reply}")
    return reply


def stand(remote, station: str) -> None:
    """Next to the station's Store block"""
    reply = probe(remote, f"store {station}")
    match = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    check(match is not None, f"{station} Station has a Store block: {reply}")
    if match:
        remote.character_teleport(*(float(v) for v in match.groups()))
        time.sleep(5)


def wait_docked(remote, ship: str) -> bool:
    for _ in range(10):
        if f"DOCKED {ship} yes" in probe(remote, f"docked {ship}", 2):
            return True
    return False


def admin(remote, text: str, wait: float = 3.0) -> str:
    reply = " ".join(rig.chat(remote, text, wait))
    log(f"{text} -> {reply}")
    return reply


def stock(remote, station: str, item: str) -> tuple[float, float, float, float]:
    """OCT's stock of the item at the station, what it asks and bids and how much it still buys, as the book has them"""
    reply = admin(remote, f"!oct market show {station} {item}")
    m = re.search(
        rf"{re.escape(item)}: OCT buys ([\d,.]+) SC up to ([\d,.]+), sells ([\d,.]+) SC, stock ([\d,.]+)",
        reply,
    )
    if not m:
        return (-1.0, -1.0, -1.0, -1.0)
    bid, wanted, ask, have = (float(v.replace(",", "")) for v in m.groups())
    return (have, ask, bid, wanted)


def money(remote) -> dict[str, int]:
    reply = admin(remote, "!oct money")
    return {
        m[1]: int(m[2].replace(",", ""))
        for m in re.finditer(r"(OCT|You): ([\d,]+) SC", reply)
    }


def summary(name: str) -> int:
    failed = [what for passed, what in checks if not passed]
    print(f"{len(checks) - len(failed)}/{len(checks)} {name} checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0
