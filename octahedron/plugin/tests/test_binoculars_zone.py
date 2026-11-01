"""Checks the Binoculars mod's tool in a station safe zone that forbids shooting, with the
server and the headless client: on the client the binoculars' zoom works there, and a rifle
stays refused before and after them. Red without BinocularsInZones.

    uv run --no-project --with httpx python tests/test_binoculars_zone.py

Needs the Workshop copies of Binoculars and Scope Framework, as OCT_WORKSHOP says.
"""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-912")
import rig  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_zone as zone  # noqa: E402

MODS = {2754014019: "Scope Framework", 2777644246: "Binoculars"}
PROBE = (
    "binoculars-probe",
    Path(__file__).resolve().parent / "binoculars-probe",
    "BinocularsProbe.xml",
    "binoculars-probe",
)
BINOCULARS = "BinocularsItem"
RIFLE = "AutomaticRifleItem"
DENIED = "SafeZoneDenied"
# More than the server takes to tell the client
WAIT = 10.0


def prepare() -> None:
    rig.prepare_world()
    items = "".join(
        f'<ModItem FriendlyName="{name}"><Name>{id_}.sbm</Name><PublishedFileId>{id_}'
        "</PublishedFileId><PublishedServiceName>Steam</PublishedServiceName></ModItem>"
        for id_, name in MODS.items()
    )
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = rig.WORLD / name
        text = path.read_text(encoding="utf-8")
        path.write_text(
            text.replace("<Mods />", f"<Mods>{items}</Mods>", 1), encoding="utf-8"
        )
    for folder in (rig.SERVER_DATA / "Mods", rig.APPDATA / "Mods"):
        folder.mkdir(parents=True, exist_ok=True)
        for id_ in MODS:
            link = folder / f"{id_}.sbm"
            link.unlink(missing_ok=True)
            link.symlink_to(pl.WORKSHOP / str(id_))
    rig.write_plugin_config(rig.CONFIG_SOURCE.read_text(encoding="utf-8"))
    rig.prepare_server(extra_dev_folders=[pl.LOCAL_WORLD_MODS, PROBE])


def probe() -> list[str]:
    """The gun's subtype, CanShoot of its secondary and primary action, and the zoom mode"""
    path = rig.APPDATA / "binoculars-probe.txt"
    return path.read_text(encoding="utf-8").split() if path.exists() else []


def holds(remote, subtype: str, secondary: str, timeout: float = WAIT) -> bool:
    """Equips the gun and tells if its secondary action comes to answer as expected"""
    remote.character_equip(subtype)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = probe()
        if state[:1] == [subtype] and state[1] == secondary:
            return True
        time.sleep(0.25)
    print(f"  last state: {probe()}", flush=True)
    return False


def run() -> int:
    rig.stop_client()
    rig.stop_server()
    _, _, inside, _ = zone.layout()
    prepare()
    rig.start_server()
    remote = rig.start_client(extra_dev_folders=[PROBE])
    remote.character_teleport(*inside)
    remote.character_set_dampeners(True)
    rig.wait_until(
        lambda: holds(remote, RIFLE, DENIED, 3), 120, "a rifle refused in the zone"
    )

    results = {"rifle refused": True}
    results["binoculars allowed"] = holds(remote, BINOCULARS, "OK")
    remote.set_input_state(mouse_right=True)
    time.sleep(1)
    remote.clear_input_state()
    state = probe()
    print(f"  after a right click: {state}", flush=True)
    results["binoculars zoom"] = state[-1:] == ["IronSight"]
    results["rifle refused after binoculars"] = holds(remote, RIFLE, DENIED)
    remote.character_primary_action(True)
    time.sleep(3)
    results["rifle does not fire"] = not remote.get_character()["weapon"]["shooting"]
    remote.character_primary_action(False)

    for name, passed in results.items():
        print(f"{'PASS' if passed else 'FAIL'} {name}", flush=True)
    rig.stop_client()
    rig.stop_server()
    errors = [
        line
        for line in rig.server_game_log().splitlines()
        if "Octahedron" in line and ("Exception" in line or "Critical" in line)
    ]
    print(f"{'FAIL' if errors else 'PASS'} no plugin errors {errors[:3]}", flush=True)
    return 0 if all(results.values()) and not errors else 1


if __name__ == "__main__":
    try:
        code = run()
    finally:
        rig.stop_client()
        rig.stop_server()
    print("passed" if code == 0 else "failed", flush=True)
    sys.exit(code)
