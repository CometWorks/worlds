"""Checks Good.bot's hints in global chat (SE-1538) and that the config has them.

Offline, on config/octahedron.cfg: hints are on, between 30 and 120 minutes apart, and there
are at least three different ones.

On the test server, with the waits cut to 4 to 8 s: a client hears hints from Good.bot, every
one a hint of the config, never the same one twice in a row, one at a time and no closer or
further apart than the two waits, all of them within two minutes, and the server logged as many.

    OCT_TASK=SE-1538 uv run --no-project --with httpx python tests/test_hints.py

`--offline` runs only the config checks. tests/hints has the unit tests of the timer
(`dotnet test`), tests/hints_cluster.py checks that a cluster says a hint once.

Run on nas on 2026-11-14: 10/10 checks, 21 hints in 120 s.
"""

from __future__ import annotations

import os
import sys
import time
import xml.etree.ElementTree as ET

os.environ.setdefault("OCT_TASK", "SE-1538")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
from station_tests import check  # noqa: E402
from test_client import log  # noqa: E402

AUTHOR = "Good.bot"
MIN_S, MAX_S = 4, 8
WATCH = 120
# A hint shows up to this much early or late: the client is polled, and the chat takes a moment
SLACK = 2.0


def config_hints(text: str) -> list[str]:
    return [h.findtext("Text") or "" for h in ET.fromstring(text).iter("HintEntry")]


def check_config(text: str) -> None:
    root = ET.fromstring(text)
    hints = config_hints(text)
    check(root.findtext("HintsEnabled") == "true", "the config has the hints on")
    low, high = (float(root.findtext(k)) for k in ("HintMinMinutes", "HintMaxMinutes"))
    check((low, high) == (30, 120), f"a hint every {low:g} to {high:g} minutes")
    check(
        len(hints) >= 3 and len(set(hints)) == len(hints) and all(hints),
        f"the config has {len(hints)} different hints",
    )


def test_config(text: str) -> str:
    """The config with the waits between hints cut to seconds"""
    for key, seconds in (("HintMinMinutes", MIN_S), ("HintMaxMinutes", MAX_S)):
        old = f"<{key}>{ET.fromstring(text).findtext(key)}</{key}>"
        assert old in text, old
        text = text.replace(old, f"<{key}>{seconds / 60:.4f}</{key}>")
    return text


def watch(remote, seconds: float) -> list[tuple[float, str]]:
    """(when first seen, text) of every line Good.bot says in the client's chat for seconds"""
    key = lambda h: (h.get("sender"), h.get("text"), h.get("timestamp"))  # noqa: E731
    seen = {key(h) for h in remote.get_chat_messages().get("hud", [])}
    heard = []
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        time.sleep(0.25)
        for h in remote.get_chat_messages().get("hud", []):
            if key(h) in seen:
                continue
            seen.add(key(h))
            if h.get("sender") == AUTHOR:
                heard.append((time.monotonic(), h.get("text", "")))
                log(f"{AUTHOR}: {h.get('text')}")
    return heard


def check_heard(check, heard: list[tuple[float, str]], hints: list[str]) -> None:
    """check(ok, what) records a check"""
    texts = [t for _, t in heard]
    gaps = [b - a for (a, _), (b, _) in zip(heard, heard[1:])]
    span = f"{min(gaps, default=0):.1f} to {max(gaps, default=0):.1f} s apart"
    check(
        WATCH / MAX_S - 2 <= len(heard) <= WATCH / MIN_S + 1,
        f"{len(heard)} hints in {WATCH} s, {span}",
    )
    check(set(texts) <= set(hints), f"every line is a hint of the config: {set(texts)}")
    check(set(texts) == set(hints), f"all {len(hints)} hints came")
    check(
        all(a != b for a, b in zip(texts, texts[1:])),
        "no hint came twice in a row",
    )
    check(
        bool(gaps) and min(gaps) >= MIN_S - SLACK,
        f"one hint at a time, none sooner than {MIN_S} s after the last ({span})",
    )
    check(
        bool(gaps) and max(gaps) <= MAX_S + SLACK,
        f"none later than {MAX_S} s after the last ({span})",
    )


def main() -> int:
    text = rig.CONFIG_SOURCE.read_text(encoding="utf-8")
    check_config(text)
    if "--offline" in sys.argv:
        return st.summary("hints")

    text = test_config(text)
    hints = config_hints(text)

    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config(text)
    rig.prepare_server()
    rig.start_server()
    remote = rig.start_client()
    try:
        heard = watch(remote, WATCH)
        check_heard(check, heard, hints)
        logged = rig.server_game_log().count("Hints: ")
        check(
            logged >= len(heard),
            f"the server logged {logged} hints, the client heard {len(heard)}",
        )
    finally:
        rig.stop_client()
        rig.stop_server()
    return st.summary("hints")


if __name__ == "__main__":
    sys.exit(main())
