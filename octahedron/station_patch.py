"""Makes a station patch from blueprint changes (SE-1234): the From and To blueprints of a
StationPatches entry, each prepared the way build_world.py prepares that grid for a world.

The plugin builds added and changed blocks from the To blueprint as they are in it, so both
must look like the live grid: the station's color, names, share modes, emptied programs and
the rest of what build_world.py sets. A patch made from the raw blueprints would bring blocks
in unprepared.

From is the blueprint and build_world.py of the commit the live world was built from (--since),
To those of the working tree, so a change to the preparation is part of the patch too.

    python station_patch.py 2026-12-station-v2 "Beta Station" --since 04f52f6

writes 2026-12-station-v2-from.sbc and -to.sbc to config/storage/Octahedron and prints the entry
for octahedron.cfg. A grid named "<station> Shipyard" is that station's shipyard. With --stations
it makes a pair for every station built from the station blueprint, each patch id ending in the
station's first word: 2026-12-station-v2-beta.

Both files keep only the main grid and its blocks that differ between them, by min cell: the plugin
compares just those, and a pair per station stays a few kB. config/storage goes into every new
world's Storage folder, and Deploy Octahedron stages it into a kept world's, where the plugin finds
the files.
"""

import argparse
import gzip
import importlib.util
import io
import random
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"


def load(folder: Path, name: str):
    """build_world.py of folder as module name: its blueprint paths are next to it."""
    spec = importlib.util.spec_from_file_location(name, folder / "build_world.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def checkout(rev: str, into: Path) -> Path:
    """build_world.py and the blueprints of the commit rev, extracted into into."""
    repo = Path(
        subprocess.check_output(
            ["git", "rev-parse", "--show-toplevel"], cwd=HERE, text=True
        ).strip()
    )
    prefix = HERE.relative_to(repo).as_posix()
    data = subprocess.check_output(
        ["git", "archive", rev, f"{prefix}/build_world.py", f"{prefix}/blueprints"],
        cwd=repo,
    )
    with tarfile.open(fileobj=io.BytesIO(data)) as tar:
        tar.extractall(into, filter="data")
    return into / prefix


def prepare(world, grid: str, blueprint: Path | None = None) -> ET.ElementTree:
    """The blueprint of grid as build_world module world puts it into a world. blueprint
    replaces the module's own blueprint file of that grid."""
    ids = world.Ids(random.Random(0))
    place = ((0.0, 0.0, 0.0), (0.0, 0.0, -1.0), (0.0, 1.0, 0.0))
    if grid.endswith(" Shipyard"):
        name = grid.removesuffix(" Shipyard")
        moon = name == "Moon Station"
        attr = "MOON_SHIPYARD_BLUEPRINT" if moon else "SHIPYARD_BLUEPRINT"
        if blueprint:
            setattr(world, attr, blueprint)
        source = getattr(world, attr)
        template = world.moon_shipyard_grids() if moon else world.shipyard_grids()
        color = None if moon else world.STATION_COLORS[name]
        grids = world.shipyard(template, ids, grid, *place, 0, color)
    else:
        source = blueprint or world.STATION_BLUEPRINT
        template = ET.parse(source).getroot().find(".//CubeGrid")
        grids = [world.station(template, ids, grid, *place, 0)]
    tree = ET.parse(source)
    holder = tree.getroot().find(".//CubeGrids")
    holder.clear()
    for prepared in grids:
        prepared.tag = "CubeGrid"
        prepared.attrib.pop(XSI_TYPE, None)
        holder.append(prepared)
    return tree


def trim(old: ET.ElementTree, new: ET.ElementTree):
    """Leaves only the main grid in both, with the blocks whose min cell is in one of them alone or
    holds a block that differs in anything but its entity id."""

    def main_grid(tree):
        holder = tree.getroot().find(".//CubeGrids")
        grid = max(holder, key=lambda g: len(g.find("CubeBlocks")))
        for other in [g for g in holder if g is not grid]:
            holder.remove(other)
        return grid.find("CubeBlocks")

    def by_cell(blocks):
        cells = {}
        for block in blocks:
            m = block.find("Min")
            cell = (
                tuple(int(m.get(a, 0)) for a in "xyz") if m is not None else (0, 0, 0)
            )
            copy = ET.fromstring(ET.tostring(block))
            for field in copy.findall("EntityId"):
                copy.remove(field)
            cells[cell] = (block, ET.tostring(copy))
        return cells

    blocks = main_grid(old), main_grid(new)
    cells = [by_cell(b) for b in blocks]
    keep = {
        c
        for c in cells[0].keys() | cells[1].keys()
        if cells[0].get(c, (0, 0))[1] != cells[1].get(c, (0, 0))[1]
    }
    for holder, found in zip(blocks, cells):
        for cell, (block, _) in found.items():
            if cell not in keep:
                holder.remove(block)
    return len(keep)


def write(world, tree: ET.ElementTree, path: Path):
    ET.indent(tree, "  ")
    world.write_xml(tree, path)
    if path.suffix == ".gz":
        # write_xml wrote plain XML; the plugin reads a .gz name gzipped
        path.write_bytes(gzip.compress(path.read_bytes(), mtime=0))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("id", help="the patch id, unique in the config")
    parser.add_argument(
        "grids", nargs="*", help='the grids, like "Beta Station" (none with --stations)'
    )
    parser.add_argument(
        "--stations",
        action="store_true",
        help="every station built from the station blueprint, the patch id ending in its first word",
    )
    parser.add_argument(
        "--since",
        default="HEAD",
        help="the commit the live world was built from (default HEAD)",
    )
    parser.add_argument(
        "--gz",
        action="store_true",
        help="write the files gzipped, .sbc.gz: a whole station's pair is megabytes",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=HERE / "config/storage/Octahedron",
        help="where to write the files",
    )
    args = parser.parse_args()
    if args.stations == bool(args.grids):
        parser.error("name the grids or give --stations")
    ET.register_namespace("xsi", "http://www.w3.org/2001/XMLSchema-instance")
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    with tempfile.TemporaryDirectory() as temp:
        old = load(checkout(args.since, Path(temp)), "build_world_since")
        new = load(HERE, "build_world")
        args.out.mkdir(parents=True, exist_ok=True)
        stations = [
            (f"{args.id}-{g.split()[0].lower()}", g) for g in new.STATION_COLORS
        ]
        for id_, grid in (
            stations if args.stations else [(args.id, g) for g in args.grids]
        ):
            ext = ".sbc.gz" if args.gz else ".sbc"
            source, target = f"{id_}-from{ext}", f"{id_}-to{ext}"
            pair = prepare(old, grid), prepare(new, grid)
            changed = trim(*pair)
            write(old, pair[0], args.out / source)
            write(new, pair[1], args.out / target)
            print(f"<!-- {grid}: {changed} blocks differ -->", file=sys.stderr)
            print(
                f"<StationPatchEntry>\n  <Id>{id_}</Id>\n  <Grid>{grid}</Grid>\n"
                f"  <From>{source}</From>\n  <To>{target}</To>\n</StationPatchEntry>"
            )


if __name__ == "__main__":
    sys.exit(main())
