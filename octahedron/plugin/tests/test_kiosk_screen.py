"""Checks the kiosk's touch screen on the test server with a headless rendering client:
what the LCD shows and what clicks on it do, through TouchScreenAPI's real cursor.

The client looks at a point of the LCD with Remote's look-at, closed-loop on the cursor
the API computed from that view, and holds the left mouse button with Remote's input
state. The mod's /kiosk screen writes the frame to its local storage: the sprites, the
hit area of everything clickable, the cursor and how often the screen script failed.

Checked on every screen reachable by clicks (Storage with a ship and From hand, Mine and
Faction, Transfer without a faction, as a member and as a leader, Market prices and My
listings): text vertically centered in its row, the row's background exactly its hit
area, nothing clickable overlapping or off the screen, the hover highlight on the row
under the cursor only, a click picks that row and the screen stays alive, selectors
keep the chosen value, and the economy by clicks: deposit, withdraw, give, take, the
members setting, buy, sell, offer, order and cancel, read back through /kiosk.
Prints one line per check and a pass/fail summary; exit code 0 = all passed.

    OCT_TASK=SE-781 OCT_WORLD_SOURCE=<world from build_world.py> \\
        uv run --no-project --with httpx python tests/test_kiosk_screen.py
"""

from __future__ import annotations

import os
import re
import sys
import time

os.environ.setdefault("OCT_TASK", "SE-781")
import rig  # noqa: E402
import test_kiosks as kiosks  # noqa: E402
from test_client import log  # noqa: E402

check = kiosks.check
IRON = "Ingot/Iron"
PLATE = "Component/SteelPlate"
# The drawn line height of a label: TouchScreenAPI's Label.UpdateHeight
LINE_HEIGHT = 28.8


class Frame:
    """One /kiosk screen report"""

    def __init__(self, text: str):
        self.failures = 0
        self.screen: dict[str, str] = {}
        self.cursor = (0.0, 0.0)
        self.on_screen = False
        self.viewport = (0.0, 0.0, 0.0, 0.0)
        self.scale = 1.0
        self.hits: dict[str, dict] = {}
        self.sprites: list[dict] = []
        for line in text.splitlines():
            f = line.split("\t")
            if f[0] == "request":
                self.failures = int(f[2])
            elif f[0] == "screen":
                self.screen = dict(c.split("=", 1) for c in f[1:])
            elif f[0] == "cursor":
                self.cursor = (float(f[1]), float(f[2]))
                self.on_screen = f[3] == "True"
            elif f[0] == "viewport":
                self.viewport = tuple(float(v) for v in f[1:5])
                self.scale = float(f[5])
            elif f[0] == "hit":
                x1, y1, x2, y2 = (float(v) for v in f[2:6])
                px, py = float(f[7]), float(f[8])
                # A row scrolled out of the list keeps its last hit area, or none yet
                placed = (x1, y1, x2, y2) != (0, 0, 0, 0) and (
                    abs(px - x1) < 1 and abs(py - y1) < 1
                )
                self.hits[f[1]] = {
                    "rect": (x1, y1, x2, y2),
                    "over": f[6] == "True",
                    "shown": f[9] == "True" and placed,
                }
            elif f[0] == "sprite":
                self.sprites.append(
                    {
                        "type": f[1],
                        "data": f[2],
                        "x": float(f[3]),
                        "y": float(f[4]),
                        "w": float(f[5]),
                        "h": float(f[6]),
                        "scale": float(f[7]),
                        "color": f[8],
                    }
                )

    def shown(self, prefix: str = "") -> dict[str, dict]:
        return {
            k: h for k, h in self.hits.items() if h["shown"] and k.startswith(prefix)
        }

    def texts(self) -> list[dict]:
        return [s for s in self.sprites if s["type"] == "TEXT"]

    def alive(self) -> bool:
        """The screen draws: the script has not failed and the window bar and tabs are there"""
        return self.failures == 0 and len(self.sprites) > 5

    def background(self, rect: tuple) -> dict | None:
        """The square drawn exactly as this rectangle"""
        x1, y1, x2, y2 = rect
        for s in self.sprites:
            if (
                s["type"] == "TEXTURE"
                and s["data"] == "SquareSimple"
                and abs(s["x"] - x1) <= 1
                and abs(s["y"] - (y1 + y2) / 2) <= 1
                and abs(s["w"] - (x2 - x1)) <= 1
                and abs(s["h"] - (y2 - y1)) <= 1
            ):
                return s
        return None

    def __str__(self) -> str:
        return " ".join(f"{k}={v}" for k, v in self.screen.items())


