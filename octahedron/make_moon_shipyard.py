#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Generates the Moon Shipyard blueprint (SE-986).

    uv run make_moon_shipyard.py <blueprint folder>

Writes bp.sbc and thumb.png into the folder, as a local blueprint folder
(Blueprints/local/<name>) has them, and prints the checks: block counts, PCU,
open conveyor ports, the conveyor network, the build volume and how much of it
the welders and the deconstructors reach.

Two arcs, each a helix making one full turn around a build volume about 140 m
across, the second the first turned 180 degrees around the axis. Each arc is a
round tube 5 blocks across around a line of conveyor tubes, its surface fitted
with armor slopes, corners and half blocks. Stations along the arcs branch off
that line through a T junction toward the axis, to a second T that stands on
the arc's inner face between an OmniArc welder and an OmniArc deconstructor, so
their turrets see the build volume. Each arc ends in a rounded pod along the
axis: two large reactors and two large cargo containers beside the conveyor
line, and a connector at the tip. Two thin beams join the pods at one end, so
the shipyard is a single grid, and a bare industrial pipe between them joins
the arcs' conveyors. In the middle of each arc an advanced rotor faces the
axis, the base of an arm that holds the ship: one with a large head, the other
with the small head. Dark blue interior lights sit in notches of the inner
faces. Everything is black carbon fiber.
Reuses the SE-952 helpers and its template blueprint for the block XML.
"""

import argparse
import copy
import math
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy.spatial import cKDTree

import make_helix_shipyard as helix
from make_helix_shipyard import NX, NY, NZ, O, X, Y, Z, add, cross, neg, scale

NAME = "Moon Shipyard"
GRID = helix.GRID

# Radius of the arcs' center lines in blocks (75 m, 150 m across)
R = 30
# Radius of each arc's tube in blocks, the one the armor shapes fit best
TUBE_RADIUS = 2.5
# Blocks along the axis that the arcs' center lines span, one full turn
SPAN = 112
# Blocks each pod reaches past the end of its arc, the connector at the tip:
# overall 2 * POD + SPAN + 1 = 127 blocks (317.5 m)
POD = 7
# The pods are boxes with rounded edges: half their size away from the axis and
# around it, and the radius of the edges, in blocks
POD_HALF = (2.5, 4.5)
POD_ROUND = 2.0
# Samples per block edge when fitting the armor shapes to the surface
SAMPLES = 6
# Stations per arc, evenly spaced along it, each a welder and a deconstructor
STATIONS = 14
# The tools' Up, the axis their turret turns around, in order of preference:
# around the axis, or along it
TOOL_UP = ("around", "along")
# A light every this many conveyor cells along an arc
LIGHT_EVERY = 14
# Beams joining the two pods at the bottom end: their offsets across the pod
# and their depth into it
BEAM_OFFSETS = (-2, 2)
BEAM_DEPTH = 3

BLACK = ((0.0, -0.96, -0.5), "CarbonFibre_Armor")  # the game's default black
LIGHT_RGB = (0.05, 0.15, 0.85)  # dark blue
LIGHT_RADIUS = 10
LIGHT_INTENSITY = 4
# ToolCore settings the deconstructors carry in Viktor's Omni Arc Beam Shipyard
DESTRO_STORAGE = "CAAQABgQIAAoDzAAOg8NAAAAACUAAAAAPQAAAABAAA=="

ALL = [X, NX, Y, NY, Z, NZ]
# Subtype: (object builder type, size, conveyor ports as (local cell,
# direction), PCU, DLC), local X the block's Right, Y its Up, Z its Backward,
# as in make_helix_shipyard.BLOCKS
BLOCKS = {
    "LargeBlockArmorBlock": ("CubeBlock", (1, 1, 1), [], 1, None),
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
    "SmallLight": ("InteriorLight", (1, 1, 1), [], 20, None),
    "LargeGridBeamBlock": ("CubeBlock", (1, 1, 1), [], 1, "HeavyIndustry"),
    "LargeGridBeamBlockEnd": ("CubeBlock", (1, 1, 1), [], 1, "HeavyIndustry"),
    "LargeBlockConveyorPipeSeamless": helix.BLOCKS["LargeBlockConveyorPipeSeamless"],
}
# The armor shapes the surface is fitted with: which points of the block, in
# local coordinates from 0 to 1 (x its Right, y its Up, z its Backward), are
# solid. Read off their mount points: a slope's Front and Bottom are full.
SHAPES = {
    "LargeBlockArmorBlock": lambda x, y, z: x == x,
    "LargeBlockArmorRoundSlope": lambda x, y, z: y * y + z * z <= 1,
    "LargeBlockArmorSlope": lambda x, y, z: y + z <= 1,
    "LargeBlockArmorCorner": lambda x, y, z: 1 - x + y + z <= 1,
    "LargeBlockArmorCornerInv": lambda x, y, z: 1 - x + y + z >= 1,
    "LargeBlockArmorSlope2Base": lambda x, y, z: y <= 1 - z / 2,
    "LargeBlockArmorSlope2Tip": lambda x, y, z: y <= (1 - z) / 2,
    "LargeHalfArmorBlock": lambda x, y, z: z <= 0.5,
    "LargeHalfSlopeArmorBlock": lambda x, y, z: y + z <= 0.5,
}
for _shape in SHAPES:
    BLOCKS.setdefault(_shape, ("CubeBlock", (1, 1, 1), [], 1, None))
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
# Head grids of the arm bases, first arc then second: (head subtype, the head
# grid's origin from the stator's base cell along its (Up, Forward, Right) in
# meters), as measured on the rotors of the Omni Arc Beam Shipyard
HEADS = [
    ("LargeAdvancedRotor", (0.2, 0.0, 0.0)),
    ("SmallAdvancedRotor", (0.2, 0.5, -0.5)),
]


class Yard:
    def __init__(self):
        self.blocks = []  # (subtype, forward, up, cells, ports, color)
        self.cells = {}  # cell: index into blocks

    def block(self, subtype, forward, up, corner, color):
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
        return len(self.blocks) - 1

    def box(self, subtype, right, up, cells, color):
        """Places a block on the box of cells, its Right and Up along right and up."""
        forward = cross(up, right)
        toward = add(right, up, neg(forward))
        corner = tuple(
            (min if toward[a] > 0 else max)(c[a] for c in cells) for a in range(3)
        )
        self.block(subtype, forward, up, corner, color)
        assert sorted(self.blocks[-1][3]) == sorted(cells), subtype


def tube_piece(dirs):
    """(subtype, forward, up) of the conveyor tube that connects to dirs."""
    subtype, forward, up = helix.pipe_piece(dirs)
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


STARTS = (0.0, math.pi)


def center_line(start, t):
    angle = start + 2 * np.pi * t
    return (R * np.cos(angle), R * np.sin(angle), SPAN * t)


class Solid:
    """Signed distance in blocks to the surface the armor follows, negative
    inside: the arcs' tubes and the pods, each pod given as (end, u, v, w)."""

    def __init__(self, pods):
        t = np.linspace(0, 1, 20000)
        self.curves = [
            cKDTree(np.stack(center_line(start, t), axis=1)) for start in STARTS
        ]
        self.pods = pods

    def tube(self, p):
        return (
            np.min(
                [
                    c.query(p, distance_upper_bound=TUBE_RADIUS + 3, workers=-1)[0]
                    for c in self.curves
                ],
                axis=0,
            )
            - TUBE_RADIUS
        )

    def __call__(self, p):
        d = self.tube(p)
        half = np.array([*POD_HALF, POD / 2 + 0.5]) - POD_ROUND
        for end, *frame in self.pods:
            q = (p - end) @ np.array(frame, float).T
            q = np.abs(q - (0, 0, POD / 2)) - half
            box = np.linalg.norm(np.maximum(q, 0), axis=1) + np.minimum(q.max(1), 0)
            d = np.minimum(d, box - POD_ROUND)
        return d


