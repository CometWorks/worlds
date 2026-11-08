"""Makes a station patch from blueprint changes (SE-1234): the From and To blueprints of a
StationPatches entry, each prepared the way build_world.py prepares that grid for a world.

The plugin builds added and changed blocks from the To blueprint as they are in it, so both
must look like the live grid: the station's color, names, share modes, emptied programs and
the rest of what build_world.py sets. A patch made from the raw blueprints would bring blocks
in unprepared.

From is the blueprint and build_world.py of the commit the live world was built from (--since),
To those of the working tree, so a change to the preparation is part of the patch too.

    python station_patch.py 2026-12-station-v2 "Beta Station" --since 04f52f6 --out config

writes 2026-12-station-v2-from.sbc and -to.sbc to the config folder and prints the entry for
octahedron.cfg. A grid named "<station> Shipyard" is that station's shipyard.
"""

import argparse
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


def write(world, tree: ET.ElementTree, path: Path):
    ET.indent(tree, "  ")
    world.write_xml(tree, path)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("id", help="the patch id, unique in the config")
    parser.add_argument("grid", help='the grid, like "Beta Station"')
    parser.add_argument(
        "--since",
        default="HEAD",
        help="the commit the live world was built from (default HEAD)",
    )
    parser.add_argument(
        "--out", type=Path, default=HERE / "config", help="where to write the files"
    )
    args = parser.parse_args()
    ET.register_namespace("xsi", "http://www.w3.org/2001/XMLSchema-instance")
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    with tempfile.TemporaryDirectory() as temp:
        old = load(checkout(args.since, Path(temp)), "build_world_since")
        new = load(HERE, "build_world")
        source, target = f"{args.id}-from.sbc", f"{args.id}-to.sbc"
        write(old, prepare(old, args.grid), args.out / source)
        write(new, prepare(new, args.grid), args.out / target)
    print(
        f"<StationPatchEntry>\n  <Id>{args.id}</Id>\n  <Grid>{args.grid}</Grid>\n"
        f"  <From>{source}</From>\n  <To>{target}</To>\n</StationPatchEntry>"
    )


if __name__ == "__main__":
    sys.exit(main())
