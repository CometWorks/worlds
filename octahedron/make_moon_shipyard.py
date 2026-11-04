#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Generates the Moon Shipyard blueprint (SE-986).

    uv run make_moon_shipyard.py <blueprint folder>

Writes bp.sbc and thumb.png into the folder, as a local blueprint folder
(Blueprints/local/<name>) has them, and prints the checks: block counts, PCU,
open conveyor ports, the conveyor network, the build volume, how much of it the
welders and the deconstructors reach, and how the tubes keep clear of the
large blocks.

Two arcs, each a helix making one full turn around a build volume about 140 m
across, the second the first turned 180 degrees around the axis. The base of
the arcs is the end where they are joined. Each arc is a line of conveyor tubes
with OmniArc welders and deconstructors inline, placed where they reach most of
the build volume. Below the base each arc's line turns down to a junction with
two connectors. A conveyor between the junctions joins the arcs, eight cargo
containers and eight large reactors on either side of it. In the middle of each
arc an advanced rotor faces the axis, the base of an arm that holds the ship:
one with a large head, the other with the medium head.

Each arc is covered by a round tube of small armor blocks in two segments,
each a grid of its own on the medium head of a locked advanced rotor, so that
no grid gets more blocks than its collision takes. The base segment's rotor is
at the arc's base, on the side facing the other arc; the far segment's goes on
from the arc's far end, inside the tube's cap, a half sphere as wide as the
tube. The segments meet at the arm base, a few centimeters apart. The storage
between the arcs has a cover of its own on a third rotor. The tubes keep clear
of every large block, so the grids never touch, and a small magnetic plate
inside each segment locks it to a conveyor junction. Dark blue lights shine
through holes in the tubes. Everything is black carbon fiber.
Reuses the SE-952 helpers and its template blueprint for the block XML.
"""

import argparse
import copy
import math
import random
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage
from scipy.spatial import cKDTree

import make_helix_shipyard as helix
from make_helix_shipyard import NX, NY, NZ, O, X, Y, Z, add, cross, neg, scale

NAME = "Moon Shipyard"
GRID = helix.GRID
SMALL = 0.5

# Radius of the arcs' center lines in blocks (75 m, 150 m across)
R = 30
# Blocks along the axis that the arcs span, one full turn
SPAN = 112
# Welders and deconstructors per arc
STATIONS = 14
# Height of the conveyor between the arcs' bases, below the build volume
BRIDGE_Z = -5
# Height of the base rotors on the lines down to it
BASE_ROTOR_Z = -2
# Cargo containers and reactors on either side of that conveyor, per arc
BRIDGE_SLOTS = 4
BRIDGE_KINDS = ("LargeBlockLargeContainer", "LargeBlockLargeGenerator")

# The tubes, in meters: least space between a small and a large block, the
# space the tube's inside keeps around the large blocks, the wall's thickness,
# how round the bulges are, how far the tool turrets swing from their block's
# center, and the radius of the holes around the connectors and arm bases
HARD_GAP = 0.1
TUBE_GAP = 0.35
SHELL = 0.3  # the thinnest wall that stays closed
BLEND = 1.0
TURRET = 2.2
OPENING = GRID / 2 * math.sqrt(2) + 0.25
# The least gap where an arc's tube segments meet; the far rotor's head
# displacement, within the medium head's range, sets it
SEAM_GAP = 0.02
DISPLACEMENT = 0.11
# A small magnetic plate locks each tube segment to a conveyor junction where
# the segment's grid puts the junction's face this far (m) along the plate's
# Up from its center, the range it locks in on a pasted plate
PLATE_FACE = (0.05, 0.2)
# Most blocks a tube segment may have: a grid's collision takes up to 65,535
# shapes, and nearly every block of the tube is a shape of its own
MAX_SEGMENT_BLOCKS = 45000
# How far from the middle the storage cover reaches: past the storage, along
# the conveyor toward the arcs, so that the base segments stay under that
COVER_END = 50.0
# Samples per small block edge when fitting the armor shapes to the tube
SAMPLES = 4
# One light per this many meters of tube, and the holes around each
LIGHT_SPACING = 5.0
HOLE_RADIUS = (0.45, 0.8)
HOLES_PER_LIGHT = 3
SEED = 986

BLACK = ((0.0, -0.96, -0.5), "CarbonFibre_Armor")  # the game's default black
LIGHT_RGB = (0.05, 0.15, 0.85)  # dark blue
LIGHT_RADIUS = 4
LIGHT_INTENSITY = 3
# ToolCore settings the deconstructors carry in Viktor's Omni Arc Beam Shipyard
DESTRO_STORAGE = "CAAQABgQIAAoDzAAOg8NAAAAACUAAAAAPQAAAABAAA=="

ALL = [X, NX, Y, NY, Z, NZ]
# Subtype: (object builder type, size, conveyor ports as (local cell,
# direction), PCU, DLC), local X the block's Right, Y its Up, Z its Backward,
# as in make_helix_shipyard.BLOCKS
BLOCKS = {
    "ConveyorTube": ("ConveyorConnector", (1, 1, 1), [(O, Y), (O, NY)], 10, None),
    "ConveyorTubeCurved": (
        "ConveyorConnector",
        (1, 1, 1),
        [(O, NY), (O, X)],
        10,
        None,
    ),
    "ConveyorTubeT": ("Conveyor", (1, 1, 1), [(O, NY), (O, X), (O, NX)], 10, None),
    "LargeBlockConveyor": ("Conveyor", (1, 1, 1), [(O, d) for d in ALL], 10, None),
    "OmniArcBeam": ("ConveyorSorter", (1, 1, 1), [(O, Y), (O, NY)], 250, None),
    "OmniArcDestro": ("ConveyorSorter", (1, 1, 1), [(O, Y), (O, NY)], 250, None),
    "Connector": ("ShipConnector", (1, 1, 1), [(O, Z)], 125, None),
    "LargeBlockLargeGenerator": helix.BLOCKS["LargeBlockLargeGenerator"],
    "LargeBlockLargeContainer": (
        "CargoContainer",
        (3, 3, 3),
        [((1, 0, 1), NY)],
        10,
        None,
    ),
    "LargeAdvancedStator": ("MotorAdvancedStator", (1, 2, 1), [(O, NY)], 100, None),
    # Small grid
    "SmallBlockSmallMagneticPlate": ("LandingGear", (1, 1, 1), [], 35, None),
    "SmallAdvancedRotor": ("MotorAdvancedRotor", (3, 2, 3), [], 1, None),
    "SmallBlockSmallLight": ("InteriorLight", (1, 1, 1), [], 20, None),
}
# The armor shapes the tubes are fitted with: which points of the block, in
# local coordinates from 0 to 1 (x its Right, y its Up, z its Backward), are
# solid. Read off their mount points: a slope's Front and Bottom are full.
SHAPES = {
    "SmallBlockArmorBlock": lambda x, y, z: x == x,
    "SmallBlockArmorRoundSlope": lambda x, y, z: y * y + z * z <= 1,
    "SmallBlockArmorSlope": lambda x, y, z: y + z <= 1,
    "SmallBlockArmorCorner": lambda x, y, z: 1 - x + y + z <= 1,
    "SmallBlockArmorCornerInv": lambda x, y, z: 1 - x + y + z >= 1,
    "SmallBlockArmorSlope2Base": lambda x, y, z: y <= 1 - z / 2,
    "SmallBlockArmorSlope2Tip": lambda x, y, z: y <= (1 - z) / 2,
    "HalfArmorBlock": lambda x, y, z: z <= 0.5,
    "HalfSlopeArmorBlock": lambda x, y, z: y + z <= 0.5,
}
for _shape in SHAPES:
    BLOCKS.setdefault(_shape, ("CubeBlock", (1, 1, 1), [], 1, None))
CUBE = "SmallBlockArmorBlock"
TOOLS = ("OmniArcBeam", "OmniArcDestro")
DEVICES = {
    *TOOLS,
    "LargeBlockLargeGenerator",
    "LargeBlockLargeContainer",
    "Connector",
    "LargeAdvancedStator",
}
# The leading elements of a block in a blueprint, in their order
HEAD_TAGS = (
    "SubtypeName",
    "EntityId",
    "Min",
    "BlockOrientation",
    "ColorMaskHSV",
    "SkinSubtypeId",
)
TUBES = {"ConveyorTube", "ConveyorTubeCurved", "ConveyorTubeT"}
# Heads of the arm bases in the middle of the arcs, first arc then second
ARM_HEADS = ("LargeAdvancedRotor", "SmallAdvancedRotor")
STARTS = (0.0, math.pi)


def head_offset(head, displacement=0.0):
    """The head grid's origin from its stator's base cell along the stator's
    (Up, Forward, Right) in meters, as measured on pasted rotors."""
    up = 0.2 + displacement
    return (up, 0.0, 0.0) if head == "LargeAdvancedRotor" else (up, 0.5, -0.5)


class Yard:
    """The blocks of one grid; cells are the grid's own."""

    def __init__(self):
        self.blocks = []  # (subtype, forward, up, cells, ports, color)
        self.owner = []  # the arc a block belongs to, None for both
        self.cells = {}  # cell: index into blocks

    def block(self, subtype, forward, up, corner, color, owner=None):
        """Places a block with its local origin cell at corner; returns its index."""
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
            assert cell not in self.cells, (subtype, cell)
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
        self.owner.append(owner)
        return len(self.blocks) - 1

    def box(self, subtype, right, up, cells, color, owner=None):
        """Places a block on the box of cells, its Right and Up along right and up."""
        forward = cross(up, right)
        toward = add(right, up, neg(forward))
        corner = tuple(
            (min if toward[a] > 0 else max)(c[a] for c in cells) for a in range(3)
        )
        index = self.block(subtype, forward, up, corner, color, owner)
        assert sorted(self.blocks[-1][3]) == sorted(cells), subtype
        return index


