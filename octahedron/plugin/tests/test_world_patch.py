"""World patches on a plain server (SE-1358): the grids of a WorldPatches entry go into a kept world
once, and a world built with them gets none twice.

1. A world built from OCT_OLD_WORLDS (the commit the live worlds were built from, without the space
   elevators) with the config of this working copy: the server adds the patch's grids, logs
   APPLIED and keeps the id in the world's ledger.
2. The same world started again: the ledger has the patch, nothing is added.
3. A world built from this working copy (rig.WORLD_SOURCE): its ledger lists the patch from the
   start, nothing is added.

Each step stops the server, which saves, and counts the lane computers in the saved world. The
worlds get Trade Lanes MP (OCT_TRADE_LANES or its Workshop copy), whose blocks the computers are.

    OCT_TASK=SE-1358w [OCT_OLD_WORLDS=70fcefb] uv run --no-project --with httpx python tests/test_world_patch.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
"""

from __future__ import annotations

import io
import os
import re
import subprocess
import sys
import tarfile
import xml.etree.ElementTree as ET
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1358w")
import rig  # noqa: E402
from test_client import log  # noqa: E402
from test_elevator_entry import with_trade_lanes  # noqa: E402

OLD = os.environ.get("OCT_OLD_WORLDS", "70fcefb")
SEED = 2
VARIABLE = "Octahedron-WorldPatches"
APPLY_TIMEOUT = 120.0

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(("PASS " if ok else "FAIL ") + what)


def patches() -> list[tuple[str, list[str]]]:
    """(id, names of its grids) of each WorldPatches entry of the config"""
    config = ET.parse(rig.CONFIG_SOURCE).getroot()
    out = []
    for entry in config.iterfind("WorldPatches/WorldPatchEntry"):
        blueprint = ET.parse(
            rig.CONFIG_STORAGE / "Octahedron" / entry.findtext("Grids")
        ).getroot()
        out.append(
            (
                entry.findtext("Id"),
                [g.findtext("DisplayName") for g in blueprint.iter("CubeGrid")],
            )
        )
    return out


def old_world() -> Path:
    """The world build_world.py of commit OLD builds with SEED"""
    out = rig.TEST_ROOT / f"{rig.TASK}-old-{OLD}"
    if (out / "SANDBOX_0_0_0_.sbs").exists():
        return out
    repo = rig.PLUGIN.parent.parent
    src = rig.TEST_ROOT / f"{rig.TASK}-old-{OLD}-src"
    data = subprocess.check_output(["git", "archive", OLD, "octahedron"], cwd=repo)
    with tarfile.open(fileobj=io.BytesIO(data)) as tar:
        tar.extractall(src, filter="data")
    log(f"building the world of {OLD} into {out}")
    subprocess.run(
        [sys.executable, "build_world.py", str(out), "--seed", str(SEED)],
        cwd=src / "octahedron",
        check=True,
        stdout=subprocess.DEVNULL,
    )
    return out


def saved_grids() -> dict[str, int]:
    """Lane computer grids of the saved world by name, and how many there are of each"""
    text = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    names: dict[str, int] = {}
    for entity in re.findall(
        r"<MyObjectBuilder_EntityBase .*?</MyObjectBuilder_EntityBase>", text, re.S
    ):
        name = re.search(r"<DisplayName>(.*?)</DisplayName>", entity)
        if name and "<SubtypeName>TradeLaneComputer</SubtypeName>" in entity:
            key = name[1].replace("&gt;", ">")
            names[key] = names.get(key, 0) + 1
    return names


def ledger() -> set[str]:
    text = (rig.WORLD / "Sandbox.sbc").read_text(encoding="utf-8")
    m = re.search(rf"<Key>{VARIABLE}</Key>\s*<Value[^>]*>([^<]*)</Value>", text)
    return set(m[1].split(",")) if m and m[1] else set()


def run(label: str) -> str:
    """Starts the server on the prepared world, waits for its world patch lines and stops it: its log"""
    rig.start_server()
    try:
        rig.wait_until(
            lambda: "World patch LEDGER" in rig.server_game_log(),
            APPLY_TIMEOUT,
            f"{label}: the world patch ledger line",
        )
        ids = [i for i, _ in patches()]
        rig.wait_until(
            lambda: all(
                re.search(
                    rf"World patch (APPLIED |LEDGER .*){re.escape(i)}",
                    rig.server_game_log(),
                )
                for i in ids
            )
            or "not applied" in rig.server_game_log(),
            APPLY_TIMEOUT,
            f"{label}: the patches applied or in the ledger",
        )
        return rig.server_game_log()
    finally:
        rig.stop_server()


def lines(text: str) -> list[str]:
    return [
        l.split("World patch ", 1)[1].strip()
        for l in text.splitlines()
        if "World patch " in l
    ]


def main() -> int:
    rig.stop_server()
    wanted = patches()
    if not wanted:
        print("no WorldPatches in the config: nothing to test", flush=True)
        return 1
    names = {n for _, grids in wanted for n in grids}
    ids = {i for i, _ in wanted}
    rig.write_plugin_config()

    # 1. A world from before the patches
    os.environ["OCT_WORLD_SOURCE"] = str(old_world())
    rig._world_source = None
    rig.prepare_world()
    # Without the mod the game drops every grid of its lane computer blocks
    with_trade_lanes()
    before = saved_grids()
    check(
        not names & set(before),
        f"the world of {OLD} lacks the patch grids ({len(before)} lane computers)",
    )
    rig.prepare_server()
    text = run("old world")
    log("\n".join(lines(text)))
    applied = [l for l in lines(text) if l.startswith("APPLIED")]
    check(
        len(applied) == len(ids),
        f"applied once each: {len(applied)} APPLIED lines for {len(ids)} patches",
    )
    after = saved_grids()
    check(
        all(after.get(n) == 1 for n in names)
        and len(after) == len(before) + len(names),
        f"the saved world has each patch grid once: {len(after)} lane computers",
    )
    check(ledger() == ids, f"the ledger lists the patches: {sorted(ledger())}")

    # 2. Started again
    text = run("restart")
    check(
        not any(l.startswith("APPLIED") for l in lines(text)),
        "nothing applied on the restart",
    )
    check(saved_grids() == after, "the same lane computers after the restart")

    # 3. A world built with the patch grids
    os.environ.pop("OCT_WORLD_SOURCE")
    rig._world_source = None
    rig.prepare_world()
    with_trade_lanes()
    built = saved_grids()
    check(ledger() == ids, f"a world built now lists the patches: {sorted(ledger())}")
    text = run("new world")
    check(
        not any(l.startswith("APPLIED") for l in lines(text)),
        "nothing applied to a world built now",
    )
    check(
        saved_grids() == built and all(built.get(n) == 1 for n in names),
        "its lane computers stay as built",
    )

    failed = [what for ok, what in checks if not ok]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} world patch checks passed",
        flush=True,
    )
    for what in failed:
        print("FAILED", what, flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
