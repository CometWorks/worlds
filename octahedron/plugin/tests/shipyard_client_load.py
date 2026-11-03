"""A client loads a blueprint into the hologram table, with or without Multigrid Projector on the client, and
orders it. The delivered cockpit's toolbar must point at the ship's blocks (SE-772). load_projection calls
MyProjectorBase.InitFromObjectBuilder as the blueprint screen does; a blueprint stored in the world doesn't go
through it. See tests/shipyard_cluster.py for the same on a cluster.

    OCT_WORLD_SOURCE=<world> OCT_MGP_REPO=<MGP working copy> \\
        uv run --no-project --with httpx python tests/shipyard_client_load.py mgp|vanilla bp.sbc

Prints one line per check; exit code 0 = all passed.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

import rig  # noqa: E402
import se_autopilot as ap  # noqa: E402
import test_client as tc  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as arm  # noqa: E402
from se_remote import CallOp  # noqa: E402

MODE, BLUEPRINT = sys.argv[1], Path(sys.argv[2])
MGP = (
    pl.MGP[0],
    pl.MGP[1],
    "MultigridProjectorClient.xml",
    "viktor-ferenczi/se-multigrid-projector",
)
failed = []


def check(ok, what):
    print(("PASS " if ok else "FAIL ") + str(what)[:1500], flush=True)
    if not ok:
        failed.append(what)


prepare_client = rig.prepare_client


def client_with_mgp(extra_dev_folders=()):
    prepare_client(extra_dev_folders)
    legacy = rig.PULSAR / "Legacy"
    if MODE == "mgp":
        (legacy / "Profiles" / "Current.xml").write_text(
            rig.profile_xml(["remote", "direct-transport", MGP[3]]), encoding="utf-8"
        )
        rig.register_source(legacy / "Sources" / "sources.xml", MGP[0], MGP[1], MGP[2])
    target = rig.APPDATA / "Blueprints/local/Loaded"
    target.mkdir(parents=True, exist_ok=True)
    (target / "bp.sbc").write_text(
        BLUEPRINT.read_text(encoding="utf-8"), encoding="utf-8"
    )
    (target / "bp.sbcB5").unlink(missing_ok=True)  # the game's binary copy would win


def load(remote):
    table = min(tc.tables(remote), key=lambda b: b["distance"])
    block = remote.get_block(table["gridId"], tuple(table["min"]))
    print(remote.load_projection(block["entityId"], name="Loaded"), flush=True)
    time.sleep(10)
    print(
        remote.get_block(table["gridId"], tuple(table["min"]))["projector"], flush=True
    )
    tc.stand_by(remote, table)


def main() -> int:
    rig.prepare_client = client_with_mgp
    rig.stop_client()
    rig.stop_server()
    so.prepare(arm.TABLES, 60)
    rig.start_server()
    remote = rig.start_client()
    try:
        rig.ensure_character(remote)
        # An admin ignoring PCU limits orders any ship as a free build
        remote.set_admin_flag("ignorePcu", True)
        time.sleep(3)  # the server gets the flag with the admin menu's event
        tc.with_config(remote, FreeBuildPcuLimit=100000, ShipyardRepasteShips="true")
        arm.at_table(remote, "Gamma")
        load(remote)
        reply = ""
        for _ in range(4):
            reply = arm.admin(remote, "!oct order", 8)
            print(reply, flush=True)
            if "is in the queue" in reply:
                break
        check("is in the queue" in reply, f"ordered: {reply}")
        if "is in the queue" not in reply:
            return 1
        deadline = time.time() + float(os.environ.get("OCT_BLUEPRINT_SECONDS", 1500))
        line = None
        ended = None
        while time.time() < deadline and not line and not ended:
            log = rig.server_game_log()
            line = re.search(r".*off the arm, pasted anew as (\d+).*", log)
            ended = re.search(r".*Shipyard order \S+ .* ended: .*", log)
            time.sleep(10)
        for stalled in re.findall(r".*Shipyard order \S+ stalled:.*", log):
            print(stalled, flush=True)
        check(line, f"re-pasted: {line and line[0]} {ended and ended[0]}")
        if not line:
            return 1
        ship = int(line[1])
        time.sleep(20)
        cockpit = ap.find_cockpit(remote, ship)
        block = remote.get_block(ship, tuple(cockpit["min"]))
        xml = remote.get_object_builder(block["entityId"])
        refs = set(re.findall(r"<BlockEntityId>(\d+)", xml))
        cockpit_id = block["entityId"]
        groups = remote.list_block_groups(ship)
        check(groups, f"groups: { {k: len(v) for k, v in groups.items()} }")
        print(
            [i.get("name") for i in remote.get_toolbar().get("items", [])], flush=True
        )
    finally:
        rig.stop_client()
        rig.stop_server()
    # Slots may be on any of the ship's grids
    sector, _ = so.saved()
    ids = {e.text for e in sector.findall("./SectorObjects/*/CubeBlocks/*/EntityId")}
    # Not every ship has slots on the cockpit this finds, but toolbars emptied by the paste are wrong
    slots = sector.findall(f"./SectorObjects/*[EntityId='{ship}']//BlockEntityId")
    check(
        slots and refs <= ids,
        f"{len(slots)} slots on the ship, cockpit {cockpit_id}: on {sorted(refs)}, "
        f"off the ship: {sorted(refs - ids)}",
    )
    print("FAILED" if failed else "OK", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