class Lcd:
    """The kiosk as the player's cursor reaches it"""

    def __init__(self, remote, position, right, up, forward):
        self.remote = remote
        self.storage = rig.APPDATA / "Storage"
        self.serial = 0
        # The screen is on the block's local +X face
        self.center = [p + 1.2 * r for p, r in zip(position, right)]
        self.axes = (forward, up)
        self.a = self.b = 0.0
        self.calibrated = None

    def frame(self, timeout: float = 5.0) -> Frame:
        self.serial += 1
        self.remote.send_chat(f"/kiosk screen {self.serial}")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for path in self.storage.glob("*/kiosk-screen.txt"):
                text = path.read_text("utf-8")
                if text.startswith(f"request\t{self.serial}\t"):
                    return Frame(text)
            time.sleep(0.1)
        raise TimeoutError("no /kiosk screen report: the screen script does not run")

    def until(self, what, timeout: float = 12.0) -> Frame:
        """The frame once what(frame) holds, or the last one"""
        deadline = time.monotonic() + timeout
        while True:
            frame = self.frame()
            if what(frame) or time.monotonic() > deadline:
                return frame
            time.sleep(0.5)

    def _look(self, a: float, b: float) -> Frame:
        target = [
            c + a * f + b * u
            for c, f, u in zip(self.center, self.axes[0], self.axes[1])
        ]
        self.remote.character_look_at(*target, tolerance=0.2)
        time.sleep(0.5)
        return self.frame()

    def calibrate(self) -> bool:
        """How many pixels a metre along the block's axes moves the cursor"""
        base = self._look(0, 0)
        moved_a = self._look(0.25, 0)
        moved_b = self._look(0, 0.25)
        if not (base.on_screen and moved_a.on_screen and moved_b.on_screen):
            log(f"cursor off screen: {base.cursor} {moved_a.cursor} {moved_b.cursor}")
            return False
        self.calibrated = (
            (
                (moved_a.cursor[0] - base.cursor[0]) / 0.25,
                (moved_a.cursor[1] - base.cursor[1]) / 0.25,
            ),
            (
                (moved_b.cursor[0] - base.cursor[0]) / 0.25,
                (moved_b.cursor[1] - base.cursor[1]) / 0.25,
            ),
        )
        log(f"calibrated: {self.calibrated}")
        return True

    def aim(self, x: float, y: float, tolerance: float = 3.0) -> Frame:
        """Moves the cursor to this pixel of the screen, correcting from what it reports"""
        (ax, ay), (bx, by) = self.calibrated
        det = ax * by - bx * ay
        frame = self.frame()
        for _ in range(6):
            dx, dy = x - frame.cursor[0], y - frame.cursor[1]
            if frame.on_screen and abs(dx) <= tolerance and abs(dy) <= tolerance:
                break
            self.a += (dx * by - dy * bx) / det
            self.b += (ax * dy - ay * dx) / det
            frame = self._look(self.a, self.b)
        return frame

    def hover(self, name: str, frame: Frame | None = None) -> Frame:
        x1, y1, x2, y2 = (frame or self.frame()).hits[name]["rect"]
        self.aim((x1 + x2) / 2, (y1 + y2) / 2, min(3.0, (y2 - y1) / 4))
        time.sleep(0.4)  # the screen script draws every 10 ticks
        return self.frame()

    def press(self, seconds: float = 0.5) -> None:
        self.remote.set_input_state(mouse_left=True, mode="override")
        time.sleep(seconds)
        self.remote.clear_input_state()
        time.sleep(0.5)

    def click(self, name: str, frame: Frame | None = None) -> Frame:
        """Aims at it and clicks; a check fails if the screen died or the aim was off"""
        hovered = self.hover(name, frame)
        hit = hovered.hits.get(name)
        # A checkbox reports no hover: the API's wrapper has no handler for it
        if hit is None or not (hit["over"] or name == "members"):
            check(False, f"the cursor reaches {name}: at {hovered.cursor}, {hit}")
        self.press()
        after = self.frame()
        if not after.alive():
            check(False, f"the screen is alive after clicking {name}: {after.failures}")
        return after


