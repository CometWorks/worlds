"""Checks the money rules on the test server with the headless client: OCT's 1e9 cut to
its cap at the first run, a new account's starting balance, no economy top-up of OCT
while the rules are on (and the game's top-up with them off, the known positive), two
settlements by !oct payday with the tax, subsidy and OCT's balance above its cap, OCT
topped up to its floor, and the state saved in the world and read back after a restart.
The player tax threshold is lowered to 10,000 SC so the client's own account pays tax.
Prints what it saw; the asserts stop it at the first rule broken.

    OCT_TASK=SE-681 uv run --no-project --with httpx python tests/test_money.py
"""

from __future__ import annotations

import re
import time

import rig
from test_client import log, with_config

START = "Money rules start: OCT's balance cut from 1,000,000,000 SC to its cap 20,000,000 SC"
SETTLED = re.compile(r"Money day \S+ settled: .*")
TOPPED = "Money: OCT topped up by 5,000,000 SC to its floor"
# Money options of every step, on top of the world's config
BASE = {"MoneyPlayerTaxThreshold": 10_000}


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


def money(remote) -> dict[str, int]:
    """!oct money as {"OCT": balance, "You": balance}"""
    lines = " ".join(rig.chat(remote, "!oct money", 3))
    found = {
        name: int(amount.replace(",", ""))
        for name, amount in re.findall(r"(OCT|You): ([\d,]+) SC", lines)
    }
    log(f"!oct money: {found}")
    return found


def payday(remote) -> str:
    marks = log_marks()
    reply = " ".join(rig.chat(remote, "!oct payday", 3))
    assert reply == "Settled, see the server log.", reply
    # The game log is written with a delay
    deadline = time.monotonic() + 120
    while not (lines := SETTLED.findall(plugin_log(marks))):
        assert time.monotonic() < deadline, "no settlement logged"
        time.sleep(5)
    log(lines[-1])
    return lines[-1]


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
    assert money(remote) == {"OCT": 20_000_000, "You": 25_000}, "starting balances"

    # Two economy ticks: with the rules on, OCT stays where it is
    time.sleep(130)
    assert money(remote)["OCT"] == 20_000_000, "the economy topped OCT up"

    # Tax 1 % of 15,000 = 150, subsidy 5,000, all of it back to the only active player
    line = payday(remote)
    assert "tax 150 SC from 1 players" in line, line
    assert "pool 5,150 SC to 1 active players at 5,150 SC each" in line, line
    assert money(remote) == {"OCT": 20_000_000, "You": 30_000}, "after the first day"

    with_config(remote, **BASE, MoneyOctFloor=25_000_000)
    wait_log(TOPPED)
    assert money(remote)["OCT"] == 25_000_000, "OCT topped up to its floor"

    # Tax 200, subsidy 5,000 and OCT's 3,000,000 above the new cap
    with_config(remote, **BASE, MoneyOctCap=22_000_000)
    line = payday(remote)
    assert "OCT above its cap 3,000,000 SC" in line, line
    assert "pool 3,005,200 SC to 1 active players" in line, line
    assert "OCT topped up 5,000,000 SC during the day" in line, line
    assert money(remote) == {
        "OCT": 22_000_000,
        "You": 3_035_000,
    }, "after the second day"

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
    state = re.search(r"day=\d{4}-\d\d-\d\d;topups=0;seen=\d+:\d+", saved)
    assert state, "money state in the saved world"
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
