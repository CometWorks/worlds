#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow"]
# ///
"""Generates the double helix shipyard blueprint (SE-952).

    uv run make_helix_shipyard.py <blueprint folder>

Writes bp.sbc and thumb.png into the folder, as a local blueprint folder
(Blueprints/local/<name>) has them, and prints the checks: block counts, PCU,
open conveyor ports, the build volume and how far the welders reach into it.

Two strands of industrial conveyor pipe wind around a build volume 71 blocks
across and 200 blocks long. Every quarter turn an OmniArc beam welder sits
inline in each strand, the conveyor passing through it. A ring of pipe at
each end joins the strands. Four towers point outward from each ring, every 90
degrees, each with a docking connector at its tip and a refill station beside
it. The towers at the strands' ends also hold the cargo and the reactors.
Block types, templates and colors come from the current shipyard
(blueprints/omni-arc-beam-shipyard.sbc).
"""

import argparse
import colorsys
import copy
import math
import random
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

TEMPLATE_BLUEPRINT = Path(__file__).parent / "blueprints/omni-arc-beam-shipyard.sbc"
NAME = "Double Helix Shipyard"

# Radius of the strands and the rings, in blocks from the axis. Every pipe cell
# is more than CLEAR blocks from the axis, so the build volume is 2 * CLEAR + 1 =
# 71 blocks (177.5 m) across around a center block. At 40 blocks the welders
# didn't reach the axis in game (SE-959).
RADIUS = 36
CLEAR = 35
# Blocks between the rings: the build volume's length (500 m)
LENGTH = 200
TURNS = 4
# Welders per strand and turn: one every 90 degrees, from ring to ring
WELDERS_PER_TURN = 4
# The current shipyard's welders, cargo and reactors. Cargo and reactors scale by
# the ratio of welders, rounded up to a whole number per strand end.
TEMPLATE_WELDERS = 24
TEMPLATE_CARGO = 8
TEMPLATE_REACTORS = 4
STRAND_ENDS = 4
# Pipe from a ring to the connector at a tower's tip, in blocks
TOWER = 13
# The welder's reach (ToolCore definition of OmniArcBeam and its models): it
# targets blocks whose center is within 100 m of a point 1 m past its emitter,
# where the turret points then, not where it will aim. The emitter sits 1.18 m
# from the ball and the ball 1.75 m from the block's center, so a block within
# REACH of the welder block's center is in reach in any pose. The ball turns all
# the way around the block's Up and tilts at most 45 degrees out of that plane.
# The Octahedron plugin's free build space counts the same (ShipyardOrders).
GRID = 2.5
REACH = 100 - 1 - 1.18 - 1.75
TILT = 45

XSI = "http://www.w3.org/2001/XMLSchema-instance"
XSI_TYPE = f"{{{XSI}}}type"
ET.register_namespace("xsi", XSI)
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")

# The current shipyard's colors: (ColorMaskHSV, skin)
YELLOW = ((0.12222222, -0.1, 0.26), "Hazard_Armor")  # pipes, welders, connectors
RED = ((1.0, 0.2, 0.55), "Clean_Armor")  # frame, cargo, reactors
DARK = ((0.0, -0.8, -0.3), "Battered_Armor")  # caps, refill stations