def layout(lcd: Lcd, screen: str) -> Frame:
    """The checks that hold on every screen"""
    frame = lcd.frame()
    check(frame.alive(), f"{screen}: the screen draws: {len(frame.sprites)} sprites")
    vx, vy, vw, vh = frame.viewport
    tolerance = 2 * frame.scale
    shown = frame.shown()
    outside = [
        n
        for n, h in shown.items()
        if h["rect"][0] < vx - 1
        or h["rect"][1] < vy - 1
        or h["rect"][2] > vx + vw + 1
        or h["rect"][3] > vy + vh + 1
    ]
    check(not outside, f"{screen}: everything clickable is on the screen: {outside}")
    names = list(shown)
    overlaps = [
        (a, b)
        for i, a in enumerate(names)
        for b in names[i + 1 :]
        if shown[a]["rect"][0] < shown[b]["rect"][2] - 1
        and shown[b]["rect"][0] < shown[a]["rect"][2] - 1
        and shown[a]["rect"][1] < shown[b]["rect"][3] - 1
        and shown[b]["rect"][1] < shown[a]["rect"][3] - 1
    ]
    check(not overlaps, f"{screen}: no two clickable things overlap: {overlaps}")

    rows = {
        n: h for n, h in shown.items() if n.startswith(("row:", "listing:", "action:"))
    }
    off_center, no_background = [], []
    for name, hit in rows.items():
        x1, y1, x2, y2 = hit["rect"]
        if frame.background(hit["rect"]) is None:
            no_background.append(name)
        middle = (y1 + y2) / 2
        cells = [t for t in frame.texts() if x1 - 1 <= t["x"] <= x2 + 1]
        if not cells:
            off_center.append((name, "no text"))
            continue
        # Text is drawn from its top edge
        centers = [t["y"] + LINE_HEIGHT * t["scale"] / 2 for t in cells]
        offset = min((c - middle for c in centers), key=abs)
        if abs(offset) > tolerance:
            off_center.append((name, round(offset, 1)))
    check(not off_center, f"{screen}: text centered in its row: {off_center}")
    check(
        not no_background, f"{screen}: the background is the hit area: {no_background}"
    )
    return frame


def rows_hover_and_click(lcd: Lcd, screen: str, prefix: str = "row:") -> None:
    """Every row: the highlight only on the row under the cursor, and a click picks it"""
    frame = lcd.frame()
    names = list(frame.shown(prefix))
    check(bool(names), f"{screen}: has rows")
    for name in names[:8]:
        frame = lcd.hover(name)
        over = [n for n, h in frame.shown().items() if h["over"]]
        check(over == [name], f"{screen}: hover on {name} highlights only it: {over}")
        background = frame.background(frame.hits[name]["rect"])
        others = {
            frame.background(h["rect"])["color"]
            for n, h in frame.shown(prefix).items()
            if n != name and frame.background(h["rect"])
        }
        check(
            background is not None and background["color"] not in others,
            f"{screen}: {name} looks different under the cursor",
        )
        frame = lcd.click(name, frame)
        picked = frame.screen.get("selected" if prefix == "row:" else "listing")
        check(
            frame.alive() and picked == name.split(":", 1)[1],
            f"{screen}: clicking {name} picks it and the screen stays: {frame}",
        )
        if not frame.alive():
            raise RuntimeError(f"{screen}: the screen died on {name}")


def state(lcd: Lcd, args: str = "state") -> kiosks.State:
    """What the server says, again if the screen's own request was in the way"""
    for _ in range(6):
        result = kiosks.kiosk(lcd.remote, args)
        if result.kiosk:
            return result
        time.sleep(1)
    return result


def selector_text(frame: Frame, name: str) -> str:
    """The text drawn between a selector's arrows"""
    _, y1, x2, y2 = frame.hits[f"{name}:prev"]["rect"]
    x3 = frame.hits[f"{name}:next"]["rect"][0]
    inside = [
        t["data"] for t in frame.texts() if x2 < t["x"] < x3 and y1 - 5 < t["y"] < y2
    ]
    return inside[0] if inside else ""


def choose(lcd: Lcd, name: str, value: str) -> Frame:
    frame = lcd.frame()
    for _ in range(12):
        if frame.screen.get(name) == value:
            break
        frame = lcd.click(f"{name}:next", frame)
    check(frame.screen.get(name) == value, f"{name} chosen: {value}: {frame}")
    return frame


def act(lcd: Lcd, name: str, done, what: str) -> None:
    """Clicks an action until the server took it: a click during the screen's own refresh
    gets "Not so fast", as a player's would"""
    for _ in range(4):
        lcd.click(name)
        time.sleep(1)
        if done():
            check(True, what)
            return
        log(f"{name}: not yet, status: {lcd.frame().screen.get('status')}")
    check(False, f"{what}: {lcd.frame()}")


