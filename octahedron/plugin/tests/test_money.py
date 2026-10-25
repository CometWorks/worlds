"""Checks the money rules on the test server with the headless client: OCT's 1e9 cut to
its cap at the first run, a new account's starting balance, no economy top-up of OCT
while the rules are on (and the game's top-up with them off, the known positive), the
daily tax by !oct taxday, hourly payouts by !oct payday with a part of the held tax, the
subsidy and OCT's balance above its cap, OCT topped up to its floor, the day's totals,
and the state saved in the world and read back after a restart.
The player tax threshold is lowered to 10,000 SC so the client's own account pays tax.
A payout the server makes on its own at the top of an hour is counted in. Prints what
it saw; the asserts stop it at the first rule broken.

    OCT_TASK=SE-681 uv run --no-project --with httpx python tests/test_money.py

OCT_WORLD_SOURCE=<folder> runs it on a world built by build_world.py instead of the
committed one.
"""

from __future__ import annotations

import re
import time

import rig
from test_client import log, with_config

START = "Money rules start: OCT's balance cut from 1,000,000,000 SC to its cap 20,000,000 SC"
TAX = re.compile(r"Money tax \S+: ([\d,]+) SC from (\d+) players.*")
HOUR = re.compile(
    r"Money hour \S+ \S+: tax ([\d,]+) SC, subsidy ([\d,]+) SC, OCT above its cap ([\d,]+) SC, "
    r"pool ([\d,]+) SC to (\d+) active players at ([\d,]+) SC each.*"
)
CLOSED = re.compile(
    r"Money day \S+ closed: paid out ([\d,]+) SC in (\d+) hourly payouts.*"
)
TOPPED = "Money: OCT topped up by 5,000,000 SC to its floor"
SUBSIDY = 5_000 // 24
# Money options of every step, on top of the world's config
BASE = {"MoneyPlayerTaxThreshold": 10_000}


def number(text: str) -> int:
    return int(text.replace(",", ""))


def log_files() -> list:
    return [rig.SERVER_LOG, rig.SERVER_CONFIG / "info.log"] + sorted(
        rig.SERVER_DATA.glob("SpaceEngineersDedicated*.log")
    )


def plugin_log(marks: dict | None = None) -> str:
    """The server's logs, or with marks (from log_marks) only what came after them"""
    text = ""
    for path in log_files():
        if not path.exists():
            continue
        data = path.read_bytes()
        mark = (marks or {}).get(path, 0)
        text += data[mark if len(data) >= mark else 0 :].decode(errors="replace")
    return text


def log_marks() -> dict:
    return {p: p.stat().st_size for p in log_files() if p.exists()}


class Ledger:
    """The client's expected balance: what it was, plus every payout logged since"""

    def __init__(self, balance: int) -> None:
        self.balance = balance
        self.marks = log_marks()
        self.seen: set[str] = set()
        self.shares: list[int] = []

    def payouts(self) -> list[re.Match]:
        found = [
            m for m in HOUR.finditer(plugin_log(self.marks)) if m[0] not in self.seen
        ]
        for m in found:
            assert m[5] == "1", m[0]
            self.seen.add(m[0])
            self.balance += number(m[6])
            self.shares.append(number(m[6]))
        return found


def money(remote) -> dict[str, int]:
    """!oct money as {"OCT": balance, "You": balance}"""
    lines = " ".join(rig.chat(remote, "!oct money", 3))
    found = {
        name: number(amount)
        for name, amount in re.findall(r"(OCT|You): ([\d,]+) SC", lines)
    }
    log(f"!oct money: {found}")
    return found


def command(remote, text: str, pattern: re.Pattern) -> re.Match:
    """Runs !oct payday or !oct taxday and waits for its log line"""
    marks = log_marks()
    reply = " ".join(rig.chat(remote, text, 3))
    assert reply == "Done, see the server log.", reply
    # The game log is written with a delay
    deadline = time.monotonic() + 120
    while not (found := list(pattern.finditer(plugin_log(marks)))):
        assert time.monotonic() < deadline, f"nothing logged for {text}"
        time.sleep(5)
    log(found[-1][0])
    return found[-1]


def check_payout(m: re.Match, oct_excess: int) -> int:
    """A payout to the only active player: its share is the whole pool"""
    tax, subsidy, excess, pool, share = (number(m[i]) for i in (1, 2, 3, 4, 6))
    assert subsidy == SUBSIDY, m[0]
    assert excess == oct_excess, m[0]
    assert pool == tax + subsidy + excess == share, m[0]
    return tax


