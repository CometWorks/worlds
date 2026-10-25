"""Checks Hangar's retrieval fee with the headless client. Hangar (OCT_HANGAR_REPO, a
working copy of CometWorks/hangar 1.1.0 or later) runs on the server with
AdminSafeZonesOnly, as on the world, and holds ships of the player's, stored as if
saved in a station's safe zone. The client spawns at a station:

- Outside every safe zone a load is refused and the ship stays stored.
- The ship saved at this station comes back free.
- The ship saved at another station costs 1 SC per 10 PCU, paid to OCT: 100 SC for
  1,000 PCU. It lands inside this station's zone.
- A ship of 1,000,000 PCU from another station costs 100,000 SC, more than the
  player has, and is refused; with HangarRetrieveFeePer10Pcu 0 it comes back free.

The entries' PCU is what Hangar recorded at the save; the stored grids themselves are
a small ship.

    OCT_TASK=SE-686 OCT_HANGAR_REPO=<hangar working copy> OCT_WORLD_SOURCE=<world> \\
        uv run --no-project --with httpx python tests/test_hangar_fee.py
"""

from __future__ import annotations

import math
import os
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-686")
import rig  # noqa: E402
import test_client as tc  # noqa: E402
from se_remote import CallOp  # noqa: E402
from test_money import Ledger, money, wait_log  # noqa: E402

ZONE_RADIUS = 250.0
STARTING_BALANCE = 25_000
OUTSIDE_LOAD = "You can only load grids inside a station's safe zone."
BLOBS = rig.SERVER_DATA / "HangarFeeBlobs"
# Grid name -> (PCU recorded at the save, saved here or elsewhere)
SHIPS = {
    "FeeHome": (1_000, "here"),
    "FeeFar": (1_000, "elsewhere"),
    "FeeDear": (1_000_000, "elsewhere"),
}


def safe_zones() -> dict[str, tuple[float, float, float]]:
    """The world's admin safe zones, name -> center"""
    sector = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    zones = {}
    for entity in sector.find("SectorObjects"):
        if entity.findtext("SafeZoneBlockId") == "0":
            p = entity.find("PositionAndOrientation/Position")
            zones[entity.findtext("DisplayName")] = tuple(
                float(p.get(a)) for a in "xyz"
            )
    assert len(zones) == 7, zones
    return zones


def hangar_config(entries: str) -> str:
    return (
        '<?xml version="1.0" encoding="utf-8"?><PluginConfig>'
        "<AdminSafeZonesOnly>true</AdminSafeZonesOnly>"
        f"<HangarEntries>{entries}</HangarEntries></PluginConfig>"
    )


def entry(name: str, pcu: int, zone: str) -> str:
    return (
        f"<HangarEntry><Id>fee-{name.lower()}</Id><Scope>Player</Scope>"
        f"<OwnerId>{rig.CLIENT_ID}</OwnerId><OwnerName>{rig.CLIENT_NAME}</OwnerName>"
        f"<GridName>{name}</GridName><SavedUtc>2026-10-25T00:00:00Z</SavedUtc>"
        f"<GridCount>1</GridCount><Blocks>4</Blocks><Pcu>{pcu}</Pcu>"
        f"<BlobPath>{BLOBS / (name + '.sbc')}</BlobPath><Zone>{zone}</Zone>"
        "</HangarEntry>"
    )


def write_blob(name: str, position) -> None:
    """A stored small ship at position, as Hangar saves a grid"""
    grid = rig.grid(name, None, rig.small_ship_blocks(), position, tag="CubeGrid")
    BLOBS.mkdir(parents=True, exist_ok=True)
    (BLOBS / f"{name}.sbc").write_text(
        '<?xml version="1.0"?><Definitions '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShipBlueprints><ShipBlueprint>'
        f'<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="{name}" />'
        f"<CubeGrids>{grid}</CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>",
        encoding="utf-8",
    )


def toward(origin, target, meters: float):
    d = math.dist(origin, target)
    return [o + meters * (t - o) / d for o, t in zip(origin, target)]


def load(remote, name: str) -> str:
    return tc.ship_reply(remote, f"!hangar load {name}")


def stored(remote) -> str:
    return tc.ship_reply(remote, "!hangar list")


