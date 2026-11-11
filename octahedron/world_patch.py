"""Makes a world patch (SE-1358): the grids a world built from the working tree has and one built
from an older commit hasn't, for the plugin to add to a world kept from that commit (WorldPatches
in config/octahedron.cfg, plugin/ServerPlugin/WorldPatches.cs).

Both worlds are built with build_world.py of their own commit and the same seed, so the new grids
are where a world built now has them. Grids are told apart by name.

    python world_patch.py 2026-11-12-space-elevators --since 70fcefb --seed 2

writes config/storage/Octahedron/2026-11-12-space-elevators.sbc, a blueprint of the new grids at
their world positions, and prints the entry for octahedron.cfg. config/storage goes into every
new world's Storage folder, and Deploy Octahedron stages it into a kept world's, where the plugin
finds the file. A world built after the entry is in octahedron.cfg lists the patch as applied.
"""

import argparse
import copy
import io
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
XSI = "http://www.w3.org/2001/XMLSchema-instance"
XSI_TYPE = f"{{{XSI}}}type"


def checkout(rev: str, into: Path) -> Path:
    """The octahedron folder of the commit rev, extracted into into."""
    repo = Path(
        subprocess.check_output(
            ["git", "rev-parse", "--show-toplevel"], cwd=HERE, text=True
        ).strip()
    )
    prefix = HERE.relative_to(repo).as_posix()
    data = subprocess.check_output(["git", "archive", rev, prefix], cwd=repo)
    with tarfile.open(fileobj=io.BytesIO(data)) as tar:
        tar.extractall(into, filter="data")
    return into / prefix


def grids(folder: Path, seed: int, out: Path) -> dict[str, list[ET.Element]]:
    """The cube grids of the world build_world.py in folder builds, by name."""
    subprocess.run(
        [sys.executable, "build_world.py", str(out), "--seed", str(seed)],
        cwd=folder,
        check=True,
        stdout=subprocess.DEVNULL,
    )
    sector = ET.parse(out / "SANDBOX_0_0_0_.sbs").getroot()
    found = {}
    for entity in sector.iterfind("SectorObjects/MyObjectBuilder_EntityBase"):
        if entity.get(XSI_TYPE) == "MyObjectBuilder_CubeGrid":
            found.setdefault(entity.findtext("DisplayName"), []).append(entity)
    return found


def blueprint(patch: str, added: list[ET.Element]) -> ET.ElementTree:
    ET.register_namespace("xsi", XSI)
    root = ET.Element("Definitions")
    ships = ET.SubElement(root, "ShipBlueprints")
    ship = ET.SubElement(
        ships, "ShipBlueprint", {XSI_TYPE: "MyObjectBuilder_ShipBlueprintDefinition"}
    )
    ET.SubElement(
        ship, "Id", Type="MyObjectBuilder_ShipBlueprintDefinition", Subtype=patch
    )
    ET.SubElement(ship, "DisplayName").text = patch
    cubes = ET.SubElement(ship, "CubeGrids")
    for entity in added:
        grid = ET.SubElement(cubes, "CubeGrid")
        grid.extend(copy.deepcopy(list(entity)))
    ET.indent(root, "  ")
    return ET.ElementTree(root)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("id", help="patch id: letters, digits, '.', '_' and '-'")
    parser.add_argument(
        "--since", required=True, help="commit the live world was built from"
    )
    parser.add_argument(
        "--seed", type=int, default=2, help="seed the live world was built with"
    )
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        old = grids(checkout(args.since, tmp / "old"), args.seed, tmp / "old-world")
        new = grids(HERE, args.seed, tmp / "new-world")
    gone = sorted(set(old) - set(new))
    if gone:
        print(
            f"warning: the patch doesn't remove grids: {', '.join(gone)}",
            file=sys.stderr,
        )
    added = [e for name in sorted(set(new) - set(old)) for e in new[name]]
    if not added:
        sys.exit(f"no new grids since {args.since}")
    path = HERE / "config/storage/Octahedron" / f"{args.id}.sbc"
    path.parent.mkdir(parents=True, exist_ok=True)
    blueprint(args.id, added).write(path, encoding="utf-8", xml_declaration=True)
    print(
        f"{path.relative_to(HERE)}: {len(added)} grids: {', '.join(e.findtext('DisplayName') for e in added)}"
    )
    print(
        "    <WorldPatchEntry>\n"
        f"      <Id>{args.id}</Id>\n"
        f"      <Grids>{path.name}</Grids>\n"
        "    </WorldPatchEntry>"
    )


if __name__ == "__main__":
    main()
