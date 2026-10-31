"""Checks the faction leaderboard on the test server with the headless client. The
kiosk probe (tests/kiosks/probe) makes the factions and docks a ship at Alpha
Station: the player founds LBT, and LBB is a faction of a fresh identity's.

The leaderboard probe (tests/leaderboard/probe) reads a station panel's text on the
server: on a client the game's GetText() returns only what a script wrote there, not
the text the server sent.

Checked: only the players' factions are ranked, not OCT or the NPC factions; a
faction's cash is its bank account; a docked ship of 1,000 iron ingots counts for
its owner's faction with its PCU and with its value worked out here from the game's
block definitions and the middle of OCT's prices at every station (`!oct market
show`), which ranks LBT above LBB; money held for a faction's market order still
counts; the station panels show the board in Monospace and refresh on their own;
and the count of the whole world takes little time per tick. Prints one line per
check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-698 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_leaderboard.py

With OCT_SHOTS=<folder> the client renders and saves screenshots of an Alpha panel
from in front of it and from behind (the side the screen faces isn't checked), to see
that the board fits the curved panel.

Not covered here: a cluster (tests/leaderboard/leaderboard_cluster.py).
"""

from __future__ import annotations

import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from decimal import ROUND_HALF_EVEN, Decimal

os.environ.setdefault("OCT_TASK", "SE-698")
import rig  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
from test_client import log  # noqa: E402

PANEL = "Curved LCD Panel - Station Lounge Leaderboard"
LB_PROBE = (
    "leaderboard-probe",
    rig.PLUGIN / "tests" / "leaderboard" / "probe",
    "LeaderboardProbe.xml",
    "6D2F4A18-9B53-4E07-8C61-2E5B1F1573AA",
)
PANELS = 28  # 4 at each of the 7 stations
INTERVAL = 10
IRON = "Ingot/Iron"
# The probe's docked ship
SHIP_BLOCKS = [
    ("ShipConnector", "Connector"),
    ("CargoContainer", "LargeBlockSmallContainer"),
]
ROW = re.compile(
    r"(\d+)\. \[(\S+)\] (.*?): ([\d,]+) SC \(cash ([\d,]+) SC, grids ([\d,]+) SC\), ([\d,]+) PCU"
)
SHOTS = os.environ.get("OCT_SHOTS")
PANEL_AT = re.compile(r"AT (\S+) (\S+) (\S+) FORWARD (\S+) (\S+) (\S+)")
COUNTED = re.compile(
    r"Leaderboard: counted (\d+) grids in (\d+) ticks, (\d+) ms of work"
)
check = kiosks.check
probe = kiosks.probe


def number(text: str) -> int:
    return int(text.replace(",", ""))


def board(remote) -> dict[str, dict]:
    reply = " ".join(rig.chat(remote, "!oct leaderboard", 6))
    rows = rows_of(reply)
    log(f"!oct leaderboard -> {rows or reply}")
    return rows


def rows_of(reply: str) -> dict[str, dict]:
    """!oct leaderboard's answer as tag -> rank, total, cash, grids, pcu"""
    return {
        m[2]: {
            "rank": int(m[1]),
            "total": number(m[4]),
            "cash": number(m[5]),
            "grids": number(m[6]),
            "pcu": number(m[7]),
        }
        for m in ROW.finditer(reply)
    }


def definitions() -> dict[tuple[str, str], tuple[dict[str, int], int]]:
    """The game's block definitions: (type, subtype) -> (component counts, PCU)"""
    found = {}
    for path in (rig.DS64.parent / "Content/Data/CubeBlocks").glob("*.sbc"):
        for d in ET.parse(path).getroot().iter("Definition"):
            key = (d.findtext("Id/TypeId"), d.findtext("Id/SubtypeId"))
            if key not in SHIP_BLOCKS:
                continue
            counts: dict[str, int] = {}
            for c in d.iter("Component"):
                name = "Component/" + c.get("Subtype")
                counts[name] = counts.get(name, 0) + int(c.get("Count"))
            found[key] = (counts, int(d.findtext("PCU") or 1))
    assert len(found) == len(SHIP_BLOCKS), found
    return found


def expected(price: dict[str, int], iron: int, ships: int = 1) -> tuple[int, int]:
    """The probe's ships with the iron in all: their value in SC, rounded down once as
    the plugin does for a faction, and their PCU"""
    milli = iron * price.get(IRON, 0)
    pcu = 0
    for counts, block_pcu in definitions().values():
        milli += ships * sum(n * price.get(item, 0) for item, n in counts.items())
        pcu += ships * block_pcu
    log(f"expected: {milli // 1000:,} SC, {pcu} PCU; iron {price.get(IRON)} milli-SC")
    return milli // 1000, pcu


