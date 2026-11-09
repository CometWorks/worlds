"""Checks the lore data pads in the stations' Free Tools boxes (SE-1308).

Offline, on config/octahedron.cfg: no pad names more than one of the four rare ores or more than
one ice asteroid, and together they cover all four ores and all four ice asteroids.

On the test server, with the pad rule's 600 s cut to 2 s: every Free Tools box of a station
holds one of the configured pads; a taken pad comes back as another one; over repeated takes
every pad turns up; the GPS lines of the ice pads are the centers of the ice asteroids in the
world's save, as build_world.py placed them; a pad whose voxel map the world lacks never turns
up; a player's own pad in a box neither counts nor goes.

    OCT_TASK=SE-1308 uv run --no-project --with httpx python tests/test_data_pads.py

`--offline` runs only the config checks. tests/data_pads_cluster.py checks the pads on a cluster.

Run on nas on 2026-11-09: 16/16 checks.
"""

from __future__ import annotations

import gzip
import math
import os
import re
import struct
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-1308")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log  # noqa: E402

STATION = "Alpha"
BOXES = 4
INTERVAL = 2
ORES = ("gold", "silver", "uranium", "platinum")
GPS = re.compile(r"\{GPS:([^:}]+):([^}]+)\}")
MISSING = "Lost survey"
# The pad rule, told from the Free Tools rule for the tools by its Datapad item
PAD_RULE = re.compile(
    r"(<IntervalSeconds>)600(</IntervalSeconds>\s*<Items>\s*<RefillItem>\s*<Item>Datapad/Datapad<)"
)


def config_pads(text: str) -> list[tuple[str, str]]:
    root = ET.fromstring(text)
    return [
        (p.findtext("Title"), p.findtext("Text") or "")
        for p in root.iter("DataPadEntry")
    ]


def check_split(pads: list[tuple[str, str]]) -> None:
    check(len(pads) > 0, f"the config has {len(pads)} data pads")
    titles = [t for t, _ in pads]
    check(len(set(titles)) == len(titles), "every pad has its own title")
    ores, ice, whole = set(), set(), []
    for title, text in pads:
        named = {o for o in ORES if o in text.lower()}
        rocks = {m[1] for m in GPS.finditer(text)}
        ores |= named
        ice |= rocks
        if len(named) > 1 or len(rocks) > 1:
            whole.append(title)
    check(not whole, f"no pad tells more than one ore or asteroid: {whole}")
    check(ores == set(ORES), f"together the pads name every rare ore: {sorted(ores)}")
    want = {f"Octahedron-Ice-{i}" for i in range(1, 5)}
    check(ice == want, f"together the pads give all 4 ice asteroids: {sorted(ice)}")


def test_config(text: str) -> str:
    """The config with the pad rule's interval cut to seconds and a pad whose voxel map the
    world lacks"""
    text, count = PAD_RULE.subn(rf"\g<1>{INTERVAL}\g<2>", text)
    check(count == 1, "the Free Tools pad rule's interval cut to seconds")
    return text.replace(
        "</DataPads>",
        f"<DataPadEntry><Title>{MISSING}</Title>"
        "<Text>{GPS:No-Such-Rock:Nowhere}</Text></DataPadEntry></DataPads>",
        1,
    )


def storage_size(path) -> int:
    """Edge length of a saved octree storage, as build_world.storage_size"""
    data = gzip.open(path).read()
    i = data.index(b"\x01\x01\x11") + 3
    return struct.unpack_from("<4i", data, i)[1]


def vec(element) -> tuple[float, float, float]:
    return tuple(float(element.get(a)) for a in "xyz")


