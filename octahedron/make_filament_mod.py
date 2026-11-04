#!/usr/bin/env python3
"""Generates the OctahedronFilaments mod: the shipyards' welder and deconstructor (SE-1067).

    python3 make_filament_mod.py [output folder, default: OctahedronFilaments next to this script]
        [--omniarc <OmniArc Workshop folder>]

The filament tools are copies of the OmniArc beam welder and deconstructor
(Workshop 3412657089) that work with thin filaments instead of laser beams, like
the web of carbon fiber threads of Laconia Station's shipyard in The Expanse.
Their definitions here are OmniArc's (stats, reach, turret, animations, sound)
without its laser beams and sparks; the mod's script (filament-tools/) draws
the filaments while a tool works and gives the blocks the models and icons of
their OmniArc originals when a world loads.

The mod ships no OmniArc files, so a world with it needs OmniArc and ToolCore
too. The blocks are not public: only the shipyards have them (build_world.py
--filament-mod), players can't build them.

Rerun it after an OmniArc update that changes these two blocks.
"""

import argparse
import copy
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
OMNIARC = Path.home() / ".steam/steam/steamapps/workshop/content/244850/3412657089"
SCRIPTS = HERE / "filament-tools"
BLOCKS_FILE = "CubeBlocks_OctahedronFilaments.sbc"

# Ours -> OmniArc's definition file and subtype, our name and description
TOOLS = {
    "OctahedronFilamentWelder": (
        "OmniArcBeamWelder.sbc",
        "OmniArcBeam",
        "Filament Welder",
        "A shipyard welder. Thin carbon fiber filaments reach out from it to the "
        "block it builds.",
    ),
    "OctahedronFilamentDeconstructor": (
        "OmniArcBeamDestro.sbc",
        "OmniArcDestro",
        "Filament Deconstructor",
        "A shipyard deconstructor. Thin carbon fiber filaments reach out from it "
        "to the block it takes apart.",
    ),
}
# Placeholder model and icon until the script swaps in OmniArc's
PLACEHOLDER_MODEL = r"Models\Cubes\Large\ConveyorSorter.mwm"
PLACEHOLDER_ICON = r"Textures\GUI\Icons\Cubes\ConveyorSorterLarge.dds"

XSI = "http://www.w3.org/2001/XMLSchema-instance"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


def tool(source, subtype):
    file, original_subtype, name, description = TOOLS[subtype]
    original = next(
        d
        for d in ET.parse(source / file).getroot().iter("Definition")
        if d.findtext("Id/SubtypeId") == original_subtype
    )
    d = copy.deepcopy(original)
    d.find("Id/SubtypeId").text = subtype
    d.find("DisplayName").text = name
    d.find("Description").text = description
    d.find("Icon").text = PLACEHOLDER_ICON
    d.find("Model").text = PLACEHOLDER_MODEL
    for model in d.findall("BuildProgressModels/Model"):
        model.set("File", PLACEHOLDER_MODEL)
    d.find("BlockPairName").text = subtype
    ET.SubElement(d, "Public").text = "false"
    # No laser beams and sparks; the script draws the filaments
    for event in d.iter("Event"):
        for e in event.findall("Beam") + event.findall("ParticleEffect"):
            event.remove(e)
    return d


def write(root, path):
    ET.indent(root, "  ")
    text = ET.tostring(root, encoding="unicode")
    text = text.replace(
        "<Definitions", '<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema"', 1
    )
    if "xmlns:xsi=" not in text[:300]:
        text = text.replace("<Definitions", f'<Definitions xmlns:xsi="{XSI}"', 1)
    path.write_text('<?xml version="1.0"?>\n' + text + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "output", nargs="?", type=Path, default=HERE / "OctahedronFilaments"
    )
    parser.add_argument("--omniarc", type=Path, default=OMNIARC)
    args = parser.parse_args()

    root = ET.Element("Definitions")
    blocks = ET.SubElement(root, "CubeBlocks")
    for subtype in TOOLS:
        blocks.append(tool(args.omniarc / "Data/CubeBlocks", subtype))

    out = args.output
    shutil.rmtree(out, ignore_errors=True)
    shutil.copytree(SCRIPTS, out)
    write(root, out / "Data" / BLOCKS_FILE)
    # ToolCore reads the tool values from the files listed here
    (out / "Data/ToolCoreFiles.txt").write_text(BLOCKS_FILE + "\n", encoding="utf-8")
    print(f"{len(blocks)} blocks in {out}")


if __name__ == "__main__":
    main()
