"""Who leads OCT (SE-1015), with two admin players on one server, in a world built by
build_world.py (OCT_WORLD_SOURCE): OctLeaders names the second one, standing in for Viktor. The
first joins OCT as a plain member, although the game makes an admin who joins a faction of only
NPCs its leader. The second joins as OCT's leader, and the first stays a member.

    OCT_TASK=SE-1015 OCT_WORLD_SOURCE=<built world> uv run --no-project --with httpx python \\
        tests/test_oct_leader.py

Prints one line per check and a pass/fail summary; exit code 0 = all passed.
tests/oct_faction_cluster.py checks the same on a cluster.
"""

from __future__ import annotations

import os
import re
import sys

os.environ.setdefault("OCT_TASK", "SE-1015")
import rig  # noqa: E402
import station_tests as st  # noqa: E402
import test_table_rules as tr  # noqa: E402
from station_tests import check  # noqa: E402
from test_table_rules import Second  # noqa: E402

LEADER = rig.CLIENT_ID + 1


def main() -> int:
    rig.stop_client()
    with Second():
        rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    config, count = re.subn(
        r"<OctLeaders>[^<]*</OctLeaders>",
        f"<OctLeaders>{LEADER}</OctLeaders>",
        rig.CONFIG_SOURCE.read_text(encoding="utf-8"),
    )
    assert count == 1, "OctLeaders"
    rig.write_plugin_config(config)
    rig.prepare_server(extra_dev_folders=[st.PROBE])
    tr.both_admins()
    try:
        rig.start_server()
        a = rig.start_client()
        check(
            "ME OCT member" in st.probe(a, "whoami"),
            "the first player, an admin, joins OCT as a member",
        )
        with Second():
            b = rig.start_client()
            check(
                "ME OCT leader" in st.probe(b, "whoami"),
                "the player OctLeaders names joins OCT as its leader",
            )
        check(
            "ME OCT member" in st.probe(a, "whoami"),
            "the first player is still a member",
        )
    finally:
        with Second():
            rig.stop_client()
        rig.stop_client()
        rig.stop_server()
    joined = re.findall(r"New player \d+ joins OCT as (\w+)", rig.server_game_log())
    check(joined == ["member", "leader"], f"the server log: {joined}")
    return st.summary("OCT leader")


if __name__ == "__main__":
    sys.exit(main())
