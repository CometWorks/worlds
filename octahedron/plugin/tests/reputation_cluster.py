"""OCT's reputation (SE-1283) on a local test cluster, through the cluster's scenario bench
(CometWorks/cluster Test/): the reputations live on the World Authority, the nodes send it their
changes, and the countdown and the removal from OCT run there.

- A new player has 10,000; !oct rep on a node asks the World Authority.
- A change made on one node (!oct rep add, the way a node's damage, deposit and share changes go)
  reaches the World Authority once, and the player hears that they are below zero.
- After a handover of the player's partition to the other node, that node sees the same
  reputation, and after a graceful stop and warm start of the cluster too.
- SE-1285, with the world's config/octahedron.cfg as it is but for the timers: the trader bot
  has no reputation, OCT's hull can't be ground, !claim (Essentials' !ess claim; the console
  probe sets the alias on every process as Quasar's agent does) refuses the trader bot's hull and
  takes another member's shared hull at no reputation, which is then free to grind. Grinding the
  trader bot's hull costs, and only the World Authority applies it.
- Below -10,000 the countdown (40 s here) starts with a warning, warns at 30 and 10 s left while
  the player is handed over to the other node, and removes them from OCT at its end. The client
  and its node see them out of OCT, and they hear why.

    OCT_TASK=SE-1283C OCT_WORLD_SOURCE=<world from build_world.py> \\
        <Test>/.venv/bin/python tests/reputation_cluster.py <Test>     (from plugin/)

The setup is tests/wa_economy_cluster.py's, see there for host.env: WA + 2 regular nodes + one
headless admin client, and Essentials (OCT_ESSENTIALS_REPO, default ~/ws/se/se1/plugins/essentials,
at the hub's commit) on the nodes.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

os.environ.setdefault("OCT_TASK", "SE-1283C")
import wa_economy_cluster as wa  # noqa: E402  (takes the bench's folder from argv)
from wa_economy_cluster import (  # noqa: E402
    CLIENT_ID,
    Client,
    ClusterError,
    _client_partition,
    _handover_url,
    _partitions,
    bench_module,
    rig,
    wait_until,
)

import station_tests as st  # noqa: E402
import test_reputation as tr  # noqa: E402

NODES = ("node-1", "node-2")
KICK_SECONDS = 40
CHANGE = re.compile(
    r"Reputation of [^(]*\((\d+)\): Admin (-?[\d.]+), ([-\d.]+) -> ([-\d.]+)"
)

wa.ROOT = Path.home() / f"cluster-runs/reputation-{os.environ['OCT_TASK']}"
wa.lc.PLUGINS = wa.ROOT / "plugins"
wa.lc.PLAN = wa.ROOT / "probe-plan.txt"
wa.lc.OUT = wa.ROOT / "probe-out.txt"


def run(bench) -> None:
    cluster = bench.cluster

    def check(label, passed, detail="") -> bool:
        return bench.check(label, bool(passed), str(detail)[:600])

    def log(process: str) -> str:
        return "".join(
            p.read_text(errors="replace")
            for p in (bench.run_root / f"{process}-data").rglob("*.log")
            if p.is_file()
        )

    def connect():
        wa.link_mods(
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
        return api

    def where() -> str | None:
        for node in NODES:
            try:
                if _client_partition(_partitions(bench, node)) is not None:
                    return node
            except (ClusterError, OSError):
                pass  # not up
        return None

    def to_other() -> str:
        """Hands the client's partition over to the other node; returns it"""
        source = wait_until(where, 120, "the client's partition")
        target = NODES[1 - NODES.index(source)]
        partition = _client_partition(_partitions(bench, source))
        bench.record(
            f"handover-{source}-{target}-{int(time.time())}",
            cluster.post(
                _handover_url(bench, partition, source=source, target=target),
                timeout=300,
            ),
        )
        wait_until(lambda: where() == target, 120, f"the client on {target}")
        return target

    def rep(api) -> float | None:
        """!oct rep's answer; None for a player out of OCT. A node just restarted or handed a
        partition may not reach the World Authority for a moment"""
        reply = ""
        for _ in range(10):
            reply = " ".join(rig.chat(api, "!oct rep", 15))
            if match := re.search(r"Your reputation in OCT: (-?[\d,]+)\.", reply):
                return float(match[1].replace(",", ""))
            if "Only OCT's members" in reply:
                return None
            print(f"!oct rep on {where()} -> {reply}", flush=True)
            time.sleep(5)
        raise AssertionError(f"no reputation: {reply}")

    def settled(api, before: float) -> float:
        deadline = time.monotonic() + 30
        while (now := rep(api)) == before and time.monotonic() < deadline:
            time.sleep(2)
        return now

    def heard(api, since: dict, text: str, timeout: float) -> list[str]:
        """The chat lines since since, once one has text"""
        try:
            wait_until(
                lambda: any(text in t for t in rig.new_chat_lines(api, since, "")),
                timeout,
                f"'{text}' in the chat",
            )
        except Exception as error:  # noqa: BLE001 -- the check says what is missing
            print(error, flush=True)
        return rig.new_chat_lines(api, since, "")

    def tag(api):
        return api.get_faction().get("tag")

    cluster.kill()
    cluster.prepare(wipe=True)
    processes = ["wa-1", *NODES]
    for process in processes:
        wa.link_mods(bench.run_root / f"{process}-data" / "Mods")
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)

    api = connect()
    me = api.get_faction()["identityId"]
    st.wait_until(lambda: tag(api) == "OCT", 60, 2)
    check("the player is in OCT", tag(api) == "OCT", tag(api))
    first = wait_until(where, 120, "the client's partition")
    check(f"a new player has 10,000 (asked on {first})", rep(api) == 10_000)

    since = api.get_chat_messages()
    rig.chat(api, "!oct rep add -15000", 10)
    now = settled(api, 10_000)
    check(f"a change on {first} reaches the World Authority", now == -5_000, now)
    # The World Authority's log reaches the file a few seconds late
    wait_until(lambda: CHANGE.search(log("wa-1")), 60, "the change in the WA's log")
    time.sleep(5)
    changes = [(p, *m) for p in processes for m in CHANGE.findall(log(p))]
    check(
        "the World Authority applied it once, no node did",
        len(changes) == 1 and changes[0][0] == "wa-1",
        changes,
    )
    told = heard(api, since, "below zero", 30)
    check(
        "the player hears they are below zero",
        any("below zero" in t for t in told),
        told,
    )

    second = to_other()
    check(f"after a handover to {second}: the same reputation", rep(api) == -5_000)

    check("graceful stop", cluster.stop(grace=240))
    check(
        "the reputation is in the World Authority's global save",
        "Octahedron.Reputation" in wa.global_save(bench.run_root / "wa-1-data"),
    )
    cluster.start(processes)
    cluster.wait_ready(nodes=processes)
    bench.kill_client(CLIENT_ID)
    api = connect()
    check("the same player after the warm start", api.get_faction()["identityId"] == me)
    check("after the warm start: the same reputation", rep(api) == -5_000)

    # SE-1285: the three features together, with the world's config
    def logged(identity) -> list:
        return re.findall(
            rf"Reputation of [^(]*\({identity}\): (\w+) (-?[\d.]+)", log("wa-1")
        )

    for name, passed, detail in tr.integration(api, rep, logged):
        check(name, passed, detail)
    before, after = tr.grind(api, "Trader")
    now = settled(api, -5_000)
    check(
        "grinding the trader bot's hull costs",
        after < before and now < -5_000,
        f"{before:.0f} -> {after:.0f} integrity, {now:.0f}",
    )
    wait_until(
        lambda: ("Damage" in {k for k, _ in logged(me)}),
        60,
        "the damage in the WA's log",
    )
    time.sleep(5)
    damage = [
        (p, k, a)
        for p in processes
        for k, a in re.findall(
            rf"Reputation of [^(]*\({me}\): (\w+) (-?[\d.]+)", log(p)
        )
        if k == "Damage"
    ]
    check(
        "the World Authority applied the grinding, no node did",
        damage
        and all(p == "wa-1" for p, _, _ in damage)
        and abs(sum(float(a) for *_, a in damage) - (now + 5_000)) < 2,
        damage,
    )
    base = now

    since = api.get_chat_messages()
    started = time.monotonic()
    rig.chat(api, "!oct rep add -6000", 10)
    check("below -10,000 after the change", settled(api, base) < -10_000)
    told = heard(api, since, "If it stays there", 30)
    check(
        "below -10,000 the countdown starts with a warning",
        any("If it stays there for 40 seconds" in t for t in told),
        told,
    )
    third = to_other()
    told = heard(api, since, "You were removed from OCT", KICK_SECONDS + 60)
    elapsed = time.monotonic() - started
    expected = [
        "If it stays there for 40 seconds",
        "30 seconds left",
        "10 seconds left",
        "You were removed from OCT",
    ]
    order = [next((i for i, t in enumerate(told) if e in t), -1) for e in expected]
    check(
        f"the warnings reach the player on {third} too, then the removal and why",
        all(i >= 0 for i in order) and order == sorted(order),
        f"{order}: {told}",
    )
    check(
        "removed at the end of the countdown, not before",
        KICK_SECONDS - 5 <= elapsed <= KICK_SECONDS + 60,
        f"{elapsed:.0f} s",
    )
    st.wait_until(lambda: tag(api) is None, 60, 2)
    check("the client sees the player out of OCT", tag(api) is None, tag(api))
    check(f"{third} has the player out of OCT", rep(api) is None)
    wait_until(
        lambda: "is removed from OCT" in log("wa-1"), 60, "the removal in the WA's log"
    )
    removed = re.findall(rf"Reputation: .* \({me}\) is removed from OCT", log("wa-1"))
    check("the World Authority removed them once", len(removed) == 1, removed)

    check("graceful stop", cluster.stop(grace=240))
    for process in processes:
        check(
            f"{process}: no Octahedron failure logged",
            not re.search(r"\[Octahedron\].*(failed|Failed)", log(process)),
            re.findall(r".*\[Octahedron\].*[Ff]ailed.*", log(process))[:3],
        )