def wait_log(text: str, timeout: float = 120) -> None:
    deadline = time.monotonic() + timeout
    while text not in plugin_log():
        assert time.monotonic() < deadline, f"not logged: {text}"
        time.sleep(5)


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    # A short economy tick, so the game's top-up toward 1e9 would show within the run
    rig.prepare_world(settings={"EconomyTickInSeconds": 60})
    rig.write_plugin_config()
    rig.prepare_server()
    rig.start_server()
    log("server ready")
    wait_log(START)
    log("OCT cut to its cap at the first run")

    remote = rig.start_client()
    with_config(remote, **BASE)
    ledger = Ledger(25_000)
    assert money(remote) == {"OCT": 20_000_000, "You": 25_000}, "starting balances"

    # Two economy ticks: with the rules on, OCT stays where it is
    time.sleep(130)
    ledger.payouts()
    assert money(remote) == {"OCT": 20_000_000, "You": ledger.balance}, "OCT moved"

    # Tax 1 % of the 15,000 above the lowered threshold, held for the payouts
    ledger.payouts()
    tax_from = ledger.balance - 10_000
    line = command(remote, "!oct taxday", TAX)
    ledger.payouts()
    day_start = len(ledger.shares)
    assert number(line[1]) == tax_from // 100 and line[2] == "1", line[0]
    ledger.balance -= tax_from // 100

    # A payout: a part of the held tax, the hour's subsidy, OCT at its cap gives nothing
    paid = command(remote, "!oct payday", HOUR)
    check_payout(paid, 0)
    ledger.payouts()
    assert money(remote) == {"OCT": 20_000_000, "You": ledger.balance}, "after a payout"

    with_config(remote, **BASE, MoneyOctFloor=25_000_000)
    wait_log(TOPPED)
    ledger.payouts()
    assert money(remote)["OCT"] == 25_000_000, "OCT topped up to its floor"

    # OCT's 3,000,000 above the lowered cap goes to the player
    with_config(remote, **BASE, MoneyOctCap=22_000_000)
    paid = command(remote, "!oct payday", HOUR)
    check_payout(paid, 3_000_000)
    ledger.payouts()
    assert money(remote) == {"OCT": 22_000_000, "You": ledger.balance}, "OCT's excess"

    # Closing the day logs its totals: the payouts since the first tax day, OCT's top-up.
    # It taxes the client again.
    ledger.payouts()
    closed = command(remote, "!oct taxday", CLOSED)
    day = ledger.shares[day_start:]
    assert number(closed[2]) == len(day) and number(closed[1]) == sum(day), closed[0]
    assert "OCT topped up 5,000,000 SC" in closed[0], closed[0]
    second = list(TAX.finditer(plugin_log(ledger.marks)))[-1]
    log(second[0])
    ledger.balance -= number(second[1])
    ledger.payouts()
    assert money(remote)["You"] == ledger.balance, "after the second tax day"

    # The known positive: with the rules off the economy tops OCT up again
    with_config(remote, MoneyEnabled="false")
    time.sleep(130)
    oct = money(remote)["OCT"]
    assert oct > 22_000_000, "the economy's top-up with the rules off"
    with_config(remote, **BASE)

    rig.stop_client()
    rig.stop_server()
    log("server stopped and saved")
    saved = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    state = re.search(
        r"day=\d{4}-\d\d-\d\d;hour=\d{4}-\d\d-\d\dT\d\d;held=(\d+);.*?;seen=\d+:\d+",
        saved,
    )
    assert state, "money state in the saved world"
    assert int(state[1]) > 0, "held tax in the saved state"
    log(f"saved state: {state[0]}")

    # Read back. A floor above OCT's balance shows the rules run after the restart; a lost
    # state would run the first run again and log its start
    marks = log_marks()
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8").replace(
        "</OctahedronConfig>",
        "  <MoneyOctFloor>900000000</MoneyOctFloor>\n"
        "  <MoneyOctCap>1000000000</MoneyOctCap>\n</OctahedronConfig>",
    )
    rig.write_plugin_config(text)
    rig.start_server()
    deadline = time.monotonic() + 180
    while "Money: OCT topped up by" not in plugin_log(marks):
        assert time.monotonic() < deadline, "no top-up after the restart"
        time.sleep(5)
    assert "Money rules start" not in plugin_log(marks), "the restart lost the state"
    rig.stop_server()
    log("state read back after the restart")
    log("PASS")


if __name__ == "__main__":
    main()
