"""A completed bot ship is stored promptly and can be loaded at its station."""

import os
import sys

os.environ.setdefault("OCT_TASK", "bot-fast-hangar")

import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_shipyard_deadline as deadline  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402


def main() -> int:
    rig.stop_client()
    rig.stop_server()
    so.prepare(sa.TABLES, 60, plugins=[tc.HANGAR])
    (rig.SERVER_DATA / "Hangar.cfg").write_text(deadline.HANGAR, encoding="utf-8")
    try:
        rig.start_server()
        remote = rig.start_client()
        rig.ensure_character(remote)
        mark = sa.order(
            remote,
            "dock",
            ShipyardBotSteamIds=str(rig.CLIENT_ID),
            ShipyardBotHangarDelaySeconds=1,
        )
        assert mark is not None, "the build did not start"
        stored = sa.wait_chat(remote, "it is in your Hangar now", 600, mark)
        assert stored is not None, "the finished bot ship was not stored"
        assert "ArmDock" in deadline.hangar(remote), "ship missing from Hangar"
        assert not deadline.ship_here(
            remote, "ArmDock"
        ), "stored ship is still in the world"
        loaded = deadline.hangar(remote, "load ArmDock")
        assert "Loaded " in loaded, loaded
        print("PASS: completed bot ship stored and loaded", flush=True)
    finally:
        rig.stop_client()
        rig.stop_server()
    return 0


if __name__ == "__main__":
    sys.exit(main())
