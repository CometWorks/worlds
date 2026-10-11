"""Reads the test server's saved world: where the world edge test grids ended up,
and what the refill put into the stations' blocks.

    python tests/check_save.py [world folder]
"""

from __future__ import annotations

import math
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

from edge_grids import EDGE_GRIDS

XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
RADIUS, BRAKING, SPEED, EXPONENT = 500_000, 450_000, 360, 0.5


def limit(distance: float) -> float:
    if distance >= RADIUS:
        return max(RADIUS - distance, -10)
    return SPEED * min(1, (RADIUS - distance) / (RADIUS - BRAKING)) ** EXPONENT


def vec(element) -> tuple[float, float, float]:
    return tuple(float(element.get(k, 0)) for k in "xyz")


def edge(sector) -> None:
    ids = {i: name for name, (i, _, _) in EDGE_GRIDS.items()}
    for entity in sector.find("SectorObjects"):
        entity_id = int(entity.findtext("EntityId") or 0)
        if entity_id not in ids:
            continue
        p = vec(entity.find("PositionAndOrientation/Position"))
        v = (
            vec(entity.find("LinearVelocity"))
            if entity.find("LinearVelocity") is not None
            else (0, 0, 0)
        )
        r = math.dist(p, (0, 0, 0))
        out = sum(a * b for a, b in zip(v, p)) / r
        side = math.sqrt(max(0, sum(a * a for a in v) - out * out))
        print(
            f"{ids[entity_id]}: {r / 1000:.3f} km, outward {out:.2f} m/s, sideways {side:.2f} m/s, "
            f"limit {limit(r):.2f} m/s"
        )


def refill(sector) -> None:
    """Items in the stations' named blocks and how many unnamed containers got loot"""
    named = Counter()
    for block in sector.iter("MyObjectBuilder_CubeBlock"):
        name = (block.findtext("CustomName") or "").strip()
        kind = block.get(XSI_TYPE, "")
        items = Counter()
        for item in block.iter("MyObjectBuilder_InventoryItem"):
            content = item.find("PhysicalContent")
            key = f"{content.get(XSI_TYPE, '').replace('MyObjectBuilder_', '')}/{content.findtext('SubtypeName')}"
            items[key] += float(item.findtext("Amount") or 0)
        if name in ("Station Loot", "Free Tools", "Shipyard Reactor"):
            named[(name, tuple(sorted(items.items())))] += 1
        elif (
            kind == "MyObjectBuilder_CargoContainer"
            and items
            and "Owner" in ET.tostring(block, encoding="unicode")
        ):
            named[("other cargo with items", ())] += 1
        if kind == "MyObjectBuilder_OxygenGenerator" and items:
            named[("gas generator with items", tuple(sorted(items.items())))] += 1
    for (name, items), count in sorted(named.items()):
        shown = ", ".join(f"{k} {v:g}" for k, v in items)
        print(f"{count} x {name}: {shown}")


def starter(sector) -> None:
    """The starter ships: distance past the nearest safe zone, which way they face, owner,
    battery charge and cargo"""
    zones = []
    for entity in sector.find("SectorObjects"):
        if entity.get(XSI_TYPE) == "MyObjectBuilder_SafeZone":
            zones.append(
                (
                    vec(entity.find("PositionAndOrientation/Position")),
                    float(entity.findtext("Radius")),
                )
            )
    for entity in sector.find("SectorObjects"):
        if entity.findtext("DisplayName") != "Starter Test":
            continue
        p = vec(entity.find("PositionAndOrientation/Position"))
        f = vec(entity.find("PositionAndOrientation/Forward"))
        center, radius = min(zones, key=lambda z: math.dist(z[0], p))
        away = [a - b for a, b in zip(p, center)]
        facing = sum(a * b for a, b in zip(f, away)) / math.dist(p, center)
        owners = {
            (b.findtext("Owner"), b.findtext("BuiltBy"))
            for b in entity.iter("MyObjectBuilder_CubeBlock")
        }
        charge = [
            b.findtext("CurrentStoredPower")
            for b in entity.iter("MyObjectBuilder_CubeBlock")
            if b.get(XSI_TYPE) == "MyObjectBuilder_BatteryBlock"
        ]
        items = sum(1 for _ in entity.iter("MyObjectBuilder_InventoryItem"))
        print(
            f"Starter Test {entity.findtext('EntityId')}: {math.dist(p, center) - radius:.0f} m past a {radius:.0f} m safe zone, "
            f"facing away {facing:.3f} (cos), owner/built by {owners}, battery {charge} MWh, {items} items"
        )


if __name__ == "__main__":
    world = Path(
        sys.argv[1]
        if len(sys.argv) > 1
        else Path.home() / ".se-test/SE1-0133-ds0/data/Saves/Octahedron"
    )
    sector = ET.parse(world / "SANDBOX_0_0_0_.sbs").getroot()
    edge(sector)
    refill(sector)
    starter(sector)
