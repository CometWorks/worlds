"""Pre-seeds a world for a cluster (#1365): loads it once in a plain dedicated server with its mods, saves it, and
writes the saved world to OUT. The game creates the NPC factions the mods define at the first load of a world
that lacks them, each process with its own random ids; a cluster whose processes all load the seeded save
share one set of factions and founders.

    python3 tests/mes/ds/seed_world.py SRC OUT     (from plugin/)

SRC is a world folder (Sandbox.sbc, Sandbox_config.sbc, SANDBOX_0_0_0_.sbs); its mods are downloaded from the
Workshop by the server. Runs tests/mes/ds/run.py (test slot, RAM floor) with the probe only, no Octahedron plugin, and the
plan tests/mes/cluster/plan-seed.txt (save, wait, done). OUT must not exist. Prints the faction tags before and
after, and says so when the save holds no new faction.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

MES_TESTS = Path(__file__).resolve().parents[1]


def tags(world: Path) -> list[str]:
    return re.findall(
        r"<Tag>([^<]+)</Tag>", (world / "Sandbox.sbc").read_text(encoding="utf-8-sig")
    )


def main() -> int:
    source, out = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    if out.exists():
        raise SystemExit(f"{out} exists")
    before = tags(source)
    env = {**os.environ, "MES_WORLD": str(source), "MES_GATE": "0"}
    subprocess.run(
        [
            sys.executable,
            str(MES_TESTS / "ds/run.py"),
            str(MES_TESTS / "cluster/plan-seed.txt"),
        ],
        env=env,
        check=True,
    )
    import run  # noqa: E402  (tests/mes/ds/run.py: where the save lands)

    probe = (run.ROOT / "probe.txt").read_text(errors="replace")
    if not re.search(r"^saved$", probe, re.M):
        raise SystemExit(f"the server did not save the world: {run.ROOT / 'probe.txt'}")
    shutil.copytree(
        run.ROOT / "data/Saves/MesTest",
        out,
        ignore=shutil.ignore_patterns("Backup", "*.sbcB5", "*.sbsB5"),
    )
    after = tags(out)
    print(f"factions before: {' '.join(before)}\nfactions after:  {' '.join(after)}")
    if not set(after) - set(before):
        print(
            "no faction was added: the world held them all already, or its mods define none"
        )
    print(f"seeded world: {out}")
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    sys.exit(main())
