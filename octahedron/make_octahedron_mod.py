#!/usr/bin/env python3
"""Generates OctahedronMod, the Octahedron world's own mod for its small scripts and data (SE-1394).

    python3 make_octahedron_mod.py [output folder, default: OctahedronMod next to this script]
        [--omniarc <OmniArc Workshop folder>] [--game <SpaceEngineers folder>]

The mod is octahedron-mod/ (scripts, sounds) plus what this script generates: the
Moon Shipyard's filament welder and deconstructor (SE-1067). It also has the
stations' radar LCD script (SE-1380). It was called OctahedronFilaments before
SE-1394 and kept its Workshop item (3833786664) and its block and script ids.

The filament tools are copies of the OmniArc beam welder and deconstructor
(Workshop 3412657089) that work with thin filaments instead of laser beams, like
the web of carbon fiber threads of Laconia Station's shipyard in The Expanse.
Their definitions here are OmniArc's (stats, reach, turret, animations)
without its laser beams and sparks, and with the thunder of Pertam's electric
storm instead of its laser sound (octahedron-mod/Data/Audio_*.sbc, with the
game's waves copied in); the mod's script (octahedron-mod/) plays that sound and
draws the filaments while a tool works, and gives the
blocks the models and icons of their OmniArc originals when a world loads.

The mod ships no OmniArc files, so a world with it needs OmniArc and ToolCore
too. The blocks are not public: only the Moon Shipyard has them (build_world.py
--octahedron-mod), players can't build them.

It also generates an item for each ore, ingot and component of the game, named after
the lots OCT's Stores trade it in: "Silicon Ore (1 t)", "Steel Plate (x1000)"
(SE-1497). The plugin shows the Stores' rows of lots to the clients as these items, as
the Store screen takes a row's name, icon and unit from its item. Nobody ever holds one.

Rerun it after an OmniArc update that changes these two blocks, and after a game
update that adds ores, ingots or components.
"""

import argparse
import copy
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
OMNIARC = Path.home() / ".steam/steam/steamapps/workshop/content/244850/3412657089"
GAME = Path.home() / ".steam/steam/steamapps/common/SpaceEngineers"
SCRIPTS = HERE / "octahedron-mod"
BLOCKS_FILE = "CubeBlocks_OctahedronFilaments.sbc"
LOTS_FILE = "PhysicalItems_OctahedronLots.sbc"
# The plugin's StoreLots.Shown: OctahedronLot_<Type>_<Subtype>, of 1,000 units
LOT_PREFIX = "OctahedronLot_"
LOT_TYPES = {"Ore": "(1 t)", "Ingot": "(1 t)", "Component": "(x1000)"}

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
# mod's Audio/OctahedronMod
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


def lot_items(game):
    """A hidden component for each ore, ingot and component of the game: a lot of 1,000"""
    texts = {
        d.get("name"): d.findtext("value")
        for d in ET.parse(game / "Content/Data/Localization/MyTexts.resx")
        .getroot()
        .iter("data")
    }
    root = ET.Element("Definitions")
    items = ET.SubElement(root, "Components")
    for path in sorted((game / "Content/Data").glob("*.sbc")):
        for d in ET.parse(path).getroot().iter():
            type_id = d.findtext("Id/TypeId", "").replace("MyObjectBuilder_", "")
            if type_id not in LOT_TYPES or d.find("Icon") is None:
                continue
            subtype = d.findtext("Id/SubtypeId")
            name = texts.get(d.findtext("DisplayName"), d.findtext("DisplayName"))
            what = "kg" if type_id != "Component" else "pieces"
            item = ET.SubElement(items, "Component")
            ET.SubElement(item, "Id")
            ET.SubElement(item.find("Id"), "TypeId").text = "Component"
            ET.SubElement(item.find("Id"), "SubtypeId").text = (
                f"{LOT_PREFIX}{type_id}_{subtype}"
            )
            ET.SubElement(item, "DisplayName").text = f"{name} {LOT_TYPES[type_id]}"
            ET.SubElement(item, "Description").text = (
                f"A lot of 1,000 {what} of {name}, the way OCT's Stores trade it. The "
                f"price is per lot; a purchase puts 1,000 {what} of {name} into the "
                "inventory for each lot."
            )
            ET.SubElement(item, "Icon").text = d.findtext("Icon")
            ET.SubElement(item, "Model").text = d.findtext("Model")
            size = ET.SubElement(item, "Size")
            for axis in "XYZ":
                ET.SubElement(size, axis).text = "0.5"
            for value in ("Mass", "Volume"):
                ET.SubElement(item, value).text = (
                    f"{float(d.findtext(value, '1')) * 1000:g}"
                )
            ET.SubElement(item, "Public").text = "false"
            ET.SubElement(item, "MaxIntegrity").text = "1"
            ET.SubElement(item, "DropProbability").text = "0"
    return root


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
    parser.add_argument("output", nargs="?", type=Path, default=HERE / "OctahedronMod")
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
    lots = lot_items(args.game)
    write(lots, out / "Data" / LOTS_FILE)
    waves = out / "Audio/OctahedronMod"
    waves.mkdir(parents=True)
    for wave in SOUND_WAVES:
        shutil.copy(args.game / "Content/Audio" / wave, waves)
    # ToolCore reads the tool values from the files listed here
    (out / "Data/ToolCoreFiles.txt").write_text(BLOCKS_FILE + "\n", encoding="utf-8")
    print(f"{len(blocks)} blocks and {len(lots[0])} lot items in {out}")


if __name__ == "__main__":
    main()
