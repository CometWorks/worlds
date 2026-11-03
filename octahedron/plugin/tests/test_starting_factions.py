"""Checks the starting factions of a world built by build_world.py (OCT_WORLD_SOURCE) on the
test server with the headless client: the client joins Sheep without anyone accepting it, and
after the save it is a member of SHP next to the NPC founder, and SHP keeps its logo. An admin who joins first becomes a
leader; with --not-admin the client is no admin and stays a plain member.

    OCT_WORLD_SOURCE=<built world> uv run --no-project --with httpx python \\
        tests/test_starting_factions.py [--not-admin]
"""

from __future__ import annotations

import re
import sys
import time
import xml.etree.ElementTree as ET

import rig


def main() -> None:
    admin = "--not-admin" not in sys.argv
    rig.stop_client()
    rig.stop_server()
    rig.prepare_world()
    rig.write_plugin_config()
    rig.prepare_server()
    if not admin:
        config = rig.SERVER_DATA / "SpaceEngineers-Dedicated.cfg"
        text = re.sub(
            r"<Administrators>.*?</Administrators>",
            "<Administrators />",
            config.read_text(encoding="utf-8"),
            flags=re.S,
        )
        config.write_text(text, encoding="utf-8")
    rig.start_server()
    remote = rig.start_client()

    tags = {f["tag"]: f for f in remote.get_faction()["factions"]}
    print("factions:", {t: f["autoAccept"] for t, f in tags.items()})
    assert tags["SHP"]["autoAccept"] and tags["WOL"]["autoAccept"], "auto accept"
    assert remote.get_faction()["tag"] is None, "starts without a faction"

    remote.join_faction("SHP")
    deadline = time.monotonic() + 30
    while remote.get_faction()["tag"] != "SHP":
        assert time.monotonic() < deadline, "not in SHP 30 s after the join request"
        time.sleep(1)
    me = remote.get_faction()["identityId"]
    print("joined SHP as", me)

    rig.stop_client()
    rig.stop_server()  # saves
    checkpoint = ET.parse(rig.WORLD / "Sandbox.sbc").getroot()
    sheep = next(
        f
        for f in checkpoint.iterfind("Factions/Factions/MyObjectBuilder_Faction")
        if f.findtext("Tag") == "SHP"
    )
    members = {
        int(m.findtext("PlayerId")): m.findtext("IsLeader") == "true"
        for m in sheep.iterfind("Members/MyObjectBuilder_FactionMember")
    }
    print("saved SHP members (identity: leader):", members)
    assert len(members) == 2 and me in members, "the NPC founder and the client"
    assert members[me] == admin, f"client leader {members[me]}, expected {admin}"
    print("saved SHP logo:", sheep.findtext("FactionIcon"))
    assert sheep.findtext("FactionIcon").endswith(
        "BuilderIcon_5.dds"
    ), "the logo stayed"
    print("PASS", "admin" if admin else "not admin")


if __name__ == "__main__":
    main()
