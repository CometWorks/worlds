"""Checks the station kiosks on the test server with the headless client: the kiosk
client mod (octahedron/kiosk-mod) and TouchScreenAPI load as world mods on both
sides, and the client mod's requests go to the plugin through the same mod messages
the touch screens send. The client drives them with the mod's admin command
/kiosk instead of touch input.

The kiosk probe (tests/kiosks/probe) docks ships at Alpha Station's connectors and
sets the player's faction and rank. Checked: the plugin selects the mod's screen
script on the kiosk, reach, the docked ship list by inventory access, deposit and
withdraw between the ship and the ledger (amounts, all, too much, whole components,
the ledger limit putting the items back), From hand, the Faction switch and the
trading rank, Transfer give and take, the members setting of leaders, and the
request rate limit. Prints one line per check and a pass/fail summary; exit code
0 = all passed.

    OCT_TASK=SE-683 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_kiosks.py

Not covered: the touch screens themselves (tests/test_kiosk_screen.py clicks them
with a rendering client), and a cluster.
"""

from __future__ import annotations

import os
import re
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-683")
import rig  # noqa: E402
from test_client import log, with_config  # noqa: E402

TESTS = Path(__file__).resolve().parent
WORKSHOP = Path(
    os.environ.get(
        "OCT_WORKSHOP", rig.HOME / ".steam/steam/steamapps/workshop/content/244850"
    )
)
TOUCH_SCREEN_API = 2668820525
# The kiosk mod has no Workshop item yet: a stand-in id, served from the Mods folders
KIOSK_MOD = 900000683
MODS = {
    TOUCH_SCREEN_API: ("TouchScreenAPI", WORKSHOP / str(TOUCH_SCREEN_API)),
    KIOSK_MOD: ("Octahedron Kiosk", rig.PLUGIN.parent / "kiosk-mod"),
}
LOCAL_WORLD_MODS = (
    "local-world-mods",
    TESTS / "local-world-mods",
    "LocalWorldMods.xml",
    "local-world-mods",
)
PROBE = (
    "kiosk-probe",
    TESTS / "kiosks" / "probe",
    "KioskProbe.xml",
    "5C1A9E07-3D84-4B6F-A2E9-0E6C1D1554AA",
)
IRON = "Ingot/Iron"
PLATE = "Component/SteelPlate"

checks: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    checks.append((ok, what))
    log(f"{'PASS' if ok else 'FAIL'} {what}")


class State:
    """What the mod's /kiosk command printed for one answer of the server"""

    def __init__(self, lines: list[str]):
        self.lines = lines
        first = next((l for l in lines if l.startswith(("ok", "refused"))), "")
        self.ok = first.startswith("ok")
        self.message = first.partition(": ")[2]
        self.ships = [l.split(" ", 2)[1] for l in lines if l.startswith("ship ")]
        self.faction = next((l for l in lines if l.startswith("faction ")), "")
        self.kiosk = next((l for l in lines if l.startswith("kiosk ")), "")
        self.items: dict[str, dict[str, float]] = {}
        for line in lines:
            if line.startswith("item "):
                _, item, *amounts = line.split(" ")
                self.items[item] = {
                    k: float(v) for k, v in (a.split("=") for a in amounts)
                }

    def amount(self, item: str, where: str) -> float:
        return self.items.get(item, {}).get(where, 0.0)

    def __str__(self) -> str:
        return " | ".join(self.lines)


def kiosk(remote, args: str = "state", wait: float = 3.0) -> State:
    time.sleep(0.6)  # the plugin's cooldown between requests
    lines = rig.chat(remote, f"/kiosk {args}", wait)
    return State(lines)


def expect(
    remote,
    args: str,
    what: str,
    ok: bool = True,
    message: str = "",
    amounts: dict | None = None,
) -> State:
    state = kiosk(remote, args)
    good = state.ok == ok and message in state.message
    wrong = [
        f"{item} {where}={state.amount(item, where)} not {value}"
        for (item, where), value in (amounts or {}).items()
        if abs(state.amount(item, where) - value) > 1e-6
    ]
    check(good and not wrong, f"{what}: {'; '.join(wrong) or state}")
    return state


def probe(remote, args: str, wait: float = 3.0) -> str:
    reply = " ".join(rig.chat(remote, f"!kprobe {args}", wait))
    log(f"!kprobe {args} -> {reply}")
    return reply


