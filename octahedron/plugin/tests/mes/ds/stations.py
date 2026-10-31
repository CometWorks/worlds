"""SE-829: economy only on the world's stations. Loads a built Octahedron world in one dedicated server,
with its mods and the Octahedron plugin, runs an economy tick and plan-stations.txt, and judges it:

- the game's generator added no trade faction and no station;
- the world's stations are stocked, and only a pirate base of the control spawn has stock besides them;
- MES finds no base-game economy outpost or Trade Operators Coalition group eligible, and spawns none.

    python3 tests/mes/ds/stations.py WORLD [--expect-stations] [--generate]     (from plugin/, as a background job)

--expect-stations: the check of main, passes when MES does spawn them, so the rig is shown to see them.
--generate: GenerateFactionsOnStart true in a copy of the world, the positive control for the game's own
generator; prints what it made and doesn't judge. Output: ~/.se-test/<MES_TASK>-ds0.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

MES_TESTS = Path(__file__).resolve().parents[1]
PLUGIN = MES_TESTS.parents[1]
os.environ.setdefault("MES_TASK", "se829-stations")
sys.path.insert(0, str(MES_TESTS / "ds"))
import run  # noqa: E402

# MES groups of economy stations: the base game's outposts of its Builder, Miner, Trader and Military
# factions and everything of Trade Operators Coalition
ECONOMY = re.compile(r"^(P([1-9]|1[0-2])_|TOC-)")


def prepared(world: Path, generate: bool) -> Path:
    out = run.ROOT.parent / f"{os.environ['MES_TASK']}-world"
    shutil.rmtree(out, ignore_errors=True)
    shutil.copytree(world, out, ignore=shutil.ignore_patterns("Backup", "*.sbsB5"))
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        text = (out / name).read_text(encoding="utf-8-sig")
        text = text.replace(
            "<EnableEconomy>true</EnableEconomy>",
            "<EnableEconomy>true</EnableEconomy><EconomyTickInSeconds>30</EconomyTickInSeconds>",
        )
        # The ore mod is local (id 0); the planets' vanilla ores do here
        text = re.sub(
            r'<ModItem FriendlyName="OctahedronOres">.*?</ModItem>',
            "",
            text,
            flags=re.S,
        )
        if generate:
            text = text.replace(
                "<GenerateFactionsOnStart>false", "<GenerateFactionsOnStart>true"
            )
        (out / name).write_text(text, encoding="utf-8")
    return out


def main() -> int:
    world = Path(sys.argv[1]).resolve()
    expect_stations = "--expect-stations" in sys.argv
    generate = "--generate" in sys.argv
    env = {
        **os.environ,
        "MES_WORLD": str(prepared(world, generate)),
        "MES_WORLD_NAME": "Octahedron",
        "MES_GATE": "1",
    }
    subprocess.run(
        [
            sys.executable,
            str(MES_TESTS / "ds/run.py"),
            str(MES_TESTS / "ds/plan-stations.txt"),
            str(PLUGIN.parent / "config/octahedron.cfg"),
        ],
        env=env,
        check=True,
    )
    probe = (run.ROOT / "probe.txt").read_text(errors="replace")
    errors = re.findall(r"^error .*", probe, re.M)
    factions = re.findall(r"^factions end (\S+) (\S+) stations=(\d+)", probe, re.M)
    with_stations = {t: int(n) for t, _, n in factions if int(n)}
    stores = re.findall(
        r"^stores end \d+ (\S+) store=(\d+) contract=(\d+) items=(\d+) station=(\w+) faction=(\S+)",
        probe,
        re.M,
    )
    ours = [s for s in stores if s[4] == "True"]
    others = [s for s in stores if s[4] != "True"]
    eligible = re.findall(r"^eligible (\S+) (\S+) (\S+) \[(.*)\]", probe, re.M)
    economy = sorted({(g, f) for _, _, g, f in eligible if ECONOMY.match(g)})
    spawned = re.findall(r"^spawned (\S+) (\S+) (\w+)", probe, re.M)

    print(f"probe errors: {len(errors)}")
    for error in errors[:3]:
        print(f"  {error[:300]}")
    print(f"factions with stations: {with_stations}; all: {[f[0] for f in factions]}")
    print(f"eligible groups: {len(eligible)}, economy: {len(economy)}")
    for group, faction in economy:
        print(f"  {group} [{faction}]")
    for _, group, result in spawned:
        print(f"spawned {group}: {result}")
    print(
        f"our stations with a store: {len(ours)}, stocked: {sum(int(s[3]) > 0 for s in ours)}"
    )
    print("other grids with store or contract blocks:")
    for name, store, contract, items, _, faction in others:
        print(
            f"  {name} store={store} contract={contract} items={items} faction={faction}"
        )
    if generate:
        return 0

    pirates = [r for _, g, r in spawned if g == "P15_PirateStronghold"]
    economy_spawns = [g for _, g, r in spawned if ECONOMY.match(g) and r == "True"]
    checks = {
        "no probe errors": not errors,
        "only OCT has stations, the 7 of the world": with_stations == {"OCT": 7},
        "the 7 stations are stocked": len(ours) == 7
        and all(int(s[3]) > 0 and s[5] == "OCT" for s in ours),
        "a pirate base still spawns": pirates == ["True"],
    }
    if expect_stations:
        checks["MES finds economy groups eligible"] = bool(economy)
        checks["MES spawns them"] = bool(economy_spawns)
        checks["a spawned one trades beyond the world's stations"] = any(
            int(s[3]) > 0 and s[5] != "SPRT" for s in others
        )
    else:
        checks["MES finds no economy group eligible"] = not economy
        checks["MES spawns none"] = not economy_spawns
        # The pirate base's own store is left in; it has to be shot at to reach
        checks["no other grid but the pirate base has stock"] = not [
            s for s in others if int(s[3]) > 0 and s[5] != "SPRT"
        ]
    for name, ok in checks.items():
        print(f"{'ok  ' if ok else 'FAIL'} {name}")
    print("PASS" if all(checks.values()) else "FAIL")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
