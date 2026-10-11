"""Test rig of the Octahedron plugin: an isolated Magnetar server running a copy of
the Octahedron world with the plugin compiled from this working copy, and a
headless game client with Remote and DirectTransport that joins it.

Ports, the client id and folders come from a test slot of the workspace
(se/notes/parallel-test-work/NOTE.md), claimed under OCT_TASK. Every process
starts under run-test.sh, which waits for free RAM and holds the shared test
lock. Nothing is read from or written to the machine's own Magnetar, Pulsar or
game folders apart from copying their launchers and configs.

The world copy leaves out the mods (a client without Steam can't download them)
and the asteroids (faster loads); the stations, planets and lanes stay.

    uv run --no-project --with httpx python tests/rig.py start      # server, then the client
    uv run --no-project --with httpx python tests/rig.py chat '!oct edge'
    uv run --no-project --with httpx python tests/rig.py stop
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

HOME = Path.home()
PLUGIN = Path(__file__).resolve().parent.parent
WORLD_SOURCE = PLUGIN.parent / "server"
CONFIG_SOURCE = PLUGIN.parent / "config" / "octahedron.cfg"
WORKSPACE = Path(os.environ.get("OCT_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "notes" / "parallel-test-work"
REMOTE_REPO = Path(os.environ.get("OCT_REMOTE_REPO", WORKSPACE / "se1/plugins/remote"))
DIRECT_TRANSPORT_REPO = Path(
    os.environ.get("OCT_DIRECT_TRANSPORT_REPO", WORKSPACE / "se1/direct-transport")
)
MAGNETAR = Path(os.environ.get("OCT_MAGNETAR_DIR", HOME / ".config/Magnetar"))
DS64 = Path(
    os.environ.get(
        "OCT_DS64",
        HOME / ".steam/debian-installation/steamapps/common"
        "/SpaceEngineersDedicatedServer/DedicatedServer64",
    )
)
DS_CONFIG_TEMPLATE = (
    HOME / ".config/SpaceEngineersDedicated/SpaceEngineers-Dedicated.cfg"
)
PULSAR_TEMPLATE = Path(os.environ.get("OCT_PULSAR_TEMPLATE", HOME / ".config/Pulsar"))

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))
from se_remote import RemoteAPI  # noqa: E402

TASK = os.environ.get("OCT_TASK", "SE1-0133")
TEST_ROOT = HOME / ".se-test"
PLUGIN_ID = "F7FB710B-7B41-45D6-B05C-A01DDE0FEE3A"
# Measured RSS (notes/parallel-test-work): a --no-render and a rendering client; the
# server is a guess
CLIENT_GIB = 3.7
RENDER_GIB = 5.5
SERVER_GIB = 4.0


def _slot() -> dict[str, int]:
    slot = subprocess.run(
        [str(PARALLEL / "slot.sh"), "claim", TASK],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()
    text = subprocess.run(
        [str(PARALLEL / "slot.sh"), "env", slot],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return {k: int(v) for k, v in re.findall(r"(\w+_BASE)=(\d+)", text)}


SLOT = _slot()

SERVER_ROOT = TEST_ROOT / f"{TASK}-ds0"
SERVER_CONFIG = SERVER_ROOT / "magnetar"
SERVER_DATA = SERVER_ROOT / "data"
SERVER_PID = SERVER_ROOT / "server.pid"
SERVER_LOG = SERVER_ROOT / "server.log"
SERVER_PORT = SLOT["DS_SERVER_PORT_BASE"]
WORLD = SERVER_DATA / "Saves" / "Octahedron"

PULSAR = TEST_ROOT / f"{TASK}-c0"
LAUNCHER = PULSAR / "Interim.bin"
APPDATA = TEST_ROOT / f"{TASK}-c0-data"
CLIENT_PORT = SLOT["REMOTE_PORT_BASE"]
CLIENT_ID = SLOT["CLIENT_ID_BASE"]
CLIENT_NAME = "OctTester"
CLIENT_PID = APPDATA / "game.pid"
CLIENT_LOG = APPDATA / "launch.log"


# --- world -------------------------------------------------------------------


def _vec(tag: str, v) -> str:
    return f'<{tag} x="{v[0]}" y="{v[1]}" z="{v[2]}" />'


def block(
    xsi_type: str, subtype: str, pos, extra: str = "", forward="Forward", up="Up"
) -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="{xsi_type}">'
        f"<SubtypeName>{subtype}</SubtypeName>{_vec('Min', pos)}"
        f'<BlockOrientation Forward="{forward}" Up="{up}" />{extra}'
        "</MyObjectBuilder_CubeBlock>"
    )


def small_ship_blocks(owner: int = 0) -> list[str]:
    """A large grid cockpit with a battery and two armor blocks behind it"""
    own = f"<Owner>{owner}</Owner><BuiltBy>{owner}</BuiltBy>" if owner else ""
    return [
        block("MyObjectBuilder_Cockpit", "LargeBlockCockpit", (0, 0, 0), own),
        block("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 0, 1), own),
        block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 2), own),
        block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 3), own),
    ]


def grid(
    name: str,
    entity_id: int | None,
    blocks: list[str],
    position=(0, 0, 0),
    forward=(0, 0, -1),
    up=(0, 1, 0),
    velocity=(0, 0, 0),
    tag: str = "MyObjectBuilder_EntityBase",
) -> str:
    kind = (
        ' xsi:type="MyObjectBuilder_CubeGrid"'
        if tag == "MyObjectBuilder_EntityBase"
        else ""
    )
    entity = f"<EntityId>{entity_id}</EntityId>" if entity_id else ""
    return (
        f"<{tag}{kind}><SubtypeName />{entity}"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        "<PositionAndOrientation>"
        + _vec("Position", position)
        + _vec("Forward", forward)
        + _vec("Up", up)
        + "</PositionAndOrientation>"
        "<GridSizeEnum>Large</GridSizeEnum>"
        f"<CubeBlocks>{''.join(blocks)}</CubeBlocks>"
        "<IsStatic>false</IsStatic>"
        + _vec("LinearVelocity", velocity)
        + f"<DisplayName>{name}</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks>"
        f"</{tag}>"
    )


def prepare_world(
    extra_grids=(),
    settings: dict | None = None,
    table_blueprint: str | list[str] | None = None,
) -> None:
    """A fresh copy of the server world without mods and asteroids, with extra grids
    (entity XML) added. table_blueprint (a grid builder XML, or a list of them taken in
    turn) is loaded into every station hologram table, as if a player had, and the tables
    are switched on."""
    shutil.rmtree(WORLD, ignore_errors=True)
    shutil.copytree(
        WORLD_SOURCE, WORLD, ignore=shutil.ignore_patterns("Octahedron-Asteroid-*.vx2")
    )
    sector = WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text, count = re.subn(
        r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_VoxelMap">'
        r"(?:(?!</MyObjectBuilder_EntityBase>).)*?<StorageName>Octahedron-Asteroid-"
        r".*?</MyObjectBuilder_EntityBase>\s*",
        "",
        text,
        flags=re.S,
    )
    assert count > 1000, count
    if table_blueprint:
        blueprints = (
            [table_blueprint] if isinstance(table_blueprint, str) else table_blueprint
        )
        tables = iter(range(1_000_000))
        text, count = re.subn(
            r'(<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_Projector">\s*'
            r"<SubtypeName>LargeBlockConsole</SubtypeName>.*?)<Enabled>false</Enabled>",
            lambda m: f"{m[1]}<Enabled>true</Enabled><ProjectedGrids>"
            f"{blueprints[next(tables) % len(blueprints)]}</ProjectedGrids>",
            text,
            flags=re.S,
        )
        assert count >= 28, count
    text = text.replace(
        "</SectorObjects>", "".join(extra_grids) + "</SectorObjects>", 1
    )
    sector.write_text(text, encoding="utf-8")

    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = WORLD / name
        text = path.read_text(encoding="utf-8")
        text, count = re.subn(r"<Mods>.*?</Mods>", "<Mods />", text, flags=re.S)
        assert count == 1, name
        for key, value in (settings or {}).items():
            text, count = re.subn(
                rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text
            )
            if count == 0:
                text, count = re.subn(
                    r"</Settings>", f"<{key}>{value}</{key}></Settings>", text
                )
            assert count == 1, (name, key)
        path.write_text(text, encoding="utf-8")


# --- plugin sources and profiles ---------------------------------------------------


def profile_xml(dev_folders, local=()) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
        for i in dev_folders
    )
    locals_ = "".join(f"<string>{i}</string>" for i in local)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>"
        f"<Local>{locals_}</Local><Mods /></Profile>\n"
    )


def unregister_source(sources: Path, name: str) -> None:
    tree = ET.parse(sources)
    local = tree.getroot().find("LocalPluginSources")
    for plugin in local.findall("LocalPlugin") if local is not None else []:
        if plugin.findtext("Name") == name:
            local.remove(plugin)
    tree.write(sources, encoding="utf-8", xml_declaration=True)


def register_source(sources: Path, name: str, folder: Path, file: str) -> None:
    """Points the loader's dev folder source of this name at a folder"""
    unregister_source(sources, name)
    tree = ET.parse(sources)
    local = tree.getroot().find("LocalPluginSources")
    if local is None:
        local = ET.SubElement(tree.getroot(), "LocalPluginSources")
    plugin = ET.SubElement(local, "LocalPlugin")
    for tag, value in (
        ("Name", name),
        ("Folder", str(folder)),
        ("File", file),
        ("Enabled", "true"),
    ):
        ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