def tube_piece(dirs):
    """(subtype, forward, up) of the conveyor tube that connects to dirs."""
    try:
        subtype, forward, up = helix.pipe_piece(dirs)
    except ValueError:
        return "LargeBlockConveyor", Z, Y
    if subtype == "LargeBlockConveyorPipeIntersection":
        return "LargeBlockConveyor", Z, Y
    return (
        {
            "LargeBlockConveyorPipeSeamless": "ConveyorTube",
            "LargeBlockConveyorPipeCorner": "ConveyorTubeCurved",
            "LargeBlockConveyorPipeT": "ConveyorTubeT",
        }[subtype],
        forward,
        up,
    )


def center_line(start, t):
    angle = start + 2 * np.pi * t
    return (R * np.cos(angle), R * np.sin(angle), SPAN * t)


def axis_toward(v, exclude):
    """The grid direction closest to v, not along any of exclude."""
    return max(
        (d for d in ALL if d not in exclude and neg(d) not in exclude),
        key=lambda d: sum(a * b for a, b in zip(d, v)),
        default=None,
    )


def tool_margin(cell, up, targets):
    """By how many meters a tool at cell with its Up along up reaches each of the
    targets (m), -inf where its turret can't tilt to it."""
    up = np.array(up, float)
    d = targets - GRID * np.array(cell, float)
    level = d - np.outer(d @ up, up)
    ball = level / np.maximum(np.linalg.norm(level, axis=1), 1e-9)[:, None]
    aim = d - ball * helix.BALL
    distance = np.maximum(np.linalg.norm(aim, axis=1), 1e-9)
    tilt = np.degrees(np.arcsin(np.minimum(np.abs(aim @ up) / distance, 1)))
    return np.where(tilt <= helix.TILT, helix.REACH - distance, -np.inf)


def volume(clear, step=3):
    """Points (m) of the build volume, closer than clear (m) to the axis along the
    arcs' span, every step-th block; and points along its axis."""
    n = math.floor(clear / GRID)
    xs = np.arange(-n, n + 1, step)
    x, y, z = np.meshgrid(xs, xs, np.arange(0, SPAN + 1, step), indexing="ij")
    disc = np.hypot(x, y) * GRID < clear
    points = np.stack([x[disc], y[disc], z[disc]], 1) * GRID
    axis = np.stack([np.zeros(SPAN + 1), np.zeros(SPAN + 1), np.arange(SPAN + 1)], 1)
    return points.astype(float), axis * GRID