run.administrators = [CLIENT_ID]


def build_essentials() -> Path:
    """Essentials as a local plugin (DLL + manifest) for CLUSTER_EXTRA_PLUGINS"""
    repo = tr.ESSENTIALS[1]
    out = wa.lc.PLUGINS / ".build" / "Essentials"
    subprocess.run(
        ["nice", "-n", "19", "dotnet", "build", str(repo / "ServerPlugin/ServerPlugin.csproj"),
         "-c", "Release", "--nologo", "-v", "q", f"-p:Magnetar={os.environ['MAGNETAR_HOME']}",
         "-p:MagnetarData=", "-o", str(out)],
        check=True,
    )  # fmt: skip
    commit = subprocess.check_output(
        ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
    ).strip()
    folder = wa.lc.PLUGINS / "Essentials"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "Essentials.dll").write_bytes((out / "Essentials.dll").read_bytes())
    (folder / "Essentials.xml").write_text(
        '<?xml version="1.0"?>\n<PluginData xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="GitHubPlugin">'
        "<Id>DF09925B-6D53-41F2-899C-CA6214B5DD6F</Id><FriendlyName>Essentials</FriendlyName>"
        f"<RepoId>CometWorks/essentials</RepoId><Commit>{commit}</Commit>"
        "<Runtimes>CoreCLR</Runtimes></PluginData>\n",
        encoding="utf-8",
    )
    return folder