def masks():
    """The armor shapes in every orientation, without repeats: (subtype, forward,
    up, which samples of a block are solid, the directions of its full faces)."""
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


def axis_toward(v, exclude):
    """The grid direction closest to v, not along any of exclude."""
    return max(
        (d for d in ALL if d not in exclude and neg(d) not in exclude),
        key=lambda d: sum(a * b for a, b in zip(d, v)),
        default=None,
    )


def build():
    """Returns the yard and the stators as (block index, head)."""
    yard = Yard()
    conveyor = {}  # cell: directions to connect, turned into tubes at the end
    exposed = set()  # cells that stay open: around the tools, in front of lights
    shown = set()  # conveyor cells left uncovered: the stations' Ts, the pipe
    stators = []

    def connect(cells):
        for cell in cells:
            conveyor.setdefault(cell, set())
        for a, b in zip(cells, cells[1:]):
            step = add(b, neg(a))
            assert sum(map(abs, step)) == 1, (a, b)
            conveyor[a].add(step)
            conveyor[b].add(neg(step))

    def free(cell):
        return cell not in yard.cells and cell not in conveyor

    # Frames of the pods: u away from the axis, v around it, w away from the arc
    lines, pods = [], []
    for start in STARTS:
        sign = 1 if start == 0 else -1
        line = helix.rasterize(
            lambda t: center_line(start, t), math.hypot(2 * math.pi * R, SPAN)
        )
        lines.append(line)
        for end, w in ((line[0], NZ), (line[-1], Z)):
            pods.append((end, (sign, 0, 0), (0, sign, 0), w))
    solid = Solid(pods)
    xs = np.arange(-R - 5, R + 6)
    zs = np.arange(-POD - 3, SPAN + POD + 4)
    space = np.stack(np.meshgrid(xs, xs, zs, indexing="ij"), -1).reshape(-1, 3)
    tube = {tuple(c) for c in space[solid.tube(space) <= 0]}
    depth = dict(zip(map(tuple, space), solid(space)))

    for strand, line in enumerate(lines):
        tips = []
        for end, u, v, w in pods[2 * strand : 2 * strand + 2]:

            def at(du, dv, dw):
                return add(end, scale(u, du), scale(v, dv), scale(w, dw))

            stem = [at(0, 0, dw) for dw in range(1, POD)]
            line = stem[::-1] + line if w == NZ else line + stem
            yard.block("Connector", w, u, at(0, 0, POD), BLACK)
            tips.append((stem[-1], w))
            exposed.add(at(0, 0, POD + 1))
            kinds = ["LargeBlockLargeContainer", "LargeBlockLargeGenerator"]
            for side in (1, -1):
                for slot, subtype in enumerate(kinds if side > 0 else kinds[::-1]):
                    cells = [
                        at(du, side * dv, 3 * slot + dw)
                        for du in (-1, 0, 1)
                        for dv in (1, 2, 3)
                        for dw in (1, 2, 3)
                    ]
                    yard.box(subtype, w, scale(v, side), cells, BLACK)
                    conveyor.setdefault(at(0, 0, 3 * slot + 2), set()).add(
                        scale(v, side)
                    )
        connect(line)
        for cell, w in tips:
            conveyor[cell].add(w)

        def straight(j):
            return add(line[j], neg(line[j - 1])) == add(line[j + 1], neg(line[j]))

        def inward_at(j):
            along = add(line[j + 1], neg(line[j]))
            return axis_toward((-line[j][0], -line[j][1], 0), [along, Z]), along

        def branch(j, inward, fits):
            """From line[j] along inward to the arc's inner face: (stub, face cell),
            or None. fits(face) checks the cells around the face cell."""
            cell, stub = line[j], []
            for _ in range(4):
                cell = add(cell, inward)
                if not free(cell) or cell not in tube:
                    return None
                if add(cell, inward) not in tube:
                    return (stub, cell) if fits(cell) else None
                stub.append(cell)
            return None

        def site(t, fits, used):
            """The straight conveyor cell nearest to the arc at t where a branch
            fits: (index, inward, stub, face cell, fits' result)."""
            target = center_line(STARTS[strand], t)
            order = sorted(
                range(POD, len(line) - POD), key=lambda j: math.dist(line[j], target)
            )
            for j in order[:80]:
                if not straight(j) or any(abs(j - k) < 3 for k in used):
                    continue
                inward, along = inward_at(j)
                result = []
                found = branch(
                    j,
                    inward,
                    lambda f: result.append(fits(f, inward, along)) or result[-1],
                )
                if found:
                    used.append(j)
                    return (j, inward) + found + (result[-1],)
            raise AssertionError(f"no branch site near t={t}")

        used = []

        # The arm base in the middle of the arc, its head toward the axis, its
        # conveyor port joined to the line
        def stator_fits(face, inward, along):
            return all(free(add(face, scale(inward, k))) for k in (1, 2, 3))

        j, inward, stub, face, _ = site(0.5, stator_fits, used)
        connect([line[j]] + stub)
        conveyor[stub[-1] if stub else line[j]].add(inward)
        index = yard.block("LargeAdvancedStator", Z, inward, face, BLACK)
        exposed |= {add(face, scale(inward, k)) for k in (2, 3)}
        stators.append((index, HEADS[strand]))

        # The stations: the stub reaches through the inner face to a T standing on
        # it between the two tools. Each tool's turret turns around its Up, so
        # nothing but the face behind it may sit in that plane.
        def station(face, inward, along):
            t = add(face, inward)
            ups = {"around": axis_toward(along, [inward, Z]), "along": Z}
            for up in (ups[name] for name in TOOL_UP):
                tools = [add(t, up), add(t, neg(up))]
                ring = [
                    add(c, d)
                    for c in tools
                    for d in ALL
                    if d not in (up, neg(up), neg(inward))
                ]
                if (
                    all(free(c) and c not in tube for c in [t] + tools)
                    and all(free(c) for c in ring)
                    and all(add(c, neg(inward)) in tube for c in tools)
                ):
                    return up
            return None

        for n in range(STATIONS):
            j, inward, stub, face, up = site((n + 0.5) / STATIONS, station, used)
            t = add(face, inward)
            connect([line[j]] + stub + [face, t])
            shown.add(t)
            for subtype, d in zip(TOOLS, (up, neg(up))):
                conveyor[t].add(d)
                yard.block(subtype, inward, d, add(t, d), BLACK)
            # Open around the tools and the T, but for the face they stand on
            for c in (t, add(t, up), add(t, neg(up))):
                exposed |= {add(c, d) for d in ALL if d != neg(inward)}

        # Lights in notches of the inner face, between the stations
        for j in range(POD + LIGHT_EVERY // 2, len(line) - POD, LIGHT_EVERY):
            for k in range(j, j + LIGHT_EVERY // 2):
                if any(abs(k - n) < 4 for n in used):
                    continue
                steps = [add(line[k], neg(line[k - 1])), add(line[k + 1], neg(line[k]))]
                inward = axis_toward((-line[k][0], -line[k][1], 0), steps + [Z])
                found = inward and branch(k, inward, lambda f: True)
                if found:
                    face = found[1]
                    yard.block("SmallLight", inward, Z, face, BLACK)
                    exposed.add(add(face, inward))
                    break

    # The beams join the pods at the bottom end, along X between their inner
    # walls, and a bare industrial pipe between them joins the arcs' conveyors
    beams = [(x, dy, -BEAM_DEPTH) for dy in BEAM_OFFSETS for x in range(3 - R, R - 2)]
    across = [(x, 0, -BEAM_DEPTH) for x in range(-R, R + 1)]
    connect(across)
    pipe = set(across[1:-1])
    shown |= {c for c in pipe if depth[c] > 0}
    exposed |= {add(c, d) for c in shown & pipe for d in ALL} - pipe

    for cell, dirs in sorted(conveyor.items()):
        subtype, forward, up = tube_piece(dirs)
        if cell in pipe:
            subtype = "LargeBlockConveyorPipeSeamless"
        yard.block(subtype, forward, up, cell, BLACK)
    for cell in beams:
        # End pieces at the pods, their flat Front against the pod
        if abs(cell[0]) == R - 3:
            forward = (1 if cell[0] > 0 else -1, 0, 0)
            yard.block("LargeGridBeamBlockEnd", forward, Z, cell, BLACK)
        else:
            yard.block("LargeGridBeamBlock", X, Z, cell, BLACK)
    yard.fit = armor(yard, solid, depth, exposed, shown)
    return yard, stators


def armor(yard, solid, depth, exposed, shown):
    """Fills the solid with armor shapes, each block the shape and orientation
    whose solid samples agree most with the surface. A block beside a hidden
    conveyor or storage block covers it with a full face. Returns the share of
    samples the shapes get wrong where the surface cuts a block."""
    offsets, shapes = masks()
    hidden = {
        c
        for c, i in yard.cells.items()
        if c not in shown
        and yard.blocks[i][0]
        in TUBES
        | {
            "LargeBlockConveyor",
            "LargeBlockConveyorPipeSeamless",
            "LargeBlockLargeContainer",
            "LargeBlockLargeGenerator",
        }
    }
    need = {}
    for c in hidden:
        for d in ALL:
            need.setdefault(add(c, d), set()).add(neg(d))
    cells = sorted(
        c
        for c in {c for c, v in depth.items() if v < 0.9} | set(need)
        if c not in yard.cells and c not in exposed
    )
    # Only blocks the surface may cut need samples: the rest are all in or all out
    inside = np.zeros((len(cells), len(offsets)), bool)
    inside[[depth.get(c, 1) <= -0.87 for c in cells]] = True
    cut = np.array([-0.87 < depth.get(c, 1) < 0.87 for c in cells])
    points = (np.array(cells, float)[cut][:, None, :] + offsets).reshape(-1, 3)
    inside[cut] = (solid(points) <= 0).reshape(-1, len(offsets))
    m = np.array([s[3] for s in shapes])
    agree = inside.astype(np.int32) @ m.T + (~inside).astype(np.int32) @ (~m).T
    empty = (~inside).sum(1)
    for row, c in enumerate(cells):
        if c in need:
            empty[row] = -1
            agree[row, [not need[c] <= s[4] for s in shapes]] = -1
    best = agree.argmax(1)
    place = agree[np.arange(len(cells)), best] > empty
    chosen = {c: shapes[k] for c, k, p in zip(cells, best, place) if p}
    # Cubes with cubes all around them stay empty: nothing can see them
    cube = {c for c, s in chosen.items() if s[0] == "LargeBlockArmorBlock"}
    for c, (subtype, forward, up, _, _) in chosen.items():
        if c in cube and all(add(c, d) in cube for d in ALL):
            continue
        yard.block(subtype, forward, up, c, BLACK)
    yard.masks = {c: s[3] for c, s in chosen.items()}
    cut = inside.any(1) & ~inside.all(1)
    wrong = len(offsets) - np.maximum(agree.max(1), empty)
    return wrong[cut].sum() / (cut.sum() * len(offsets))


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


def blueprint_xml(yard, stators):
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
    for subtype, forward, up, cells, _, (hsv, skin) in yard.blocks:
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
                ET.SubElement(b, tag)
            b.find("SubtypeName").text = subtype
        if subtype == "SmallLight":
            for tag, value in (
                ("Enabled", "true"),
                ("Radius", LIGHT_RADIUS),
                ("ColorRed", LIGHT_RGB[0]),
                ("ColorGreen", LIGHT_RGB[1]),
                ("ColorBlue", LIGHT_RGB[2]),
                ("ColorAlpha", 1),
                ("Intensity", LIGHT_INTENSITY),
            ):
                ET.SubElement(b, tag).text = str(value)
        if b.find("EntityId") is not None:
            b.find("EntityId").text = str(next(ids))
        set_vector(b.find("Min"), [min(c[a] for c in cells) for a in range(3)])
        b.find("BlockOrientation").attrib = {
            "Forward": helix.DIRECTION_NAMES[forward],
            "Up": helix.DIRECTION_NAMES[up],
        }
        set_vector(b.find("ColorMaskHSV"), hsv)
        b.find("SkinSubtypeId").text = skin
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
    for index, (head, (du, df, dr)) in stators:
        _, forward, up, cells, _, (hsv, skin) = yard.blocks[index]
        g = copy.deepcopy(heads[head])
        top = g.find("CubeBlocks")[0]
        top.find("EntityId").text = str(next(ids))
        top.find("ParentEntityId").text = elements[index].findtext("EntityId")
        elements[index].find("TopBlockId").text = top.findtext("EntityId")
        for tag in ("ColorMaskHSV", "SkinSubtypeId"):
            for e in top.findall(tag):
                top.remove(e)
        color = ET.Element("ColorMaskHSV")
        set_vector(color, hsv)
        top.insert(2, color)
        skin_element = ET.Element("SkinSubtypeId")
        skin_element.text = skin
        top.insert(3, skin_element)
        right = cross(forward, up)
        position = [
            GRID * cells[0][a] + du * up[a] + df * forward[a] + dr * right[a]
            for a in range(3)
        ]
        place(g, f"{NAME} arm base", position, forward, up)
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


def thumbnail(yard, path, size=512):
    """A view across the axis, turned and tipped; tools, lights and rotors marked."""
    marks = {
        "OmniArcBeam": (120, 220, 255),
        "OmniArcDestro": (255, 140, 60),
        "SmallLight": (60, 90, 255),
        "LargeAdvancedStator": (255, 255, 255),
    }
    yaw, pitch = math.radians(25), math.radians(20)
    dots = []
    for subtype, _, _, cells, _, _ in yard.blocks:
        mark = marks.get(subtype)
        for x, y, z in cells:
            a = z * math.cos(yaw) - x * math.sin(yaw)
            b = z * math.sin(yaw) + x * math.cos(yaw)
            sy = y * math.cos(pitch) - b * math.sin(pitch)
            depth = y * math.sin(pitch) + b * math.cos(pitch)
            shade = int(60 + 70 * (R - depth) / (2 * R))
            dots.append((depth, a, -sy, mark or (shade, shade, shade + 12), mark))
    left, right = min(d[1] for d in dots), max(d[1] for d in dots)
    low, high = min(d[2] for d in dots), max(d[2] for d in dots)
    k = (size - 32) / max(right - left, high - low)
    image = Image.new("RGB", (size, size), (8, 10, 16))
    draw = ImageDraw.Draw(image)
    for _, sx, sy, color, mark in sorted(dots, key=lambda d: -d[0]):
        px = (size - k * (right - left)) / 2 + k * (sx - left)
        py = (size - k * (high - low)) / 2 + k * (sy - low)
        r = 3 if mark else 1.2
        draw.ellipse((px - r, py - r, px + r, py + r), fill=color)
    image.save(path)


def reach(yard, subtype, clear, step=3):
    """How well the tools of subtype reach the build volume (closer than clear
    to the axis, along the arcs' span) with a clear line of sight from their
    turret's ball: the share of its blocks they reach (every step-th block), the
    share some tool has in range but none sees, and the least margin in meters
    by which one reaches and sees each block on the axis."""
    cells = np.array(list(yard.cells))
    low = cells.min(0)
    occupied = np.full(cells.max(0) - low + 1, -1, np.int32)  # -2: a full block
    shapes = []
    for c, i in yard.cells.items():
        occupied[tuple(np.subtract(c, low))] = len(shapes) if c in yard.masks else -2
        if c in yard.masks:
            shapes.append(yard.masks[c])
    shapes = np.array(shapes)

    def blocked(points, own):
        cell = np.floor(points + 0.5).astype(int)
        index = cell - low
        inside = ((index >= 0) & (index < occupied.shape)).all(-1)
        what = np.full(points.shape[:-1], -1)
        what[inside] = occupied[tuple(index[inside].T)]
        sub = np.clip(((points - cell + 0.5) * SAMPLES).astype(int), 0, SAMPLES - 1)
        sub = (sub[..., 0] * SAMPLES + sub[..., 1]) * SAMPLES + sub[..., 2]
        hit = (what == -2) | ((what >= 0) & shapes[np.maximum(what, 0), sub])
        return (hit & (cell != own).any(-1)).any(-1)

    xs = np.arange(-math.floor(clear), math.floor(clear) + 1)
    x, y, z = np.meshgrid(xs, xs, np.arange(0, SPAN + 1), indexing="ij")
    disc = np.hypot(x, y) < clear
    volume = np.stack([x[disc], y[disc], z[disc]], 1)
    volume = volume[(volume % step == 0).all(1)]
    axis = np.stack([np.zeros(SPAN + 1), np.zeros(SPAN + 1), np.arange(SPAN + 1)], 1)
    targets = np.concatenate([volume, axis]).astype(float)
    seen = np.full(len(targets), -np.inf)
    near = np.zeros(len(targets), bool)
    for s, _, up, block_cells, _, _ in yard.blocks:
        if s != subtype:
            continue
        center, up = np.array(block_cells[0], float), np.array(up, float)
        d = (targets - center) * GRID
        level = d - np.outer(d @ up, up)
        ball = level / np.maximum(np.linalg.norm(level, axis=1), 1e-9)[:, None]
        aim = d - ball * helix.BALL
        distance = np.maximum(np.linalg.norm(aim, axis=1), 1e-9)
        tilt = np.degrees(np.arcsin(np.abs(aim @ up) / distance))
        margin = np.where(tilt <= helix.TILT, helix.REACH - distance, -np.inf)
        ok = margin >= 0
        near |= ok
        start = center + ball[ok] * helix.BALL / GRID
        steps = np.linspace(0, 1, 4 * int(np.ceil(np.abs(distance[ok]).max() / GRID)))
        line = start[:, None] + (targets[ok] - start)[:, None] * steps[None, :, None]
        clear_line = ~blocked(line, np.array(block_cells[0]))
        seen[np.flatnonzero(ok)[clear_line]] = np.maximum(
            seen[np.flatnonzero(ok)[clear_line]], margin[ok][clear_line]
        )
    n = len(volume)
    reached = seen[:n] >= 0
    return reached.mean(), (near[:n] & ~reached).mean(), seen[n:].min()


def report(yard):
    """Prints the checks; True when they pass."""
    counts = Counter(b[0] for b in yard.blocks)
    for subtype, n in sorted(counts.items()):
        print(f"{n:6} {subtype}")
    pcu = sum(BLOCKS[s][3] * n for s, n in counts.items())
    print(f"blocks {len(yard.blocks)}, PCU {pcu} (main grid)")
    print(f"armor shapes get {100 * yard.fit:.2f}% of the surface's samples wrong")
    cells = list(yard.cells)
    size = [
        int(max(c[a] for c in cells) - min(c[a] for c in cells) + 1) for a in range(3)
    ]
    print(f"bounding box {size} blocks, {[GRID * s for s in size]} m")
    clear = min(
        math.hypot(c[0], c[1])
        for b in yard.blocks
        if b[0] not in (*TOOLS, "LargeAdvancedStator", "ConveyorTubeT")
        for c in b[3]
        if 0 <= c[2] <= SPAN
    )
    print(
        f"build volume {SPAN + 1} blocks long, no block but the stations and the arm"
        f" bases within {clear:.2f} blocks ({GRID * clear:.1f} m) of the axis"
    )
    open_ports, groups = networks(yard)
    print(
        f"open tube ports {len(open_ports)}, devices {sum(map(len, groups))} in"
        f" {len(groups)} network(s)"
    )
    for p in open_ports:
        print("  ", p)
    ok = not open_ports and len(groups) == 1
    for subtype in TOOLS:
        share, hidden, axis = reach(yard, subtype, clear)
        print(
            f"{counts[subtype]} {subtype} reach and see {100 * share:.1f}% of the"
            f" build volume ({100 * hidden:.1f}% in range but out of sight), the axis"
            f" with {axis:.2f} m to spare"
        )
        ok &= axis >= 0
    return ok


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    yard, stators = build()
    args.folder.mkdir(parents=True, exist_ok=True)
    (args.folder / "bp.sbc").write_bytes(blueprint_xml(yard, stators))
    thumbnail(yard, args.folder / "thumb.png")
    sys.exit(0 if report(yard) else 1)


if __name__ == "__main__":
    main()
