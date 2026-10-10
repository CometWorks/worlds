#!/usr/bin/env python3
"""Generates the OctahedronOres mod from the game's own definitions.

    make_ore_mod.py [output folder, default: OctahedronOres next to this script]

- Moon: no gold, silver or platinum. Its gold ore map values (about as many
  pixels as its magnesium ones) become uranium 50-100 m deep.
- Asteroids: no gold, silver or uranium. Meteorites don't bring them either.
- EarthLike already has gold and silver and neither uranium nor platinum, so it
  stays vanilla.

Rerun it after a game update that changes these definitions.
"""

import copy
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

DATA = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers/Content/Data"
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name("OctahedronOres")

# Moon ore map value -> (start depth, deposit depth) of the uranium replacing its gold
MOON_URANIUM = {168: (50, 6), 172: (55, 8), 176: (60, 10), 180: (70, 10), 184: (80, 12), 188: (88, 12)}
MOON_REMOVED = {"Silver_01", "Platinum_01"}
NOT_IN_ASTEROIDS = ("Gold_01", "Silver_01", "Uraninite_01")

XSI = "http://www.w3.org/2001/XMLSchema-instance"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")

HEADER = '<?xml version="1.0"?>\n'


def definitions(tag):
    root = ET.Element("Definitions")
    return root, ET.SubElement(root, tag)


def write(root, path):
    ET.indent(root, "  ")
    text = ET.tostring(root, encoding="unicode")
    text = text.replace("<Definitions", f'<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema"', 1)
    if "xmlns:xsi=" not in text[:300]:
        text = text.replace("<Definitions", f'<Definitions xmlns:xsi="{XSI}"', 1)
    path.write_text(HEADER + text + "\n", encoding="utf-8")


def moon():
    vanilla = ET.parse(DATA / "PlanetGeneratorDefinitions.sbc").getroot()
    moon = next(d for d in vanilla.iter("Definition") if d.findtext("Id/SubtypeId") == "Moon")
    moon = copy.deepcopy(moon)
    mappings = moon.find("OreMappings")
    for ore in list(mappings):
        value = int(ore.get("Value"))
        if ore.get("Type") in MOON_REMOVED:
            mappings.remove(ore)
        elif value in MOON_URANIUM:
            assert ore.get("Type") == "Gold_01", ore.attrib
            start, depth = MOON_URANIUM[value]
            ore.set("Type", "Uraninite_01")
            ore.set("Start", str(start))
            ore.set("Depth", str(depth))
    assert not any(ore.get("Type") in ("Gold_01", "Silver_01", "Platinum_01") for ore in mappings)
    root = ET.Element("Definitions")
    root.append(moon)
    return root


def asteroid_materials():
    vanilla = ET.parse(DATA / "VoxelMaterials_asteroids.sbc").getroot()
    root, parent = definitions("VoxelMaterials")
    for material in vanilla.iter("VoxelMaterial"):
        if material.findtext("Id/SubtypeId") not in NOT_IN_ASTEROIDS:
            continue
        material = copy.deepcopy(material)
        for tag in ("SpawnsInAsteroids", "SpawnsFromMeteorites"):
            node = material.find(tag)
            if node is None:
                node = ET.Element(tag)
                # Right after IsRare, where vanilla puts them
                material.insert(list(material).index(material.find("IsRare")) + 1, node)
            node.text = "false"
        parent.append(material)
    assert len(parent) == len(NOT_IN_ASTEROIDS)
    return root


def main():
    shutil.rmtree(OUT / "Data", ignore_errors=True)
    (OUT / "Data").mkdir(parents=True)
    write(moon(), OUT / "Data/PlanetGeneratorDefinitions.sbc")
    write(asteroid_materials(), OUT / "Data/VoxelMaterials_asteroids.sbc")
    print(OUT)


if __name__ == "__main__":
    main()
