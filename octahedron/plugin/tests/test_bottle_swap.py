"""Checks on the test server that a station's Free Tools boxes swap used bottles for full ones
(SE-947): the station probe takes a full oxygen and a full hydrogen bottle out of a box and leaves
a half full and an empty one, as a player would, and the next refill takes the used ones out and
tops the box up again. With a rule that lists no hydrogen bottles, the empty one still goes, and
no full one replaces it. Prints one line per check and a pass/fail summary; exit code 0 = all
passed.

    OCT_TASK=SE-947 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_bottle_swap.py

tests/bottle_swap_cluster.py checks the swap on a cluster.

Run on nas on 2026-11-03: 11/11 checks.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-947")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check, probe  # noqa: E402
from test_client import log  # noqa: E402

STATION = "Alpha"
# The Free Tools rule tops up every minute
REFILL = 150
NO_HYDROGEN = re.compile(
    r"\s*<RefillItem>\s*<Item>GasContainerObject/HydrogenBottle</Item>.*?</RefillItem>",
    re.S,
)


def bottles(reply: str) -> dict[str, tuple[int, int]]:
    """subtype -> (full, used) over the station's Free Tools boxes, and boxes -> (count, 0)"""
    counts = {
        m[1]: (int(m[2]), int(m[3]))
        for m in re.finditer(r"(\w+Bottle) full=(\d+) used=(\d+)", reply)
    }
    boxes = re.search(r"boxes=(\d+)", reply)
    counts["boxes"] = (int(boxes[1]) if boxes else 0, 0)
    return counts


def no_used(counts: dict[str, tuple[int, int]]) -> bool:
    return all(
        counts.get(b, (0, 1))[1] == 0 for b in ("OxygenBottle", "HydrogenBottle")
    )


def swap_and_refill(probe, check, label: str, hydrogen_back: bool) -> None:
    """probe(args) runs a station probe command where the station is and returns its reply,
    check(ok, what) records a check"""

    def count() -> dict[str, tuple[int, int]]:
        return bottles(probe(f"bottles {STATION}"))

    before = st.wait_until(
        lambda: (c := count())["boxes"][0] and no_used(c) and c, REFILL, 5
    )
    check(bool(before), f"{label}: the boxes hold no used bottles: {before}")
    if not before:
        return
    oxygen, hydrogen = before["OxygenBottle"][0], before["HydrogenBottle"][0]

    swapped = bottles(probe(f"bottleswap {STATION}"))
    check(
        swapped.get("OxygenBottle") == (oxygen - 1, 1)
        and swapped.get("HydrogenBottle") == (hydrogen - 1, 1),
        f"{label}: a player leaves a half full and an empty bottle for full ones: {swapped}",
    )

    after = st.wait_until(lambda: no_used(c := count()) and c, REFILL, 5)
    log(f"{label}: after the refill {after}")
    check(bool(after), f"{label}: the refill takes the used bottles out: {after}")
    if not after:
        return
    check(
        after["OxygenBottle"] == (oxygen, 0),
        f"{label}: full oxygen bottles back to {oxygen}: {after['OxygenBottle']}",
    )
    want = hydrogen if hydrogen_back else hydrogen - 1
    check(
        after["HydrogenBottle"] == (want, 0),
        f"{label}: full hydrogen bottles at {want}: {after['HydrogenBottle']}",
    )


def run(remote) -> None:
    def sprobe(args: str) -> str:
        return probe(remote, args)

    swap_and_refill(sprobe, check, "world config", hydrogen_back=True)

    text, removed = NO_HYDROGEN.subn("", rig.CONFIG_SOURCE.read_text(encoding="utf-8"))
    check(removed == 1, "a config whose Free Tools rule lists no hydrogen bottles")
    rig.write_plugin_config(text)
    log(f"config without hydrogen bottles: {rig.chat(remote, '!oct reload', 3)}")
    swap_and_refill(sprobe, check, "no hydrogen rule", hydrogen_back=False)


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    rig.start_server()
    remote = rig.start_client()
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary("bottle swap")


if __name__ == "__main__":
    sys.exit(main())