# Subtype: (object builder type, size, conveyor ports as (local cell, direction),
# PCU, DLC). Local X is the block's Right, Y its Up and Z its Backward. The
# ports are the conveyor dummies of the block models.
X, Y, Z = (1, 0, 0), (0, 1, 0), (0, 0, 1)
NX, NY, NZ = (-1, 0, 0), (0, -1, 0), (0, 0, -1)
O = (0, 0, 0)
BLOCKS = {
    "LargeBlockConveyorPipeSeamless": (
        "ConveyorConnector",
        (1, 1, 1),
        [(O, Y), (O, NY)],
        10,
        "HeavyIndustry",
    ),
    "LargeBlockConveyorPipeCorner": (
        "ConveyorConnector",
        (1, 1, 1),
        [(O, NY), (O, X)],
        10,
        "HeavyIndustry",
    ),
    "LargeBlockConveyorPipeT": (
        "Conveyor",
        (1, 1, 1),
        [(O, NY), (O, X), (O, NX)],
        10,
        "HeavyIndustry",
    ),
    "LargeBlockConveyorPipeIntersection": (
        "Conveyor",
        (1, 1, 1),
        [(O, Y), (O, NY), (O, X), (O, NX)],
        10,
        "HeavyIndustry",
    ),
    "LargeBlockConveyorPipeCap": ("CubeBlock", (1, 1, 1), [], 1, "HeavyIndustry"),
    "LargeBlockConveyorCap": ("CubeBlock", (1, 1, 1), [], 1, None),
    "OmniArcBeam": ("ConveyorSorter", (1, 1, 1), [(O, Y), (O, NY)], 250, None),
    "Connector": ("ShipConnector", (1, 1, 1), [(O, Z)], 125, None),
    "InsetRefillStation": (
        "MedicalRoom",
        (1, 1, 1),
        [(O, Y), (O, NY), (O, Z)],
        30,
        None,
    ),
    "LargeBlockLargeGenerator": (
        "Reactor",
        (3, 3, 3),
        [((1, 0, 1), NY), ((1, 1, 2), Z), ((1, 1, 0), NZ)],
        25,
        None,
    ),
    "LargeBlockBulkContainerB": (
        "CargoContainer",
        (6, 3, 3),
        [((1, 0, 1), NY), ((4, 0, 1), NY), ((0, 1, 1), NX), ((5, 1, 1), X)],
        10,
        "Economy2",
    ),
}
PIPES = {
    s
    for s in BLOCKS
    if s.startswith("LargeBlockConveyorPipe") and s != "LargeBlockConveyorPipeCap"
}
DIRECTION_NAMES = {
    NZ: "Forward",
    Z: "Backward",
    NX: "Left",
    X: "Right",
    Y: "Up",
    NY: "Down",
}


def add(*vectors):
    return tuple(map(sum, zip(*vectors)))


def scale(a, k):
    return tuple(p * k for p in a)


def neg(a):
    return scale(a, -1)


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def to_cell(p):
    return tuple(math.floor(c + 0.5) for c in p)


def rasterize(curve, length, closed=False):
    """Cells of a face-connected path along curve(t), t from 0 to 1.

    Where the curve crosses into a cell diagonally, the path steps first along
    the axis the curve crosses first.
    """
    steps = int(length * 8) + 1
    cells = [to_cell(curve(0.0))]
    for i in range(1, steps + 1):
        t0, t1 = (i - 1) / steps, i / steps
        start, end = cells[-1], to_cell(curve(t1))
        axes = [a for a in range(3) if end[a] != start[a]]
        assert all(abs(end[a] - start[a]) == 1 for a in axes)

        def crossing(axis):
            low, high = t0, t1
            for _ in range(40):
                mid = (low + high) / 2
                if to_cell(curve(mid))[axis] == start[axis]:
                    low = mid
                else:
                    high = mid
            return low

        cell = list(start)
        for axis in sorted(axes, key=crossing):
            cell[axis] = end[axis]
            cells.append(tuple(cell))
    if closed:
        assert cells[-1] == cells[0]
        cells.pop()
    assert len(set(cells)) == len(cells), "the path crosses itself"
    return cells


