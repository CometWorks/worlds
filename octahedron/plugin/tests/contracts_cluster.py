"""Bounties and the wreck hunt on a local test cluster (SE-702, SE-1385), through the cluster's own scenario
bench. Same bench and world as wa_economy_cluster.py; read its docstring for host.env. The nodes report the
grids, the World Authority posts and decides; the console probe on the WA runs !oct contracts there, and its
`away` line moves the clock players are away by.

    OCT_WORLD_SOURCE=<world built by build_world.py> python3 tests/contracts_cluster.py CLUSTER_TEST_DIR     (from plugin/)

Near Alpha: an NPC ship, a derelict with an arm on a rotor, another derelict already at the shipyard, and two more
derelicts. Far from every station: three armor-only wrecks of different sizes, an NPC's hulls without power,
static or not, an NPC's hull with a charged battery, a hull of a player long gone, a wreck in a safe zone, a
wreck the player is near, and a field of five debris pieces. The minimum blocks is one, so the world's Trade Lane
Computers would count; debris is the pieces under two blocks.

- With no target anywhere a node reports once a minute, the full scan; with targets every 30 seconds.
- A bounty and wreck contracts from the nodes' reports, anywhere, each at the nearest station: the derelict near
  Alpha, the armor wrecks paying by their components (60:40:25 armor blocks), an area cleanup on the debris. None
  on the derelict already at the shipyard, the safe zone's, the live hull's, the lane computers', the wreck the
  player is within sync range of, the NPC's hulls before ContractNpcWreckMinutes, or the hull of a player who is
  around. The player leaves, the NPC time passes, the player is 31 days away: those are posted too. The player
  "back": the open contract on their hull is withdrawn.
- The wrecks stay posted while their partitions sleep.
- The derelict towed in (a larger radius stands in for the tow) without its arm, which was cut off: paid what is
  left, less than posted, and the player's, with !oct scrap offered in chat. A second one towed in and scrapped
  with !oct scrap: gone, the reward kept. A third over the player's limit: paid and broken down at once.
- A block the player built and left ownerless gets a contract, which they can't take for pay.
- NPC ships held by a player's tug at the shipyard, with no contract: over a ship's PCU limit, with the turn from
  that derelict still running, one is sold at once; more than 10% over the player's, with the shipyard free,
  one is broken down; 5% over, the third is the player's with a warning, unpaid. The test's probe gives the
  tug and joins the grids as a connector would, and sets the player's limit.
- Taken wreck with the player there: its partition waking changes nothing; deleted, it counts as done.
  Taken with the player away: fails. Nobody took it: withdrawn.
- The debris area: its pieces deleted with the player near, done and paid.
- A taken static wreck stays taken when converted to a ship; deleted, it counts as done.
- A node restart keeps the contracts, and so does a graceful cluster stop and warm start, with the player's
  ownership and the memory of the given grid.

Grinding down, repairing a wreck, the seated and claimed rules, the most valuable first and the area geometry are
checked in tests/contracts/ContractTests.

Takes over an hour, most of it waiting out the 5 minute import grace on grid deletes.

"""

from __future__ import annotations

import json
import os
import re
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

import wa_economy_cluster as wa
from wa_economy_cluster import (
    CLIENT_ID,
    Client,
    bench_module,
    link_mods,
    number,
    rig,
    set_config,
    stations,
    wait_until,
)
from test_contracts import HEADER, LINE, SPRT_MEMBER, position

# Parallel benches on one host each have their own probe files and plugin copies
wa.ROOT = Path.home() / f"cluster-runs/contracts-{os.environ.get('OCT_TASK', 'SE-702')}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"

LARGE_ID = 1 << 56
ARMOR = "MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock"
GRIDS = {
    "Bounty Target": LARGE_ID + 1,
    "Test Derelict": LARGE_ID + 2,
    "Wreck Big": LARGE_ID + 3,
    "Wreck Mid": LARGE_ID + 4,
    "Wreck Small": LARGE_ID + 5,
    "Dead Hull": LARGE_ID + 6,
    "Live Hull": LARGE_ID + 7,
    "Static Hull": LARGE_ID + 8,
    "Yard Derelict": LARGE_ID + 10,
    "Derelict Arm": LARGE_ID + 11,
    "Player Tug": LARGE_ID + 14,
    "Towed Pirate": LARGE_ID + 15,
    "Zone Wreck": LARGE_ID + 16,
    "Scrap Derelict": LARGE_ID + 30,
    "Big Pirate": LARGE_ID + 31,
    "Over Pirate": LARGE_ID + 32,
    "Over Derelict": LARGE_ID + 33,
    "Gone Hull": LARGE_ID + 34,
    "Near Wreck": LARGE_ID + 35,
    **{f"Debris {i}": LARGE_ID + 40 + i for i in range(1, 6)},
}
# A player of the world who hasn't been on since the plugin started recording
GONE_PLAYER = 144115188075855777
# Their own spots: far_point's crowd together past index 6
EXTRA_AT = {
    "Gone Hull": (-40_000.0, 40_000.0, -480_000.0),
    "Near Wreck": (40_000.0, -40_000.0, -480_000.0),
    **{f"Debris {i}": (0.0, 60_000.0 + 100 * i, -470_000.0) for i in range(1, 6)},
}
NEAR = (
    "Bounty Target",
    "Test Derelict",
    "Yard Derelict",
    "Derelict Arm",
    "Player Tug",
    "Towed Pirate",
    "Scrap Derelict",
    "Big Pirate",
    "Over Pirate",
    "Over Derelict",
)
FAR = [name for name in GRIDS if name not in NEAR and name not in EXTRA_AT]
STATOR, ROTOR = LARGE_ID + 12, LARGE_ID + 13
# Ten stations' worth of room between them: a partition each, none near another
SPACING = 40_000
STATIONS_AWAY = 60_000
KNOWN_LINE = re.compile(
    r"Known grids: (\d+), in offline partitions (\w+), wrecks (\w+), reports (\d+)"
)