# --- processes -------------------------------------------------------------------


def _alive(pid_file: Path, marker: str) -> int | None:
    try:
        pid = int(pid_file.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if marker.encode() in cmdline else None


def _start(
    command: list[str], need_gib: float, cwd: Path, log: Path, pid_file: Path, env
) -> None:
    with open(log, "w") as out:
        process = subprocess.Popen(
            [str(PARALLEL / "run-test.sh"), str(need_gib), "--", *command],
            cwd=cwd,
            env={**os.environ, **env},
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
        )
    pid_file.write_text(str(process.pid))


def _stop(pid_file: Path, marker: str, timeout: float) -> None:
    """SIGTERM to the process group we started, SIGKILL after the timeout"""
    pid = _alive(pid_file, marker)
    if pid is None:
        pid_file.unlink(missing_ok=True)
        return
    os.killpg(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while True:
        try:
            os.killpg(pid, 0)
        except ProcessLookupError:
            break
        if time.monotonic() > deadline:
            os.killpg(pid, signal.SIGKILL)
            break
        time.sleep(1)
    pid_file.unlink(missing_ok=True)


# --- server ---------------------------------------------------------------------


def write_plugin_config(text: str | None = None) -> None:
    """The world's octahedron.cfg, or the given text, as the server's Octahedron.cfg"""
    SERVER_DATA.mkdir(parents=True, exist_ok=True)
    (SERVER_DATA / "Octahedron.cfg").write_text(
        text if text is not None else CONFIG_SOURCE.read_text(encoding="utf-8"),
        encoding="utf-8",
    )


def prepare_server(plugin: bool = True, bundle: Path | None = None) -> None:
    """The server's Magnetar config: the plugin compiled from this working copy as a dev
    folder, or, given a bundle made by package.sh, installed as a local plugin the way a
    cluster loads it"""
    if not (SERVER_CONFIG / "Sources").exists():
        SERVER_CONFIG.mkdir(parents=True, exist_ok=True)
        shutil.copytree(MAGNETAR / "Magnetar" / "Sources", SERVER_CONFIG / "Sources")
        shutil.copy(MAGNETAR / "Magnetar" / "config.xml", SERVER_CONFIG / "config.xml")
    sources = SERVER_CONFIG / "Sources" / "sources.xml"
    register_source(
        sources, "direct-transport", DIRECT_TRANSPORT_REPO, "DirectTransportServer.xml"
    )
    local = SERVER_CONFIG / "Local" / "Octahedron"
    shutil.rmtree(local, ignore_errors=True)
    if bundle:
        unregister_source(sources, "octahedron")
        shutil.copytree(bundle, local)
    else:
        register_source(sources, "octahedron", PLUGIN, "Octahedron.xml")
    (SERVER_CONFIG / "Profiles").mkdir(exist_ok=True)
    (SERVER_CONFIG / "Profiles" / "Current.xml").write_text(
        profile_xml(
            ["direct-transport"] + ([PLUGIN_ID] if plugin and not bundle else []),
            [PLUGIN_ID] if plugin and bundle else [],
        ),
        encoding="utf-8",
    )

    text = DS_CONFIG_TEMPLATE.read_text(encoding="utf-8")
    for tag, value in {
        "IP": "127.0.0.1",
        "ServerPort": SERVER_PORT,
        "SteamPort": SLOT["DS_STEAM_PORT_BASE"],
        "Administrators": f"<unsignedLong>{CLIENT_ID}</unsignedLong>",
        "ServerName": f"Octahedron Test {TASK}",
        "WorldName": "Octahedron",
        "PauseGameWhenEmpty": "false",
        "AutoRestartEnabled": "false",
        "IgnoreLastSession": "true",
        "RemoteApiEnabled": "false",
        "LoadWorld": WORLD,
    }.items():
        text, count = re.subn(
            rf"<{tag}>.*?</{tag}>|<{tag} />",
            f"<{tag}>{value}</{tag}>",
            text,
            flags=re.S,
        )
        assert count == 1, tag
    SERVER_DATA.mkdir(parents=True, exist_ok=True)
    (SERVER_DATA / "SpaceEngineers-Dedicated.cfg").write_text(text, encoding="utf-8")


def server_pid() -> int | None:
    return _alive(SERVER_PID, str(SERVER_CONFIG))


def start_server(timeout: float = 2400.0, env: dict | None = None) -> None:
    if server_pid():
        raise RuntimeError(f"The test server is already running, pid {server_pid()}")
    _start(
        [
            str(MAGNETAR / "MagnetarInterim.bin"),
            "-multiInstance", "-stableLogs", "-noimplicitmod",
            "-consent", "deny",
            "-config", str(SERVER_CONFIG),
            "-ds64", str(DS64),
            "-path", str(SERVER_DATA),
        ],
        SERVER_GIB, MAGNETAR, SERVER_LOG, SERVER_PID, {"SE_DIRECT_TRANSPORT": "1", **(env or {})},
    )  # fmt: skip
    deadline = time.monotonic() + timeout
    while "Game ready" not in SERVER_LOG.read_text(errors="replace"):
        if server_pid() is None:
            raise RuntimeError(f"The server exited, see {SERVER_LOG}")
        if time.monotonic() > deadline:
            raise TimeoutError(f"The server did not get ready, see {SERVER_LOG}")
        time.sleep(2)


def stop_server() -> None:
    """SIGTERM makes Magnetar save the world and quit"""
    _stop(SERVER_PID, str(SERVER_CONFIG), 180)


def server_game_log() -> str:
    logs = sorted(
        SERVER_DATA.glob("SpaceEngineersDedicated*.log"),
        key=lambda p: p.stat().st_mtime,
    )
    return logs[-1].read_text(errors="replace") if logs else ""


# --- client ---------------------------------------------------------------------


def prepare_client() -> None:
    if not LAUNCHER.exists():
        subprocess.run(
            [
                str(WORKSPACE / "notes/pulsar-dev-instances/new-pulsar-instance.sh"),
                str(PULSAR),
                str(PULSAR_TEMPLATE),
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
    legacy = PULSAR / "Legacy"
    (legacy / "Profiles" / "Current.xml").write_text(
        profile_xml(["remote", "direct-transport"]), encoding="utf-8"
    )
    sources = legacy / "Sources" / "sources.xml"
    register_source(sources, "remote", REMOTE_REPO, "Remote.xml")
    register_source(
        sources, "direct-transport", DIRECT_TRANSPORT_REPO, "DirectTransportClient.xml"
    )

    APPDATA.mkdir(parents=True, exist_ok=True)
    (APPDATA / "Remote.cfg").write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <ListenIP>127.0.0.1</ListenIP>
  <ListenPort>{CLIENT_PORT}</ListenPort>
  <AdminPassword>SpaceEngineers</AdminPassword>
</PluginConfig>
""",
        encoding="utf-8",
    )
    game_cfg = APPDATA / "SpaceEngineers.cfg"
    if not game_cfg.exists():
        shutil.copy(HOME / ".config/SpaceEngineers/SpaceEngineers.cfg", game_cfg)
    text = re.sub(
        r"(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value[^>]*>)\w+(</Value>)",
        r"\1True\2",
        game_cfg.read_text(encoding="utf-8"),
    )
    game_cfg.write_text(text, encoding="utf-8")


def client_pid() -> int | None:
    return _alive(CLIENT_PID, str(LAUNCHER))


def api() -> RemoteAPI:
    return RemoteAPI(
        f"http://127.0.0.1:{CLIENT_PORT}",
        username="admin",
        password="SpaceEngineers",
        timeout=90,
    )


def start_client(timeout: float = 2400.0, render: bool = False) -> RemoteAPI:
    """Starts the client, which joins the server, and returns its API once a
    character stands in the world. render: draw frames at 1280x720, for screenshots"""
    if client_pid():
        raise RuntimeError("The test client is already running")
    prepare_client()
    _start(
        [
            str(LAUNCHER),
            "-multiInstance", "-lazySteam", "-noprompt", "-noupdate", "-nosplash", "-stablelogs",
            "--no-steam", "--client-id", str(CLIENT_ID), "--client-name", CLIENT_NAME,
            "-appdata", str(APPDATA),
            "--quality", "minimal", "--no-audio", "--headless",
            *(["--resolution", "1280x720"] if render else ["--resolution", "640x480", "--no-render"]),
            "--connect", f"127.0.0.1:{SERVER_PORT}",
        ],
        RENDER_GIB if render else CLIENT_GIB, PULSAR, CLIENT_LOG, CLIENT_PID,
        {} if render else {"PULSAR_NO_RENDER": "true"},
    )  # fmt: skip
    remote = api()
    deadline = time.monotonic() + timeout
    while True:
        if not client_pid():
            raise RuntimeError(f"The client exited, see {CLIENT_LOG}")
        try:
            remote.ping()
            break
        except Exception:  # noqa: BLE001 -- not listening yet
            if time.monotonic() > deadline:
                raise
            time.sleep(3)
    wait_world(remote)
    ensure_character(remote)
    return remote


def wait_world(remote: RemoteAPI, timeout: float = 900.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(2)
        try:
            boxes = [
                s
                for s in remote.list_screens()
                if s.get("type") == "MyGuiScreenMessageBox"
            ]
            for box in boxes:
                remote.control_click(text="OK", screen=box["index"])
            if not boxes and remote.get_state().get("ready"):
                return
        except Exception:  # noqa: BLE001 -- the API times out while the world loads
            pass
    raise TimeoutError("The world did not become ready")


def ensure_character(remote: RemoteAPI, timeout: float = 300.0) -> dict:
    """Spawns at the first spawn point of the respawn screen until there is a live
    character"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            character = remote.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        try:
            screens = remote.list_screens()
            medical = next(
                (i for i, s in enumerate(screens) if "Medical" in s.get("type", "")),
                None,
            )
            if medical is not None:
                for button in ("Join", "Respawn"):
                    try:
                        remote.control_click(text=button, screen=medical)
                        break
                    except Exception:  # noqa: BLE001 -- the other page of the screen
                        continue
        except Exception:  # noqa: BLE001
            pass
        time.sleep(3)
    raise TimeoutError("No live character")


def stop_client() -> None:
    _stop(CLIENT_PID, str(LAUNCHER), 60)


def chat(remote: RemoteAPI, text: str, wait: float = 5.0) -> list[str]:
    """Sends a chat line and returns the lines that came back: chat messages and,
    for replies sent as scripted chat, new HUD chat lines"""
    before = remote.get_chat_messages()
    seen = {
        (h.get("sender"), h.get("text"), h.get("timestamp"))
        for h in before.get("hud", [])
    }
    remote.send_chat(text)
    time.sleep(wait)
    after = remote.get_chat_messages(after=before.get("latest", 0))
    lines = [
        m.get("Text", "") for m in after.get("messages", []) if m.get("Text") != text
    ]
    lines += [
        h.get("text", "")
        for h in after.get("hud", [])
        if (h.get("sender"), h.get("text"), h.get("timestamp")) not in seen
        and h.get("text") != text
    ]
    return lines


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_client()
        stop_server()
        prepare_world()
        write_plugin_config()
        prepare_server()
        start_server()
        start_client()
        print(f"Joined. Remote API on port {CLIENT_PORT}, server log {SERVER_LOG}")
    elif command == "chat":
        for line in chat(api(), sys.argv[2]):
            print(line)
    elif command == "stop":
        stop_client()
        stop_server()
    else:
        sys.exit(__doc__)