class Shipyard:
    def __init__(self):
        self.blocks = []  # (subtype, forward, up, cells, ports, color)
        self.cells = {}  # cell: index into blocks
        self.pipes = (
            {}
        )  # cell: (directions to connect, color), turned into blocks by finish()

    def free(self, cell):
        return cell not in self.cells and cell not in self.pipes

    def path(self, cells, color, closed=False):
        for cell in cells:
            assert cell not in self.cells, cell
            self.pipes.setdefault(cell, (set(), color))
        pairs = list(zip(cells, cells[1:])) + (
            [(cells[-1], cells[0])] if closed else []
        )
        for a, b in pairs:
            step = add(b, neg(a))
            assert sum(map(abs, step)) == 1, (a, b)
            self.pipes[a][0].add(step)
            self.pipes[b][0].add(neg(step))

    def block(self, subtype, forward, up, corner, color):
        """Places a block with its local origin cell at corner."""
        _, size, ports, _, _ = BLOCKS[subtype]
        right, backward = cross(forward, up), neg(forward)

        def turn(d):
            return add(scale(right, d[0]), scale(up, d[1]), scale(backward, d[2]))

        cells = [
            add(corner, turn((i, j, k)))
            for i in range(size[0])
            for j in range(size[1])
            for k in range(size[2])
        ]
        for cell in cells:
            assert self.free(cell), (subtype, cell)
            self.cells[cell] = len(self.blocks)
        self.blocks.append(
            (
                subtype,
                forward,
                up,
                cells,
                [(add(corner, turn(c)), turn(d)) for c, d in ports],
                color,
            )
        )

    def box(self, subtype, right, up, cells, color):
        """Places a block on the box of cells, its Right and Up along right and up."""
        forward = cross(up, right)
        # The local origin cell is the box's corner lowest along Right, Up and Backward
        toward = add(right, up, neg(forward))
        corner = tuple(
            (min if toward[a] > 0 else max)(c[a] for c in cells) for a in range(3)
        )
        self.block(subtype, forward, up, corner, color)
        assert sorted(self.blocks[-1][3]) == sorted(cells), subtype

    def finish(self):
        """Turns the pipe cells into pipe blocks, connects block ports and caps open ports.

        Returns the ports that face a block without a port of its own.
        """
        for _, _, _, _, ports, _ in self.blocks:
            for cell, d in ports:
                if add(cell, d) in self.pipes:
                    self.pipes[add(cell, d)][0].add(neg(d))
        pipes, self.pipes = self.pipes, {}
        for cell, (dirs, color) in sorted(pipes.items()):
            subtype, forward, up = pipe_piece(dirs)
            self.block(subtype, forward, up, cell, color)
        blocked, caps = [], []
        for subtype, _, _, _, ports, _ in self.blocks:
            for cell, d in ports:
                beside = add(cell, d)
                if beside not in self.cells:
                    caps.append((beside, d, subtype in PIPES))
                elif (beside, neg(d)) not in self.blocks[self.cells[beside]][4]:
                    blocked.append((subtype, cell, d))
        for cell, d, pipe in caps:
            # The cap's Bottom covers the port
            cap = "LargeBlockConveyorPipeCap" if pipe else "LargeBlockConveyorCap"
            self.block(cap, perpendicular(d), d, cell, DARK)
        return blocked


def perpendicular(d):
    return Z if d[2] == 0 else Y


def pipe_piece(dirs):
    """(subtype, forward, up) of the pipe that connects to dirs."""
    dirs = sorted(dirs)
    pairs = [d for d in dirs if neg(d) in dirs and d > neg(d)]
    if len(dirs) == 2 and pairs:
        return "LargeBlockConveyorPipeSeamless", perpendicular(dirs[0]), dirs[0]
    if len(dirs) == 2:
        # Bottom to one, Right to the other
        up, right = neg(dirs[0]), dirs[1]
        return "LargeBlockConveyorPipeCorner", cross(up, right), up
    if len(dirs) == 3 and pairs:
        side = next(d for d in dirs if neg(d) not in dirs)
        up, right = neg(side), pairs[0]
        return "LargeBlockConveyorPipeT", cross(up, right), up
    if len(dirs) == 4 and len(pairs) == 2:
        up, right = pairs
        return "LargeBlockConveyorPipeIntersection", cross(up, right), up
    raise ValueError(f"no single pipe connects {dirs}")


def ring_cell(angle, z):
    return (round(RADIUS * math.cos(angle)), round(RADIUS * math.sin(angle)), z)


