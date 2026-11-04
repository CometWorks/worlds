#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = ["numpy", "pillow", "scipy"]
# ///
"""Generates the Moon Shipyard (SE-986): a large grid and the asteroid around it.

    uv run make_moon_shipyard.py <folder>

Writes into the folder:
- bp.sbc and thumb.png: the large grid with its two arm heads, as a local
  blueprint folder (Blueprints/local/<name>) has them;
- asteroid.bin and asteroid.json: the rock around the arcs as voxel content,
  which the MoonShipyardAsteroid mod (moon-shipyard-asteroid/) turns into an
  asteroid in game;
and prints the checks: block counts, PCU, the conveyor network, how much of the
build volume the welders and the deconstructors reach, and how the rock keeps
clear of the large blocks.

Two arcs, each a helix making one full turn around a build volume about 140 m
across, the second the first turned 180 degrees around the axis. The base of
the arcs is the end where they are joined. Each arc is a line of industrial
conveyor pipes with OmniArc welders and deconstructors inline, placed where
they reach most of the build volume, and dark blue lights on T pipes. Below
the base each arc's line turns down to a junction; two pipes run out of it to
connectors past the rock's surface. A conveyor between the junctions joins the
arcs, eight cargo containers and eight large reactors on either side of it,
all under a half block thick armor cover with wedges on its edges and corners.
A row of five 5x5 landing pads, each with a connector, sits in the cover's
outside face, fed from that conveyor. In the middle of each arc a locked
advanced rotor faces the axis, the base of an arm that holds the ship: one with
a large head, the other with the medium head. The pipes and the cover are
matte black, everything else black carbon fiber.

Each arc runs in a tunnel through a rock that follows it from the junction to
a round end past the arc's far end. The rock's surface is rough with scattered
lumps, and small holes let the lights shine through. The connectors, the
conveyor between the arcs and the arm bases come out through openings. The
rock and the grid never touch, and the grid is static, so nothing needs
locking.
Reuses the SE-952 helpers and its template blueprint for the block XML.
"""

import argparse
import base64
import copy
import itertools
import json
import math
import random
import struct
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
# The arcs' center lines: radius and length along the axis, in blocks
R = 30
SPAN = 112
# Tool stations per arc, a welder and a deconstructor each
STATIONS = 14
# The conveyor between the arcs, in blocks below the arcs' base
BRIDGE_Z = -5
# Storage boxes (3x3x3 blocks) on either side of that conveyor, per arc
BRIDGE_SLOTS = 4
BRIDGE_KINDS = ("LargeBlockLargeContainer", "LargeBlockLargeGenerator")
# How far the tool turrets swing from their block's center (m)
TURRET = 2.2
# How many blocks out from its junction each connector sits, past the rock's
# surface so that ships can dock on it
CONNECTOR_OUT = 4
# The radius of the openings around the connectors, the conveyor between the
# arcs and the arm bases (m)
OPENING = GRID / 2 * math.sqrt(2) + 0.75

# The rock, in meters (a voxel is 1 m): the room it leaves around the large
# blocks, the wall's thickness, how round its inner bulges are, how rough its
# surface is (noise amplitude and scale), the lumps on it (radius range, one
# per this many square meters of surface), the holes over the lights (radius,
# a second one tilted up to this many degrees)
ROCK_GAP = 1.0
ROCK_WALL = 2.5
BLEND = 1.5
ROUGH = (0.5, 2.5)
LUMP_RADIUS = (0.6, 1.6)
LUMP_AREA = 5.0
HOLE_RADIUS = 0.9
HOLE_TILT = 35
# The rock's storage: its edge (voxels), the voxel of the grid's origin, the
# chunks the content goes out in, the asteroid's name and its material
STORAGE = 512
ORIGIN_VOXEL = (256, 256, 112)
CHUNK = 32
ROCK_NAME = "MoonShipyard"
ROCK_MATERIAL = "Uraninite_01"
SEED = 986