def stations(remote) -> list[str]:
    reply = " ".join(rig.chat(remote, "!oct ledger stations", 3))
    names = [s.strip() for s in reply.split(",") if s.strip()]
    log(f"stations: {names}")
    return names


def market(remote, station: str, owner: str = "faction:LBT") -> str:
    return " ".join(rig.chat(remote, f'!oct market show "{station}" {owner} all', 5))


def prices(show, names: list[str]) -> dict[str, int]:
    """milli-SC per unit: the middle of OCT's bid and ask, averaged over the stations.
    show(station) is what `!oct market show <station> <owner> all` answered there."""
    mids: dict[str, list[Decimal]] = {}
    for station in names:
        for m in re.finditer(
            r"(\S+/\S+): OCT buys ([\d,.]+) SC(?: \(specialty\))?, sells ([\d,.]+) SC",
            show(station),
        ):
            bid, ask = (Decimal(v.replace(",", "")) * 1000 for v in (m[2], m[3]))
            if bid > 0 and ask > 0:
                mids.setdefault(m[1], []).append((bid + ask) / 2)
    return {
        item: int((sum(v) / len(v)).quantize(Decimal(1), rounding=ROUND_HALF_EVEN))
        for item, v in mids.items()
    }


def balance(remote, owner: str = "faction:LBT") -> int:
    m = re.search(r"balance ([\d,]+) SC", market(remote, "Alpha", owner))
    return number(m[1]) if m else -1


def panels(remote) -> list[dict]:
    found = remote.find_nearby_blocks(
        subtype="LargeCurvedLCDPanel", max_distance=500, limit=40
    )
    return [p for p in found if p.get("customName") == PANEL]


def panel_text(remote) -> str:
    """Alpha's first leaderboard panel on the server: PANEL count content-type font, the text"""
    return "\n".join(rig.chat(remote, "!lbprobe panel Alpha", 3))


def lbt_line(text: str) -> str:
    return next((l for l in text.splitlines() if " LBT " in l), "")


def wait_panel(remote, ending: str, timeout: float) -> str:
    """LBT's row on the panel once it ends so, or as it is at the timeout"""
    deadline = time.monotonic() + timeout
    line = ""
    while time.monotonic() < deadline:
        line = lbt_line(panel_text(remote))
        if line.endswith(ending):
            break
        time.sleep(3)
    return line


def screenshots(remote, at: list[float]) -> None:
    """2.5 m from the panel on either side of it, looking at it"""
    position, forward = at[:3], at[3:]
    Path(SHOTS).mkdir(parents=True, exist_ok=True)
    for side, sign in (("front", 1), ("back", -1)):
        remote.character_teleport(
            *(p + sign * 2.5 * f for p, f in zip(position, forward))
        )
        time.sleep(4)
        remote.character_look_at(*position)
        time.sleep(4)
        path = Path(SHOTS) / f"leaderboard-{side}.png"
        remote.screenshot_save(str(path), 1280, 720)
        log(f"screenshot {path}")