def build():
    yard = Shipyard()
    top = LENGTH + 1
    for z in (0, top):

        def circle(t):
            return (
                RADIUS * math.cos(2 * math.pi * t),
                RADIUS * math.sin(2 * math.pi * t),
                z,
            )

        yard.path(rasterize(circle, 8 * RADIUS, closed=True), RED, closed=True)

    welders = TURNS * WELDERS_PER_TURN + 1
    for start in (0.0, math.pi):
        # A welder every quarter turn, inline where the strand runs level along the
        # ring's tangent, its Up along the pipe and its Forward toward the axis
        spots = []
        for n in range(welders):
            angle = start + 2 * math.pi * n / WELDERS_PER_TURN
            z = round(1 + (LENGTH - 1) * n / (welders - 1))
            tangent = to_cell((-math.sin(angle), math.cos(angle), 0))
            spots.append((angle, z, ring_cell(angle, z), tangent))
        # The strand leaves the bottom ring beside the first welder and joins the
        # top ring beside the last one
        cells = [add(spots[0][2], neg(spots[0][3]), NZ)]
        for (a0, z0, cell, tangent), (a1, z1, _, _) in zip(spots, spots[1:]):
            cells += [add(cell, neg(tangent)), cell]
            # The arc from beside this welder to beside the next
            turn = math.asin(1 / RADIUS)

            def arc(t):
                angle = a0 + turn + (a1 - a0 - 2 * turn) * t
                return (
                    RADIUS * math.cos(angle),
                    RADIUS * math.sin(angle),
                    z0 + (z1 - z0) * t,
                )

            path = rasterize(arc, math.hypot(RADIUS * (a1 - a0), z1 - z0))
            assert path[0] == add(cell, tangent)
            cells += path[:-1]
        _, _, cell, tangent = spots[-1]
        cells += [add(cell, neg(tangent)), cell, add(cell, tangent)]
        cells.append(add(cell, tangent, Z))
        assert cells[0] in yard.pipes and cells[-1] in yard.pipes
        yard.path(cells, YELLOW)
        for angle, _, cell, tangent in spots:
            del yard.pipes[cell]
            inward = to_cell((-math.cos(angle), -math.sin(angle), 0))
            yard.block("OmniArcBeam", inward, tangent, cell, YELLOW)

    # A tower every 90 degrees on both rings. Frame: u away from the axis, v along
    # the ring, w away from the build volume.
    ratio = 2 * welders / TEMPLATE_WELDERS
    cargo = math.ceil(TEMPLATE_CARGO * ratio / STRAND_ENDS)
    reactors = math.ceil(TEMPLATE_REACTORS * ratio / STRAND_ENDS)
    for z, w in ((0, NZ), (top, Z)):
        for quarter in range(4):
            angle = quarter * math.pi / 2
            origin = ring_cell(angle, z)
            u = to_cell((math.cos(angle), math.sin(angle), 0))
            v = to_cell((-math.sin(angle), math.cos(angle), 0))

            def at(du, dv, dw):
                return add(origin, scale(u, du), scale(v, dv), scale(w, dw))

            yard.path([at(0, 0, i) for i in range(TOWER + 1)], YELLOW)
            yard.block("Connector", w, v, at(0, 0, TOWER + 1), YELLOW)
            yard.block("InsetRefillStation", u, w, at(1, 0, TOWER), DARK)
            if quarter % 2 == 0:
                storage(yard, at, v, w, cargo, reactors)
    blocked = yard.finish()
    return yard, blocked, 2 * welders


def storage(yard, at, v, w, cargo, reactors):
    """Reactors and cargo in two stacks either side of a tower, from 2 blocks out.

    Each block's Bottom port faces the tower; the blocks in a stack join end to end.
    """
    stacks = [[], []]
    for subtype in ["LargeBlockBulkContainerB"] * cargo + [
        "LargeBlockLargeGenerator"
    ] * reactors:
        min(stacks, key=lambda s: sum(BLOCKS[b][1][0] for b in s)).append(subtype)
    for side, stack in zip((1, -1), stacks):
        up = scale(v, side)
        start = 2
        for subtype in stack:
            length = BLOCKS[subtype][1][0]
            cells = [
                at(du, side * dv, dw)
                for du in (-1, 0, 1)
                for dv in (1, 2, 3)
                for dw in range(start, start + length)
            ]
            # A reactor's Front and Back ports and a container's long axis run along the stack
            right = cross(w, up) if subtype == "LargeBlockLargeGenerator" else w
            yard.box(subtype, right, up, cells, RED)
            start += length
        assert start - 1 <= TOWER


