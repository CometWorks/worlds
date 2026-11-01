"""The shipyard builds only the blocks of the DLCs the ordering player owns (SE-805). The shipyard's
welders are the station's, which own no DLC.

- A player without DLCs gets the ship without its DLC blocks, told so, and it is finished: no
  component goes to a block it can't place.
- A player owning them (!sprobe owndlc, as the server sees a Steam client that owns them) gets all
  of the ship, welded by the same welders.

    uv run --no-project --with httpx python tests/test_shipyard_dlc.py
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-805")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_client as tc  # noqa: E402
import test_shipyard_order as so  # noqa: E402

NAME = "Dlc Test"
DLC = [
    ("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlockWarfare2", (0, 1, 2)),
    ("MyObjectBuilder_ButtonPanel", "LargeSciFiButtonPanel", (0, 1, 4)),
]
BLUEPRINT = rig.grid(
    NAME,
    777000805000100,
    [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
        *(
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, z))
            for z in range(8)
        ),
        *(rig.block(*block) for block in DLC),
    ],
    tag="MyObjectBuilder_CubeGrid",
)
PLAIN = 9
failed = []


def check(ok, what) -> None:
    print(("PASS " if ok else "FAIL ") + str(what)[:1000], flush=True)
    if not ok:
        failed.append(what)


def ship_subtypes() -> list[str]:
    sector, _ = so.saved()
    ship = next(
        o
        for o in sector.find("SectorObjects")
        if o.findtext("DisplayName") == NAME and o.find("CubeBlocks") is not None
    )
    return [b.findtext("SubtypeName") or "" for b in ship.find("CubeBlocks")]


def run(owned: bool) -> None:
    rig.stop_client()
    rig.stop_server()
    so.prepare(BLUEPRINT, 60)
    rig.start_server()
    remote = rig.start_client()
    try:
        table = min(tc.tables(remote), key=lambda b: b["distance"])
        tc.stand_by(remote, table)
        if owned:
            reply = st.probe(remote, "owndlc")
            check("OWNDLC" in reply, f"owns every DLC: {reply}")
        quote = tc.ship_reply(remote, "!oct order")
        check("Your quote for" in quote, f"quote: {quote}")
        check(
            ("Left out" in quote) != owned,
            f"{'nothing' if owned else 'the DLC blocks'} left out: {quote}",
        )
        reply = tc.ship_reply(remote, "!oct order")
        check("is in the queue" in reply, f"ordered: {reply}")
        ready = so.wait_chat(remote, "is ready", 600)
        check(True, ready)
    finally:
        rig.stop_client()
        rig.stop_server()
    so.check_ship(NAME, PLAIN + (len(DLC) if owned else 0), complete=True)
    subtypes = ship_subtypes()
    wanted = {block[1] for block in DLC}
    check(
        wanted <= set(subtypes) if owned else not wanted & set(subtypes),
        f"DLC blocks {'built' if owned else 'not built'}: {sorted(subtypes)}",
    )
    log = rig.server_game_log()
    check(" no progress " not in log, "the order did not stall")
    check(
        not re.search(r"Octahedron.*(Exception|Critical)", log),
        "no plugin errors",
    )


if __name__ == "__main__":
    started = time.monotonic()
    try:
        run(owned=False)
        run(owned=True)
    finally:
        rig.stop_client()
        rig.stop_server()
    print(f"{time.monotonic() - started:.0f} s", flush=True)
    print("FAILED" if failed else "OK", flush=True)
    sys.exit(1 if failed else 0)