def build():
    """The large grid: returns (yard, arcs), each arc a dict of its parts."""
    yard = Yard()
    conveyor = {}  # cell: directions to connect, turned into tubes at the end
    owner = {}  # conveyor cell: arc

    def connect(cells, arc):
        for cell in cells:
            conveyor.setdefault(cell, set())
            owner[cell] = arc if owner.get(cell, arc) == arc else None
        for a, b in zip(cells, cells[1:]):
            step = add(b, neg(a))
            assert sum(map(abs, step)) == 1, (a, b)
            conveyor[a].add(step)
            conveyor[b].add(neg(step))

    def free(cell):
        return cell not in yard.cells and cell not in conveyor

    arcs = []
    length = math.hypot(2 * math.pi * R, SPAN)
    for arc, start in enumerate(STARTS):
        sign = 1 - 2 * arc
        out, inward = (sign, 0, 0), (-sign, 0, 0)
        line = helix.rasterize(lambda t: center_line(start, t), length)
        assert line[0] == (sign * R, 0, 0), line[0]
        # Down from the arc's base to the junction, then across to the middle
        junction = (sign * R, 0, BRIDGE_Z)
        down = [(sign * R, 0, z) for z in range(BRIDGE_Z + 1, 0)]
        across = [(x, 0, BRIDGE_Z) for x in range(0, sign * R, sign)]
        connect(across + [junction] + down + line, arc)
        for forward, up in ((out, Z), (NZ, out)):
            assert free(add(junction, forward))
            yard.block("Connector", forward, up, add(junction, forward), BLACK, arc)
            conveyor[junction].add(forward)

        # Locked advanced rotors: the tube's at the base, on the side facing the
        # other arc, the arm base in the middle facing the axis, and the far
        # tube segment's past the arc's end. A rotor whose Up is -X must not
        # face +Z, nor one whose Up is -Y face +X: its head turns over on paste.
        def rotor(cell, up, head, displacement=0.0):
            forward = {X: Z, NX: NZ, NY: NX}.get(up, X)
            assert all(free(add(cell, scale(up, k))) for k in (1, 2, 3))
            conveyor[cell].add(up)
            index = yard.block(
                "LargeAdvancedStator", forward, up, add(cell, up), BLACK, arc
            )
            return dict(index=index, head=head, displacement=displacement)

        base = rotor((sign * R, 0, BASE_ROTOR_Z), inward, "SmallAdvancedRotor")

        def straight(j):
            return add(line[j], neg(line[j - 1])) == add(line[j + 1], neg(line[j]))

        middle = center_line(start, 0.5)
        sites = sorted(
            (j for j in range(1, len(line) - 1) if straight(j)),
            key=lambda j: math.dist(line[j], middle),
        )
        for j in sites:
            step = add(line[j + 1], neg(line[j]))
            up = axis_toward((-line[j][0], -line[j][1], 0), [step, Z])
            if up in (X, NX) and all(
                free(add(line[j], scale(up, k))) for k in (1, 2, 3)
            ):
                arm = rotor(line[j], up, ARM_HEADS[arc])
                arm["cell"], arm["up"] = line[j], up
                break
        # The far segment's rotor goes on from the arc's end along its last
        # direction, so its head sits inside the cap. At the arm base the arc
        # runs the other way along Y: the segments meet there on a Y face of
        # the base segment's grid, and the far rotor's displacement brings the
        # far segment's grid as close to it as it goes.
        end = line[-1]
        a_ = start + 2 * math.pi
        tangent = (-math.sin(a_), math.cos(a_), SPAN / (2 * math.pi * R))
        up = axis_toward(tangent, [Z])
        arm_cell = arm["cell"]
        travel = math.cos(start + math.pi) * 2 * math.pi * R
        seam = dict(
            x=GRID * arm_cell[0],
            y=GRID * arm_cell[1] + SMALL / 2,
            z=GRID * arm_cell[2],
            dir=1 if travel > 0 else -1,
        )

        def seam_gap(d):
            y0 = GRID * (end[1] + up[1]) + (0.2 + d) * up[1]
            return ((y0 + SMALL / 2 - seam["y"]) * seam["dir"]) % SMALL

        displacement = min(
            (round(d, 3) for d in np.linspace(-DISPLACEMENT, DISPLACEMENT, 221)),
            key=lambda d: (seam_gap(d) < SEAM_GAP, seam_gap(d)),
        )
        seam["gap"] = seam_gap(displacement)
        far = rotor(end, up, "SmallAdvancedRotor", displacement)
        arcs.append(
            dict(
                sign=sign,
                line=line,
                junction=junction,
                base=base,
                arm=arm,
                end=end,
                tangent=np.array(tangent) / np.linalg.norm(tangent),
                far=far,
                seam=seam,
                tools=[],
            )
        )

    # The storage between the arcs, either side of the conveyor joining them,
    # each box's port on the conveyor
    for a, arc in enumerate(arcs):
        sign = arc["sign"]
        for k in range(BRIDGE_SLOTS):
            xs = [sign * (1 + 3 * k + i) for i in range(3)]
            for side in (1, -1):
                subtype = BRIDGE_KINDS[(k + (side < 0)) % 2]
                cells = [
                    (x, side * dy, BRIDGE_Z + dz)
                    for x in xs
                    for dy in (1, 2, 3)
                    for dz in (-1, 0, 1)
                ]
                index = yard.box(subtype, X, (0, side, 0), cells, BLACK, a)
                port = ((xs[1], side, BRIDGE_Z), (0, -side, 0))
                assert port in yard.blocks[index][4], (subtype, yard.blocks[index][4])
                conveyor[(xs[1], 0, BRIDGE_Z)].add((0, side, 0))

    # A locked rotor in the middle of the storage holds its cover
    middle = (0, 0, BRIDGE_Z)
    assert all(add(middle, (0, k, 0)) not in yard.cells for k in (1, 2, 3))
    conveyor[middle].add(Y)
    yard.bridge = dict(
        index=yard.block("LargeAdvancedStator", X, Y, add(middle, Y), BLACK),
        head="SmallAdvancedRotor",
        displacement=0.0,
    )

    # The tools inline, on straight conveyor cells where they add the most
    # reach: welders and deconstructors in turn, the arcs in turn
    targets = np.concatenate(volume(R * GRID - 5))
    candidates = []
    for a, arc in enumerate(arcs):
        line = arc["line"]
        for j in range(3, len(line) - 4):
            step = add(line[j + 1], neg(line[j]))
            if step != add(line[j], neg(line[j - 1])) or conveyor[line[j]] != {
                step,
                neg(step),
            }:
                continue
            reach = tool_margin(line[j], step, targets) >= 0
            candidates.append((a, j, step, reach))
    counts = {tool: np.zeros(len(targets)) for tool in TOOLS}
    used = {0: [], 1: []}
    tools = {}
    for _ in range(STATIONS):
        for a, arc in enumerate(arcs):
            for tool in TOOLS:
                a_, j, step, reach = max(
                    (
                        c
                        for c in candidates
                        if c[0] == a and all(abs(c[1] - k) > 1 for k in used[a])
                    ),
                    key=lambda c: c[3] @ 0.5 ** (counts[tool] + 1),
                )
                used[a].append(j)
                counts[tool] += reach
                cell = arc["line"][j]
                forward = axis_toward((-cell[0], -cell[1], 0), [step])
                tools[cell] = (tool, forward, step)
                arc["tools"].append(cell)

    for cell, dirs in sorted(conveyor.items()):
        if cell in tools:
            subtype, forward, up = tools[cell]
            assert dirs == {up, neg(up)}
        else:
            subtype, forward, up = tube_piece(dirs)
        yard.block(subtype, forward, up, cell, BLACK, owner[cell])
    return yard, arcs


# Small grid tubes


def pack(cells):
    return (
        ((cells[:, 0] + 2048) << 24)
        | ((cells[:, 1] + 2048) << 12)
        | (cells[:, 2] + 2048)
    )


def unpack(keys):
    return np.stack(
        [(keys >> 24) - 2048, ((keys >> 12) & 4095) - 2048, (keys & 4095) - 2048], 1
    )


def smin(a, b, k):
    """Smooth minimum of a and b, blended over k."""
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0, 1)
    return b + (a - b) * h - k * h * (1 - h)


def chunked(f, points, size=400_000):
    return np.concatenate(
        [f(points[i : i + size]) for i in range(0, len(points), size)]
    )


class Boxes:
    """Distances from points (m) to a set of large grid cells' boxes."""

    def __init__(self, cells):
        self.cells = sorted(cells)
        self.centers = GRID * np.array(self.cells, float)
        self.tree = cKDTree(self.centers)

    def distance(self, points, half=GRID / 2, bound=8.0):
        def part(p):
            _, i = self.tree.query(p, k=8, distance_upper_bound=bound, workers=-1)
            valid = i < len(self.centers)
            c = self.centers[np.minimum(i, len(self.centers) - 1)]
            q = np.maximum(np.abs(p[:, None, :] - c) - half, 0)
            return np.where(valid, np.linalg.norm(q, axis=2), 1e3).min(1)

        return chunked(part, points)


def masks():
    """The armor shapes in every orientation, without repeats: (subtype, forward,
    up, which samples of a block are solid, the directions of its full faces);
    and the samples, from the block's center in blocks."""
    i = (np.arange(SAMPLES) + 0.5) / SAMPLES - 0.5
    offsets = np.stack(np.meshgrid(i, i, i, indexing="ij"), -1).reshape(-1, 3)
    found, seen = [], set()
    for subtype, solid in SHAPES.items():
        for forward in ALL:
            for up in ALL:
                if sum(a * b for a, b in zip(forward, up)):
                    continue
                local = offsets @ np.array([cross(forward, up), up, neg(forward)]).T
                mask = solid(*(local + 0.5).T)
                if mask.tobytes() in seen:
                    continue
                seen.add(mask.tobytes())
                full = {d for d in ALL if mask[offsets @ d == offsets.max()].all()}
                found.append((subtype, forward, up, mask, full))
    return offsets, found