def entity_ids(seed=952):
    rng = random.Random(seed)
    while True:
        yield rng.randrange(10**17, 10**18)


def blueprint_xml(yard):
    template = ET.parse(TEMPLATE_BLUEPRINT).getroot()
    root = copy.deepcopy(template)
    bp = root.find(".//ShipBlueprint")
    grid = bp.find("CubeGrids/CubeGrid")
    blocks = grid.find("CubeBlocks")
    templates = {}
    for b in blocks:
        templates.setdefault(b.findtext("SubtypeName"), b)
    ids = entity_ids()

    blocks.clear()
    for subtype, forward, up, cells, _, (hsv, skin) in yard.blocks:
        if subtype in templates:
            b = copy.deepcopy(templates[subtype])
            for tag in ("NumberInGrid", "CustomName"):
                for e in b.findall(tag):
                    b.remove(e)
        else:
            b = ET.Element(
                "MyObjectBuilder_CubeBlock",
                {XSI_TYPE: f"MyObjectBuilder_{BLOCKS[subtype][0]}"},
            )
            for tag in (
                "SubtypeName",
                "EntityId",
                "Min",
                "BlockOrientation",
                "ColorMaskHSV",
                "SkinSubtypeId",
            ):
                ET.SubElement(b, tag)
            b.find("SubtypeName").text = subtype
        b.find("EntityId").text = str(next(ids))
        low = [min(c[a] for c in cells) for a in range(3)]
        b.find("Min").attrib = dict(zip("xyz", map(str, low)))
        b.find("BlockOrientation").attrib = {
            "Forward": DIRECTION_NAMES[forward],
            "Up": DIRECTION_NAMES[up],
        }
        b.find("ColorMaskHSV").attrib = dict(zip("xyz", map(str, hsv)))
        b.find("SkinSubtypeId").text = skin
        blocks.append(b)

    for tag in ("ConveyorLines", "BlockGroups"):
        for e in grid.findall(tag):
            grid.remove(e)
    grid.find("EntityId").text = grid.find("Name").text = str(next(ids))
    grid.find("DisplayName").text = NAME
    pose = grid.find("PositionAndOrientation")
    pose.find("Forward").attrib = {"x": "0", "y": "0", "z": "-1"}
    pose.find("Up").attrib = {"x": "0", "y": "1", "z": "0"}
    for axis, value in zip("XYZW", "0001"):
        pose.find(f"Orientation/{axis}").text = value

    bp.find("Id").set("Subtype", NAME)
    for e in bp.findall("DLC"):
        bp.remove(e)
    after = list(bp).index(bp.find("DisplayName"))
    for i, dlc in enumerate(sorted({BLOCKS[b[0]][4] for b in yard.blocks} - {None})):
        e = ET.Element("DLC")
        e.text = dlc
        bp.insert(after + 1 + i, e)
    grids = bp.find("CubeGrids")
    for e in grids.findall("CubeGrid")[1:]:
        grids.remove(e)
    for e in bp.findall("WorkshopIds"):
        bp.remove(e)
    bp.find("WorkshopId").text = "0"
    ET.indent(root, "  ")
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def thumbnail(yard, path, size=512):
    """An oblique view along the axis, the welders drawn larger."""

    def rotate(p, axis, angle):
        c, s = math.cos(math.radians(angle)), math.sin(math.radians(angle))
        i, j = [a for a in range(3) if a != axis]
        q = list(p)
        q[i], q[j] = p[i] * c - p[j] * s, p[i] * s + p[j] * c
        return q

    dots = []
    for subtype, _, _, cells, _, (hsv, _) in yard.blocks:
        welder = subtype == "OmniArcBeam"
        rgb = colorsys.hsv_to_rgb(
            hsv[0] % 1, min(1, max(0, hsv[1] + 0.8)), min(1, max(0, hsv[2] + 0.45))
        )
        color = (120, 220, 255) if welder else tuple(int(255 * c) for c in rgb)
        for cell in cells:
            # Axis turned 40 degrees from the view, tipped 20 degrees toward it, then
            # laid on the image's diagonal
            x, y, depth = rotate(rotate(rotate(cell, 1, 40), 0, 20), 2, 30)
            dots.append((depth, x, -y, color, welder))
    left, right = min(d[1] for d in dots), max(d[1] for d in dots)
    low, high = min(d[2] for d in dots), max(d[2] for d in dots)
    margin = 16
    k = (size - 2 * margin) / max(right - left, high - low)
    image = Image.new("RGB", (size, size), (12, 14, 20))
    draw = ImageDraw.Draw(image)
    for _, sx, sy, color, welder in sorted(dots, key=lambda d: d[0]):
        px = (size - k * (right - left)) / 2 + k * (sx - left)
        py = (size - k * (high - low)) / 2 + k * (sy - low)
        r = 3 if welder else 1
        draw.ellipse((px - r, py - r, px + r, py + r), fill=color)
    image.save(path)


