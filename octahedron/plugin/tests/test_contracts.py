"""Checks OCT's contracts on the test server with the headless client, through the admin
commands (!oct contracts, !oct contract ...): three hauling contracts posted at every
station and none of the game's own, an acquisition at each station for the stock OCT
lacks, a bounty on an NPC grid and a wreck contract on a derelict placed near
Alpha Station, posted with the player out of sync range of it. Then the client takes and hands in a hauling and an acquisition
contract (paid by OCT, the items in OCT's stock at Alpha, no new acquisition for them),
takes the wreck contract and completes the bounty. A larger salvage radius stands in for the tow:
the derelict is the player's once it is in reach, and the reward is paid. A derelict already
at the shipyard gets no contract, and a block the player built and left ownerless, debris,
gets an area cleanup they can't be paid for. After a save and restart the open contracts must be the same ones, not posted
again, and the save shows the derelict's new owner. Prints one line per check
and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-688 OCT_WORLD_SOURCE=<world built by build_world.py> \\
        uv run --no-project --with httpx python tests/test_contracts.py
"""

from __future__ import annotations

import re
import sys

import rig
from test_client import log

checks: list[tuple[bool, str]] = []
# Options of the test on top of the world's config: a magnesium stock a character can carry,
# without the market's hourly restock, which would fill it before the first contracts;
# a low threat for the small test grid
OPTIONS = {
    "MarketEnabled": "false",
    "MarketMinStock": "Ingot/Magnesium=10",
    "ContractAcquisitionMaxAmount": 10,
    "ContractBountyMinThreat": 1,
    "ContractSalvageRadius": 500,
    # A single armor block is worth a contract: the test's own debris
    "ContractWreckMinReward": 1,
    # The hourly split would change the balances the test compares
    "MoneySubsidyPerActivePlayer": 0,
}
STATIONS = 7
LINE = re.compile(r"^\s*(\d+) (\w+) (\w+) ([\d,]+) SC ?(.*)$")
HEADER = re.compile(r"^(.+ Station): (\d+) contracts, (\d+) others$")
SPRT_MEMBER = 144115188075855894


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(f"{'PASS' if ok else 'FAIL'} {what}")