BLACK = ((0.0, -0.96, -0.5), "CarbonFibre_Armor")  # the game's default black
MATTE = ((0.0, -0.96, -0.5), None)  # the same black, no skin
# Lights on the conveyor, one per this many meters of arc
LIGHT_SPACING = 10.0
LIGHT_RGB = (0.05, 0.15, 0.85)  # dark blue
LIGHT_RADIUS = 12
LIGHT_INTENSITY = 8
# The OmniArc tools' ToolCore settings (its ToolRepo, protobuf): activated,
# no beam drawing, the mode below, primary action, every relation targeted, no
# work colour, the turret tracking targets. Without Activated and TrackTargets
# a tool doesn't work on its own.
TOOL_MODES = {"OmniArcBeam": 8, "OmniArcDestro": 16}  # weld, grind
TOOL_SETTINGS = "08 01 10 00 18 {mode:02x} 20 00 28 0f 30 00 3a 0f 0d 00 00 00 00 25 00 00 00 00 3d 00 00 00 00 40 01"
# The welders start on, the deconstructors off
TOOLS_ON = {"OmniArcBeam"}

ALL = [X, NX, Y, NY, Z, NZ]
# Subtype: (object builder type, size, conveyor ports as (local cell,
# direction), PCU, DLC), local X the block's Right, Y its Up, Z its Backward,
# as in make_helix_shipyard.BLOCKS
PIPES = (
    "LargeBlockConveyorPipeSeamless",
    "LargeBlockConveyorPipeCorner",
    "LargeBlockConveyorPipeT",
    "LargeBlockConveyorPipeIntersection",
)
BLOCKS = {
    **{pipe: helix.BLOCKS[pipe] for pipe in PIPES + ("LargeBlockConveyorPipeCap",)},
    # Its port at its Bottom
    "LargeBlockConveyorPipeEnd": (
        "ConveyorConnector",
        (1, 1, 1),
        [(O, NY)],
        10,
        "HeavyIndustry",
    ),
    "LargeBlockConveyorPipeJunction": (
        "Conveyor",
        (1, 1, 1),
        [(O, d) for d in ALL],
        10,
        "HeavyIndustry",
    ),
    "OmniArcBeam": ("ConveyorSorter", (1, 1, 1), [(O, Y), (O, NY)], 250, None),
    "OmniArcDestro": ("ConveyorSorter", (1, 1, 1), [(O, Y), (O, NY)], 250, None),
    "Connector": ("ShipConnector", (1, 1, 1), [(O, Z)], 125, None),
    # A 5x5 landing pad, a connector in the middle of its top, the conveyor
    # port under it
    "LargeBlockStructural_PlatformConnector": (
        "ShipConnector",
        (5, 1, 5),
        [((2, 0, 2), NY)],
        125,
        None,
    ),
    # The cover: half blocks against what they cover (their Front), the
    # half slope a wedge along its Front and Bottom, the half slope corner a
    # corner of one at its Right, Back and Bottom
    "LargeHalfArmorBlock": ("CubeBlock", (1, 1, 1), [], 1, None),
    "LargeHalfSlopeArmorBlock": ("CubeBlock", (1, 1, 1), [], 1, None),
    "LargeBlockArmorHalfSlopeCorner": ("CubeBlock", (1, 1, 1), [], 1, None),
    "LargeBlockLargeGenerator": helix.BLOCKS["LargeBlockLargeGenerator"],
    "LargeBlockLargeContainer": (
        "CargoContainer",
        (3, 3, 3),
        [((1, 0, 1), NY)],
        10,
        None,
    ),
    "LargeAdvancedStator": ("MotorAdvancedStator", (1, 2, 1), [(O, NY)], 100, None),
    # The large grid's interior light, mounted on its Back
    "SmallLight": ("InteriorLight", (1, 1, 1), [], 20, None),
    # Heads of the arm bases
    "LargeAdvancedRotor": ("MotorAdvancedRotor", (1, 1, 1), [], 1, None),
    "SmallAdvancedRotor": ("MotorAdvancedRotor", (3, 2, 3), [], 1, None),
}
TOOLS = ("OmniArcBeam", "OmniArcDestro")
DEVICES = {
    *TOOLS,
    "LargeBlockLargeGenerator",
    "LargeBlockLargeContainer",
    "Connector",
    "LargeBlockStructural_PlatformConnector",
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
TUBES = {*PIPES, "LargeBlockConveyorPipeJunction", "LargeBlockConveyorPipeEnd"}
# What may sit on a tube's port that leads nowhere
PORT_COVERS = {"SmallLight", "LargeBlockConveyorPipeCap"}
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
    """(subtype, forward, up) of the conveyor pipe that connects to dirs."""
    if len(dirs) == 1:
        (d,) = dirs
        return "LargeBlockConveyorPipeEnd", helix.perpendicular(neg(d)), neg(d)
    try:
        return helix.pipe_piece(dirs)
    except ValueError:
        return "LargeBlockConveyorPipeJunction", Z, Y


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
        out = (sign, 0, 0)
        line = helix.rasterize(lambda t: center_line(start, t), length)
        assert line[0] == (sign * R, 0, 0), line[0]
        # Down from the arc's base to the junction, then across to the middle
        junction = (sign * R, 0, BRIDGE_Z)
        down = [(sign * R, 0, z) for z in range(BRIDGE_Z + 1, 0)]
        across = [(x, 0, BRIDGE_Z) for x in range(0, sign * R, sign)]
        connect(across + [junction] + down + line, arc)
        # The connectors on pipes out of the junction, clear of the rock
        for forward, up in ((out, Z), (NZ, out)):
            stub = [add(junction, scale(forward, k)) for k in range(CONNECTOR_OUT)]
            connect(stub, arc)
            tip = add(junction, scale(forward, CONNECTOR_OUT))
            assert free(tip)
            yard.block("Connector", forward, up, tip, BLACK, arc)
            conveyor[stub[-1]].add(forward)

        # The arm base: a locked advanced rotor in the middle, facing the axis.
        # A rotor whose Up is -X must not face +Z, nor one whose Up is -Y face
        # +X: its head turns over on paste.
        def rotor(cell, up, head, displacement=0.0):
            forward = {X: Z, NX: NZ, NY: NX}.get(up, X)
            assert all(free(add(cell, scale(up, k))) for k in (1, 2, 3))
            conveyor[cell].add(up)
            index = yard.block(
                "LargeAdvancedStator", forward, up, add(cell, up), BLACK, arc
            )
            return dict(index=index, head=head, displacement=displacement)

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
        end = line[-1]
        a_ = start + 2 * math.pi
        tangent = (-math.sin(a_), math.cos(a_), SPAN / (2 * math.pi * R))
        arcs.append(
            dict(
                sign=sign,
                line=line,
                junction=junction,
                arm=arm,
                end=end,
                tangent=np.array(tangent) / np.linalg.norm(tangent),
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

    # Landing pads in a row under the storage, on the base's outside, each on a
    # pipe down from the conveyor between the arcs; a cap closes the port a
    # pad's pipe leaves open where the storage joins the conveyor too
    span = BRIDGE_SLOTS * 3
    pad_z = BRIDGE_Z - 2
    for px in range(-span + 2, span, 5):
        side = 0 if px > 0 else 1 if px < 0 else None
        connect([(px, 0, BRIDGE_Z), (px, 0, BRIDGE_Z - 1)], side)
        conveyor[(px, 0, BRIDGE_Z - 1)].add(NZ)
        cells = [(px + i, j, pad_z) for i in range(-2, 3) for j in range(-2, 3)]
        yard.box("LargeBlockStructural_PlatformConnector", X, NZ, cells, BLACK, side)
        if len(conveyor[(px, 0, BRIDGE_Z)]) == 5:
            yard.block("LargeBlockConveyorPipeCap", NZ, X, (px, 0, BRIDGE_Z + 1), MATTE)
            conveyor[(px, 0, BRIDGE_Z)].add(Z)
    # A half block thick armor cover around the storage, wedges on its edges
    # and corners
    inside = [(-span, span), (-3, 3), (BRIDGE_Z - 1, BRIDGE_Z + 1)]
    for cell in itertools.product(*(range(lo - 1, hi + 2) for lo, hi in inside)):
        normal = [(cell[a] > hi) - (cell[a] < lo) for a, (lo, hi) in enumerate(inside)]
        out = [
            tuple(n if b == a else 0 for b in range(3))
            for a, n in enumerate(normal)
            if n
        ]
        if not out or not free(cell):
            continue
        if len(out) == 1:
            up = next(d for d in ALL if d not in (out[0], neg(out[0])))
            yard.block("LargeHalfArmorBlock", neg(out[0]), up, cell, MATTE)
        elif len(out) == 2:
            yard.block("LargeHalfSlopeArmorBlock", neg(out[0]), out[1], cell, MATTE)
        else:
            # Right, Back and Bottom against the storage
            a, b, c = next(
                q for q in itertools.permutations(out) if cross(q[1], q[2]) == neg(q[0])
            )
            yard.block("LargeBlockArmorHalfSlopeCorner", b, c, cell, MATTE)

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

    # Dark blue lights on the arcs' conveyor, one every LIGHT_SPACING, each on
    # the branch of a T pipe, facing away from it in a random direction
    taken = set(yard.cells) | set(conveyor)
    branch = {}
    rng = random.Random(SEED)
    for a, arc in enumerate(arcs):
        line = arc["line"]
        arc["lights"] = []
        last = -1e9
        walked = 0.0
        for j in range(2, len(line) - 2):
            walked += GRID
            cell = line[j]
            dirs = conveyor[cell]
            if cell in tools or len(dirs) != 2 or walked - last < LIGHT_SPACING:
                continue
            # a T's branch: square to a straight run, or opposite one end of
            # a corner
            sides = [
                d
                for d in ALL
                if d not in dirs
                and tube_piece(dirs | {d})[0] == "LargeBlockConveyorPipeT"
            ]
            rng.shuffle(sides)
            for d in sides:
                spot = add(cell, d)
                # clear of other blocks and of the turrets' swing
                if spot in taken or any(
                    add(spot, e) in taken for e in ALL if e != neg(d)
                ):
                    continue
                if any(math.dist(spot, t) * GRID < TURRET + GRID for t in arc["tools"]):
                    continue
                branch[cell] = d
                arc["lights"].append((spot, d))
                taken.add(spot)
                last = walked
                break

    for cell, dirs in sorted(conveyor.items()):
        if cell in tools:
            subtype, forward, up = tools[cell]
            assert dirs == {up, neg(up)}
        else:
            subtype, forward, up = tube_piece(
                dirs | {branch[cell]} if cell in branch else dirs
            )
        yard.block(subtype, forward, up, cell, MATTE, owner[cell])
    for a, arc in enumerate(arcs):
        for spot, d in arc["lights"]:
            up = next(e for e in ALL if e not in (d, neg(d)))
            yard.block("SmallLight", d, up, spot, BLACK, a)
    return yard, arcs


# Small grid tubes


# Signed distances


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


# The asteroid


class Rock:
    """The rock around the arcs as a signed distance (m, negative inside the
    rock) over the grid's frame: each arc's tube from its junction to a round
    end, hollowed out around the large blocks, with lumps and holes."""

    def __init__(self, yard, arcs):
        rng = np.random.default_rng(SEED)
        paths, inner = [], []
        for a, arc in enumerate(arcs):
            t = np.linspace(0, 1, 40000)
            helix_points = GRID * np.stack(center_line(STARTS[a], t), axis=1)
            j = GRID * np.array(arc["junction"], float)
            s = np.linspace(0, 1, 100)[:, None]
            path = np.concatenate([j * (1 - s) + helix_points[0] * s, helix_points])
            paths.append(path)
            # The inside: the arc's conveyor blocks with their corners in it
            line = cKDTree(path)
            cells = np.array(arc["line"], float) * GRID
            corners = np.array(
                [[x, y, z] for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)]
            )
            far = line.query((cells[:, None, :] + corners * GRID / 2).reshape(-1, 3))[0]
            inner.append(np.percentile(far, 95) + ROCK_GAP)
        self.path = np.concatenate(paths)
        self.line = cKDTree(self.path)
        self.inner = max(inner)
        self.outer = self.inner + ROCK_WALL
        self.own = Boxes(list(yard.cells))
        tools = [c for arc in arcs for c in arc["tools"]]
        self.tools = cKDTree(GRID * np.array(tools, float))
        # The openings: the connectors and the conveyor between the arcs at
        # each junction, and the arm bases
        self.openings = []
        for arc in arcs:
            j = GRID * np.array(arc["junction"], float)
            for d in ((arc["sign"], 0, 0), (-arc["sign"], 0, 0), (0, 0, -1)):
                self.openings.append((j, np.array(d, float), OPENING))
            arm = arc["arm"]
            self.openings.append(
                (
                    GRID * np.array(arm["cell"], float),
                    np.array(arm["up"], float),
                    OPENING,
                )
            )
        # A hole over each light, and one more tilted off it
        self.holes = []
        for arc in arcs:
            for spot, d in arc["lights"]:
                start = GRID * np.array(spot, float)
                d = np.array(d, float)
                self.holes.append((start, d, HOLE_RADIUS))
                side = np.cross(d, rng.normal(size=3))
                side /= np.linalg.norm(side)
                tilt = math.radians(rng.uniform(15, HOLE_TILT))
                self.holes.append(
                    (start, d * math.cos(tilt) + side * math.sin(tilt), HOLE_RADIUS)
                )
        # Lumps on the surface: centers just outside it, all around the arcs
        length = np.linalg.norm(np.diff(self.path, axis=0), axis=1).sum()
        area = 2 * math.pi * self.outer * length
        n = int(area / LUMP_AREA)
        along = self.path[rng.integers(0, len(self.path), n)]
        direction = rng.normal(size=(n, 3))
        direction /= np.linalg.norm(direction, axis=1)[:, None]
        centers = along + direction * self.outer
        # put each on the surface of the arc it came from: nearest path point
        _, i = self.line.query(centers)
        out = centers - self.path[i]
        out /= np.maximum(np.linalg.norm(out, axis=1), 1e-9)[:, None]
        self.lump_radius = rng.uniform(*LUMP_RADIUS, n)
        self.lumps = cKDTree(self.path[i] + out * (self.outer + 0.2))
        self.lump_count = n
        # Value noise on a coarse lattice for the surface's roughness
        self.noise = rng.uniform(-1, 1, (64, 64, 160))

    def roughness(self, p):
        q = (p / ROUGH[1]) % np.array(self.noise.shape)
        return ROUGH[0] * ndimage.map_coordinates(self.noise, q.T, order=1, mode="wrap")

    def hollow(self, p):
        """Signed distance (m) to the tunnel, negative inside it."""
        h = self.line.query(p, distance_upper_bound=self.outer + 8, workers=-1)[0]
        h = np.minimum(h, 1e3) - self.inner
        h = smin(h, self.own.distance(p, bound=10.0) - ROCK_GAP, BLEND)
        turret = self.tools.query(p, distance_upper_bound=TURRET + 6, workers=-1)[0]
        return smin(h, np.minimum(turret, 1e3) - TURRET - ROCK_GAP, BLEND)

    def distance(self, p):
        """Signed distance (m) to the rock, negative inside it."""
        d = self.line.query(p, distance_upper_bound=self.outer + 8, workers=-1)[0]
        outer = np.minimum(d, 1e3) - self.outer - self.roughness(p)
        near, i = self.lumps.query(p, k=4, distance_upper_bound=LUMP_RADIUS[1] + 1)
        valid = i < self.lump_count
        r = self.lump_radius[np.minimum(i, self.lump_count - 1)]
        lumps = np.where(valid, near - r, 1e3).min(1)
        s = np.maximum(np.minimum(outer, lumps), -self.hollow(p))
        for start, direction, radius in self.openings + self.holes:
            d = p - start
            along = d @ direction
            side = np.linalg.norm(d - np.outer(along, direction), axis=1)
            s = np.maximum(s, -np.maximum(side - radius, -along))
        return s

    def chunks(self):
        """{chunk coordinate: content (CHUNK^3 bytes, x fastest)} of the chunks
        with any rock in them; content 255 is full, the surface at 127."""
        origin = np.array(ORIGIN_VOXEL, float)
        reach = self.outer + LUMP_RADIUS[1] + ROUGH[0] + 2
        half = CHUNK / 2 * math.sqrt(3)
        n = STORAGE // CHUNK
        grid = np.stack(np.meshgrid(*[np.arange(n)] * 3, indexing="ij"), -1).reshape(
            -1, 3
        )
        centers = grid * CHUNK + CHUNK / 2 - origin
        near = self.line.query(centers, distance_upper_bound=reach + half)[0] < 1e9
        r = np.arange(CHUNK)
        local = np.stack(np.meshgrid(r, r, r, indexing="ij"), -1).reshape(-1, 3)
        out = {}
        for c in grid[near]:
            p = c * CHUNK + local - origin
            s = chunked(self.distance, p.astype(float))
            content = np.clip((0.5 - s) * 255, 0, 255).round().astype(np.uint8)
            if content.any():
                # x fastest: the order MyStorageData indexes in
                out[tuple(int(v) for v in c)] = (
                    content.reshape(CHUNK, CHUNK, CHUNK).transpose(2, 1, 0).tobytes()
                )
        return out


def rock_files(rock, folder):
    """Writes asteroid.bin (the chunks, run-length coded) and asteroid.json;
    returns the chunks."""
    chunks = rock.chunks()
    data = bytearray(b"MSA1")
    material = ROCK_MATERIAL.encode()
    data += struct.pack("<iii", STORAGE, CHUNK, len(chunks))
    data += struct.pack("<i", len(material)) + material
    for c, content in sorted(chunks.items()):
        runs = []
        v = np.frombuffer(content, np.uint8)
        edges = np.flatnonzero(np.diff(v)) + 1
        starts = np.concatenate([[0], edges])
        ends = np.concatenate([edges, [len(v)]])
        for s, e in zip(starts, ends):
            n = e - s
            while n > 0:
                runs.append(struct.pack("<BH", v[s], min(n, 65535)))
                n -= 65535
        data += struct.pack("<iiii", *c, len(runs)) + b"".join(runs)
    (folder / "asteroid.bin").write_bytes(bytes(data))
    (folder / "asteroid.json").write_text(
        json.dumps(
            dict(
                name=ROCK_NAME,
                material=ROCK_MATERIAL,
                storage=STORAGE,
                origin_voxel=ORIGIN_VOXEL,
                chunks=len(chunks),
            ),
            indent=2,
        )
        + "\n"
    )
    return chunks


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
            elif subtype in TUBES and (
                j is None or yard.blocks[j][0] not in PORT_COVERS
            ):
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


def element(subtype, forward, up, corner, templates, ids, color=BLACK):
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
        "SmallLight": (
            ("Enabled", "true"),
            ("Radius", LIGHT_RADIUS),
            ("ColorRed", LIGHT_RGB[0]),
            ("ColorGreen", LIGHT_RGB[1]),
            ("ColorBlue", LIGHT_RGB[2]),
            ("ColorAlpha", 1),
            ("Intensity", LIGHT_INTENSITY),
        ),
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
    set_vector(b.find("ColorMaskHSV"), color[0])
    if color[1]:
        b.find("SkinSubtypeId").text = color[1]
    else:
        b.remove(b.find("SkinSubtypeId"))
    return b


def blueprint_xml(yard, arcs):
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
    templates["OmniArcDestro"] = copy.deepcopy(templates["OmniArcBeam"])
    templates["OmniArcDestro"].find("SubtypeName").text = "OmniArcDestro"
    for subtype, mode in TOOL_MODES.items():
        settings = bytes.fromhex(TOOL_SETTINGS.format(mode=mode))
        for value in templates[subtype].iter("Value"):
            value.text = base64.b64encode(settings).decode()
        templates[subtype].find("Enabled").text = str(subtype in TOOLS_ON).lower()
    ids = helix.entity_ids(986)

    elements = []
    blocks.clear()
    for subtype, forward, up, cells, _, color in yard.blocks:
        corner = [min(c[a] for c in cells) for a in range(3)]
        b = element(subtype, forward, up, corner, templates, ids, color)
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
    static = grid.find("IsStatic")
    if static is None:
        static = ET.SubElement(grid, "IsStatic")
    static.text = "true"
    for a, arc in enumerate(arcs):
        rotor, name = arc["arm"], f"{NAME} arm base {a + 1}"
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
        grids.append(g)

    # The deconstructors grind their own grid too (AffectOwnGrid), except
    # where its blocks can't be destroyed
    for g in grids.findall("CubeGrid"):
        g.find("DestructibleBlocks").text = "false"
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


def thumbnail(yard, chunks, path, size=512):
    """A view across the axis, turned and tipped: the rock's surface in grey,
    tools, rotors and lights marked."""
    marks = {
        "OmniArcBeam": (120, 220, 255),
        "OmniArcDestro": (255, 140, 60),
        "LargeAdvancedStator": (255, 255, 255),
        "SmallLight": (60, 90, 255),
    }
    points, colors = [], []
    for subtype, _, _, cells, _, _ in yard.blocks:
        if subtype in marks:
            points += [np.array(c, float) for c in cells]
            colors += [marks[subtype]] * len(cells)
    origin = np.array(ORIGIN_VOXEL)
    r = np.arange(CHUNK)
    local = np.stack(np.meshgrid(r, r, r, indexing="ij"), -1).reshape(-1, 3)
    for c, content in chunks.items():
        v = np.frombuffer(content, np.uint8).reshape(CHUNK, CHUNK, CHUNK)
        v = v.transpose(2, 1, 0).reshape(-1)
        surface = (v > 60) & (v < 200)
        p = (np.array(c) * CHUNK + local[surface] - origin) / GRID
        points += list(p[::3])
        colors += [None] * len(p[::3])
    yaw, pitch = math.radians(25), math.radians(20)
    dots = []
    for (x, y, z), mark in zip(points, colors):
        a = z * math.cos(yaw) - x * math.sin(yaw)
        b = z * math.sin(yaw) + x * math.cos(yaw)
        sy = y * math.cos(pitch) - b * math.sin(pitch)
        depth = y * math.sin(pitch) + b * math.cos(pitch)
        shade = int(60 + 70 * (R - depth) / (2 * R))
        dots.append((depth, a, -sy, mark or (shade, shade - 4, shade - 12), mark))
    left, right = min(d[1] for d in dots), max(d[1] for d in dots)
    low, high = min(d[2] for d in dots), max(d[2] for d in dots)
    k = (size - 32) / max(right - left, high - low)
    image = Image.new("RGB", (size, size), (8, 10, 16))
    draw = ImageDraw.Draw(image)
    for _, sx, sy, color, mark in sorted(dots, key=lambda d: -d[0]):
        px = (size - k * (right - left)) / 2 + k * (sx - left)
        py = (size - k * (high - low)) / 2 + k * (sy - low)
        r = 2.0 if mark else 0.7
        draw.ellipse((px - r, py - r, px + r, py + r), fill=color)
    image.save(path)


def report(yard, arcs, rock, chunks):
    """Prints the checks; True when they pass."""
    counts = Counter(b[0] for b in yard.blocks)
    for subtype, n in sorted(counts.items()):
        print(f"{n:6} {subtype}")
    pcu = sum(BLOCKS[s][3] * n for s, n in counts.items())
    print(f"main grid: blocks {len(yard.blocks)}, PCU {pcu}")
    ok = True

    # The rock keeps clear of every large block but the parts that stand out
    # through its openings
    corners = np.array(
        [[x, y, z] for x in (-1, 0, 1) for y in (-1, 0, 1) for z in (-1, 0, 1)]
    )
    where = [(b[0], c) for b in yard.blocks for c in b[3]]
    p = GRID * (np.array([c for _, c in where], float)[:, None] + corners * 0.5)
    gaps = chunked(rock.distance, p.reshape(-1, 3)).reshape(len(where), -1).min(1)
    worst = (gaps.min(), where[int(gaps.argmin())])
    print(
        f"rock: tunnel radius {rock.inner:.2f} m, outside radius {rock.outer:.2f} m"
        f" (+ roughness up to {ROUGH[0]} m and {rock.lump_count} lumps),"
        f" {len(rock.holes)} holes over {sum(len(a['lights']) for a in arcs)} lights,"
        f" {ROCK_MATERIAL}"
    )
    print(
        f"   nearest large block {worst[0]:.2f} m from the rock's surface"
        f" ({worst[1][0]} at {worst[1][1]})"
    )
    ok &= worst[0] >= 0.5
    full = sum(np.frombuffer(c, np.uint8).sum() for c in chunks.values()) / 255
    print(
        f"   {len(chunks)} chunks of {CHUNK}^3 voxels, about {full / 1000:.0f}k m3 of"
        f" rock, storage {STORAGE}^3, the grid's origin at voxel {ORIGIN_VOXEL}"
    )
    lo = np.array(ORIGIN_VOXEL) + np.min([GRID * np.array(c) for c in yard.cells], 0)
    hi = np.array(ORIGIN_VOXEL) + np.max([GRID * np.array(c) for c in yard.cells], 0)
    edge = [min(c) for c in zip(*chunks)], [max(c) for c in zip(*chunks)]
    inside = (
        min(edge[0]) >= 1
        and max(edge[1]) < STORAGE // CHUNK - 1
        and (lo > 8).all()
        and (hi < STORAGE - 8).all()
    )
    print(f"   rock and grid inside the storage with a chunk to spare: {inside}")
    ok &= inside

    open_ports, groups = networks(yard)
    print(
        f"open tube ports {len(open_ports)}, devices {sum(map(len, groups))} in"
        f" {len(groups)} network(s)"
    )
    for p in open_ports:
        print("  ", p)
    ok &= not open_ports and len(groups) == 1
    points, axis = volume(R * GRID - 5)
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
    rock = Rock(yard, arcs)
    args.folder.mkdir(parents=True, exist_ok=True)
    (args.folder / "bp.sbc").write_bytes(blueprint_xml(yard, arcs))
    chunks = rock_files(rock, args.folder)
    thumbnail(yard, chunks, args.folder / "thumb.png")
    sys.exit(0 if report(yard, arcs, rock, chunks) else 1)


if __name__ == "__main__":
    main()
