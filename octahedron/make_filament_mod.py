#!/usr/bin/env python3
"""Generates the OctahedronFilaments mod: the shipyards' welder and deconstructor (SE-1067).

    python3 make_filament_mod.py [output folder, default: OctahedronFilaments next to this script]
        [--omniarc <OmniArc Workshop folder>] [--variants]

The filament tools are copies of the OmniArc beam welder and deconstructor
(Workshop 3412657089) that draw thin filaments, like the carbon fiber threads
of Laconia Station's shipyard in The Expanse, instead of laser beams: a bundle
of them from all over the tool to the block it works on while it hits, a few
idle strands across the tool while it is on. No sparks. Everything else (stats,
reach, turret, animations, sound) is OmniArc's.

The mod ships no OmniArc files. Its definitions name a vanilla model, and the
mod's script (filament-tools/) gives the blocks the models and icons of their
OmniArc originals when a world loads, so the world needs OmniArc and ToolCore
loaded before this mod. The blocks are not public: only the shipyards have them
(build_world.py, place_moon_shipyard.py), players can't build them.

--variants adds copies of the welder with other looks (LOOKS), suffixed with
the look's name, for comparing them side by side in a test world.

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

# (OmniArc file, subtype, dummy prefix, emitter) -> our subtype and name
TOOLS = {
    "OctahedronFilamentWelder": (
        "OmniArcBeamWelder.sbc",
        "OmniArcBeam",
        "",
        "Filament Welder",
        "A shipyard welder. Thin carbon fiber filaments reach out from it to the "
        "block it builds.",
    ),
    "OctahedronFilamentDeconstructor": (
        "OmniArcBeamDestro.sbc",
        "OmniArcDestro",
        "destro",
        "Filament Deconstructor",
        "A shipyard deconstructor. Thin carbon fiber filaments reach out from it "
        "to the block it takes apart.",
    ),
}
# Placeholder model and icon until the script swaps in OmniArc's
PLACEHOLDER_MODEL = r"Models\Cubes\Large\ConveyorSorter.mwm"
PLACEHOLDER_ICON = r"Textures\GUI\Icons\Cubes\ConveyorSorterLarge.dds"

# Name -> (material, color RGBA, width of the main filament, of the others, of the idle strands).
# The billboards blend as Standard: RGB above 1 glows, alpha is the cover.
LOOKS = {
    "": ("WeaponLaser", (0.55, 0.57, 0.6, 1.0), 0.04, 0.015, 0.008),
    "Dark": ("WeaponLaser", (0.06, 0.06, 0.07, 1.0), 0.04, 0.015, 0.008),
    "Pale": ("WeaponLaser", (1.6, 1.65, 1.75, 0.8), 0.03, 0.012, 0.006),
    "Square": ("Square", (0.45, 0.46, 0.5, 1.0), 0.03, 0.01, 0.006),
}
# The dummies the filaments start from, on top of the emitter: OmniArc's charge
# beams start there (dummy prefix + these)
STRANDS = [f"chargeemitter{i}" for i in range(1, 9)]
THREADS = STRANDS + [f"{k}charge{i}" for k in ("main", "upper") for i in range(1, 5)]

XSI = "http://www.w3.org/2001/XMLSchema-instance"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


def sub(parent, tag, text=None, **attrib):
    e = ET.SubElement(parent, tag, attrib)
    if text is not None:
        e.text = str(text)
    return e


def beam(parent, start, width, look, end=None):
    material, color, *_ = look
    b = sub(parent, "Beam")
    sub(b, "Start", start)
    sub(b, "EndLocation", "Emitter" if end else "Hit")
    if end:
        sub(b, "End", end)
    sub(b, "Material", material)
    sub(b, "Width", width)
    c = sub(b, "Color")
    for axis, value in zip("XYZW", color):
        sub(c, axis, value)


def filaments(tool_values, prefix, look):
    """Replaces OmniArc's laser beams and sparks with filaments."""
    _, _, main, thread, strand = look
    emitter = tool_values.findtext("Emitter")
    events = {e.get("Trigger"): e for e in tool_values.findall("Event")}
    assert set(events) == {"Enabled", "Hit"}, events.keys()
    for event in events.values():
        for e in event.findall("Beam") + event.findall("ParticleEffect"):
            event.remove(e)
    for i, name in enumerate(STRANDS):
        beam(
            events["Enabled"],
            prefix + name,
            strand,
            look,
            f"{prefix}endemitter{1 + i // 4}",
        )
    beam(events["Hit"], emitter, main, look)
    for name in THREADS:
        beam(events["Hit"], prefix + name, thread, look)


def tool(source, subtype, name, description, prefix, look):
    original = next(
        d
        for d in ET.parse(source).getroot().iter("Definition")
        if d.findtext("Id/SubtypeId") == TOOLS[subtype][1]
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
    sub(d, "Public", "false")
    filaments(d.find("ToolValues"), prefix, look)
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
    parser.add_argument("--variants", action="store_true")
    args = parser.parse_args()

    root = ET.Element("Definitions")
    blocks = sub(root, "CubeBlocks")
    for subtype, (file, _, prefix, name, description) in TOOLS.items():
        source = args.omniarc / "Data/CubeBlocks" / file
        looks = LOOKS if args.variants and prefix == "" else {"": LOOKS[""]}
        for look, values in looks.items():
            label = f"{name} {look}".strip()
            blocks.append(tool(source, subtype, label, description, prefix, values))
            blocks[-1].find("Id/SubtypeId").text = subtype + look
            blocks[-1].find("BlockPairName").text = subtype + look

    out = args.output
    shutil.rmtree(out, ignore_errors=True)
    shutil.copytree(SCRIPTS, out)
    write(root, out / "Data" / BLOCKS_FILE)
    # ToolCore reads the tool values from the files listed here
    (out / "Data/ToolCoreFiles.txt").write_text(BLOCKS_FILE + "\n", encoding="utf-8")
    print(f"{len(blocks)} blocks in {out}")


if __name__ == "__main__":
    main()