def reach(yard):
    """How far the welders reach into the build volume: the least margin on the
    axis in meters, the radius in blocks within which they reach every block, and
    the share of the volume's blocks they reach."""
    xs = np.arange(-CLEAR, CLEAR + 1)
    x, y = np.meshgrid(xs, xs)
    disc = x**2 + y**2 <= CLEAR**2
    x, y = x[disc], y[disc]
    radius = np.hypot(x, y)
    welders = [
        (np.array(cells[0], float), np.array(up, float))
        for subtype, _, up, cells, _, _ in yard.blocks
        if subtype == "OmniArcBeam"
    ]
    margins = []
    for z in range(1, LENGTH + 1):
        cells = np.stack([x, y, np.full(len(x), z)], 1).astype(float)
        best = np.full(len(cells), -np.inf)
        for center, up in welders:
            d = (cells - center) * GRID
            distance = np.linalg.norm(d, axis=1)
            tilt = np.degrees(np.arcsin(np.abs(d @ up) / distance))
            best = np.maximum(best, np.where(tilt <= TILT, REACH - distance, -np.inf))
        margins.append(best)
    margins = np.array(margins)
    missed = (margins < 0).any(0)
    covered = radius[missed].min() if missed.any() else CLEAR + 1
    return margins[:, radius == 0].min(), covered, (margins >= 0).mean()


def report(yard, blocked, welders):
    """Prints the checks; True when every conveyor port is connected or capped."""
    counts = Counter(b[0] for b in yard.blocks)
    for subtype, n in sorted(counts.items()):
        print(f"{n:6} {subtype}")
    pcu = sum(BLOCKS[s][3] * n for s, n in counts.items())
    print(f"blocks {len(yard.blocks)}, PCU {pcu}, welders {welders}")
    cells = list(yard.cells)
    size = [max(c[a] for c in cells) - min(c[a] for c in cells) + 1 for a in range(3)]
    print(f"bounding box {size} blocks")
    clear = min(math.hypot(c[0], c[1]) for c in cells if 1 <= c[2] <= LENGTH)
    print(
        f"build volume {LENGTH} blocks long between the rings, no block within {clear:.2f} blocks of the axis"
    )
    axis, covered, share = reach(yard)
    print(
        f"welders reach the axis with {axis:.2f} m to spare, every block within {covered:.1f} blocks of the axis, {100 * share:.1f}% of the volume"
    )
    open_ports = [
        (s, c, d)
        for s, _, _, _, ports, _ in yard.blocks
        for c, d in ports
        if add(c, d) not in yard.cells
    ]
    print(
        f"open conveyor ports {len(open_ports)}, ports against a block without a port {len(blocked)}"
    )
    for b in open_ports + blocked:
        print("  ", b)
    return not open_ports and not blocked and clear > CLEAR and axis >= 0


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    yard, blocked, welders = build()
    args.folder.mkdir(parents=True, exist_ok=True)
    (args.folder / "bp.sbc").write_bytes(blueprint_xml(yard))
    thumbnail(yard, args.folder / "thumb.png")
    sys.exit(0 if report(yard, blocked, welders) else 1)


if __name__ == "__main__":
    main()
