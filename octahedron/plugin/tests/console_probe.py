"""What the cluster tests share to run admin commands on a process of a local test cluster: the
console probe (tests/console-probe) runs a plan file's chat command lines as the server console.
build() makes the Octahedron plugin and the probe local plugins for CLUSTER_EXTRA_PLUGINS.
"""

from __future__ import annotations

import os
import subprocess
import time
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[1]
import rig  # noqa: E402  (the world copy; claims the task's slot, starts nothing)

ROOT = Path.home() / "cluster-runs/console-probe"
PLUGINS = ROOT / "plugins"
PLAN = ROOT / "probe-plan.txt"
OUT = ROOT / "probe-out.txt"
PROBE_ID = "3B8E1F52-6C47-4D0A-9E21-5A7C0D1553AA"


def build() -> None:
    """Octahedron and the probe as local plugins (DLL + manifest per folder) for CLUSTER_EXTRA_PLUGINS"""
    magnetar = os.environ["MAGNETAR_HOME"]
    commit = subprocess.check_output(
        ["git", "-C", str(PLUGIN), "rev-parse", "HEAD"], text=True
    ).strip()
    for project, name, plugin_id in (
        ("ServerPlugin/ServerPlugin.csproj", "Octahedron", rig.PLUGIN_ID),
        ("tests/console-probe/ConsoleProbe.csproj", "ConsoleProbe", PROBE_ID),
    ):
        out = PLUGINS / ".build" / name
        subprocess.run(
            ["nice", "-n", "19", "dotnet", "build", str(PLUGIN / project), "-c", "Release",
             "--nologo", "-v", "q", f"-p:Magnetar={magnetar}", "-o", str(out)],
            check=True,
        )  # fmt: skip
        folder = PLUGINS / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / f"{name}.dll").write_bytes((out / f"{name}.dll").read_bytes())
        (folder / f"{name}.xml").write_text(
            '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
            f"<Id>{plugin_id}</Id><FriendlyName>{name}</FriendlyName><RepoId>CometWorks/worlds</RepoId>"
            f"<Commit>{commit}</Commit><Runtimes>CoreCLR</Runtimes></PluginData>\n",
            encoding="utf-8",
        )


class Probe:
    """Sends one command line at a time to the probe and waits for its replies"""

    def __init__(self) -> None:
        self.step = 0

    def replies(self, step: int) -> list[str]:
        lines = OUT.read_text(errors="replace").splitlines()
        return [l.split("\t", 1)[1] for l in lines if l.split("\t", 1)[0] == str(step)]

    def run(self, line: str, timeout: float = 120, settle: float = 2) -> list[str]:
        step = self.step
        self.step += 1
        with PLAN.open("a") as plan:
            plan.write(line + "\n")
        deadline = time.monotonic() + timeout
        while not self.replies(step):
            if time.monotonic() > deadline:
                return []
            time.sleep(1)
        time.sleep(settle)
        return self.replies(step)
