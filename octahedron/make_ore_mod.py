#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Generates the OctahedronOres mod from the game's own definitions and ore maps.

    uv run make_ore_mod.py [--seed 42] [output folder, default: OctahedronOres next to this script]

- Moon: no gold, silver or platinum. Its gold ore map values (about as many
  pixels as its magnesium ones) become uranium 50-100 m deep.
- Asteroids: no gold, silver or uranium. Meteorites don't bring them either.
- EarthLike already has gold and silver and neither uranium nor platinum, so its
  ore rules stay vanilla.
- EarthLike and the Moon get their own ore maps: every deposit of the vanilla
  maps moves to a random free spot on the same cube face, keeping its shape and
  ores. The seed picks the spots. Use the seed of the world the mod goes with
  (build_world.py), so a world built with another seed has its ores elsewhere,
  on the planets as well as in the asteroids.

Rerun it after a game update that changes these definitions or ore maps.
"""

import argparse
import copy
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

DATA = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers/Content/Data"
FACES = ("front", "back", "left", "right", "up", "down")
PLANETS = ("EarthLike", "Moon")
# Blue channel of a <face>_mat.png: the ore map value, 255 where there is no ore
ORE_CHANNEL = 2
NO_ORE = 255

# Moon ore map value -> (start depth, deposit depth) of the uranium replacing its gold
MOON_URANIUM = {168: (50, 6), 172: (55, 8), 176: (60, 10), 180: (70, 10), 184: (80, 12), 188: (88, 12)}
MOON_REMOVED = {"Silver_01", "Platinum_01"}
NOT_IN_ASTEROIDS = ("Gold_01", "Silver_01", "Uraninite_01")

XSI = "http://www.w3.org/2001/XMLSchema-instance"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


def write(root, path):
    ET.indent(root, "  ")
    text = ET.tostring(root, encoding="unicode")
    text = text.replace("<Definitions", '<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema"', 1)
    if "xmlns:xsi=" not in text[:300]:
        text = text.replace("<Definitions", f'<Definitions xmlns:xsi="{XSI}"', 1)
    path.write_text('<?xml version="1.0"?>\n' + text + "\n", encoding="utf-8")


def planets():
    """EarthLike as is and the Moon with its new ore rules. Both must come from the
    mod, or the game reads their ore maps from its own content."""
    vanilla = ET.parse(DATA / "PlanetGeneratorDefinitions.sbc").getroot()
    root = ET.Element("Definitions")
    for definition in vanilla.iter("Definition"):
        if definition.findtext("Id/SubtypeId") in PLANETS:
            root.append(copy.deepcopy(definition))
    assert len(root) == len(PLANETS)
    moon = next(d for d in root if d.findtext("Id/SubtypeId") == "Moon")
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
    return root


def asteroid_materials():
    vanilla = ET.parse(DATA / "VoxelMaterials_asteroids.sbc").getroot()
    root = ET.Element("Definitions")
    parent = ET.SubElement(root, "VoxelMaterials")
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


def scatter(ores, rng):
    """Moves every deposit (a connected patch of ore pixels) to a random spot on the
    face where it touches no other deposit."""
    size = ores.shape[0]
    labels, _ = ndimage.label(ores != NO_ORE)
    out = np.full_like(ores, NO_ORE)
    taken = np.zeros(ores.shape, bool)
    for index, box in enumerate(ndimage.find_objects(labels), 1):
        mask = labels[box] == index
        # One pixel of margin, so moved deposits don't merge
        margin = ndimage.binary_dilation(np.pad(mask, 1))
        height, width = margin.shape
        for _ in range(1000):
            y, x = rng.integers(0, size - height + 1), rng.integers(0, size - width + 1)
            if not (taken[y : y + height, x : x + width] & margin).any():
                break
        else:
            raise RuntimeError("No free spot for a deposit")
        taken[y : y + height, x : x + width] |= margin
        out[y + 1 : y + height - 1, x + 1 : x + width - 1][mask] = ores[box][mask]
    return out


def ore_maps(out, rng):
    for planet in PLANETS:
        folder = out / "Data/PlanetDataFiles" / planet
        folder.mkdir(parents=True)
        for face in FACES:
            image = Image.open(DATA / "PlanetDataFiles" / planet / f"{face}_mat.png")
            pixels = np.array(image)
            before = np.unique(pixels[:, :, ORE_CHANNEL], return_counts=True)
            pixels[:, :, ORE_CHANNEL] = scatter(pixels[:, :, ORE_CHANNEL], rng)
            after = np.unique(pixels[:, :, ORE_CHANNEL], return_counts=True)
            assert all((a == b).all() for a, b in zip(before, after)), f"{planet} {face}: ore pixels changed"
            Image.fromarray(pixels, image.mode).save(folder / f"{face}_mat.png", optimize=True)
            print(f"{planet} {face}_mat.png")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=Path, nargs="?", default=Path(__file__).with_name("OctahedronOres"))
    parser.add_argument("--seed", type=int, default=42, help="ore map seed, the same as the world's")
    args = parser.parse_args()

    out = args.output
    shutil.rmtree(out / "Data", ignore_errors=True)
    (out / "Data").mkdir(parents=True)
    write(planets(), out / "Data/PlanetGeneratorDefinitions.sbc")
    write(asteroid_materials(), out / "Data/VoxelMaterials_asteroids.sbc")
    ore_maps(out, np.random.default_rng(args.seed))
    print(out)


if __name__ == "__main__":
    main()