def main() -> int:
    # A host whose Magnetar never loaded Multigrid Projector gets its build from elsewhere
    wa.MGP_CACHE = Path(os.environ.get("OCT_MGP_CACHE", wa.MGP_CACHE))
    run.world_template = wa.prepare()
    # The world's config as it is, but for the countdown
    tr.check_shipped_config()
    wa.set_config(
        ReputationKickAfterSeconds=KICK_SECONDS, ReputationWarnSeconds="30,10"
    )
    # SE-1285: the hulls, their owners in OCT, Essentials and the !claim alias
    sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text = text.replace(
        "</SectorObjects>",
        "".join(tr.hull(n) for n in ("Trader", "Shared", "OCT")) + "</SectorObjects>",
        1,
    )
    sector.write_text(text, encoding="utf-8")
    tr.add_members(rig.WORLD)
    essentials = build_essentials()
    os.environ["CLUSTER_EXTRA_PLUGINS"] = ",".join(
        [
            os.environ["CLUSTER_EXTRA_PLUGINS"].replace("wa:", ""),
            f"node:{essentials}",
        ]
    )
    os.environ.update(tr.ALIASES)
    label = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%SZ-reputation")
    results = bench_module.HOST.results / label
    results.mkdir(parents=True, exist_ok=True)
    outcome = bench_module.run_scenario(
        run,
        "P2-OCT-REPUTATION",
        "SE-1283: OCT's reputation on a cluster",
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
