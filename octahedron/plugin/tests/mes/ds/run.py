"""Standalone Magnetar test of the Octahedron plugin's MES spawn gate (MesCluster, folded in from
CometWorks/mod-mes): one dedicated server, no clients.

Runs the DS's Empty World with MES and a few MES encounter mods (downloaded by the server from the
Workshop), the Octahedron plugin from this working copy and the test probe (tests/mes/probe) as dev
folders. The probe fires MES spawn requests from given positions and lists the NPC grids; check.py
judges the result.

    python3 tests/mes/ds/run.py PLAN [Octahedron.cfg]     (from plugin/, as a background job)

Every port and folder comes from the test slot MES_TASK (default 1509-mes), see
testing/parallel-test-work/README.md of the se workspace. The server starts under run-test.sh (RAM
floor, shared test lock) and is stopped with SIGTERM when the plan is done or after MES_TIMEOUT s.
Output: ~/.se-test/<task>-ds0/{server.log, probe.txt, data/SpaceEngineersDedicated_*.log}.
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

HOME = Path.home()
MES_TESTS = Path(__file__).resolve().parents[1]
PLUGIN = MES_TESTS.parents[1]
WORKSPACE = Path(os.environ.get("MES_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "testing/parallel-test-work"
MAGNETAR = HOME / ".config/Magnetar"
DS64 = (
    HOME
    / ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64"
)
EMPTY_WORLD = DS64.parent / "Content/CustomWorlds/Empty World"
TASK = os.environ.get("MES_TASK", "1509-mes")
ROOT = HOME / ".se-test" / f"{TASK}-ds0"
TIMEOUT = float(os.environ.get("MES_TIMEOUT", "2400"))
# MES_WORLD: load this world folder as it is instead of the Empty World with MODS (seed_world.py)
SOURCE_WORLD = Path(os.environ["MES_WORLD"]) if os.environ.get("MES_WORLD") else None
# MES_GATE=0: the probe only, without the Octahedron plugin
GATE = os.environ.get("MES_GATE", "1") != "0"
SERVER_GIB = 5.0

PLUGIN_ID = re.search(
    r"<Id>([^<]+)</Id>", (PLUGIN / "Octahedron.xml").read_text()
).group(1)
PROBE_ID = re.search(
    r"<Id>([^<]+)</Id>", (MES_TESTS / "probe/MesClusterProbe.xml").read_text()
).group(1)

# MES, encounter content with space random encounters and cargo ships, and the vanilla suppressors
MODS = {
    1521905890: "Modular Encounters Systems",
    2802625870: "MES Wreckage",
    2609155402: "MES Space Pirates Enhanced",
    2615864222: "MES Terran Heavy Industries Engineering",
    888457124: "Suppress Vanilla Cargo Ships",
    888457381: "Suppress Vanilla Encounters",
}


def run(*command) -> str:
    return subprocess.run(
        [str(c) for c in command], check=True, capture_output=True, text=True
    ).stdout.strip()


def slot() -> dict[str, int]:
    number = os.environ.get("MES_SLOT") or run(PARALLEL / "slot.sh", "claim", TASK)
    values = {
        k: int(v)
        for k, v in re.findall(
            r"(\w+_BASE)=(\d+)", run(PARALLEL / "slot.sh", "env", number)
        )
    }
    values["SLOT"] = int(number)
    return values


def prepare(ports: dict[str, int], plan: Path, plugin_cfg: Path | None) -> Path:
    config, data = ROOT / "magnetar", ROOT / "data"
    if data.exists():
        # Keep the downloaded mods, drop everything else of the last run
        for child in data.iterdir():
            if child.name != "content":
                shutil.rmtree(child) if child.is_dir() else child.unlink()
    data.mkdir(parents=True, exist_ok=True)
    (config / "Sources").mkdir(parents=True, exist_ok=True)
    (config / "Profiles").mkdir(exist_ok=True)
    shutil.copy(MAGNETAR / "Magnetar/config.xml", config / "config.xml")
    # The install's hub cache: core plugins (dotnet-compat, linux-compat) come from the hub
    if not (config / "GitHub").exists() and (MAGNETAR / "Magnetar/GitHub").exists():
        shutil.copytree(MAGNETAR / "Magnetar/GitHub", config / "GitHub")

    (config / "Sources/sources.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<SourcesConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><ShowWarning>false</ShowWarning><LocalHubSources />'
        "<RemoteHubSources><RemoteHub><Name>MagnetarHub</Name><Repo>CometWorks/magnetar-hub</Repo><Branch>main</Branch>"
        "<Enabled>true</Enabled><Trusted>true</Trusted></RemoteHub></RemoteHubSources><RemotePluginSources /><LocalPluginSources>"
        + (
            f"<LocalPlugin><Name>octahedron</Name><Folder>{PLUGIN}</Folder><File>Octahedron.xml</File><Enabled>true</Enabled></LocalPlugin>"
            if GATE
            else ""
        )
        + f"<LocalPlugin><Name>mes-probe</Name><Folder>{MES_TESTS / 'probe'}</Folder><File>MesClusterProbe.xml</File><Enabled>true</Enabled></LocalPlugin>"
        "</LocalPluginSources><ModSources /></SourcesConfig>\n"
    )
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>"
        for i in ((PLUGIN_ID, PROBE_ID) if GATE else (PROBE_ID,))
    )
    (config / "Profiles/Current.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        f'xmlns:xsd="http://www.w3.org/2001/XMLSchema"><Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>'
        "<Local /><Mods /></Profile>\n"
    )

    world = data / "Saves/MesTest"
    if SOURCE_WORLD:
        shutil.copytree(SOURCE_WORLD, world, ignore=shutil.ignore_patterns("Backup"))
    else:
        shutil.copytree(EMPTY_WORLD, world)
        mods = "".join(
            f'<ModItem FriendlyName="{name}"><Name>{wid}.sbm</Name><PublishedFileId>{wid}</PublishedFileId>'
            "<PublishedServiceName>Steam</PublishedServiceName></ModItem>"
            for wid, name in MODS.items()
        )
        for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
            text = (world / name).read_text(encoding="utf-8-sig")
            text = text.replace("<Mods />", f"<Mods>{mods}</Mods>", 1)
            # The Octahedron world's sync distance (the gate's default gap is 4 x this); the Empty World has none
            text = re.sub(r"<SyncDistance>\d+</SyncDistance>", "", text)
            text = re.sub(
                r"(<ViewDistance>\d+</ViewDistance>)",
                r"\1<SyncDistance>3000</SyncDistance>",
                text,
                count=1,
            )
            (world / name).write_text(text, encoding="utf-8")
    (world / "Sandbox.sbsB5").unlink(missing_ok=True)
    (world / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)

    (data / "SpaceEngineers-Dedicated.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<IP>127.0.0.1</IP><SteamPort>{ports['DS_STEAM_PORT_BASE']}</SteamPort><ServerPort>{ports['DS_SERVER_PORT_BASE']}</ServerPort>"
        f"<Administrators /><Banned /><ServerName>Octahedron MES Gate Test {TASK}</ServerName><WorldName>MesTest</WorldName>"
        "<PauseGameWhenEmpty>false</PauseGameWhenEmpty><AutoRestartEnabled>false</AutoRestartEnabled>"
        "<IgnoreLastSession>true</IgnoreLastSession><RemoteApiEnabled>false</RemoteApiEnabled>"
        f"<LoadWorld>{world}</LoadWorld></MyConfigDedicated>\n"
    )
    # The plugin's other rules have nothing to act on in the Empty World
    shutil.copy(
        plugin_cfg or PLUGIN.parent / "config/octahedron.cfg", data / "Octahedron.cfg"
    )
    shutil.copy(plan, ROOT / "plan.txt")
    (ROOT / "probe.txt").unlink(missing_ok=True)
    return config


def main() -> int:
    plan = Path(sys.argv[1]).resolve()
    plugin_cfg = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
    ports = slot()
    ROOT.mkdir(parents=True, exist_ok=True)
    config = prepare(ports, plan, plugin_cfg)
    log, probe = ROOT / "server.log", ROOT / "probe.txt"
    with open(log, "w") as out:
        server = subprocess.Popen(
            [
                str(PARALLEL / "run-test.sh"),
                str(SERVER_GIB),
                "--",
                str(MAGNETAR / "MagnetarInterim.bin"),
                "-multiInstance",
                "-stableLogs",
                "-noimplicitmod",
                "-consent",
                "deny",
                "-config",
                str(config),
                "-ds64",
                str(DS64),
                "-path",
                str(ROOT / "data"),
            ],
            cwd=MAGNETAR,
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
            env={
                **os.environ,
                "MESPROBE_PLAN": str(ROOT / "plan.txt"),
                "MESPROBE_OUT": str(probe),
            },
        )
    (ROOT / "server.pid").write_text(str(server.pid))
    deadline, status = time.monotonic() + TIMEOUT, "timeout"
    while time.monotonic() < deadline:
        if server.poll() is not None:
            status = f"server exited {server.returncode}"
            break
        if probe.exists() and re.search(r"^done$", probe.read_text(), re.M):
            status = "done"
            break
        time.sleep(5)
    if server.poll() is None:
        os.killpg(server.pid, signal.SIGTERM)
        try:
            server.wait(180)
        except subprocess.TimeoutExpired:
            os.killpg(server.pid, signal.SIGKILL)
    print(f"run: {status}, logs in {ROOT}")
    return 0 if status == "done" else 1


if __name__ == "__main__":
    sys.exit(main())
