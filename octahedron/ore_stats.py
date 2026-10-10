#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Writes the ore statistics of an Octahedron world as Markdown.

    uv run ore_stats.py <world folder> <OctahedronOres folder> <output .md> [--seed N]

EarthLike and the Moon are counted from the mod's ore maps: every ore pixel is a
column of ore rock, as wide as the pixel is on the planet's surface and as deep
as its ore mapping says. The asteroid belt is an estimate: the game generates
the asteroids' deposits from their seeds with its own random generator, so the
script takes the expected values of generator 4's rules (MyCompositeShapes)
for the belt's asteroid sizes.

Amounts are mined ore in tonnes with a ship drill (0.009 x the voxel material's
MinedOreRatio x the world's HarvestRatioMultiplier, per m3 of ore rock, divided
by the ore item's volume), before refining.
"""

import argparse
import gzip
import random
import struct
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

CONTENT = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers/Content"
FACES = ("front", "back", "left", "right", "up", "down")
NO_ORE = 255
SHIP_DRILL_RATIO = 0.009
VOXEL_GENERATOR = 4
SAMPLES = 200_000


def definitions(path):
    return ET.parse(path).getroot()


def voxel_materials(mod):
    """Subtype -> (mined ore, MinedOreRatio, SpawnsInAsteroids, MinVersion, MaxVersion, spawn multiplier)"""
    materials = {}
    paths = sorted((CONTENT / "Data").glob("VoxelMaterials*.sbc")) + sorted(
        (mod / "Data").glob("VoxelMaterials*.sbc")
    )
    for path in paths:
        for material in definitions(path).iter("VoxelMaterial"):
            materials[material.findtext("Id/SubtypeId")] = (
                material.findtext("MinedOre"),
                float(material.findtext("MinedOreRatio") or 1),
                (material.findtext("SpawnsInAsteroids") or "true") == "true",
                int(material.findtext("MinVersion") or 0),
                int(material.findtext("MaxVersion") or 2**31),
                int(
                    material.findtext("AsteroidGeneratorSpawnProbabilityMultiplier")
                    or 1
                ),
            )
    return materials


def ore_volumes():
    """Ore subtype -> item volume in m3"""
    volumes = {}
    for item in definitions(CONTENT / "Data/PhysicalItems.sbc").iter("PhysicalItem"):
        if item.findtext("Id/TypeId") == "Ore":
            volumes[item.findtext("Id/SubtypeId")] = (
                float(item.findtext("Volume")) / 1000
            )
    return volumes


def tonnes_per_m3(material, materials, volumes, harvest):
    ore, ratio = materials[material][:2]
    return SHIP_DRILL_RATIO * ratio * harvest / volumes[ore] / 1000


def pixel_areas(size, radius):
    """Surface area of each pixel of a cube map face of a sphere"""
    centers = (np.arange(size) + 0.5) / size * 2 - 1
    u, v = np.meshgrid(centers, centers)
    return radius**2 * (2 / size) ** 2 / (1 + u * u + v * v) ** 1.5


def planet(name, radius, mod, materials, volumes, harvest):
    """Ore -> (deposits, m3 of ore rock, tonnes)"""
    definition = next(
        d
        for d in definitions(mod / "Data/PlanetGeneratorDefinitions.sbc")
        if d.findtext("Id/SubtypeId") == name
    )
    mapping = {
        int(o.get("Value")): (o.get("Type"), float(o.get("Depth")))
        for o in definition.find("OreMappings")
    }
    deposits, rock, tonnes = Counter(), Counter(), Counter()
    areas = None
    for face in FACES:
        ores = np.array(
            Image.open(mod / "Data/PlanetDataFiles" / name / f"{face}_mat.png")
        )[:, :, 2]
        if areas is None:
            areas = pixel_areas(ores.shape[0], radius)
        # A deposit is a patch of touching ore pixels; its pixels may mix several ores
        patches = ndimage.label(ores != NO_ORE)[0]
        in_patch = defaultdict(set)
        for value in np.unique(ores):
            if value == NO_ORE or int(value) not in mapping:
                continue
            material, depth = mapping[int(value)]
            ore = materials[material][0]
            mask = ores == value
            in_patch[ore].update(np.unique(patches[mask]).tolist())
            volume = float(areas[mask].sum()) * depth
            rock[ore] += volume
            tonnes[ore] += volume * tonnes_per_m3(material, materials, volumes, harvest)
        for ore, patch_ids in in_patch.items():
            deposits[ore] += len(patch_ids)
    return {ore: (deposits[ore], rock[ore], tonnes[ore]) for ore in rock}


def belt_asteroids(world):
    """Storage edge length of each procedural asteroid, and the ice ones"""
    procedural, ice = [], []
    for path in sorted(world.glob("Octahedron-*.vx2")):
        data = gzip.open(path).read()
        size = struct.unpack_from("<4i", data, data.index(b"\x01\x01\x11") + 3)[1]
        (ice if "-Ice-" in path.name else procedural).append(size)
    return procedural, ice


def material_lists(materials, rng):
    """Samples the deposit material lists generator 4 gives asteroids (MyCompositeShapes ctor).
    None stands for the 1% that become whole ice asteroids."""
    kinds = defaultdict(list)
    for subtype, (ore, _, spawns, low, high, _) in materials.items():
        if spawns and low <= VOXEL_GENERATOR <= high and ore != "Stone":
            kinds[ore].append(subtype)
    for _ in range(SAMPLES):
        if rng.random() < 0.01:
            yield None
            continue
        picked = [rng.choice(sorted(subtypes)) for subtypes in kinds.values()]
        weighted = [m for m in picked for _ in range(materials[m][5])]
        for limit in (4 if rng.random() > 0.8 else 2, 2 if rng.random() > 0.4 else 1):
            while len(weighted) > limit:
                weighted.pop(rng.randrange(len(weighted)))
        yield weighted


def belt(procedural, settings, materials, volumes, harvest):
    """Expected ore -> (deposits, m3 of ore rock, tonnes) of the belt's procedural asteroids,
    and how many of them are expected to be whole ice asteroids"""
    count_coefficient = float(settings.findtext("DepositsCountCoefficient"))
    size_denominator = float(settings.findtext("DepositSizeDenominator"))
    lists = list(material_lists(materials, random.Random(0)))
    ice_share = sum(weighted is None for weighted in lists) / len(lists)
    deposits, rock, tonnes = Counter(), Counter(), Counter()
    for size, asteroids in Counter(procedural).items():
        # The primary prefab is at most 512 m, and the deposits follow its size
        size = min(size, 512)
        level = 1 if size <= 64 else 2 if size <= 128 else 3 if size <= 256 else 4
        count = int(count_coefficient * level)
        spread = size / size_denominator + 8
        # E[4/3 pi r^3] for r = 5 + U * spread, U uniform on [0, 1)
        sphere = np.pi / 3 * ((5 + spread) ** 4 - 5**4) / spread
        share = Counter()
        for weighted in lists:
            if weighted is not None:
                for i in range(count):
                    # The deposits take the materials in turn
                    share[weighted[i % len(weighted)]] += 1
        for material, n in share.items():
            ore = materials[material][0]
            expected = asteroids * n / len(lists)
            deposits[ore] += expected
            rock[ore] += expected * sphere
            tonnes[ore] += (
                expected * sphere * tonnes_per_m3(material, materials, volumes, harvest)
            )
    stats = {ore: (deposits[ore], rock[ore], tonnes[ore]) for ore in rock}
    return stats, ice_share * len(procedural)


def table(stats):
    rows = ["| Ore | Deposits | Ore rock (m³) | Ore (t) |", "|---|---:|---:|---:|"]
    for ore, (deposits, rock, tonnes) in sorted(
        stats.items(), key=lambda item: -item[1][2]
    ):
        rows.append(f"| {ore} | {deposits:,.0f} | {rock:,.0f} | {tonnes:,.0f} |")
    return "\n".join(rows)


def chart(title, stats):
    ores = sorted(stats, key=lambda ore: -stats[ore][2])
    names = ", ".join(f'"{ore}"' for ore in ores)
    values = ", ".join(f"{stats[ore][2] / 1000:.1f}" for ore in ores)
    return "\n".join(
        [
            "```mermaid",
            "xychart-beta",
            f'    title "{title}"',
            f"    x-axis [{names}]",
            '    y-axis "Ore (kt)"',
            f"    bar [{values}]",
            "```",
        ]
    )


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("world", type=Path)
    parser.add_argument("mod", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--seed", type=int, help="the seed both were built with, for the heading"
    )
    args = parser.parse_args()

    checkpoint = definitions(args.world / "Sandbox.sbc")
    settings = checkpoint.find("Settings")
    harvest = float(settings.findtext("HarvestRatioMultiplier"))
    sector = definitions(args.world / "SANDBOX_0_0_0_.sbs")
    radii = {
        e.findtext("StorageName").split("-")[0]: float(e.findtext("Radius"))
        for e in sector.find("SectorObjects")
        if e.findtext("Radius") and e.findtext("StorageName")
    }
    materials = voxel_materials(args.mod)
    volumes = ore_volumes()
    earth = planet(
        "EarthLike", radii["EarthLike"], args.mod, materials, volumes, harvest
    )
    moon = planet("Moon", radii["Moon"], args.mod, materials, volumes, harvest)
    procedural, ice = belt_asteroids(args.world)
    asteroids, whole_ice = belt(procedural, settings, materials, volumes, harvest)

    sizes = ", ".join(
        f"{n} of {size} m" for size, n in sorted(Counter(procedural).items())
    )
    summary = Counter()
    for stats in (earth, moon, asteroids):
        for ore, (_, _, tonnes) in stats.items():
            summary[ore] += tonnes
    overview = [
        "| Ore | EarthLike (t) | Moon (t) | Asteroid belt (t) |",
        "|---|---:|---:|---:|",
    ]
    for ore in sorted(summary, key=lambda ore: -summary[ore]):
        cells = [
            f"{stats[ore][2]:,.0f}" if ore in stats else "-"
            for stats in (earth, moon, asteroids)
        ]
        overview.append(f"| {ore} | " + " | ".join(cells) + " |")

    args.output.write_text(
        f"""# Ore statistics: {checkpoint.findtext("SessionName")}

Generated by `ore_stats.py` from the world in `{args.world.name}/`{f" (seed {args.seed})" if args.seed is not None else ""} and the
OctahedronOres mod built with the same seed. Amounts are approximate: ore in
tonnes as a ship drill mines it (HarvestRatioMultiplier {harvest:g}), before
refining. Ore rock is the volume of the ore-bearing voxels.

- EarthLike and the Moon are counted from the mod's ore maps: each ore pixel is
  a column of ore as wide as the pixel on the surface (radius {radii["EarthLike"]:,.0f} m and
  {radii["Moon"]:,.0f} m) and as deep as its ore mapping. A deposit is a connected patch of
  ore pixels on one cube face (one crossing a face edge counts twice), counted
  once for every ore in it.
- The asteroid belt is an expectation, not a count: the game rolls each
  asteroid's ores from its seed with its own random generator. It applies
  generator 4's rules (MyCompositeShapes) to the belt's {len(procedural)} procedural asteroids
  ({sizes}), with DepositsCountCoefficient {settings.findtext("DepositsCountCoefficient")} and
  DepositSizeDenominator {settings.findtext("DepositSizeDenominator")}. A deposit is a sphere, and
  part of it may stick out of the rock, so the belt's amounts are upper bounds.
- Not counted: the stone around the deposits, the {len(ice)} placed ice asteroids
  (whole `Asteroid512-001` shapes of ice) and about {whole_ice:.0f} procedural asteroids
  the generator makes whole ice.

## Overview

{chr(10).join(overview)}

## EarthLike

{table(earth)}

{chart("EarthLike", earth)}

## Moon

{table(moon)}

{chart("Moon", moon)}

## Asteroid belt

{table(asteroids)}

{chart("Asteroid belt (expected)", asteroids)}
""",
        encoding="utf-8",
    )
    print(args.output)


if __name__ == "__main__":
    main()