def armor(count: int, start_z: int = 0) -> list[str]:
    return [
        rig.block(*ARMOR, (x, 0, z + start_z))
        for x in range(5)
        for z in range(count // 5)
    ]


def hull(stored_mwh: float, owner: int = SPRT_MEMBER) -> list[str]:
    own = f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy>"
    return [
        rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0), own),
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 0, 1),
            own + f"<CurrentStoredPower>{stored_mwh}</CurrentStoredPower>",
        ),
        *armor(25, 2),
    ]


def zone_around(center: tuple[float, float, float]) -> str:
    """An enabled admin safe zone"""
    x, y, z = center
    return f"""<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_SafeZone">
      <SubtypeName /><EntityId>{LARGE_ID + 17}</EntityId><PersistentFlags>CastShadows InScene</PersistentFlags>
      <PositionAndOrientation><Position x="{x}" y="{y}" z="{z}" /><Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>
      <DisplayName>Test Zone</DisplayName><Radius>200</Radius><Enabled>true</Enabled><SafeZoneBlockId>0</SafeZoneBlockId>
      <Shape>Sphere</Shape>
    </MyObjectBuilder_EntityBase>"""


def zone_point(
    where: dict[str, tuple[float, float, float]],
) -> tuple[float, float, float]:
    """Inside 500 km of the center: on load the game pulls a grid further out back to that, the zone
    stays where it is and wouldn't hold the grid any more"""
    spot = far_point(where, FAR.index("Zone Wreck"))
    scale = 450_000 / sum(v * v for v in spot) ** 0.5
    return tuple(v * scale for v in spot)


def far_point(
    where: dict[str, tuple[float, float, float]], index: int
) -> tuple[float, float, float]:
    """A spot along a line clear of every station's bounty range"""
    for y in range(STATIONS_AWAY, 2_000_000, SPACING):
        spot = (STATIONS_AWAY, float(y), SPACING * index + 500_000.0)
        if all(
            sum((a - b) ** 2 for a, b in zip(spot, s)) ** 0.5 >= STATIONS_AWAY
            for s in where.values()
        ):
            return spot
    raise AssertionError("no room")