def grid_position(remote, name: str):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        for grid in remote.list_grids():
            if grid.get("name") == name:
                result = remote.call(
                    [CallOp.grid_to_world(grid["entityId"], (0, 0, 0))]
                )
                world = result.call(0)
                position = world.get("world") or world.get("position")
                tc.log(f"{name} at {position}")
                return position
        time.sleep(2)
    raise AssertionError(f"{name} is not in the world")


def main() -> None:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    zones = safe_zones()
    rig.prepare_server(extra_dev_folders=[tc.HANGAR])
    # Entries at every station, so the "here" ones match wherever the client spawns
    entries = "".join(
        entry(f"{name}{zone.split()[0]}", pcu, zone)
        for zone in zones
        for name, (pcu, _) in SHIPS.items()
    )
    (rig.SERVER_DATA / "Hangar.cfg").write_text(
        hangar_config(entries), encoding="utf-8"
    )
    rig.start_server()
    tc.log("server ready")
    remote = rig.start_client()

    home = remote.get_character()["position"]
    here = min(zones, key=lambda z: math.dist(zones[z], home))
    center = zones[here]
    assert math.dist(center, home) < ZONE_RADIUS, f"spawned outside {here}'s zone"
    elsewhere = next(z for z in zones if z != here)
    tc.log(f"{math.dist(center, home):.0f} m from the zone's center")
    tc.log(f"spawned in {here}'s safe zone, the other station is {elsewhere}")
    names = {
        name: f"{name}{(here if where == 'here' else elsewhere).split()[0]}"
        for name, (_, where) in SHIPS.items()
    }
    # Stored 60 m further out from the zone's center than the player, off the station
    for name in names.values():
        write_blob(name, toward(home, center, -60))

    # Outside every zone the load is refused
    remote.character_teleport(*toward(center, home, 2 * ZONE_RADIUS))
    time.sleep(4)
    assert load(remote, names["FeeHome"]) == OUTSIDE_LOAD
    assert names["FeeHome"] in stored(remote)
    remote.character_teleport(*home)
    time.sleep(4)

    ledger = Ledger(STARTING_BALANCE)
    balances = money(remote)
    assert balances["You"] == STARTING_BALANCE, balances
    oct = balances["OCT"]

    reply = load(remote, names["FeeHome"])
    assert reply == f"Loaded {names['FeeHome']} from hangar.", reply
    assert math.dist(grid_position(remote, names["FeeHome"]), center) < ZONE_RADIUS
    ledger.payouts()
    assert money(remote) == {"OCT": oct, "You": ledger.balance}, "same station free"

    reply = load(remote, names["FeeFar"])
    paid = f"You paid 100 SC to bring your ship here, it was stored at {elsewhere}."
    assert paid in reply and f"Loaded {names['FeeFar']} from hangar." in reply, reply
    assert math.dist(grid_position(remote, names["FeeFar"]), center) < ZONE_RADIUS
    ledger.balance -= 100
    ledger.payouts()
    assert money(remote) == {"OCT": oct + 100, "You": ledger.balance}, "100 SC to OCT"
    wait_log(f"paid 100 SC for 1000 PCU from '{elsewhere}' to '{here}'")

    reply = load(remote, names["FeeDear"])
    expected = (
        f"Your ship was stored at {elsewhere}. Bringing it here costs 100,000 SC "
        f"(1,000,000 PCU), you have {ledger.balance:,} SC."
    )
    assert reply == expected, reply
    assert names["FeeDear"] in stored(remote)
    ledger.payouts()
    assert money(remote) == {"OCT": oct + 100, "You": ledger.balance}, "nothing taken"

    tc.with_config(remote, HangarRetrieveFeePer10Pcu=0)
    reply = load(remote, names["FeeDear"])
    assert reply == f"Loaded {names['FeeDear']} from hangar.", reply
    ledger.payouts()
    assert money(remote) == {"OCT": oct + 100, "You": ledger.balance}, "fee 0 is free"

    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if ("Octahedron" in line or "Hangar" in line)
        and ("Exception" in line or "Critical" in line)
    ]
    assert not errors, errors


if __name__ == "__main__":
    try:
        main()
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed")