def ice_centers() -> dict[str, tuple[float, float, float]]:
    """Storage name -> center of the ice asteroids in the world copy: corner + R * half, as
    build_world.voxel_map placed them"""
    root = ET.parse(rig.WORLD / "SANDBOX_0_0_0_.sbs").getroot()
    centers = {}
    for entity in root.iter("MyObjectBuilder_EntityBase"):
        name = entity.findtext("StorageName") or ""
        if not name.startswith("Octahedron-Ice-"):
            continue
        place = entity.find("PositionAndOrientation")
        corner, f, u = (vec(place.find(k)) for k in ("Position", "Forward", "Up"))
        r = (
            f[1] * u[2] - f[2] * u[1],
            f[2] * u[0] - f[0] * u[2],
            f[0] * u[1] - f[1] * u[0],
        )
        half = storage_size(rig.WORLD / f"{name}.vx2") / 2
        centers[name] = tuple(corner[i] + (r[i] + u[i] - f[i]) * half for i in range(3))
    return centers


def boxes(reply: str) -> list[list[str]]:
    """The pads of each box, a GPS pad as title@x,y,z"""
    body = reply.split("PADS ", 1)[1] if "PADS " in reply else ""
    return [[p for p in b.split("|") if p] for b in body.split(";")]


def run(probe, check, pads: list[tuple[str, str]], centers) -> None:
    """probe(args) runs a station probe command where the station is and returns its reply,
    check(ok, what) records a check"""
    ours = {t for t, _ in pads}
    full = lambda b: len(b) == BOXES and all(  # noqa: E731
        sum(p.split("@")[0] in ours for p in box) == 1 for box in b
    )

    first = st.wait_until(
        lambda: full(b := boxes(probe(f"datapads {STATION}"))) and b, 60, 2
    )
    check(bool(first), f"every Free Tools box holds one of our pads: {first}")

    seen: dict[str, str] = {}
    misses = 0
    for _ in range(40):
        got = st.wait_until(
            lambda: full(b := boxes(probe(f"datapads {STATION}"))) and b,
            INTERVAL * 5,
            1,
        )
        if not got:
            misses += 1
            if misses > 2:
                break
            continue
        for box in got:
            for pad in box:
                title, _, gps = pad.partition("@")
                seen[title] = gps
        if ours - {MISSING} <= set(seen):
            break
        probe(f"datapads {STATION} take")
    log(f"pads seen: {sorted(seen)}")
    check(
        misses <= 2, f"a taken pad comes back within {INTERVAL * 5} s ({misses} misses)"
    )
    check(
        ours - {MISSING} <= set(seen),
        f"every pad turns up: missing {sorted(ours - {MISSING} - set(seen))}",
    )
    check(MISSING not in seen, "a pad whose voxel map the world lacks never turns up")

    want = {t: GPS.search(x)[1] for t, x in pads if GPS.search(x) and t != MISSING}
    for title, storage in want.items():
        got = seen.get(title, "")
        center = centers.get(storage)
        ok = (
            bool(got and center)
            and math.dist(tuple(float(c) for c in got.split(",")), center) < 2
        )
        check(ok, f"{title}: GPS {got} is the center of {storage} {center}")

    probe(f"datapads {STATION} take")
    probe(f"datapads {STATION} add")
    after = st.wait_until(
        lambda: full(b := boxes(probe(f"datapads {STATION}"))) and b,
        INTERVAL * 5,
        1,
    )
    check(
        bool(after) and "Player note" in after[0],
        f"a player's own pad stays and doesn't count: {after}",
    )


def main() -> int:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    check_split(config_pads(text))
    if "--offline" in sys.argv:
        return st.summary("data pads")

    text = test_config(text)
    pads = config_pads(text)

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    centers = ice_centers()
    check(len(centers) == 4, f"the world has 4 ice asteroids: {centers}")
    rig.write_plugin_config(text)
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()
    start = time.monotonic()
    try:
        run(lambda args: probe(remote, args), check, pads, centers)
    finally:
        log(f"in game for {time.monotonic() - start:.0f} s")
        rig.stop_client()
        rig.stop_server()
    return st.summary("data pads")


if __name__ == "__main__":
    sys.exit(main())