class Tube:
    """One segment of the tube of small armor around an arc, a grid on the head
    of a locked rotor. The base segment, on the rotor at the arc's base, covers
    the arc up to its arm base and half the storage; the far segment, on the
    rotor past the arc's end, covers the rest and closes the end with a cap."""

    def __init__(self, yard, arcs, a, part, x_seams=None):
        self.yard = yard
        self.arc = arc = arcs[a]
        self.sign = arc["sign"]
        self.part = part
        rotor = yard.bridge if part == "bridge" else arc[part]
        self.rotor = rotor
        _, forward, up, cells, _, _ = yard.blocks[rotor["index"]]
        self.rotor_cells = set(cells)
        right = cross(forward, up)
        du, df, dr = head_offset("SmallAdvancedRotor", rotor["displacement"])
        self.origin = GRID * np.array(cells[0], float) + np.array(
            add(scale(up, du), scale(forward, df), scale(right, dr)), float
        )
        self.axes = np.array([right, up, neg(forward)], float)  # the grid's X, Y, Z
        # The base segment and the storage cover meet on a face of the base
        # segment's grid at the end of the storage
        self.seam = arc["seam"]
        if part == "base":
            k = round((COVER_END - self.sign * (self.origin[0] + SMALL / 2)) / SMALL)
            self.x_seams = [self.origin[0] + SMALL / 2 + self.sign * k * SMALL]
        else:
            self.x_seams = x_seams
        j = GRID * np.array(arc["junction"], float)
        if part == "bridge":
            own = [c for c in yard.cells if c[2] <= BRIDGE_Z + 1 and abs(c[0]) < R - 1]
            self.tools = cKDTree([[1e6, 1e6, 1e6]])
            s = np.linspace(-1, 1, 800)[:, None]
            self.path = j * [0, 1, 1] + s * [R * GRID, 0, 0]
            self.corridors = []
        else:
            # The inside bulges around the large blocks it holds, but not around
            # the arm base, which stands out through a hole
            # nor around the far stator's top, where its head and nothing else is
            far_top = yard.blocks[arc["far"]["index"]][3][1]
            own = [
                c
                for c, i in yard.cells.items()
                if yard.owner[i] in (a, None)
                and i != arc["arm"]["index"]
                and c != far_top
            ]
            self.tools = cKDTree(GRID * np.array(arc["tools"], float))
            # The center line: the arc, down to the junction, across to the
            # middle, and on from the arc's end to the cap's center
            t = np.linspace(0, 1, 40000)
            helix_points = GRID * np.stack(center_line(STARTS[a], t), axis=1)
            self.end = helix_points[-1]
            s = np.linspace(0, 1, 400)[:, None]
            self.path = np.concatenate(
                [
                    j * s + helix_points[0] * (1 - s),
                    j * (1 - s) + j * [0, 1, 1] * s,
                    helix_points,
                ]
            )
            # Holes for the connectors and the arm base's head, from the
            # conveyor through the wall
            arm = arc["arm"]
            self.corridors = [
                (j, np.array([self.sign, 0, 0], float)),
                (j, np.array([0, 0, -1], float)),
                (np.array(arm["cell"], float) * GRID, np.array(arm["up"], float)),
            ]
        self.own = Boxes(own)
        self.line = cKDTree(self.path)
        self.length = np.linalg.norm(np.diff(self.path, axis=0), axis=1).sum()
        # Inside radius: the arc's conveyor blocks with their corners in it
        line_cells = np.array(arc["line"], float) * GRID
        corners = np.array(
            [[x, y, z] for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)]
        )
        far = (
            self.line.query(
                (line_cells[:, None, :] + corners * GRID / 2).reshape(-1, 3), workers=-1
            )[0]
            .reshape(-1, 8)
            .max(1)
        )
        self.deviation = far
        if part == "bridge":
            self.inner = arcs[0]["inner"]
            return
        self.inner = arc["inner"] = float(np.percentile(far, 95)) + TUBE_GAP
        # The cap: a half sphere as wide as the tube, centered on the arc's
        # last direction just far enough out that the far stator's block is
        # well inside it
        corners = GRID * (
            np.array(yard.blocks[arc["far"]["index"]][3][0], float) + corners / 2
        )
        for length in np.arange(0, 10, 0.25):
            self.cap = self.end + arc["tangent"] * length
            axis = self.end + np.outer(np.linspace(0, 1, 100), self.cap - self.end)
            room = self.inner - TUBE_GAP - BLEND / 2
            if (
                cKDTree(np.concatenate([self.path, axis])).query(corners)[0].max()
                <= room
            ):
                break
        else:
            raise AssertionError("the far stator doesn't fit into the cap")
        self.cap_length = length
        self.path = np.concatenate([self.path, axis])
        self.line = cKDTree(self.path)
        self.length = np.linalg.norm(np.diff(self.path, axis=0), axis=1).sum()

    def world(self, cells):
        return self.origin + SMALL * np.asarray(cells, float) @ self.axes

    def local(self, points):
        return np.rint((np.asarray(points) - self.origin) @ self.axes.T / SMALL).astype(
            np.int64
        )

    def hollow(self, p):
        """Signed distance (m) to the tube's inside, negative inside."""
        h = (
            self.line.query(p, distance_upper_bound=self.inner + 4, workers=-1)[0]
            - self.inner
        )
        h = np.minimum(h, 1e3)
        # The storage between the arcs
        center = np.array([0, 0, BRIDGE_Z * GRID])
        half = np.array([(3 * BRIDGE_SLOTS + 0.5) * GRID, 3.5 * GRID, 1.5 * GRID])
        half = half + TUBE_GAP
        q = np.abs(p - center) - (half - BLEND)
        pod = np.linalg.norm(np.maximum(q, 0), axis=1) + np.minimum(q.max(1), 0) - BLEND
        h = smin(h, pod, BLEND)
        h = smin(h, self.own.distance(p) - TUBE_GAP, BLEND / 2)
        turret = self.tools.query(p, distance_upper_bound=TURRET + 4, workers=-1)[0]
        return smin(h, np.minimum(turret, 1e3) - TURRET, BLEND / 2)

    def solid(self, p):
        """Signed distance (m) to the tube's wall, negative inside it."""
        h = self.hollow(p)
        s = np.maximum(-h, h - SHELL)
        for start, direction in self.corridors:
            d = p - start
            along = d @ direction
            side = np.linalg.norm(d - np.outer(along, direction), axis=1)
            s = np.maximum(s, -np.maximum(side - OPENING, -along))
        return s

    def candidates(self):
        """Keys of the small cells near the tube's wall."""
        keys = []

        def near(points, radius):
            n = math.ceil(radius / SMALL) + 1
            r = np.arange(-n, n + 1)
            offsets = np.stack(np.meshgrid(r, r, r, indexing="ij"), -1).reshape(-1, 3)
            offsets = offsets[np.linalg.norm(offsets, axis=1) * SMALL <= radius + SMALL]
            centers = np.unique(self.local(points), axis=0)
            step = max(1, 3_000_000 // len(offsets))
            for i in range(0, len(centers), step):
                block = (centers[i : i + step, None, :] + offsets).reshape(-1, 3)
                keys.append(np.unique(pack(block)))

        reach = self.inner + SHELL + 1.5
        near(self.path[:: max(1, int(len(self.path) / (self.length / 0.5)))], reach)
        near(self.own.centers, GRID / 2 * math.sqrt(3) + TUBE_GAP + SHELL + 1.5)
        near(self.tools.data, TURRET + SHELL + 1.5)
        x = np.arange(
            -(3 * BRIDGE_SLOTS + 2) * GRID, (3 * BRIDGE_SLOTS + 2) * GRID, 1.0
        )
        pod = np.stack(np.meshgrid(x, np.arange(-10, 11, 1.0), [BRIDGE_Z * GRID]), -1)
        near(pod.reshape(-1, 3), 5.5)
        keys = np.unique(np.concatenate(keys))
        return keys[self.mine(self.world(unpack(keys)))]

    def beyond(self, points):
        """Which points are past the seam at the arm base, along the arc."""
        s = self.seam
        near = (np.abs(points[:, 2] - s["z"]) < 10) & (
            np.abs(points[:, 0] - s["x"]) < 10
        )
        return np.where(
            near, (points[:, 1] - s["y"]) * s["dir"] > 0, points[:, 2] > s["z"]
        )

    def mine(self, points):
        """Which points belong to this segment, whole blocks on the seam."""
        x, z = points[:, 0], points[:, 2]
        if self.part == "far":
            return self.beyond(points - [0, SMALL / 2 * self.seam["dir"], 0])
        if self.part == "bridge":
            # whole blocks only: the base segments' blocks end on the seams
            inside = (x > min(self.x_seams) + SMALL / 2) & (
                x < max(self.x_seams) - SMALL / 2
            )
            return (z < 0) & inside
        storage = (z < 0) & (self.sign * x < self.sign * self.x_seams[0])
        near = self.beyond(points + [0, SMALL / 2 * self.seam["dir"], 0])
        return ~near & ~storage

    def build(self, every, rng):
        """Fits the armor shapes to the wall, then adds the neck, the lights and
        the holes. Returns the stats."""
        offsets, shapes = masks()
        keys = self.candidates()
        cells = unpack(keys)
        s = chunked(self.solid, self.world(cells))
        half = SMALL * math.sqrt(3) / 2
        inside, cut = s <= -half, np.abs(s) < half
        points = (cells[cut][:, None, :] + offsets).reshape(-1, 3)
        filled = (chunked(self.solid, self.world(points)) <= 0).reshape(
            -1, len(offsets)
        )
        m = np.array([sh[3] for sh in shapes], np.float32)
        f = filled.astype(np.float32)
        agree = f @ m.T + (1 - f) @ (1 - m).T
        empty = (~filled).sum(1)
        best = agree.argmax(1)
        place = agree[np.arange(len(best)), best] > empty
        chosen = {}
        for k in keys[inside]:
            chosen[int(k)] = shapes[0]
        for k, b, p in zip(keys[cut], best, place):
            if p:
                chosen[int(k)] = shapes[b]
        wrong = len(offsets) - np.maximum(agree.max(1), empty)
        cut_any = filled.any(1) & ~filled.all(1)
        fit = wrong[cut_any].sum() / max(1, cut_any.sum() * len(offsets))

        # No small block within HARD_GAP of a large one
        placed = np.array(sorted(chosen), np.int64)
        centers = self.world(unpack(placed))
        gap = every.distance(centers, half=GRID / 2 + SMALL / 2)
        for k in placed[gap < HARD_GAP]:
            del chosen[int(k)]
        dropped = int((gap < HARD_GAP).sum())

        # The neck: 3x3 cubes from the head's top along the rotor's axis up to
        # the wall, the head taking cells 0-2 x 0-1 x 0-2
        hollow = {}

        def is_hollow(cell_keys):
            missing = [k for k in cell_keys if k not in hollow]
            if missing:
                h = self.hollow(self.world(unpack(np.array(missing, np.int64))))
                hollow.update(zip(missing, h))
            return [hollow[k] for k in cell_keys]

        self.head_cells = {
            int(k)
            for k in pack(
                np.array(
                    [(i, j, k) for i in range(3) for j in range(2) for k in range(3)]
                )
            )
        }
        assert not self.head_cells & set(chosen)
        neck = []
        for j in range(2, 80):
            layer = [
                int(k)
                for k in pack(np.array([(i, j, k) for i in range(3) for k in range(3)]))
            ]
            neck += layer
            if min(is_hollow(layer)) >= 0:
                break
        else:
            raise AssertionError("the neck doesn't reach the wall")
        for k in neck:
            chosen[k] = shapes[0]
        self.neck = set(neck)
        # The neck may come close only to its own rotor
        others = Boxes([c for c in every.cells if c not in self.rotor_cells])
        points = self.world(unpack(np.array(neck, np.int64)))
        gap = others.distance(points, half=GRID / 2 + SMALL / 2).min()
        assert gap >= HARD_GAP, ("neck too close", gap)

        # Seal the wall where it lets the outside in: where it is thinner than
        # a block and falls between two layers of them, the fit leaves it empty
        sealed = 0
        for _ in range(6):
            leaks, breach = self.leaks(chosen, cells)
            if not leaks or not len(breach):
                break
            gap = every.distance(self.world(unpack(breach)), half=GRID / 2 + SMALL / 2)
            breach = breach[gap >= HARD_GAP]
            for k in breach:
                chosen[int(k)] = shapes[0]
            sealed += len(breach)

        self.plate = self.place_plate(chosen, every, shapes[0])

        # Lights on the inside of the wall, each with a hole beside it
        lights = []
        spots = []
        chosen_keys = set(chosen)
        for k, (subtype, forward, up, mask, full) in chosen.items():
            if k in self.neck:
                continue
            cell = unpack(np.array([k]))[0]
            for d in ALL:
                if d not in full:
                    continue
                light = int(pack((cell + np.array(d))[None])[0])
                if light not in chosen_keys and light not in self.head_cells:
                    spots.append((light, neg(d)))
        rng.shuffle(spots)
        spot_cells = self.world(unpack(np.array([s[0] for s in spots], np.int64)))
        ok_gap = every.distance(spot_cells, half=GRID / 2 + SMALL / 2) >= HARD_GAP
        ok_hollow = self.hollow(spot_cells) < 0
        picked = []
        want = int(self.length / LIGHT_SPACING)
        grid_hash = {}
        for (light, wall), p, ok1, ok2 in zip(spots, spot_cells, ok_gap, ok_hollow):
            if not (ok1 and ok2) or light in chosen_keys:
                continue
            bucket = tuple((p // LIGHT_SPACING).astype(int))
            near = [
                q
                for dx in (-1, 0, 1)
                for dy in (-1, 0, 1)
                for dz in (-1, 0, 1)
                for q in grid_hash.get(
                    (bucket[0] + dx, bucket[1] + dy, bucket[2] + dz), []
                )
            ]
            if any(np.linalg.norm(p - q) < LIGHT_SPACING for q in near):
                continue
            grid_hash.setdefault(bucket, []).append(p)
            picked.append((light, wall, p))
            chosen_keys.add(light)
            if len(picked) >= want:
                break
        holes = cut_blocks = 0
        box = np.stack(np.meshgrid(*[np.arange(-1.0, 1.01, 0.5)] * 3), -1).reshape(
            -1, 3
        )
        for light, wall, p in picked:
            w = np.array(wall, float) @ self.axes  # the wall's direction, world
            lateral = np.cross(w, [0, 0, 1] if abs(w[2]) < 0.5 else [1, 0, 0])
            lateral /= np.linalg.norm(lateral)
            mount = int(pack((unpack(np.array([light]))[0] + np.array(wall))[None])[0])
            # Holes around the light, each its own direction and size
            first = rng.uniform(0, 2 * math.pi)
            for n in range(HOLES_PER_LIGHT):
                angle = (
                    first + 2 * math.pi * n / HOLES_PER_LIGHT + rng.uniform(-0.5, 0.5)
                )
                side = lateral * math.cos(angle) + np.cross(w, lateral) * math.sin(
                    angle
                )
                center = p + w * (SMALL + SHELL / 2) + side * rng.uniform(0.8, 1.6)
                radius = rng.uniform(*HOLE_RADIUS)
                cut = 0
                for k in pack(np.unique(self.local(center + box), axis=0)):
                    k = int(k)
                    if (
                        k in chosen
                        and k != mount
                        and k not in self.neck
                        and np.linalg.norm(
                            self.world(unpack(np.array([k])))[0] - center
                        )
                        <= radius
                    ):
                        del chosen[k]
                        cut += 1
                holes += cut > 0
                cut_blocks += cut
            lights.append((light, wall))
        self.blocks = chosen
        self.lights = lights
        return dict(
            fit=fit,
            dropped=dropped,
            leaks=leaks,
            sealed=sealed,
            holes=holes,
            cut=cut_blocks,
        )

    def place_plate(self, chosen, every, cube):
        """Puts a small magnetic plate on the face of a conveyor block of the
        segment, turned into a junction, where the segment's grid puts the
        face PLATE_FACE along the plate's Up from its center; cubes from the
        plate's back to the wall hold it. Picks the block farthest from the
        segment's rotor. Returns (plate key, its Up in the grid, the block's
        index, the face's distance, the cubes holding it)."""
        yard = self.yard
        best = None
        rejected = Counter()
        for i, (subtype, _, _, cells, ports, _) in enumerate(yard.blocks):
            if subtype not in TUBES:
                continue
            cell = cells[0]
            center = GRID * np.array(cell, float)
            if not self.mine(center[None])[0]:
                continue
            used = {d for _, d in ports}
            for d in ALL:
                if d in used or add(cell, d) in yard.cells:
                    continue
                d_ = np.array(d, float)
                face = center + GRID / 2 * d_
                key = self.local((face - np.mean(PLATE_FACE) * d_)[None])[0]
                plate = self.world(key[None])[0]
                f = (face - plate) @ d_
                if not PLATE_FACE[0] - 1e-6 <= f <= PLATE_FACE[1] + 1e-6:
                    rejected["face"] += 1
                    continue
                if not self.mine(plate[None])[0]:
                    rejected["not mine"] += 1
                    continue
                up = np.rint(self.axes @ d_).astype(int)
                stud = []
                for k in range(1, 12):
                    k_ = int(pack((key + k * up)[None])[0])
                    if k_ in chosen:
                        break
                    stud.append(k_)
                else:
                    rejected["no wall"] += 1
                    continue
                keys = np.array([int(pack(key[None])[0])] + stud, np.int64)
                if set(keys.tolist()) & (self.head_cells | self.neck):
                    rejected["neck"] += 1
                    continue
                if stud:
                    points = self.world(unpack(np.array(stud, np.int64)))
                    others = Boxes([c for c in every.cells if c != cell])
                    # the stud starts 0.25 - f from the face it stands over
                    if (
                        every.distance(points, half=GRID / 2 + SMALL / 2).min() < 0.05
                        or others.distance(points, half=GRID / 2 + SMALL / 2).min()
                        < HARD_GAP
                    ):
                        rejected["gap"] += 1
                        continue
                reach = np.linalg.norm(plate - self.origin)
                if best is None or reach > best[0]:
                    best = (reach, keys, tuple(up.tolist()), i, f)
        assert best, ("no place for the magnetic plate", self.part, self.sign, rejected)
        _, keys, up, i, f = best
        chosen.pop(int(keys[0]), None)
        for k in keys[1:]:
            chosen[int(k)] = cube
        self.neck |= set(int(k) for k in keys)
        _, _, _, cells, _, color = yard.blocks[i]
        yard.blocks[i] = (
            "LargeBlockConveyor",
            Z,
            Y,
            cells,
            [(cells[0], d) for d in ALL],
            color,
        )
        return int(keys[0]), up, i, f, len(keys) - 1

    def leaks(self, chosen, cells):
        """Cells inside the tube that an outside path reaches past its wall,
        not counting the openings and the seams; and the keys of the empty wall
        cells such a path goes through."""
        low, high = cells.min(0) - 1, cells.max(0) + 1
        blocked = np.zeros(high - low + 1, bool)
        placed = unpack(np.array(sorted(chosen), np.int64))
        blocked[tuple((placed - low).T)] = True
        points = self.world(cells)
        opening = np.zeros(len(points), bool)
        if self.part != "bridge":
            s = self.seam
            opening = (
                (np.abs(points[:, 1] - s["y"]) < 1.0)
                & (np.abs(points[:, 2] - s["z"]) < 10)
                & (np.abs(points[:, 0] - s["x"]) < 10)
            )
        for x in self.x_seams or []:
            opening |= (points[:, 2] < 0) & (np.abs(points[:, 0] - x) < 1.0)
        for start, direction in self.corridors:
            d = points - start
            along = d @ direction
            side = np.linalg.norm(d - np.outer(along, direction), axis=1)
            opening |= (side < OPENING + 1) & (along > -1)
        blocked[tuple((cells[opening] - low).T)] = True
        labels, _ = ndimage.label(~blocked)
        outside = labels[0, 0, 0]
        if not hasattr(self, "_hollow") or len(self._hollow) != len(cells):
            self._hollow = chunked(self.hollow, points)
        h = self._hollow
        index = tuple((cells - low).T)
        reached = (labels[index] == outside) & ~blocked[index]
        inner = reached & (h < 0) & ~opening
        # Empty wall cells on the path, next to a reached inside cell
        dense = np.zeros(blocked.shape, bool)
        dense[tuple((cells[inner] - low).T)] = True
        beside = np.zeros(len(cells), bool)
        for d in ALL:
            shifted = cells - low + d
            ok = ((shifted >= 0) & (shifted < blocked.shape)).all(1)
            beside[ok] |= dense[tuple(shifted[ok].T)]
        wall = reached & (h >= 0) & (h <= SHELL + SMALL) & beside & ~opening
        return int(inner.sum()), pack(cells[wall])


def build_tubes(yard, arcs):
    # The far stators' top cells are empty, their heads sit lower: the cap's
    # wall may pass through them
    tops = {yard.blocks[arc["far"]["index"]][3][1] for arc in arcs}
    every = Boxes([c for c in yard.cells if c not in tops])
    rng = random.Random(SEED)
    tubes, stats = [], []
    for a in range(len(arcs)):
        for part in ("base", "far"):
            tube = Tube(yard, arcs, a, part)
            stats.append(tube.build(every, rng))
            tubes.append(tube)
    x_seams = [tubes[0].x_seams[0], tubes[2].x_seams[0]]
    tube = Tube(yard, arcs, 0, "bridge", x_seams)
    stats.append(tube.build(every, rng))
    tubes.append(tube)
    return tubes, stats


def networks(yard):
    """Joins blocks whose ports face each other; returns (open tube ports, the
    networks as lists of device block indexes)."""
    parent = list(range(len(yard.blocks)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    open_ports = []
    for i, (subtype, _, _, _, ports, _) in enumerate(yard.blocks):
        for cell, d in ports:
            j = yard.cells.get(add(cell, d))
            if j is not None and (add(cell, d), neg(d)) in yard.blocks[j][4]:
                parent[find(i)] = find(j)
            elif subtype in TUBES:
                open_ports.append((subtype, cell, d))
    groups = {}
    for i, b in enumerate(yard.blocks):
        if b[0] in DEVICES:
            groups.setdefault(find(i), []).append(i)
    return open_ports, list(groups.values())


def quaternion(forward, up):
    """The orientation (X, Y, Z, W) of a grid facing forward with up."""
    m = np.array([cross(forward, up), up, neg(forward)], float).T
    w = math.sqrt(max(0.0, 1 + m[0, 0] + m[1, 1] + m[2, 2])) / 2
    x = math.sqrt(max(0.0, 1 + m[0, 0] - m[1, 1] - m[2, 2])) / 2
    y = math.sqrt(max(0.0, 1 - m[0, 0] + m[1, 1] - m[2, 2])) / 2
    z = math.sqrt(max(0.0, 1 - m[0, 0] - m[1, 1] + m[2, 2])) / 2
    x = math.copysign(x, m[2, 1] - m[1, 2])
    y = math.copysign(y, m[0, 2] - m[2, 0])
    z = math.copysign(z, m[1, 0] - m[0, 1])
    return x, y, z, w


def set_vector(e, v):
    e.attrib = dict(zip("xyz", map(str, v)))


def element(subtype, forward, up, corner, templates, ids):
    """The blueprint element of a block, from the template's block of its type
    where there is one."""
    if subtype in templates:
        b = copy.deepcopy(templates[subtype])
        for tag in ("Owner", "ShareMode", "NumberInGrid", "CustomName"):
            for e in b.findall(tag):
                b.remove(e)
        # The template leaves out what has its default value
        for i, tag in enumerate(HEAD_TAGS[1:], 1):
            if b.find(tag) is None and (
                tag != "EntityId" or BLOCKS[subtype][0] != "CubeBlock"
            ):
                b.insert(i, ET.Element(tag))
    else:
        b = ET.Element(
            "MyObjectBuilder_CubeBlock",
            {helix.XSI_TYPE: f"MyObjectBuilder_{BLOCKS[subtype][0]}"},
        )
        for tag in HEAD_TAGS:
            if tag != "EntityId" or BLOCKS[subtype][0] != "CubeBlock":
                ET.SubElement(b, tag)
        b.find("SubtypeName").text = subtype
    extra = {
        "SmallBlockSmallLight": (
            ("Enabled", "true"),
            ("Radius", LIGHT_RADIUS),
            ("ColorRed", LIGHT_RGB[0]),
            ("ColorGreen", LIGHT_RGB[1]),
            ("ColorBlue", LIGHT_RGB[2]),
            ("ColorAlpha", 1),
            ("Intensity", LIGHT_INTENSITY),
        ),
        "SmallBlockSmallMagneticPlate": (("Enabled", "true"), ("AutoLock", "true")),
    }
    for tag, value in extra.get(subtype, ()):
        ET.SubElement(b, tag).text = str(value)
    if b.find("EntityId") is not None:
        b.find("EntityId").text = str(next(ids))
    set_vector(b.find("Min"), corner)
    b.find("BlockOrientation").attrib = {
        "Forward": helix.DIRECTION_NAMES[forward],
        "Up": helix.DIRECTION_NAMES[up],
    }
    set_vector(b.find("ColorMaskHSV"), BLACK[0])
    b.find("SkinSubtypeId").text = BLACK[1]
    return b


def blueprint_xml(yard, arcs, tubes):
    root = ET.parse(helix.TEMPLATE_BLUEPRINT).getroot()
    bp = root.find(".//ShipBlueprint")
    grids = bp.find("CubeGrids")
    grid, *subgrids = grids.findall("CubeGrid")
    heads = {g.find("CubeBlocks")[0].findtext("SubtypeName"): g for g in subgrids}
    for g in subgrids:
        grids.remove(g)
    blocks = grid.find("CubeBlocks")
    templates = {}
    for b in blocks:
        templates.setdefault(b.findtext("SubtypeName"), b)
    destro = templates["OmniArcDestro"] = copy.deepcopy(templates["OmniArcBeam"])
    destro.find("SubtypeName").text = "OmniArcDestro"
    for value in destro.iter("Value"):
        value.text = DESTRO_STORAGE
    ids = helix.entity_ids(986)

    elements = []
    blocks.clear()
    for subtype, forward, up, cells, _, _ in yard.blocks:
        corner = [min(c[a] for c in cells) for a in range(3)]
        b = element(subtype, forward, up, corner, templates, ids)
        blocks.append(b)
        elements.append(b)
    for tag in ("ConveyorLines", "BlockGroups"):
        for e in grid.findall(tag):
            grid.remove(e)

    def place(g, name, position, forward, up):
        g.find("EntityId").text = g.find("Name").text = str(next(ids))
        g.find("DisplayName").text = name
        pose = g.find("PositionAndOrientation")
        set_vector(pose.find("Position"), position)
        set_vector(pose.find("Forward"), forward)
        set_vector(pose.find("Up"), up)
        for axis, value in zip("XYZW", quaternion(forward, up)):
            pose.find(f"Orientation/{axis}").text = str(value)

    place(grid, NAME, (0, 0, 0), NZ, Y)
    mounted = [(yard.bridge, tubes[4], f"{NAME} storage cover")]
    for a, arc in enumerate(arcs):
        mounted += [
            (arc["base"], tubes[2 * a], f"{NAME} tube {a + 1} base"),
            (arc["far"], tubes[2 * a + 1], f"{NAME} tube {a + 1} far"),
            (arc["arm"], None, f"{NAME} arm base {a + 1}"),
        ]
    for rotor, tube, name in mounted:
        _, forward, up, cells, _, _ = yard.blocks[rotor["index"]]
        stator = elements[rotor["index"]]
        g = copy.deepcopy(heads[rotor["head"]])
        top = g.find("CubeBlocks")[0]
        top.find("EntityId").text = str(next(ids))
        top.find("ParentEntityId").text = stator.findtext("EntityId")
        stator.find("TopBlockId").text = top.findtext("EntityId")
        stator.find("RotorLock").text = "true"
        for tag in ("ColorMaskHSV", "SkinSubtypeId"):
            for e in top.findall(tag):
                top.remove(e)
        color = ET.Element("ColorMaskHSV")
        set_vector(color, BLACK[0])
        top.insert(2, color)
        skin = ET.Element("SkinSubtypeId")
        skin.text = BLACK[1]
        top.insert(3, skin)
        du, df, dr = head_offset(rotor["head"], rotor["displacement"])
        if rotor["displacement"]:
            e = ET.Element("DummyDisplacement")
            e.text = str(rotor["displacement"])
            stator.insert(list(stator).index(stator.find("RotorLock")), e)
        right = cross(forward, up)
        position = [
            GRID * cells[0][i] + du * up[i] + df * forward[i] + dr * right[i]
            for i in range(3)
        ]
        place(g, name, position, forward, up)
        if tube:
            small = g.find("CubeBlocks")
            for key, (subtype, f, u, _, _) in sorted(tube.blocks.items()):
                corner = unpack(np.array([key]))[0].tolist()
                small.append(element(subtype, f, u, corner, {}, ids))
            for key, wall in tube.lights:
                corner = unpack(np.array([key]))[0].tolist()
                up_ = Y if wall[1] == 0 else X
                small.append(
                    element("SmallBlockSmallLight", neg(wall), up_, corner, {}, ids)
                )
            key, up_, _, _, _ = tube.plate
            corner = unpack(np.array([key]))[0].tolist()
            forward_ = Y if up_[1] == 0 else X
            small.append(
                element("SmallBlockSmallMagneticPlate", forward_, up_, corner, {}, ids)
            )
        grids.append(g)

    bp.find("Id").set("Subtype", NAME)
    for e in bp.findall("DLC"):
        bp.remove(e)
    after = list(bp).index(bp.find("DisplayName"))
    for i, dlc in enumerate(sorted({BLOCKS[b[0]][4] for b in yard.blocks} - {None})):
        e = ET.Element("DLC")
        e.text = dlc
        bp.insert(after + 1 + i, e)
    for e in bp.findall("WorkshopIds"):
        bp.remove(e)
    bp.find("WorkshopId").text = "0"
    ET.indent(root, "  ")
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def thumbnail(yard, tubes, path, size=512):
    """A view across the axis, turned and tipped; tools, lights and rotors marked."""
    marks = {
        "OmniArcBeam": (120, 220, 255),
        "OmniArcDestro": (255, 140, 60),
        "LargeAdvancedStator": (255, 255, 255),
    }
    points, colors = [], []
    for subtype, _, _, cells, _, _ in yard.blocks:
        for c in cells:
            points.append(c)
            colors.append(marks.get(subtype))
    for tube in tubes:
        keys = np.array(sorted(tube.blocks), np.int64)[::7]
        for p in tube.world(unpack(keys)) / GRID:
            points.append(p)
            colors.append(None)
        for p in tube.world(unpack(np.array([k for k, _ in tube.lights]))) / GRID:
            points.append(p)
            colors.append((60, 90, 255))
    yaw, pitch = math.radians(25), math.radians(20)
    dots = []
    for (x, y, z), mark in zip(points, colors):
        a = z * math.cos(yaw) - x * math.sin(yaw)
        b = z * math.sin(yaw) + x * math.cos(yaw)
        sy = y * math.cos(pitch) - b * math.sin(pitch)
        depth = y * math.sin(pitch) + b * math.cos(pitch)
        shade = int(50 + 60 * (R - depth) / (2 * R))
        dots.append((depth, a, -sy, mark or (shade, shade, shade + 12), mark))
    left, right = min(d[1] for d in dots), max(d[1] for d in dots)
    low, high = min(d[2] for d in dots), max(d[2] for d in dots)
    k = (size - 32) / max(right - left, high - low)
    image = Image.new("RGB", (size, size), (8, 10, 16))
    draw = ImageDraw.Draw(image)
    for _, sx, sy, color, mark in sorted(dots, key=lambda d: -d[0]):
        px = (size - k * (right - left)) / 2 + k * (sx - left)
        py = (size - k * (high - low)) / 2 + k * (sy - low)
        r = 2.5 if mark else 0.8
        draw.ellipse((px - r, py - r, px + r, py + r), fill=color)
    image.save(path)


def report(yard, arcs, tubes, stats):
    """Prints the checks; True when they pass."""
    counts = Counter(b[0] for b in yard.blocks)
    for subtype, n in sorted(counts.items()):
        print(f"{n:6} {subtype}")
    pcu = sum(BLOCKS[s][3] * n for s, n in counts.items())
    print(f"main grid: blocks {len(yard.blocks)}, PCU {pcu}")
    total_blocks, total_pcu = len(yard.blocks), pcu
    ok = True
    arm_cells = [GRID * np.array(arc["arm"]["cell"]) for arc in arcs]
    clear = []
    for tube, s in zip(tubes, stats):
        a = (tube.sign < 0) * 1
        small = Counter(v[0] for v in tube.blocks.values())
        small["SmallBlockSmallLight"] = len(tube.lights)
        small["SmallBlockSmallMagneticPlate"] = 1
        small["SmallAdvancedRotor"] = 1
        n = sum(small.values())
        p = sum(BLOCKS[k][3] * v for k, v in small.items())
        total_blocks += n
        total_pcu += p
        print(
            f"{'storage cover' if tube.part == 'bridge' else f'tube {a + 1} {tube.part}'}:"
            f" blocks {n}, PCU {p}, inside radius {tube.inner:.2f} m"
            f" (conveyor corners up to {tube.deviation.max():.2f} m from the center"
            f" line), wall {SHELL} m, {len(tube.lights)} lights, {s['holes']} holes"
            f" ({s['cut']} blocks), neck {len(tube.neck) // 9} layers"
        )
        ok &= n <= MAX_SEGMENT_BLOCKS
        print(
            f"   shapes get {100 * s['fit']:.2f}% of the wall's samples wrong;"
            f" {s['dropped']} blocks left out for the gap to large blocks;"
            f" {s['sealed']} sealed in; {s['leaks']} inside cells open to the outside"
        )
        print("  ", ", ".join(f"{k} {v}" for k, v in sorted(small.items())))
        _, _, i, f, stud = tube.plate
        print(
            f"   magnetic plate on the conveyor junction at {yard.blocks[i][3][0]},"
            f" its face {f:.3f} m along the plate's Up, {stud} cubes holding it"
        )
        # The cap is a half sphere as wide as the tube
        centers = tube.world(unpack(np.array(sorted(tube.blocks), np.int64)))
        outside = np.median(tube.line.query(centers, workers=-1)[0]) + SMALL / 2
        if tube.part == "far":
            near = np.linalg.norm(centers - tube.cap, axis=1) < tube.inner + 2
            cap = tube.line.query(centers[near], workers=-1)[0].max() + SMALL / 2
            print(
                f"   tube {outside:.2f} m, cap {cap:.2f} m from the center line,"
                f" centered {tube.cap_length:.2f} m past the arc's end"
            )
            ok &= cap <= outside + SMALL
        ok &= s["leaks"] == 0
        keys = np.array(sorted(tube.blocks), np.int64)
        points = tube.world(unpack(keys))
        inside = (points[:, 2] >= 0) & (points[:, 2] <= SPAN * GRID)
        for arm in arm_cells:
            inside &= np.linalg.norm(points - arm, axis=1) > 8
        if inside.any():
            clear.append(np.hypot(points[inside, 0], points[inside, 1]).min() - SMALL)
        if tube.part == "bridge":
            continue
        # The arm base stands on the arc's conveyor and out through a hole in
        # the tube: no tube block near it, its top beyond the tube's surface
        arm = arcs[a]["arm"]
        _, _, _, cells, _, _ = yard.blocks[arm["index"]]
        stator = Boxes(cells)
        gap = stator.distance(points, half=GRID / 2 + SMALL / 2).min()
        up = np.array(arm["up"], float)
        top = (np.array(cells, float) * GRID @ up).max() + GRID / 2
        start = GRID * np.array(arm["cell"], float)
        d = points - start
        side = np.linalg.norm(d - np.outer(d @ up, up), axis=1)
        surface = (d @ up)[side < OPENING + 2].max() + SMALL / 2
        stands = top - start @ up - (surface)
        print(
            f"   arm base ({arm['head']}) on the arc's conveyor, {gap:.2f} m from the"
            f" nearest tube block, its top {stands:.2f} m beyond the tube around it"
        )
        ok &= gap >= HARD_GAP and stands > 0
    # Where an arc's segments meet: a gap, but no more than the grids' offset
    for base, far in zip(tubes[::2], tubes[1::2]):
        a = base.world(unpack(np.array(sorted(base.blocks), np.int64)))
        b = far.world(unpack(np.array(sorted(far.blocks), np.int64)))
        s = base.seam

        def at_seam(p):
            return p[
                (np.abs(p[:, 1] - s["y"]) < 2)
                & (np.abs(p[:, 2] - s["z"]) < 10)
                & (np.abs(p[:, 0] - s["x"]) < 10)
            ]

        a, b = at_seam(a), at_seam(b)
        _, i = cKDTree(a).query(b, k=8)
        gap = np.linalg.norm(np.maximum(np.abs(b[:, None] - a[i]) - SMALL, 0), axis=2)
        print(
            f"segments meet {gap.min():.2f} m apart at y {s['y']:.2f} m at the arm"
            f" base (far rotor displacement {far.rotor['displacement']})"
        )
        ok &= SEAM_GAP <= gap.min() <= 0.25
    # And where the storage cover meets the base segments
    cover = tubes[4]
    c = cover.world(unpack(np.array(sorted(cover.blocks), np.int64)))
    for base in tubes[0:4:2]:
        x = base.x_seams[0]
        a = base.world(unpack(np.array(sorted(base.blocks), np.int64)))
        a = a[(a[:, 2] < 0) & (np.abs(a[:, 0] - x) < 2)]
        b = c[np.abs(c[:, 0] - x) < 2]
        _, i = cKDTree(a).query(b, k=8)
        gap = np.linalg.norm(np.maximum(np.abs(b[:, None] - a[i]) - SMALL, 0), axis=2)
        print(
            f"storage cover meets the base segment {gap.min():.2f} m apart at x {x:.2f} m"
        )
        ok &= 0.02 <= gap.min() <= 0.35
    print(f"all grids: blocks {total_blocks}, PCU {total_pcu}")
    cells = list(yard.cells)
    size = [
        int(max(c[a] for c in cells) - min(c[a] for c in cells) + 1) for a in range(3)
    ]
    print(f"main grid bounding box {size} blocks, {[GRID * s for s in size]} m")
    clear = min(clear)
    print(
        f"build volume {SPAN + 1} blocks long, nothing but the arm bases within"
        f" {clear:.1f} m of the axis"
    )
    open_ports, groups = networks(yard)
    print(
        f"open tube ports {len(open_ports)}, devices {sum(map(len, groups))} in"
        f" {len(groups)} network(s)"
    )
    for p in open_ports:
        print("  ", p)
    ok &= not open_ports and len(groups) == 1
    points, axis = volume(clear)
    for subtype in TOOLS:
        tools = [b for b in yard.blocks if b[0] == subtype]
        margins = np.array([tool_margin(b[3][0], b[2], points) for b in tools])
        reached = (margins >= 0).sum(0)
        axis_margin = np.array([tool_margin(b[3][0], b[2], axis) for b in tools]).max(0)
        print(
            f"{len(tools)} {subtype} reach {100 * (reached > 0).mean():.1f}% of the"
            f" build volume, {reached.mean():.1f} of them each point on average, the"
            f" axis with {axis_margin.min():.2f} m to spare"
        )
        ok &= axis_margin.min() >= 0
    return ok


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    yard, arcs = build()
    tubes, stats = build_tubes(yard, arcs)
    args.folder.mkdir(parents=True, exist_ok=True)
    (args.folder / "bp.sbc").write_bytes(blueprint_xml(yard, arcs, tubes))
    thumbnail(yard, tubes, args.folder / "thumb.png")
    sys.exit(0 if report(yard, arcs, tubes, stats) else 1)


if __name__ == "__main__":
    main()
