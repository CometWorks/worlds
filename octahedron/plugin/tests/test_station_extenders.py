"""A player's ship docks at a Station Extender's connector and trades with the station's Store from
its cargo, the way it does at the station's own connectors (SE-1388). On a world built by
build_world.py with the extenders merged into the stations:
- Alpha Station has 20 connectors, all trading, the extenders' among them
- the world lists the extender patches in the station's ledger, so the server doesn't try them
- at a side connector and the top connector of the extenders a ship docks, the Store screen lists
  its inventories, and a sale from its cargo and a purchase into it go through

    OCT_TASK=SE-1388 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_station_extenders.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed. About 6 minutes.
The cluster side, the patch reaching a kept world, is tests/station_extenders_cluster.py.
"""

from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1388")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check, probe, stand  # noqa: E402
from test_client import log  # noqa: E402
from test_store import store  # noqa: E402

STATION = "Alpha"
IRON = "Ingot/Iron"
PLATE = "Component/SteelPlate"
CONFIG = Path(__file__).resolve().parents[2] / "config"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"


def extender_connectors(grid: str) -> list[tuple[str, str]]:
    """(x;y;z, subtype) of the connectors the station's extender patch adds"""
    root = ET.parse(CONFIG / "octahedron.cfg").getroot()
    entry = next(
        e
        for e in root.iterfind("StationPatches/StationPatchEntry")
        if e.findtext("Grid") == grid and "station-extenders" in e.findtext("Id")
    )
    tree = ET.parse(CONFIG / "storage/Octahedron" / entry.findtext("To"))
    found = []
    for block in tree.getroot().iter("MyObjectBuilder_CubeBlock"):
        if block.get(XSI) == "MyObjectBuilder_ShipConnector":
            m = block.find("Min")
            found.append(
                (";".join(m.get(a, "0") for a in "xyz"), block.findtext("SubtypeName"))
            )
    return found


def item(remote, ship: str, name: str) -> float:
    m = re.search(rf"ITEM {ship} (\d+)", probe(remote, f"griditem {ship} {name}"))
    return float(m[1]) if m else -1.0


def balance(remote) -> int:
    m = re.search(r"BALANCE (-?\d+)", probe(remote, "balance"))
    return int(m[1]) if m else -1


def trades_at(remote, cell: str, subtype: str) -> None:
    reply = probe(remote, f"dock {STATION} mine {IRON}=1000,AT={cell}")
    ship = re.search(r"SHIP (\d+)", reply)
    check(ship is not None, f"a ship at the extender's {subtype} at {cell}: {reply}")
    if not ship:
        return
    ship = ship[1]
    check(bool(st.wait_docked(remote, ship)), f"ship {ship} docks")
    lines = store(remote, "ships")
    check(any(l.startswith("SHIPS ") and l != "SHIPS " for l in lines), f"the Store lists its inventories: {lines}")  # fmt: skip
    money = balance(remote)
    answer = " ".join(store(remote, f"sellship {IRON} 100"))
    check("SELL Success" in answer, f"a sale from its cargo: {answer}")
    st.wait_until(lambda: item(remote, ship, IRON) == 900, 15)
    check(item(remote, ship, IRON) == 900, "the iron left its cargo")
    st.wait_until(lambda: balance(remote) > money, 15)
    check(balance(remote) > money, f"and paid: {money} -> {balance(remote)}")
    answer = " ".join(store(remote, f"buyship {PLATE} 10"))
    check("BUY Success" in answer, f"a purchase into its cargo: {answer}")
    st.wait_until(lambda: item(remote, ship, PLATE) == 10, 15)
    check(item(remote, ship, PLATE) == 10, "the plates are in its cargo")
    log(probe(remote, "undock"))


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.write_plugin_config()
    try:
        rig.start_server()
        remote = rig.start_client(extra_dev_folders=[st.STORE_PROBE])
        rig.ensure_character(remote)
        stand(remote, STATION)
        probe(remote, "balance 100000")
        reply = probe(remote, f"connectors {STATION}")
        cells = re.findall(r"CONNECTOR \d+ (\S+) trading=(\w+)", reply)
        check(len(cells) == 20 and all(t == "yes" for _, t in cells), f"20 connectors, all trading: {cells}")  # fmt: skip
        ours = extender_connectors(f"{STATION} Station")
        want = {c.replace(";", ",") for c, _ in ours}
        check(
            want <= {c for c, _ in cells},
            f"the extenders' {len(want)} connectors among them",
        )
        picked = {}
        for cell, subtype in ours:
            picked.setdefault(subtype, cell)
        for subtype in ("LargeBlockInsetConnector", "Connector"):
            trades_at(remote, picked[subtype], subtype)
    finally:
        rig.stop_client()
        rig.stop_server()
    text = rig.server_game_log()
    tried = [
        l
        for l in text.splitlines()
        if "Station patch" in l and "station-extenders" in l
    ]
    check(not tried, f"the server doesn't try the patches the world has: {tried[:3]}")
    errors = [
        line
        for line in text.splitlines()
        if "Octahedron" in line and ("xception" in line or "Critical" in line)
    ]
    check(not errors, f"no plugin errors: {errors[:3]}")
    return st.summary("station extenders")


if __name__ == "__main__":
    sys.exit(main())