def config_text() -> str:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    for key, value in OPTIONS.items():
        text, count = re.subn(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        if not count:
            text = text.replace(
                "</OctahedronConfig>", f"  <{key}>{value}</{key}>\n</OctahedronConfig>"
            )
    return text


def position(name: str, sbs: str) -> tuple[float, float, float]:
    """The position of the grid named name in a sector file"""
    i = sbs.index(f"<DisplayName>{name}</DisplayName>")
    start = sbs.rindex("<PositionAndOrientation>", 0, i)
    m = re.search(r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', sbs[start:i])
    return tuple(float(v) for v in m.groups())


def test_grids() -> list[str]:
    sbs = (rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    sx, sy, sz = position("Alpha Station Shipyard", sbs)
    # An SPRT grid 3 km up, with a cockpit and a battery: a threat score above 1
    target = rig.grid(
        "Bounty Target",
        None,
        rig.small_ship_blocks(SPRT_MEMBER),
        position=(sx, sy + 3000, sz),
    )
    # Nobody's cockpit and 30 armor blocks (trash removal takes grids under 20), 1 km from
    # the shipyard, out of the test's reach of 500 m until the test widens it
    derelict = [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
        rig.block(
            "MyObjectBuilder_InteriorTurret",
            "LargeInteriorTurret",
            (1, 1, 0),
            "<Flags><TargetNeutrals>true</TargetNeutrals></Flags>",
        ),
        *(
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
            for x in range(6)
            for z in range(5)
        ),
    ]
    # Five single armor blocks of nobody's, 100 m apart: debris, an area cleanup
    debris = [
        rig.grid(
            f"Debris {i}",
            None,
            [rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 0))],
            position=(sx + 100 * i, sy - 20_000, sz),
        )
        for i in range(1, 6)
    ]
    return [
        *debris,
        target,
        rig.grid("Test Derelict", None, derelict, position=(sx, sy - 1000, sz)),
        rig.grid("Yard Derelict", None, derelict, position=(sx, sy - 400, sz)),
    ]


def set_options(remote, **values) -> None:
    OPTIONS.update(values)
    rig.write_plugin_config(config_text())
    rig.chat(remote, "!oct reload", 4)


def target_neutrals(name: str) -> list[str]:
    """Target Neutrals of the weapons of the grid named name in the saved sector file"""
    sbs = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    end = sbs.index(f"<DisplayName>{name}</DisplayName>")
    start = sbs.rindex("<CubeBlocks>", 0, end)
    return re.findall(r"<TargetNeutrals>(\w+)</TargetNeutrals>", sbs[start:end])


def owners(name: str) -> set[str]:
    """The block owners of the grid named name in the saved sector file"""
    sbs = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    end = sbs.index(f"<DisplayName>{name}</DisplayName>")
    start = sbs.rindex("<CubeBlocks>", 0, end)
    return set(re.findall(r"<Owner>(\d+)</Owner>", sbs[start:end]))


def contracts(remote, post: bool = False) -> dict[str, list[tuple]]:
    """Station name -> [(id, kind, state, reward, detail)], plus "others" counts"""
    lines = rig.chat(remote, "!oct contracts post" if post else "!oct contracts", 4)
    if post:
        log(" | ".join(lines))
        lines = rig.chat(remote, "!oct contracts", 4)
    stations: dict[str, list[tuple]] = {}
    others: dict[str, int] = {}
    current = None
    for line in "\n".join(lines).splitlines():
        if "Known grids" in line or "Debris pieces" in line:
            log(line.strip())
        if m := HEADER.match(line.strip()):
            current = m[1]
            stations[current] = []
            others[current] = int(m[3])
        elif (m := LINE.match(line)) and current:
            stations[current].append(
                (int(m[1]), m[2], m[3], int(m[4].replace(",", "")), m[5])
            )
    stations["others"] = others  # type: ignore[assignment]
    return stations


def run_check(remote) -> None:
    """Scans, checks the targets and posts now, as the plugin does on its timers"""
    log(" | ".join(rig.chat(remote, "!oct contracts post", 4)))


def balances(remote) -> tuple[int, int]:
    """(OCT's balance, the caller's)"""
    text = " ".join(rig.chat(remote, "!oct money", 4))
    oct_balance = re.search(r"OCT: ([\d,]+) SC", text)
    mine = re.search(r"You: ([\d,]+) SC", text)
    assert oct_balance and mine, text
    return int(oct_balance[1].replace(",", "")), int(mine[1].replace(",", ""))


def one(found: dict, station: str, kind: str, state: str = "Inactive") -> tuple | None:
    return next(
        (c for c in found.get(station, []) if c[1] == kind and c[2] == state), None
    )


def paid_for(remote, contract: tuple, before: tuple[int, int], what: str) -> None:
    after = balances(remote)
    check(
        after[0] == before[0] - contract[3] and after[1] == before[1] + contract[3],
        f"{what}: OCT {before[0]:,} -> {after[0]:,}, the player {before[1]:,} -> {after[1]:,}, "
        f"reward {contract[3]:,}",
    )


def act(remote, command: str, expect: str, what: str) -> None:
    reply = " | ".join(rig.chat(remote, f"!oct contract {command}", 4))
    check(expect in reply, f"{what}: {reply}")


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world(extra_grids=test_grids())
    rig.write_plugin_config(config_text())
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    sbs = (rig.WORLD_SOURCE / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    sx, sy, sz = position("Alpha Station Shipyard", sbs)
    # Out of sync range of the test's grids: a wreck near a player gets no contract
    away = (sx, sy - 30_000, sz)
    remote.character_teleport(*away)
    # A reload first, as the cluster rig does: the options it doesn't name keep their defaults
    set_options(remote, ContractBountyMinThreat=1)

    found = contracts(remote, post=True)
    others = found.pop("others")
    check(len(found) == STATIONS, f"{len(found)} stations listed")
    for station, list_ in found.items():
        hauling = [c for c in list_ if c[1] == "hauling"]
        check(
            len(hauling) == 3 and all(c[4].startswith("to ") for c in hauling),
            f"{station}: 3 hauling contracts {[c[3] for c in hauling]}",
        )
        magnesium = [c for c in list_ if c[1] == "acquisition"]
        check(
            len(magnesium) == 1
            and magnesium[0][4] == "Ingot/Magnesium 10"
            and magnesium[0][3] > 0,
            f"{station}: one acquisition of 10 magnesium {magnesium}",
        )
        check(others.get(station) == 0, f"{station}: none of the game's own contracts")
    alpha = found["Alpha Station"]
    bounty = one(found, "Alpha Station", "bounty")
    check(
        bounty is not None and "Bounty Target" in bounty[4] and bounty[3] > 0,
        f"a bounty on the NPC grid: {bounty}",
    )
    salvage = next(
        (
            c
            for c in found["Alpha Station"]
            if c[1] == "wreck" and "Test Derelict" in c[4]
        ),
        None,
    )
    check(
        salvage is not None and salvage[3] > 0,
        f"a wreck contract on the derelict: {salvage}",
    )
    area = [c for c in found["Alpha Station"] if c[1] == "area" and "Debris" in c[4]]
    check(
        len(area) == 1 and "and 4 more" in area[0][4] and area[0][3] > 0,
        f"an area cleanup on the five debris pieces: {area}",
    )
    check(
        all(
            c[1] != "bounty" and "Derelict" not in c[4]
            for s, l in found.items()
            if s != "Alpha Station"
            for c in l
        ),
        "no bounty and nothing on the test's grids at the other stations",
    )

    # Hauling: the package comes with the contract and is handed in
    haul = one(found, "Alpha Station", "hauling")
    before = balances(remote)
    act(remote, f"accept {haul[0]}", "Success", "take the hauling contract")
    act(remote, f"deliver {haul[0]}", "Success", "hand in the package")
    paid_for(remote, haul, before, "hauling paid by OCT")

    # Acquisition: the magnesium goes into OCT's stock at Alpha
    magnesium = one(found, "Alpha Station", "acquisition")
    before = balances(remote)
    act(remote, f"accept {magnesium[0]}", "Success", "take the acquisition")
    act(
        remote, f"deliver {magnesium[0]}", "NotEnoughItems", "no magnesium, no delivery"
    )
    act(remote, f"deliver {magnesium[0]} fill", "Success", "deliver 10 magnesium")
    paid_for(remote, magnesium, before, "acquisition paid by OCT")

    check(
        not any("Yard Derelict" in c[4] for c in alpha),
        "a derelict already at the shipyard gets no contract",
    )

    # The wreck: taken, with the player beside the derelict, it waits for the tow
    identity = remote.get_faction()["identityId"]
    remote.character_teleport(sx, sy - 900, sz)
    before = balances(remote)
    act(remote, f"accept {salvage[0]}", "Success", "take the wreck contract")
    run_check(remote)
    check(balances(remote) == before, "not at the shipyard yet: not paid")
    # The radius grows instead of the grid moving: it is in reach now. The plugin's own
    # check, every 30 seconds, finishes it
    set_options(remote, ContractSalvageRadius=3000)
    rig.wait_until(
        lambda: balances(remote)[1] != before[1], 60, "the plugin's check to pay"
    )
    paid_for(remote, salvage, before, "wreck finished by the plugin, paid by OCT")

    # A ship of the player's own, ownerless: it gets a contract, but they can't take it for pay
    set_options(remote, ContractSalvageRadius=500)
    # Creative tools: the character has no building materials
    remote.set_admin_flag("creativeTools", True)
    built = remote.character_build_grid(
        "LargeBlockArmorBlock", (sx + 10, sy - 900, sz), static=False
    )
    remote.set_admin_flag("creativeTools", False)
    check(built.get("reply") is True, f"the player builds an armor block: {built}")
    remote.character_teleport(*away)
    run_check(remote)
    alpha = contracts(remote, post=True)["Alpha Station"]
    check(
        not any(c[1] == "wreck" and "Grid" in c[4] for c in alpha),
        "under the minimum blocks: no wreck contract",
    )
    own = next((c for c in alpha if c[1] == "area" and "Grid" in c[4]), None)
    check(
        own is not None, f"their ownerless block, debris, gets an area cleanup: {own}"
    )
    if own:
        before = balances(remote)
        act(remote, f"accept {own[0]}", "Success", "they take it")
        run_check(remote)
        check(balances(remote) == before, "their own debris: not paid")
        check(
            not any(c[0] == own[0] for c in contracts(remote)["Alpha Station"]),
            "and the contract is over",
        )

    before = balances(remote)
    act(remote, f"accept {bounty[0]}", "Success", "take the bounty")
    act(remote, f"complete {bounty[0]}", "finished", "complete the bounty")
    paid_for(remote, bounty, before, "bounty paid by OCT")

    found = contracts(remote, post=True)
    found.pop("others")
    alpha = found["Alpha Station"]
    check(
        len([c for c in alpha if c[1] == "hauling" and c[2] == "Inactive"]) == 3,
        "a new hauling contract replaces the delivered one",
    )
    check(
        not any(c[1] == "acquisition" for c in alpha),
        f"no acquisition at Alpha while OCT holds its magnesium: {alpha}",
    )
    check(
        not any("Test Derelict" in c[4] for c in alpha),
        "the towed derelict gets no new contract",
    )
    check(
        any(c[1] == "bounty" for c in alpha),
        "the bounty target is still there, so it gets a new bounty",
    )
    # Wrecks and areas can be added after a restart (the NPC and player clocks start anew), never
    # posted again
    open_before = {
        s: sorted(c[0] for c in l if c[1] not in ("wreck", "area"))
        for s, l in found.items()
    }
    wrecks_before = {
        c[0] for l in found.values() for c in l if c[1] in ("wreck", "area")
    }

    rig.stop_client()
    rig.stop_server()
    check(
        owners("Test Derelict") == {str(identity)},
        f"the save: the derelict is the player's {owners('Test Derelict')}",
    )
    check(
        # The save leaves out a flag that is off
        target_neutrals("Test Derelict") == []
        and target_neutrals("Yard Derelict") == ["true"],
        "the save: Target Neutrals off on the given derelict's turret, not on the other's: "
        f"{target_neutrals('Test Derelict')} {target_neutrals('Yard Derelict')}",
    )
    # The restarted server starts its log anew
    log_text = "".join(
        p.read_text(errors="replace")
        for p in [rig.SERVER_LOG, rig.SERVER_CONFIG / "info.log"]
        + sorted(rig.SERVER_DATA.glob("SpaceEngineersDedicated*.log"))
        if p.exists()
    )
    check("OCT paid" in log_text, "the server log has the payments")
    check("OCT could NOT pay" not in log_text, "OCT paid every contract")
    log("restarting")
    rig.start_server()
    remote = rig.start_client()
    found = contracts(remote, post=True)
    found.pop("others")
    open_after = {
        s: sorted(c[0] for c in l if c[1] not in ("wreck", "area"))
        for s, l in found.items()
    }
    wrecks_after = [c for l in found.values() for c in l if c[1] in ("wreck", "area")]
    check(
        open_after == open_before
        and wrecks_before <= {c[0] for c in wrecks_after}
        and len({c[4] for c in wrecks_after}) == len(wrecks_after),
        "after the restart: the same contracts, none posted again",
    )
    if open_after != open_before:
        log(f"before {open_before}\nafter {open_after}")

    rig.stop_client()
    rig.stop_server()
    failed = [what for passed, what in checks if not passed]
    print(
        f"{len(checks) - len(failed)}/{len(checks)} contract checks passed", flush=True
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
