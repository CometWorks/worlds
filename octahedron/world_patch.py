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

Grids an earlier world patch of octahedron.cfg adds are left out. A shipyard both worlds have,
at another place or turned, gets a GridMoves entry instead (SE-1369,
plugin/ServerPlugin/GridMoves.cs), id <id>-<the station's first word>: the plugin moves the kept
world's shipyard there, with its rotor heads, once it is idle. Other grids that moved are only
named.
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


def placement(entity: ET.Element) -> tuple[tuple[float, ...], ...]:
    """(position, forward, up) of an entity"""
    where = entity.find("PositionAndOrientation")
    return tuple(
        tuple(float(where.find(part).get(k)) for k in "xyz")
        for part in ("Position", "Forward", "Up")
    )


def moved(old: list[ET.Element], new: list[ET.Element]) -> bool:
    return len(old) != len(new) or any(
        max(
            abs(a - b) for u, v in zip(placement(o), placement(n)) for a, b in zip(u, v)
        )
        > 0.001
        for o, n in zip(old, new)
    )


def move_entry(patch: str, name: str, entity: ET.Element) -> str:
    position, forward, up = (
        ",".join(f"{c:.6f}".rstrip("0").rstrip(".") for c in v)
        for v in placement(entity)
    )
    return (
        "    <GridMoveEntry>\n"
        f"      <Id>{patch}-{name.split()[0].lower()}</Id>\n"
        f"      <Grid>{name}</Grid>\n"
        f"      <Position>{position}</Position>\n"
        f"      <Forward>{forward}</Forward>\n"
        f"      <Up>{up}</Up>\n"
        "    </GridMoveEntry>"
    )


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
    # Grids a world patch of the config adds already
    config = ET.parse(HERE / "config/octahedron.cfg").getroot()
    patched = {
        grid.findtext("DisplayName")
        for entry in config.iterfind("WorldPatches/WorldPatchEntry")
        for grid in ET.parse(
            HERE / "config/storage/Octahedron" / entry.findtext("Grids")
        ).iter("CubeGrid")
    }
    added = [e for name in sorted(set(new) - set(old) - patched) for e in new[name]]
    shifted = sorted(n for n in set(old) & set(new) if moved(old[n], new[n]))
    shipyards = [n for n in shifted if n.endswith(" Shipyard") and len(new[n]) == 1]
    # The rotor heads go with their shipyard
    others = [n for n in shifted if n not in shipyards and n != "Subgrid"]
    if others:
        print(
            f"warning: the patch doesn't move these grids: {', '.join(others)}",
            file=sys.stderr,
        )
    if not added and not shipyards:
        sys.exit(f"no new grids and no moved shipyards since {args.since}")
    if added:
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
    if shipyards:
        print(f"GridMoves: {', '.join(shipyards)}")
        for name in shipyards:
            print(move_entry(args.id, name, new[name][0]))


if __name__ == "__main__":
    main()