def add_grids() -> None:
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    sx, sy, sz = position("Alpha Station Shipyard", text)
    where = stations()
    blocks = {
        "Wreck Big": armor(60),
        "Wreck Mid": armor(40),
        "Wreck Small": armor(25),
        "Dead Hull": hull(0),
        "Live Hull": hull(1),
        "Static Hull": hull(0),
        "Zone Wreck": armor(10),
        "Gone Hull": hull(0, GONE_PLAYER),
        "Near Wreck": armor(10),
        **{f"Debris {i}": [rig.block(*ARMOR, (0, 0, 0))] for i in range(1, 6)},
    }
    grids = [
        rig.grid(
            "Bounty Target",
            GRIDS["Bounty Target"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx, sy + 3000, sz),
        ),
        rig.grid(
            "Test Derelict",
            GRIDS["Test Derelict"],
            [
                rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
                rig.block(
                    "MyObjectBuilder_MotorAdvancedStator",
                    "LargeAdvancedStator",
                    (2, 1, 2),
                    f"<EntityId>{STATOR}</EntityId><TopBlockId>{ROTOR}</TopBlockId>",
                ),
                *armor(30),
            ],
            position=(sx, sy - 1000, sz),
        ),
        rig.grid(
            "Derelict Arm",
            GRIDS["Derelict Arm"],
            [
                rig.block(
                    "MyObjectBuilder_MotorAdvancedRotor",
                    "LargeAdvancedRotor",
                    (0, 0, 0),
                    f"<EntityId>{ROTOR}</EntityId><ParentEntityId>{STATOR}</ParentEntityId>",
                ),
                rig.block(
                    "MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 1, 0)
                ),
            ],
            position=(sx + 5, sy - 1000 + 5, sz + 5),
        ),
        rig.grid(
            "Player Tug",
            GRIDS["Player Tug"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx + 100, sy - 400, sz),
        ),
        rig.grid(
            "Towed Pirate",
            GRIDS["Towed Pirate"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx + 200, sy - 400, sz),
        ),
        rig.grid(
            "Big Pirate",
            GRIDS["Big Pirate"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx + 200, sy - 400, sz + 100),
        ),
        rig.grid(
            "Over Pirate",
            GRIDS["Over Pirate"],
            rig.small_ship_blocks(SPRT_MEMBER),
            position=(sx + 200, sy - 400, sz - 100),
        ),
        rig.grid(
            "Scrap Derelict",
            GRIDS["Scrap Derelict"],
            [
                rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
                *armor(30),
            ],
            position=(sx, sy - 2000, sz),
        ),
        rig.grid(
            "Over Derelict",
            GRIDS["Over Derelict"],
            [
                rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
                *armor(30),
            ],
            position=(sx, sy - 2200, sz),
        ),
        rig.grid(
            "Yard Derelict",
            GRIDS["Yard Derelict"],
            [
                rig.block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 1, 0)),
                *armor(30),
            ],
            position=(sx, sy - 400, sz),
        ),
    ]
    for index, name in enumerate(FAR):
        xml = rig.grid(
            name,
            GRIDS[name],
            blocks[name],
            position=(
                zone_point(where) if name == "Zone Wreck" else far_point(where, index)
            ),
        )
        grids.append(
            xml.replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
            # A movable wreck in the zone kept its partition awake in a test run
            if name in ("Static Hull", "Zone Wreck")
            else xml
        )
    for name, at in EXTRA_AT.items():
        grids.append(rig.grid(name, GRIDS[name], blocks[name], position=at))
    grids.append(zone_around(zone_point(where)))
    text = text.replace("</SectorObjects>", "".join(grids) + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")
    sandbox = rig.WORLD / "Sandbox.sbc"
    sandbox.write_text(
        sandbox.read_text(encoding="utf-8").replace(
            "<Identities>",
            "<Identities><MyObjectBuilder_Identity>"
            f"<IdentityId>{GONE_PLAYER}</IdentityId><DisplayName>Gone Player</DisplayName>"
            "<CharacterEntityId>0</CharacterEntityId></MyObjectBuilder_Identity>",
            1,
        ),
        encoding="utf-8",
    )


def run(bench) -> None:
    cluster = bench.cluster
    probe = wa.lc.Probe()
    nodes = ("node-1", "node-2")
    processes = ["wa-1", *nodes]

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def command(line: str) -> list[str]:
        return [
            part
            for reply in probe.run(line, timeout=120, settle=3)
            for part in reply.split(" | ")
        ]

    def contracts() -> tuple[dict[str, list[tuple]], str]:
        """Station -> [(id, kind, state, reward, detail)], and the book's summary line"""
        found: dict[str, list[tuple]] = {}
        current, known = None, ""
        for line in command("!oct contracts"):
            if m := HEADER.match(line.strip()):
                current = m[1]
                found[current] = []
            elif m := LINE.match(line):
                found[current].append((int(m[1]), m[2], m[3], number(m[4]), m[5]))
            elif KNOWN_LINE.search(line):
                known = line
        return found, known

    def post() -> dict[str, list[tuple]]:
        print(" ".join(command("!oct contracts post")), flush=True)
        return contracts()[0]

    def named(found: dict, name: str, kind: str | None = None) -> list[tuple]:
        return [
            c
            for list_ in found.values()
            for c in list_
            if name in c[4] and kind in (None, c[1])
        ]

    def wreck_names(found: dict) -> set[str]:
        return {name for name in [*FAR, *EXTRA_AT] if named(found, name, "wreck")}

    def reconfigure(**values) -> None:
        set_config(**values)
        command("!oct reload")

    def away(days: float) -> None:
        # The probe's own line, with no reply to wait for; the next command runs after it
        with wa.lc.PLAN.open("a") as plan:
            plan.write(f"away {days}\n")
        time.sleep(12)

    def owner(name: str) -> tuple[str, int] | None:
        found = bench.entity_owner(bench.diag(), GRIDS[name])
        return found[0] if found else None

    def grid_owner(name: str) -> int | None:
        """The big owner of a grid, read from the node holding it"""
        for section in bench.sections(bench.diag()).values():
            for partition in section["partitions"]:
                for entity in partition["entities"] or []:
                    if entity["entityId"] == GRIDS[name]:
                        return entity["ownerIdentityId"]
        return None

    def chat(text: str, timeout: float) -> str:
        try:
            return wa.so.wait_chat(api, text, timeout)
        except TimeoutError:
            return ""

    def set_pcu_limit(limit: int) -> None:
        # The probe's own line, with no reply to wait for; the next command runs after it
        with wa.lc.PLAN.open("a") as plan:
            plan.write(f"pculimit {limit}\n")
        time.sleep(12)

    def offline() -> dict[int, list[int]]:
        return {
            int(row["Partition"]): [int(x) for x in row.get("Members") or []]
            for row in cluster.get("/registry/partitions/offline").get("Partitions", [])
        }

    def offline_partition(name: str) -> int | None:
        return next(
            (p for p, members in offline().items() if GRIDS[name] in members), None
        )

    def wake(name: str) -> str:
        """Loads the grid's partition if needed and gives its node"""
        partition = offline_partition(name)
        if partition is not None:
            cluster.post(
                f"/registry/partitions/offline/load?partition={partition}&reason=test"
            )
        where = wait_until(lambda: owner(name), 180, f"{name} on a node")
        return where[0]

    def delete(name: str) -> None:
        # A grid that just arrived on its node is refused for its import grace, 5 minutes after a load
        query = urllib.parse.urlencode({"entityId": GRIDS[name], "name": name})
        replies = []

        def attempt():
            replies.append(cluster.node(wake(name), f"/grid-delete?{query}", "POST"))
            return replies[-1].get("deleted")

        check(
            f"{name}: deleted on its node",
            wait_until(attempt, 420, f"{name} deleted", 10),
            replies[-1:],
        )

    def balances(api) -> tuple[int, int]:
        text = " ".join(rig.chat(api, "!oct money", 10))
        return number(re.search(r"OCT: ([\d,]+) SC", text)[1]), number(
            re.search(r"You: ([\d,]+) SC", text)[1]
        )

    def paid(
        api, before: tuple[int, int], reward: int, timeout: float = 90
    ) -> tuple[int, int]:
        end = time.monotonic() + timeout
        now = balances(api)
        while time.monotonic() < end and now[1] != before[1] + reward:
            time.sleep(5)
            now = balances(api)
        return now

    def still_paid(what: str, before, now, reward) -> None:
        check(
            f"{what}: OCT paid the player",
            now[0] == before[0] - reward and now[1] == before[1] + reward,
            f"{before} -> {now}, reward {reward:,}",
        )

    cluster.kill()
    cluster.prepare(wipe=True)
    for process in processes:
        link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    # Nothing but the rig removes these grids; offline only once the nodes have reported them
    cluster.handover_config({"npc.sweepSeconds": "0"})

    # --- bounty and salvage from the nodes' reports ---
    known = wait_until(
        lambda: (m := KNOWN_LINE.search(contracts()[1])) and int(m[1]) >= 7 + 6 and m,
        420,
        "the nodes' reports",
    )
    print(known[0], flush=True)

    def reports_in(seconds: int) -> int:
        before = int(KNOWN_LINE.search(contracts()[1])[4])
        time.sleep(seconds)
        return int(KNOWN_LINE.search(contracts()[1])[4]) - before

    lanes = [
        entity
        for section in bench.sections(bench.diag()).values()
        for partition in section["partitions"]
        for entity in partition["entities"] or []
        if entity["displayName"].startswith("TL ")
    ]
    check(
        "the world's lane computers are up, ownerless",
        len(lanes) == 26 and not any(e["ownerIdentityId"] for e in lanes),
        [(e["displayName"], e["ownerIdentityId"]) for e in lanes][:3],
    )

    # Three minutes on two nodes: 3 full reports each, one more at the edges of the window
    idle = reports_in(180)
    check("no target anywhere: only the full reports", idle <= 8, idle)
    # The player first, within sync range of one wreck
    link_mods(
        Client(
            bench.run_root,
            CLIENT_ID,
            bench.remote_port(bench_module.LEGACY_REMOTE_PORT),
            cluster.config.gateway_listen,
        ).user_data
        / "Mods"
    )
    api = bench.start_client(CLIENT_ID)
    bench.wait_spawned(CLIENT_ID)
    identity = api.get_faction()["identityId"]
    check("the client has an identity", identity, identity)
    Path(f"{wa.lc.PLAN}.air").touch()
    sector = (rig.WORLD / "SANDBOX_0_0_0_.sbs").read_text(encoding="utf-8")
    near_wreck = position("Near Wreck", sector)
    api.character_teleport(near_wreck[0] + 300, near_wreck[1], near_wreck[2])
    wait_until(lambda: owner("Near Wreck"), 180, "the near wreck awake")
    # Past a full report with the player there
    time.sleep(70)

    reconfigure(ContractBountiesPerStation=5, ContractHuntPerStation=30)
    found = post()
    bounty = named(found, "Bounty Target", "bounty")
    salvage = named(found, "Test Derelict", "wreck")
    check(
        "a bounty on the NPC grid",
        len(bounty) == 1 and bounty[0][3] > 0,
        found.get("Alpha Station"),
    )
    check(
        "a wreck contract on the derelict near Alpha",
        len(salvage) == 1
        and salvage[0][3] > 0
        and salvage[0] in found["Alpha Station"],
        found.get("Alpha Station"),
    )
    check(
        "a derelict already at the shipyard gets no contract",
        not named(found, "Yard Derelict"),
        found.get("Alpha Station"),
    )
    check("the bounty at Alpha", bounty[0] in found["Alpha Station"])
    check(
        "hauling still posted",
        any(c[1] == "hauling" for list_ in found.values() for c in list_),
    )
    armor_only = {"Wreck Big", "Wreck Mid", "Wreck Small"}
    check(
        "the armor wrecks anywhere, online, and none of the others yet",
        wreck_names(found) == armor_only,
        wreck_names(found),
    )
    where = stations()
    big = next(
        s for s, list_ in found.items() if any("Wreck Big" in c[4] for c in list_)
    )
    at = position("Wreck Big", sector)
    distance = lambda name: sum((a - b) ** 2 for a, b in zip(at, where[name])) ** 0.5
    check("posted at the nearest station", big == min(where, key=distance), big)
    pay = {n: named(found, n, "wreck")[0][3] for n in armor_only}
    check(
        "paying by their components: 60, 40 and 25 armor blocks",
        abs(pay["Wreck Big"] / 60 - pay["Wreck Small"] / 25) <= 1
        and abs(pay["Wreck Mid"] / 40 - pay["Wreck Small"] / 25) <= 1,
        pay,
    )
    check(
        "a wreck in an active safe zone: no contract",
        not named(found, "Zone Wreck"),
        named(found, "Zone Wreck"),
    )
    check("not the live hull", not named(found, "Live Hull"), "")
    check(
        "no lane computer, ownerless and without power as they are",
        not named(found, "TL "),
        named(found, "TL "),
    )
    check(
        "not the wreck the player is within sync range of",
        not named(found, "Near Wreck"),
        named(found, "Near Wreck"),
    )
    check(
        "not the NPC's hulls before ContractNpcWreckMinutes",
        not named(found, "Dead Hull") and not named(found, "Static Hull"),
        "",
    )
    check(
        "not the hull of a player who isn't away yet",
        not named(found, "Gone Hull"),
        "",
    )
    area = named(found, "Debris", "area")
    check(
        "an area cleanup on the debris field",
        len(area) == 1 and "and 4 more" in area[0][4] and area[0][3] > 0,
        named(found, "Debris"),
    )

    shipyard = position("Alpha Station Shipyard", sector)
    api.character_teleport(shipyard[0], shipyard[1] - 900, shipyard[2])
    time.sleep(40)
    found = post()
    check(
        "the player gone: the near wreck posted",
        named(found, "Near Wreck", "wreck"),
        wreck_names(found),
    )
    reconfigure(ContractNpcWreckMinutes=0)
    found = post()
    check(
        "the NPC's hulls without power, static too, once their time is up",
        named(found, "Dead Hull", "wreck") and named(found, "Static Hull", "wreck"),
        wreck_names(found),
    )
    check("the live hull still not", not named(found, "Live Hull"), "")
    away(31)
    found = post()
    gone = named(found, "Gone Hull", "wreck")
    check("the player 31 days away: their hull posted", gone, wreck_names(found))
    away(0)
    check(
        "the player back: the open contract on their hull withdrawn",
        wait_until(
            lambda: not named(contracts()[0], "Gone Hull"), 120, "the withdrawal", 10
        ),
        named(contracts()[0], "Gone Hull"),
    )

    # A node learns of its targets from its next full report, then reports every 30 seconds
    busy = reports_in(180)
    check("with targets: short reports too", busy >= 9, busy)

    # --- asleep ---
    cluster.handover_config(
        {"offline.minOnlineSeconds": "0", "offline.graceSeconds": "0"}
    )
    sleepers = ["Wreck Big", "Wreck Mid", "Wreck Small", "Dead Hull", "Static Hull"]
    wait_until(
        lambda: all(any(GRIDS[n] in m for m in offline().values()) for n in sleepers),
        420,
        "the far grids offline",
    )
    # Past a check with the gateway's table read
    time.sleep(90)
    found = post()
    check(
        "the wrecks stay posted while their partitions sleep",
        {"Wreck Big", "Wreck Mid", "Wreck Small", "Dead Hull", "Static Hull"}
        <= wreck_names(found),
        wreck_names(found),
    )

    # --- a player takes them ---
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(shipyard[0], shipyard[1] - 900, shipyard[2])
    holder = wait_until(lambda: owner("Test Derelict"), 180, "the derelict awake")
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {salvage[0][0]} {identity}"))
    check("take the wreck contract on the derelict", "Success" in reply, reply)
    # Past a check: a taken derelict waits for the tow
    time.sleep(40)
    check(
        "not at the shipyard yet: not paid, not given",
        balances(api) == before and not grid_owner("Test Derelict"),
        f"{balances(api)} owner {grid_owner('Test Derelict')}",
    )
    # Stripped before the tow: its arm on the rotor cut off
    delete("Derelict Arm")
    time.sleep(70)
    # The radius grows instead of the grid moving
    reconfigure(ContractSalvageRadius=1500)
    end = time.monotonic() + 120
    now = balances(api)
    while time.monotonic() < end and now[1] == before[1]:
        time.sleep(5)
        now = balances(api)
    got = now[1] - before[1]
    check(
        "towed in without its arm: paid what is left, less than posted, by OCT",
        0 < got < salvage[0][3] and now[0] == before[0] - got,
        f"{before} -> {now}, posted {salvage[0][3]:,}",
    )
    holder = owner("Test Derelict")
    check(
        f"the derelict is the player's on {holder[0]}, not scrapped",
        wait_until(lambda: grid_owner("Test Derelict") == identity, 90, "the owner")
        and holder[0] in nodes,
        grid_owner("Test Derelict"),
    )
    offer = chat("say !oct scrap within", 90)
    check("chat offers to scrap it instead", offer and "Test Derelict" in offer, offer)
    found = post()
    check(
        "the given derelict gets no new contract",
        not named(found, "Test Derelict"),
        found.get("Alpha Station"),
    )

    # The second one: towed in, then scrapped at the player's word
    scrap = named(found, "Scrap Derelict", "wreck")
    check("a wreck contract on the second derelict", scrap, found.get("Alpha Station"))
    reconfigure(ContractSalvageRadius=500)
    api.character_teleport(shipyard[0], shipyard[1] - 1900, shipyard[2])
    wait_until(lambda: owner("Scrap Derelict"), 180, "the second derelict awake")
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {scrap[0][0]} {identity}"))
    check("take the second one", "Success" in reply, reply)
    reconfigure(ContractSalvageRadius=2500)
    now = paid(api, before, scrap[0][3])
    still_paid("the second one towed in", before, now, scrap[0][3])
    wait_until(lambda: grid_owner("Scrap Derelict") == identity, 90, "the owner")
    reply = " ".join(rig.chat(api, "!oct scrap", 15))
    check(
        "!oct scrap: the shipyard takes it apart",
        "took 'Scrap Derelict' apart" in reply
        and wait_until(lambda: not owner("Scrap Derelict"), 90, "the grid gone"),
        reply,
    )
    time.sleep(20)
    check("and the reward stays the player's", balances(api) == now, balances(api))
    reply = " ".join(rig.chat(api, "!oct scrap", 15))
    check("nothing more to scrap", "no wreck to scrap" in reply, reply)

    # The third, too much for the player: the contract pays and the shipyard breaks the grid down
    reconfigure(ContractSalvageRadius=500)
    found = post()
    over_contract = named(found, "Over Derelict", "wreck")
    check(
        "a wreck contract on the third derelict",
        over_contract,
        found.get("Alpha Station"),
    )
    api.character_teleport(shipyard[0], shipyard[1] - 2100, shipyard[2])
    wait_until(lambda: owner("Over Derelict"), 180, "the third derelict awake")
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {over_contract[0][0]} {identity}"))
    check("take the third", "Success" in reply, reply)
    set_pcu_limit(1)
    reconfigure(ContractSalvageRadius=2500)
    over = chat('"Over Derelict" is too big to keep: with its', 150)
    check("more than 10% over the player's limit: in chat", over, over)
    still_paid(
        "the third broken down",
        before,
        paid(api, before, over_contract[0][3]),
        over_contract[0][3],
    )
    done = chat("'Over Derelict'", 90)
    check(
        "the shipyard breaks it down at once, the player's turn starts",
        done and wait_until(lambda: not owner("Over Derelict"), 90, "the grid gone"),
        done,
    )
    set_pcu_limit(1_000_000)
    api.character_teleport(shipyard[0], shipyard[1] - 900, shipyard[2])

    # A ship of the player's own, ownerless: it gets a contract, but they can't take it for pay
    reconfigure(ContractSalvageRadius=500)
    # Creative tools: the character has no building materials
    api.set_admin_flag("creativeTools", True)
    built = api.character_build_grid(
        "LargeBlockArmorBlock",
        (shipyard[0] + 10, shipyard[1] - 900, shipyard[2]),
        static=False,
    )
    api.set_admin_flag("creativeTools", False)
    check("the player builds an armor block", built.get("reply") is True, built)
    # Out of sync range of it, or it gets no contract
    api.character_teleport(shipyard[0], shipyard[1] - 20_000, shipyard[2])
    time.sleep(70)
    found = post()
    own = named(found, "Cleanup: Grid", "area")
    check(
        "their ownerless block, debris, gets an area contract",
        own,
        found.get("Alpha Station"),
    )
    if own:
        before = balances(api)
        reply = " ".join(command(f"!oct contract accept {own[0][0]} {identity}"))
        check("they take it", "Success" in reply, reply)
        time.sleep(40)
        check("their own ship: not paid", balances(api) == before, balances(api))
        check(
            "and the contract is over",
            not any(c[0] == own[0][0] for c in named(contracts()[0], "Grid")),
        )
    api.character_teleport(shipyard[0], shipyard[1] - 900, shipyard[2])
    found = post()

    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {bounty[0][0]} {identity}"))
    check("take the bounty", "Success" in reply, reply)
    reply = " ".join(command(f"!oct contract complete {bounty[0][0]}"))
    check("complete the bounty", "finished" in reply, reply)
    still_paid("bounty", before, paid(api, before, bounty[0][3]), bounty[0][3])

    # --- NPC ships towed in, no contract, against the PCU limits ---
    # The limits come first: the first report with the tug in place decides
    reconfigure(GroupPcuLimit=1)
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(shipyard[0] + 100, shipyard[1] - 470, shipyard[2])
    wait_until(lambda: owner("Towed Pirate"), 180, "the pirate awake")

    def tow(name: str) -> None:
        with Path(f"{wa.lc.PLAN}.tow").open("a") as plan:
            plan.write(f"{GRIDS['Player Tug']} {GRIDS[name]} {identity}\n")

    # The shipyard is still in the player's turn from the second derelict
    tow("Big Pirate")
    check(
        "the tug is the player's and holds the ship",
        wait_until(lambda: grid_owner("Player Tug") == identity, 60, "the tug"),
        grid_owner("Player Tug"),
    )
    too_big = chat('"Big Pirate" is too big to keep: it has', 150)
    sold = chat("'Big Pirate' was too big for you to keep", 90)
    check(
        "over a ship's limit, the shipyard busy: all sold at once, in chat",
        too_big
        and "is busy" in sold
        and wait_until(lambda: not owner("Big Pirate"), 90, "the grid gone"),
        f"{too_big} {sold}",
    )
    reconfigure(GroupPcuLimit=60000)
    reply = " ".join(rig.chat(api, "!oct shipyard done", 10))
    check("the turn ends by command", "Success" in reply or "turn" in reply, reply)
    set_pcu_limit(1)
    tow("Over Pirate")
    over = chat('"Over Pirate" is too big to keep: with its', 150)
    done = chat("'Over Pirate'", 90)
    check(
        "more than 10% over the player's limit, the shipyard free: broken down at once",
        over
        and done
        and wait_until(lambda: not owner("Over Pirate"), 90, "the grid gone"),
        f"{over} {done}",
    )
    rig.chat(api, "!oct shipyard done", 10)
    # What they would have comes from that message: all the ledger and the game count
    would_have = number(re.search(r"you would have ([\d,]+)", over)[1]) if over else 0
    set_pcu_limit(int(would_have / 1.05))
    before = balances(api)
    tow("Towed Pirate")
    warned = chat("you can't build blocks until", 150)
    check(
        "5% over: the player's, with a warning",
        wait_until(lambda: grid_owner("Towed Pirate") == identity, 90, "the pirate")
        and warned,
        warned,
    )
    moved = chat("out of the shipyard within", 60)
    check("a warning to move it", moved, moved)
    check(
        "towed in without a contract: not paid", balances(api) == before, balances(api)
    )
    set_pcu_limit(1_000_000)
    time.sleep(70)
    found = post()
    check(
        "the taken ship has no contract any more, its posted bounty is gone",
        not named(found, "Towed Pirate"),
        named(found, "Towed Pirate"),
    )

    # Wreck Big, with the player there: waking isn't its end, its end with them near is done
    big_contract = named(found, "Wreck Big", "wreck")[0]
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {big_contract[0]} {identity}"))
    check("take the wreck", "Success" in reply, reply)
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(at[0] + 300, at[1], at[2])
    wait_until(lambda: owner("Wreck Big"), 180, "Wreck Big awake")
    # Past a report and a check after it woke
    time.sleep(70)
    found = contracts()[0]
    check(
        "awake with the hunter near: still Active",
        [c[2] for c in named(found, "Wreck Big", "wreck")] == ["Active"],
        named(found, "Wreck Big"),
    )
    delete("Wreck Big")
    still_paid(
        "wreck gone with the hunter near",
        before,
        paid(api, before, big_contract[3], 360),
        big_contract[3],
    )

    # Wreck Mid, with the player elsewhere: fails. Wreck Small, nobody took it: withdrawn.
    cluster.handover_config({"offline.enabled": "false"})
    mid = named(found, "Wreck Mid", "wreck")[0]
    before = balances(api)
    reply = " ".join(command(f"!oct contract accept {mid[0]} {identity}"))
    check("take a second wreck", "Success" in reply, reply)
    # Awake together, so both import graces pass together
    wake("Wreck Mid")
    wake("Wreck Small")
    wake("Static Hull")
    delete("Wreck Mid")
    delete("Wreck Small")
    gone = wait_until(
        lambda: not named(contracts()[0], "Wreck Mid")
        and not named(contracts()[0], "Wreck Small"),
        360,
        "both contracts over",
    )
    check("gone: both contracts are over", gone)
    check(
        "gone with the hunter away: not paid",
        balances(api) == before,
        f"{before} -> {balances(api)}",
    )
    check(
        "withdrawn when nobody took it", "withdrawn: its target is gone" in log("wa-1")
    )

    # The debris field, cleared with the player there: done when no piece is left in it
    cleanup = named(contracts()[0], "Debris", "area")[0]
    before = balances(api)
    debris = position("Debris 3", sector)
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(debris[0] + 300, debris[1], debris[2])
    reply = " ".join(command(f"!oct contract accept {cleanup[0]} {identity}"))
    check("take the area cleanup", "Success" in reply, reply)
    for i in range(1, 5):
        delete(f"Debris {i}")
    time.sleep(70)
    check(
        "a piece left: still Active",
        [c[2] for c in named(contracts()[0], "Debris", "area")] == ["Active"],
        named(contracts()[0], "Debris"),
    )
    delete("Debris 5")
    still_paid(
        "the area cleared with the hunter near",
        before,
        paid(api, before, cleanup[3], 360),
        cleanup[3],
    )

    # The static hull, converted to a ship with the player there: the same contract, on the same grid
    static = named(contracts()[0], "Static Hull", "wreck")[0]
    before = balances(api)
    far_static = position("Static Hull", sector)
    # The long wait above outlasts the suit's oxygen out in space
    bench.wait_spawned(CLIENT_ID)
    api.character_teleport(far_static[0] + 300, far_static[1], far_static[2])
    reply = " ".join(command(f"!oct contract accept {static[0]} {identity}"))
    check("take the static wreck", "Success" in reply, reply)
    Path(f"{wa.lc.PLAN}.convert").write_text(f"{GRIDS['Static Hull']}\n")
    check(
        "converted to a ship on its node",
        wait_until(
            lambda: Path(f"{wa.lc.PLAN}.converted").exists(), 60, "the conversion"
        ),
    )
    # Past a report and a check
    time.sleep(70)
    check(
        "converted: still Active",
        [c[2] for c in named(contracts()[0], "Static Hull", "wreck")] == ["Active"],
        named(contracts()[0], "Static Hull"),
    )
    delete("Static Hull")
    still_paid(
        "static wreck gone with the hunter near",
        before,
        paid(api, before, static[3], 360),
        static[3],
    )

    # --- restarts ---
    dead = named(contracts()[0], "Dead Hull", "wreck")[0]
    reply = " ".join(command(f"!oct contract accept {dead[0]} {identity}"))
    check("take the hull", "Success" in reply, reply)
    found = post()
    open_before = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck")
    }
    print(open_before, flush=True)

    node = wake("Bounty Target")
    cluster.restart_node(node)
    cluster.wait_ready(nodes=[node])
    # Past the 3 minutes in which a node reporting without a target ends its contract
    time.sleep(200)
    found = post()
    after = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck")
    }
    check(
        f"{node} restarted: the same contracts",
        after == open_before,
        f"{open_before} -> {after}",
    )

    bench.kill_client(CLIENT_ID)
    check("graceful stop", cluster.stop(grace=240))
    saved = wa.global_save(bench.run_root / "wa-1-data")
    check(
        "the World Authority's global save has the targets, grids and given grids",
        "Octahedron-Contract-Grids" in saved
        and "Octahedron-Contracts" in saved
        and str(GRIDS["Test Derelict"]) in saved,
    )
    wa.lc.PLAN.write_text("")
    wa.lc.OUT.write_text("")
    probe.step = 0
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    time.sleep(90)
    found = post()
    after = {
        c[0]: c[2]
        for list_ in found.values()
        for c in list_
        if c[1] in ("bounty", "wreck")
    }
    check(
        "warm start: the same contracts",
        after == open_before,
        f"{open_before} -> {after}",
    )
    check(
        "warm start: the derelict stays the player's, with no new contract",
        wait_until(lambda: grid_owner("Test Derelict") == identity, 180, "the owner")
        and not named(found, "Test Derelict"),
        grid_owner("Test Derelict"),
    )

    check("graceful stop at the end", cluster.stop(grace=240))
    for process in processes:
        failures = re.findall(r"\[Octahedron\].*(?:failed|Failed).*", log(process))
        check(f"{process}: no Octahedron failure logged", not failures, failures[:3])