def run(remote) -> None:
    reply = probe(remote, "kiosk Alpha")
    stand = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    # A world from the blueprint since SE-832 has no kiosk; the character spawned at Alpha anyway
    log(f"Alpha's kiosk: {reply}")
    if stand:
        remote.character_teleport(*(float(v) for v in stand.groups()))
        time.sleep(3)
    probe(remote, "faction LBB member")
    probe(remote, "faction LBT founder")
    check("LBT founder" in probe(remote, "whoami"), "the player founded LBT")
    names = stations(remote)
    check(len(names) == 7, f"7 stations: {names}")

    rows = board(remote)
    check(set(rows) == {"LBT", "LBB"}, f"only the players' factions: {sorted(rows)}")
    lbt, lbb = rows.get("LBT", {}), rows.get("LBB", {})
    check(lbt.get("cash") == lbb.get("cash") == 0, f"new factions have 0 SC: {rows}")
    # LBB sells iron to OCT for some cash
    for line in (
        "!oct ledger deposit Alpha faction:LBB Ingot/Iron 500",
        "!oct market do sell Alpha faction:LBB Ingot/Iron 500",
    ):
        log(f"{line} -> {' '.join(rig.chat(remote, line, 4))}")
    cash = balance(remote, "faction:LBB")
    rows = board(remote)
    lbt, lbb = rows.get("LBT", {}), rows.get("LBB", {})
    check(lbb.get("cash") == cash > 0, f"LBB's cash is its account: {lbb} vs {cash}")
    check(lbb.get("rank") == 1, f"cash alone ranks LBB first: {rows}")
    check(lbt.get("grids") == 0 and lbt.get("pcu") == 0, f"no grids yet: {lbt}")

    # A ship of the player's docked at Alpha with 1,000 iron
    reply = probe(remote, f"dock Alpha mine {IRON}=1000")
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"the probe docked a ship: {reply}")
    if ship:
        kiosks.wait_docked(remote, ship[1])
    price = prices(lambda station: market(remote, station), names)
    value, pcu = expected(price, iron=1000)
    check(price.get(IRON, 0) > 0, "iron has a price")

    rows = board(remote)
    lbt, lbb = rows.get("LBT", {}), rows.get("LBB", {})
    check(lbt.get("grids") == value, f"the ship's value: {lbt.get('grids')} vs {value}")
    check(lbt.get("pcu") == pcu, f"the ship's PCU: {lbt.get('pcu')} vs {pcu}")
    check(lbt.get("total") == value, f"LBT's total is its ship: {lbt}")
    first = "LBT" if value > cash else "LBB"
    check(rows.get(first, {}).get("rank") == 1, f"{first} ranks first: {rows}")

    # Money held for a faction's market order is still the faction's
    reply = " ".join(
        rig.chat(remote, "!oct market do order Alpha faction:LBB Ingot/Gold 1000 1", 4)
    )
    log(f"order -> {reply}")
    after = balance(remote, "faction:LBB")
    check(after == cash - 1000, f"the order holds 1,000 SC: {cash} -> {after}")
    rows = board(remote)
    check(rows.get("LBB", {}).get("cash") == cash, f"held money still counts: {rows}")

    # The station panels
    found = panels(remote)
    check(len(found) == 4, f"Alpha's 4 leaderboard panels nearby: {len(found)}")
    rank = rows.get("LBT", {}).get("rank", 0)
    text = panel_text(remote)
    log("panel:\n" + text)
    check(
        re.match(r"PANEL 4 TEXT_AND_IMAGE \S*Monospace ", text) is not None,
        "4 panels at Alpha, text in Monospace",
    )
    check("FACTION LEADERBOARD" in text, "the panel shows the board")
    line = lbt_line(text)
    check(
        line.startswith(f"{rank:>2}  LBT") and line.endswith(f"{value:,} {pcu:>7,}"),
        f"LBT's row: {line!r}",
    )
    board_lines = text.splitlines()[1:]
    check(all(len(l) <= 45 for l in board_lines), "every line fits 45 chars")
    at = PANEL_AT.search(text)
    if SHOTS and at:
        screenshots(remote, [float(v) for v in at.groups()])
    log_text = rig.server_game_log()
    written = re.findall(
        r"Leaderboard: (\d+) factions on (\d+) of (\d+) panels", log_text
    )
    check(
        any(int(w[2]) == PANELS for w in written),
        f"all {PANELS} station panels found: {written[:3]}",
    )

    # The panels refresh on their own: the ship gone, LBT has nothing
    probe(remote, "undock")
    ending = f" 0 {0:>7}"
    line = wait_panel(remote, ending, timeout=6 * INTERVAL)
    check(line.endswith(ending), f"refreshed without the ship: {line!r}")

    counts = [tuple(map(int, m)) for m in COUNTED.findall(rig.server_game_log())]
    log(f"counts (grids, ticks, ms of work): {counts[-5:]}")
    check(bool(counts), "the counts are logged")
    if counts:
        grids, ticks, work = counts[-1]
        check(grids > 20, f"the whole world counted: {grids} grids")
        check(
            work / max(1, ticks) <= 5,
            f"at most 5 ms per tick: {work} ms in {ticks} ticks",
        )


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[kiosks.PROBE, LB_PROBE])
    config = rig.CONFIG_SOURCE.read_text(encoding="utf-8").replace(
        "</OctahedronConfig>",
        f"  <LeaderboardIntervalSeconds>{INTERVAL}</LeaderboardIntervalSeconds>\n"
        "</OctahedronConfig>",
    )
    rig.write_plugin_config(config)
    rig.start_server()
    remote = rig.start_client(render=bool(SHOTS))
    log("joined")
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Critical" in line):
            log("server: " + line.strip())
    failed = [what for passed, what in kiosks.checks if not passed]
    print(
        f"{len(kiosks.checks) - len(failed)}/{len(kiosks.checks)} leaderboard checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
