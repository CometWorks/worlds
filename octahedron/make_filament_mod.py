#!/usr/bin/env python3
"""Generates the OctahedronFilaments mod: the Moon Shipyard's welder and deconstructor (SE-1067).

    python3 make_filament_mod.py [output folder, default: OctahedronFilaments next to this script]
        [--omniarc <OmniArc Workshop folder>] [--game <SpaceEngineers folder>]

The filament tools are copies of the OmniArc beam welder and deconstructor
(Workshop 3412657089) that work with thin filaments instead of laser beams, like
the web of carbon fiber threads of Laconia Station's shipyard in The Expanse.
Their definitions here are OmniArc's (stats, reach, turret, animations)
without its laser beams and sparks, and with the thunder of Pertam's electric
storm instead of its laser sound (filament-tools/Data/Audio_*.sbc, with the
game's waves copied in); the mod's script (filament-tools/) plays that sound and
draws the filaments while a tool works, and gives the
blocks the models and icons of their OmniArc originals when a world loads.

The mod ships no OmniArc files, so a world with it needs OmniArc and ToolCore
too. The blocks are not public: only the Moon Shipyard has them (build_world.py
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
GAME = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers"
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
# ToolCore's Hit sound: none. The script plays the storm (OctahedronFilamentStorm)
# instead, since ToolCore stops its sound the moment a tool stops hitting. The Hit
# event still needs an effect, or ToolCore sends the script no Hit events; a material
# sound map that doesn't exist plays nothing
SOUND = "MaterialProperties/OctahedronFilamentsSilent"
# The storm's waves: Pertam's lightning (the game's WM_Lightning), from Content/Audio to the
# mod's Audio/OctahedronFilaments
SOUND_WAVES = [f"ARC/EVENT/Lightning_{i}.wav" for i in range(4)]
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
    sounds = d.findall(".//Event[@Trigger='Hit']/Sound/Name")
    assert sounds, f"no Hit sound in {original_subtype}"
    for name in sounds:
        name.text = SOUND
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
    parser.add_argument("--game", type=Path, default=GAME)
    args = parser.parse_args()

    root = ET.Element("Definitions")
    blocks = ET.SubElement(root, "CubeBlocks")
    for subtype in TOOLS:
        blocks.append(tool(args.omniarc / "Data/CubeBlocks", subtype))

    out = args.output
    shutil.rmtree(out, ignore_errors=True)
    shutil.copytree(SCRIPTS, out)
    write(root, out / "Data" / BLOCKS_FILE)
    waves = out / "Audio/OctahedronFilaments"
    waves.mkdir(parents=True)
    for wave in SOUND_WAVES:
        shutil.copy(args.game / "Content/Audio" / wave, waves)
    # ToolCore reads the tool values from the files listed here
    (out / "Data/ToolCoreFiles.txt").write_text(BLOCKS_FILE + "\n", encoding="utf-8")
    print(f"{len(blocks)} blocks in {out}")


if __name__ == "__main__":
    main()