def prepare() -> None:
    rig.prepare_world()
    items = "".join(
        f'<ModItem FriendlyName="{name}"><Name>{id_}.sbm</Name><PublishedFileId>{id_}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
        for id_, (name, _) in MODS.items()
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("<Mods />", f"<Mods>{items}</Mods>", 1), "utf-8")
    # The server takes them through local-world-mods, the client through Remote's LocalWorldModsPatch
    for mods in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        mods.mkdir(parents=True, exist_ok=True)
        for id_, (_, folder) in MODS.items():
            link = mods / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(folder)
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[LOCAL_WORLD_MODS, PROBE])


def wait_docked(remote, ship: str) -> bool:
    for _ in range(10):
        if f"DOCKED {ship} yes" in probe(remote, f"docked {ship}", 2):
            return True
    return False


def run(remote) -> None:
    reply = probe(remote, "kiosk Alpha")
    match = re.search(
        r"KIOSK (\d+) \S+ \S+ \S+ STAND (\S+) (\S+) (\S+) SCRIPT (\S*)", reply
    )
    check(match is not None, f"Alpha Station has a kiosk: {reply}")
    if not match:
        return
    kiosk_id = match[1]
    stand = tuple(float(v) for v in match.group(2, 3, 4))
    check(
        match[5] == "Octahedron_Kiosk",
        f"the plugin selected the mod's screen: {match[5]}",
    )

    far = kiosk(remote, f"state kiosk={kiosk_id}")
    check(
        not far.ok and "closer" in far.message,
        f"the mod reaches the plugin; too far from the kiosk: {far}",
    )
    remote.character_teleport(*stand)
    time.sleep(3)
    expect(remote, "state", "at the kiosk, found without its id")

    mine = re.search(r"SHIP (\d+)", probe(remote, "dock Alpha mine"))
    stranger = re.search(r"SHIP (\d+)", probe(remote, "dock Alpha stranger"))
    check(
        mine is not None and stranger is not None, "two ships built at the connectors"
    )
    if not (mine and stranger):
        return
    mine, stranger = mine[1], stranger[1]
    check(
        wait_docked(remote, mine) and wait_docked(remote, stranger), "both ships docked"
    )

    state = expect(
        remote,
        "state",
        "the docked ship",
        amounts={(IRON, "source"): 1000, (PLATE, "source"): 50},
    )
    check(
        state.ships == [mine],
        f"only the ship the player has access to is listed: {state.ships}",
    )
    expect(
        remote,
        f"deposit ship={stranger} item={IRON} amount=1",
        "another player's ship is refused",
        ok=False,
        message="not docked",
    )

    expect(
        remote,
        f"deposit item={IRON} amount=400",
        "deposit 400 iron from the ship",
        message="Stored 400",
        amounts={(IRON, "mine"): 400, (IRON, "source"): 600},
    )
    expect(
        remote,
        f"deposit ship={mine} item={PLATE} amount=all",
        "deposit all plates",
        amounts={(PLATE, "mine"): 50, (PLATE, "source"): 0},
    )
    expect(
        remote,
        f"withdraw item={IRON} amount=100",
        "withdraw 100 iron into the ship",
        amounts={(IRON, "mine"): 300, (IRON, "source"): 700},
    )
    expect(
        remote,
        f"deposit item={IRON} amount=5000",
        "more than the ship has",
        ok=False,
        message="only 700",
        amounts={(IRON, "source"): 700},
    )
    expect(
        remote,
        f"withdraw item={PLATE} amount=0.5",
        "half a plate",
        ok=False,
        amounts={(PLATE, "mine"): 50},
    )
    shown = " ".join(rig.chat(remote, "!oct ledger show Alpha me", 4))
    check(
        "Ingot/Iron 300" in shown and "Component/SteelPlate 50" in shown,
        f"the ledger agrees: {shown}",
    )

    # 300 kg of iron and 1 t of plates; another 700 kg would pass 1.5 t
    with_config(remote, LedgerPlayerLimitTonnes=1.5)
    expect(
        remote,
        f"deposit item={IRON} amount=700",
        "over the ledger limit, the items stay in the ship",
        ok=False,
        message="over the limit",
        amounts={(IRON, "mine"): 300, (IRON, "source"): 700},
    )
    with_config(remote)

    expect(
        remote,
        f"withdraw hand item={IRON} amount=10",
        "withdraw into the character's inventory",
        amounts={(IRON, "mine"): 290},
    )
    hand = expect(remote, "state hand", "From hand shows the inventory")
    carried = hand.amount(IRON, "source")
    check(carried >= 10, f"the character carries the iron: {carried}")
    expect(
        remote,
        f"deposit hand item={IRON} amount=4",
        "deposit from the character",
        amounts={(IRON, "mine"): 294, (IRON, "source"): carried - 4},
    )

    # Factions: a member, then a leader, then the members setting
    expect(
        remote,
        f"give item={IRON} amount=1",
        "no faction, no transfer",
        ok=False,
        message="not in a faction",
    )
    probe(remote, "faction KME member")
    check("KME member" in probe(remote, "whoami"), "the player is a KME member")
    state = expect(remote, "state", "member's state")
    check(
        state.faction.startswith("faction KME trade=0 members=0 set=0"), state.faction
    )
    expect(
        remote,
        f"give item={IRON} amount=50",
        "any member gives to the faction",
        amounts={(IRON, "mine"): 244, (IRON, "faction"): 50},
    )
    expect(
        remote,
        f"take item={IRON} amount=10",
        "a member may not take",
        ok=False,
        message="rank",
        amounts={(IRON, "faction"): 50},
    )
    expect(
        remote,
        f"deposit faction item={IRON} amount=10",
        "a member may not use the Faction switch",
        ok=False,
        message="rank",
    )
    expect(
        remote,
        "members amount=1",
        "a member may not change who trades",
        ok=False,
        message="leaders",
    )

    probe(remote, "faction KME leader")
    check("KME leader" in probe(remote, "whoami"), "the player is a KME leader")
    expect(
        remote,
        f"take item={IRON} amount=10",
        "a leader takes from the faction",
        amounts={(IRON, "mine"): 254, (IRON, "faction"): 40},
    )
    expect(
        remote,
        f"deposit faction item={IRON} amount=20",
        "Faction switch: deposit from the ship into the faction's ledger",
        amounts={(IRON, "faction"): 60, (IRON, "source"): 680, (IRON, "mine"): 254},
    )
    expect(
        remote,
        f"withdraw faction item={IRON} amount=5",
        "Faction switch: withdraw from the faction's ledger",
        amounts={(IRON, "faction"): 55, (IRON, "source"): 685},
    )
    state = expect(remote, "members amount=1", "a leader lets every member trade")
    check("members=1" in state.faction, state.faction)

    probe(remote, "faction KME member")
    expect(
        remote,
        f"take item={IRON} amount=5",
        "with the setting a member takes",
        amounts={(IRON, "mine"): 259, (IRON, "faction"): 50},
    )
    probe(remote, "faction KME leader")
    expect(remote, "members", "the leader turns it off again")
    probe(remote, "faction KME member")
    expect(
        remote,
        f"take item={IRON} amount=5",
        "without it the member may not take again",
        ok=False,
        message="rank",
    )

    probe(remote, "faction KFO founder")
    state = expect(remote, "state", "founder's state")
    check(
        state.faction.startswith("faction KFO trade=1 members=0 set=1"), state.faction
    )
    expect(
        remote,
        f"withdraw faction hand item={IRON} amount=1",
        "the founder's new faction has nothing at Alpha",
        ok=False,
        message="none of that",
    )

    # Two requests at once: the second comes too soon
    before = remote.get_chat_messages()
    remote.send_chat("/kiosk state")
    remote.send_chat("/kiosk state")
    time.sleep(3)
    after = remote.get_chat_messages(after=before.get("latest", 0))
    texts = [h.get("text", "") for h in after.get("hud", [])]
    check(
        any("Not so fast" in t or "Still working" in t for t in texts),
        f"the rate limit refuses the second request: {[t for t in texts if t.startswith(('ok', 'refused'))]}",
    )

    probe(remote, "undock")


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    prepare()
    rig.start_server()
    remote = rig.start_client()
    log("joined")
    try:
        run(remote)
    finally:
        rig.stop_client()
        rig.stop_server()
    for line in rig.server_game_log().splitlines():
        if "Octahedron" in line and ("rror" in line or "Kiosk" in line):
            log("server: " + line.strip())
    failed = [what for passed, what in checks if not passed]
    print(f"{len(checks) - len(failed)}/{len(checks)} kiosk checks passed", flush=True)
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