run.administrators = [CLIENT_ID]


def main() -> int:
    # Its computers are the static, ownerless grids the wreck rule has to leave alone
    wa.pl.MODS[3816175430] = "Trade Lanes MP"
    wa.pl.MOD_ITEMS += (
        '<ModItem FriendlyName="Trade Lanes MP"><Name>3816175430.sbm</Name>'
        "<PublishedFileId>3816175430</PublishedFileId>"
        "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
    )
    run.world_template = wa.prepare()
    # The probe on the nodes too, for the conversion; it runs its plan on the World Authority only
    os.environ["CLUSTER_EXTRA_PLUGINS"] = os.environ["CLUSTER_EXTRA_PLUGINS"].replace(
        f"wa:{wa.lc.PLUGINS / 'ConsoleProbe'}", str(wa.lc.PLUGINS / "ConsoleProbe")
    )
    Path(f"{wa.lc.PLAN}.convert").unlink(missing_ok=True)
    Path(f"{wa.lc.PLAN}.converted").unlink(missing_ok=True)
    Path(f"{wa.lc.PLAN}.tow").unlink(missing_ok=True)
    Path(f"{wa.lc.PLAN}.towed").unlink(missing_ok=True)
    add_grids()
    set_config(
        ContractsEnabled="true",
        MoneyEnabled="true",
        ContractBountyMinThreat=1,
        # Debris is the grids under 3 blocks: the lane computers, and the test's pieces
        ContractSalvageMinBlocks=3,
        ContractWreckMinReward=1,
        ContractNpcWreckMinutes=600,
        # The hourly split would change the balances the test compares
        MoneySubsidyPerActivePlayer=0,
        ContractSalvageRadius=500,
        ContractHuntPerStation=0,
        ContractBountiesPerStation=0,
        MarketEnabled="false",
    )
    results = bench_module.HOST.results / datetime.now(timezone.utc).strftime(
        "%Y%m%d-%H%M%SZ-contracts"
    )
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-CONTRACTS",
        "SE-702, SE-1385: bounties and the wreck hunt on a cluster",
        "3",
        results,
    )
    print(
        json.dumps(
            {"passed": outcome.passed, "error": outcome.error, "results": str(results)}
        )
    )
    print(
        f"{sum(c.passed for c in outcome.checks)}/{len(outcome.checks)} checks passed"
    )
    for item in outcome.checks:
        if not item.passed:
            print(
                "FAILED: " + item.label + (f" - {item.detail}" if item.detail else "")
            )
    print("PASS" if outcome.passed else "FAIL")
    return 0 if outcome.passed else 1


if __name__ == "__main__":
    sys.exit(main())