def listings(lcd: Lcd) -> int:
    return sum(line.startswith("listing ") for line in state(lcd, "market").lines)


def run(remote) -> None:
    reply = kiosks.probe(remote, "kiosk Alpha")
    match = re.search(r"STAND (\S+) (\S+) (\S+)", reply)
    check(match is not None, f"Alpha Station has a kiosk: {reply}")
    if not match:
        return
    remote.character_teleport(*(float(v) for v in match.groups()))
    time.sleep(3)
    pose = kiosks.probe(remote, "kioskaxes Alpha")
    numbers = [float(v) for v in re.findall(r"-?\d+\.\d+", pose)]
    check(len(numbers) == 12, f"the kiosk's axes: {pose}")
    lcd = Lcd(remote, numbers[0:3], numbers[3:6], numbers[6:9], numbers[9:12])
    frame = lcd.until(lambda f: f.alive() and f.screen.get("status", "?") == "")
    check(frame.alive(), f"the kiosk screen runs on the client: {frame}")
    if not lcd.calibrate():
        check(False, "the cursor reaches the screen")
        return
    mine = re.search(r"SHIP (\d+)", kiosks.probe(remote, "dock Alpha mine"))
    check(mine is not None, "a ship built at a connector")
    kiosks.wait_docked(remote, mine[1])

    # Storage, the ship
    lcd.until(lambda f: bool(f.shown("row:")))
    layout(lcd, "Storage/Ship")
    rows_hover_and_click(lcd, "Storage/Ship")
    frame = lcd.click(f"row:{IRON}")
    check(frame.screen.get("selected") == IRON, f"iron chosen: {frame}")
    layout(lcd, "Storage/Ship with a row chosen")

    # The chosen amount counts at once, and stays on the selector after the refresh
    choose(lcd, "amount", "10")
    act(
        lcd,
        "action:Deposit",
        lambda: state(lcd).amount(IRON, "mine") == 10,
        "Storage: Deposit moves the chosen 10 iron to the ledger",
    )
    check(
        state(lcd).amount(IRON, "source") == 990, "Storage: the ship has 10 iron less"
    )
    time.sleep(7)  # the screen's refresh rebuilds the selector
    shown = selector_text(lcd.frame(), "amount")
    check(shown == "10", f"the amount selector still shows 10: {shown}")
    act(
        lcd,
        "action:Withdraw",
        lambda: state(lcd).amount(IRON, "mine") == 0,
        "Storage: Withdraw moves the 10 iron back",
    )

    # From hand: the screen of the playtest
    lcd.click("source:From hand")
    frame = lcd.until(
        lambda f: f.screen.get("hand") == "True" and bool(f.shown("row:"))
    )
    check(frame.screen.get("hand") == "True", f"From hand: {frame}")
    layout(lcd, "Storage/From hand")
    rows_hover_and_click(lcd, "Storage/From hand")
    lcd.click("source:Ship")

    # Transfer without a faction
    lcd.click("page:Transfer")
    frame = lcd.until(lambda f: any("Join a faction" in t["data"] for t in f.texts()))
    check(
        any("Join a faction" in t["data"] for t in frame.texts()),
        f"Transfer asks to join a faction: {frame}",
    )
    layout(lcd, "Transfer without a faction")

    # Transfer as a member and as a leader
    kiosks.probe(remote, "faction KME member")
    frame = lcd.until(lambda f: "action:Give" in f.shown())
    layout(lcd, "Transfer as a member")
    check("action:Take" not in frame.shown(), "a member has no Take button")
    state(lcd, f"deposit item={IRON} amount=50")
    lcd.until(lambda f: f"row:{IRON}" in f.shown())
    rows_hover_and_click(lcd, "Transfer as a member")
    lcd.click(f"row:{IRON}")
    act(
        lcd,
        "action:Give",
        lambda: state(lcd).amount(IRON, "faction") == 10,
        "Transfer: Give moves 10 iron to the faction",
    )
    kiosks.probe(remote, "faction KME leader")
    lcd.until(lambda f: "action:Take" in f.shown() and "members" in f.shown())
    layout(lcd, "Transfer as a leader")
    act(
        lcd,
        "action:Take",
        lambda: state(lcd).amount(IRON, "faction") == 0,
        "Transfer: Take moves it back",
    )
    act(
        lcd,
        "members",
        lambda: "members=1" in state(lcd).faction,
        "Transfer: the leader's checkbox lets the members trade",
    )
    act(
        lcd,
        "members",
        lambda: "members=0" in state(lcd).faction,
        "Transfer: and off again",
    )

    # The Faction switch on Storage
    lcd.click("page:Storage")
    frame = lcd.until(lambda f: "ledger:Faction" in f.shown() and bool(f.shown("row:")))
    lcd.click("ledger:Faction", frame)
    frame = lcd.until(
        lambda f: f.screen.get("faction") == "True" and bool(f.shown("row:"))
    )
    check(frame.screen.get("faction") == "True", f"the Faction switch: {frame}")
    layout(lcd, "Storage, the faction's ledger")
    lcd.click(f"row:{IRON}")
    act(
        lcd,
        "action:Deposit",
        lambda: state(lcd).amount(IRON, "faction") == 10,
        "Storage: Deposit into the faction's ledger",
    )
    lcd.click("ledger:Mine")

    # Market prices
    lcd.click("page:Market")
    frame = lcd.until(lambda f: "view:Prices" in f.shown() and bool(f.shown("row:")))
    check("view:Prices" in frame.shown(), f"the market opens: {frame}")
    layout(lcd, "Market/Prices")
    rows_hover_and_click(lcd, "Market/Prices")

    frame = lcd.frame()
    rows = [n for n in frame.hits if n.startswith("row:")]
    visible = frame.shown("row:")
    if len(visible) < len(rows):
        # The rest of the list: the scroll bar is drawn right of the rows
        right = max(h["rect"][2] for h in visible.values())
        check(
            any(
                s["type"] == "TEXTURE"
                and s["data"] == "SquareSimple"
                and s["x"] >= right - 1
                and s["h"] > 20 * frame.scale
                for s in frame.sprites
            ),
            f"the long list has a scroll bar: {len(visible)} of {len(rows)} rows",
        )

    frame = lcd.until(
        lambda f: f"row:{IRON}" in f.shown() or f"row:{PLATE}" in f.shown()
    )
    item = IRON if f"row:{IRON}" in frame.shown() else PLATE
    lcd.click(f"row:{item}")
    choose(lcd, "amount", "1")
    before = state(lcd, "market").amount(item, "mine")
    act(
        lcd,
        "action:Buy",
        lambda: state(lcd, "market").amount(item, "mine") == before + 1,
        "Market: Buy takes 1 from OCT",
    )
    act(
        lcd,
        "action:Sell",
        lambda: state(lcd, "market").amount(item, "mine") == before,
        "Market: Sell gives 1 back",
    )
    frame = lcd.frame()
    price_before = frame.screen["price"]
    frame = lcd.click("price:next", frame)
    check(frame.screen.get("price") != price_before, f"the price selector: {frame}")
    state(lcd, f"deposit item={item} amount=5")
    act(lcd, "action:Offer", lambda: listings(lcd) >= 1, "Market: Offer lists it")
    act(lcd, "action:Order", lambda: listings(lcd) >= 2, "Market: Order lists it")

    # My listings
    lcd.click("view:My listings")
    frame = lcd.until(
        lambda f: f.screen.get("listings") == "True" and bool(f.shown("listing:"))
    )
    check(bool(frame.shown("listing:")), f"My listings shows them: {frame}")
    layout(lcd, "Market/My listings")
    rows_hover_and_click(lcd, "Market/My listings", "listing:")
    count = listings(lcd)
    frame = lcd.frame()
    lcd.click(next(iter(frame.shown("listing:"))), frame)
    act(
        lcd,
        "action:Cancel listing",
        lambda: listings(lcd) < count,
        "Market: Cancel listing takes it down",
    )
    lcd.click("view:Prices")
    lcd.click("page:Storage")
    frame = lcd.until(lambda f: f.screen.get("page") == "0" and bool(f.shown("row:")))
    check(frame.alive(), f"back on Storage, the screen still runs: {frame}")
    kiosks.probe(remote, "undock")


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    kiosks.prepare()
    rig.start_server()
    remote = rig.start_client(render=True)
    log("joined")
    try:
        run(remote)
    except RuntimeError as e:
        check(False, str(e))
    finally:
        rig.stop_client()
        rig.stop_server()
    failed = [what for passed, what in kiosks.checks if not passed]
    print(
        f"{len(kiosks.checks) - len(failed)}/{len(kiosks.checks)} kiosk screen checks passed",
        flush=True,
    )
    for what in failed:
        print(f"FAILED: {what}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
