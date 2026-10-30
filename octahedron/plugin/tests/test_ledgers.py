"""Checks the station warehouse ledgers on the test server with the headless client,
through the admin commands (!oct ledger ...): deposits and withdrawals with the game's
item definitions (whole components, fractional ingots, unknown items and stations
refused), the 100 t limit by item mass and the config changing it, transfers to the
station faction's ledger, reservations released, given and taken, and a build
reservation past the limit. Then the server saves and restarts, and the ledgers must
come back from the world as they were. Prints one line per check and a pass/fail
summary; exit code 0 = all passed.

    OCT_TASK=SE-682 uv run --no-project --with httpx python tests/test_ledgers.py
"""

from __future__ import annotations

import sys

import rig
from test_client import log, with_config

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(f"{'PASS' if ok else 'FAIL'} {what}")


def ledger(remote, args: str, expect: str, what: str) -> list[str]:
    """Runs !oct ledger <args> and checks that a reply line contains expect"""
    lines = rig.chat(remote, f"!oct ledger {args}", 4)
    reply = " | ".join(lines)
    check(any(expect in line for line in lines), f"{what}: {reply}")
    return lines


def ok(remote, args: str, what: str) -> None:
    ledger(remote, args, "done.", what)


def refused(remote, args: str, expect: str, what: str) -> None:
    ledger(remote, args, expect, what)


def shows(remote, args: str, expected: list[str], what: str) -> None:
    lines = rig.chat(remote, f"!oct ledger show {args}", 4)
    text = "\n".join(lines)
    missing = [e for e in expected if e not in text]
    check(not missing, f"{what}: missing {missing}" if missing else what)
    if missing:
        log(text)


def final_state(remote, when: str) -> None:
    shows(
        remote,
        "Alpha me",
        [
            "(P",
            ": 1.3 t",
            "Ingot/Iron 700",
            "Component/SteelPlate 30",
            "reserved build-1: Component/SteelPlate 10",
        ],
        f"{when}: my Alpha ledger",
    )
    shows(
        remote,
        "Alpha faction:OCT",
        ["OCT (F", "Ingot/Iron 300", "Component/SteelPlate 10"],
        f"{when}: the faction's Alpha ledger",
    )
    shows(remote, "Beta me", ["Ingot/Iron 5"], f"{when}: my Beta ledger")
    shows(remote, "Gamma", ["Gamma Station: empty."], f"{when}: Gamma is empty")


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    # The market keeps OCT's daily stock in OCT's ledgers, which this test reads as its own
    rig.write_plugin_config(
        rig.CONFIG_SOURCE.read_text(encoding="utf-8").replace(
            "<MarketEnabled>true<", "<MarketEnabled>false<"
        )
    )
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client()
    log("joined")

    stations = " ".join(rig.chat(remote, "!oct ledger stations", 4))
    check(
        "Alpha Station" in stations and "Moon Station" in stations,
        f"stations: {stations}",
    )

    ok(remote, "deposit Alpha me Ingot/Iron 1000", "deposit iron")
    shows(remote, "Alpha me", ["Ingot/Iron 1,000", ": 1 t"], "one tonne of iron")
    refused(
        remote,
        "deposit Alpha me Component/SteelPlate 1.5",
        "whole units",
        "half a plate",
    )
    ok(remote, "deposit Alpha me Ingot/Iron 2.5", "fractional iron")
    refused(
        remote, "deposit Alpha me Ingot/Unobtainium 1", "Unknown item", "unknown item"
    )
    refused(remote, "deposit Nowhere me Ingot/Iron 1", "No station", "unknown station")
    refused(
        remote, "withdraw Alpha me Ingot/Iron 2000", "Only 1,002.5", "withdraw too much"
    )

    # 1 kg an ingot: up to 100 t exactly, and not a kilogram more
    ok(remote, "deposit Alpha me Ingot/Iron 98997.5", "fill to 100 t")
    refused(
        remote, "deposit Alpha me Ingot/Iron 1", "over the limit of 100 t", "past 100 t"
    )
    ok(remote, "withdraw Alpha me Ingot/Iron 99000", "withdraw 99 t")
    # A steel plate is 20 kg
    ok(remote, "deposit Alpha me Component/SteelPlate 50", "50 plates")
    shows(remote, "Alpha me", [": 2 t"], "1 t of iron and 1 t of plates")
    with_config(remote, LedgerPlayerLimitTonnes=2, MarketEnabled="false")
    refused(
        remote,
        "deposit Alpha me Component/SteelPlate 1",
        "over the limit of 2 t",
        "limit from the config",
    )
    with_config(remote, MarketEnabled="false")

    ok(
        remote,
        "transfer Alpha me faction:OCT Component/SteelPlate 10",
        "give the faction plates",
    )
    refused(
        remote,
        "transfer Alpha me faction:OCT Component/SteelPlate 41",
        "Only 40",
        "transfer too many",
    )

    # A listing: counted, reserved, partly released, sold to the faction
    ok(remote, "reserve Alpha me listing-1 Ingot/Iron 400", "list iron")
    refused(
        remote, "withdraw Alpha me Ingot/Iron 601", "Only 600", "withdraw listed iron"
    )
    shows(
        remote,
        "Alpha me",
        ["reserved listing-1 (counted): Ingot/Iron 400", ": 1.8 t"],
        "the listing counts",
    )
    ok(remote, "release Alpha me listing-1 Ingot/Iron 100", "release part")
    refused(
        remote,
        "release Alpha faction:OCT listing-1",
        "someone else",
        "release another's listing",
    )
    ok(remote, "give Alpha me listing-1 faction:OCT", "sell the rest to the faction")
    refused(
        remote,
        "give Alpha me listing-1 faction:OCT",
        "No reservation",
        "the listing is gone",
    )

    # A build: bought plates past the limit, mostly used up
    ok(
        remote,
        "fill Alpha me build-1 Component/SteelPlate 10000",
        "200 t of bought plates",
    )
    ok(remote, "take Alpha me build-1 Component/SteelPlate 9990", "the build uses them")
    ok(remote, "withdraw Alpha me Component/SteelPlate 10", "plates out")

    ok(remote, "deposit Beta me Ingot/Iron 5", "iron at Beta")
    final_state(remote, "before the restart")

    rig.stop_client()
    rig.stop_server()
    sandbox = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    check(
        "Octahedron.Ledgers" in sandbox and "octahedron-ledgers 1" in sandbox,
        "the saved Sandbox.sbc holds the ledgers",
    )
    log("restarting")
    rig.start_server()
    remote = rig.start_client()
    final_state(remote, "after the restart")
    ok(remote, "release Alpha me build-1", "the build's leftovers back")
    shows(
        remote, "Alpha me", ["Component/SteelPlate 40"], "leftovers in the free stock"
    )

    rig.stop_client()
    rig.stop_server()
    failed = [what for passed, what in checks if not passed]
    print(f"{len(checks) - len(failed)}/{len(checks)} ledger checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
