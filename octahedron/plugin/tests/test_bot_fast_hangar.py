"""A completed bot ship is stored only when another job waits."""

import os
import sys

os.environ.setdefault("OCT_TASK", "bot-fast-hangar")

import rig  # noqa: E402
import test_client as tc  # noqa: E402
import test_projection_limits as pl  # noqa: E402
import test_shipyard_deadline as deadline  # noqa: E402
import test_shipyard_order as so  # noqa: E402
import test_support_arm as sa  # noqa: E402
import test_table_rules as tr  # noqa: E402


def main() -> int:
    queued = len(sys.argv) > 1 and sys.argv[1] == "queued"
    rig.stop_client()
    with tr.Second():
        rig.stop_client()
    rig.stop_server()
    so.prepare(sa.TABLES, 60, plugins=[tc.HANGAR])
    tr.both_admins()
    if queued:
        with tr.Second():
            mods = rig.APPDATA / "Mods"
            mods.mkdir(parents=True, exist_ok=True)
            for mod in set(pl.MODS) | set(rig.STATION_MODS):
                link = mods / f"{mod}.sbm"
                link.unlink(missing_ok=True)
                link.symlink_to(pl.WORKSHOP / str(mod))
    (rig.SERVER_DATA / "Hangar.cfg").write_text(deadline.HANGAR, encoding="utf-8")
    try:
        rig.start_server()
        remote = rig.start_client()
        rig.ensure_character(remote)
        other = None
        if queued:
            with tr.Second():
                other = rig.start_client()
                rig.ensure_character(other)
            sa.at_table(other, "Alpha")
        bots = f"{rig.CLIENT_ID}-{rig.CLIENT_ID + 1}"
        mark = sa.order(
            remote,
            "dock",
            ShipyardBotSteamIds=bots,
            ShipyardBotHangarDelaySeconds=1,
            ShipyardToolSpeed=0.2 if queued else 2,
        )
        assert mark is not None, "the build did not start"
        if not queued:
            ready = sa.wait_chat(remote, "Your ship 'ArmDock' is ready", 600, mark)
            assert ready is not None, "the ship was not delivered in the yard"
            assert "ArmDock" not in deadline.hangar(remote), "unqueued ship was stored"
            assert deadline.ship_here(remote, "ArmDock"), "ship missing from the yard"
            print("PASS: no waiting job leaves the ship in the yard", flush=True)
            return 0
        with tr.Second():
            tr.load(other, so.SMALL)
            tr.settled(other, "table", "paid")
            answer = tr.press(other)
            if "Your quote for" in answer:
                answer = tr.press(other)
            assert "is in the queue" in answer, answer
        tc.with_config(
            remote,
            FreeBuildPcuLimit=0,
            ShipyardLaborFeePer100Pcu=sa.LABOR_FEE,
            ShipyardTableMinutes=600,
            ShipyardTopUpMinutes=0,
            ShipyardRepasteShips="true",
            ShipyardBotSteamIds=bots,
            ShipyardBotHangarDelaySeconds=1,
            ShipyardToolSpeed=2,
        )
        stored = sa.wait_chat(remote, "it is in your Hangar now", 600, mark)
        assert stored is not None, "the finished bot ship was not stored"
        assert "ArmDock" in deadline.hangar(remote), "ship missing from Hangar"
        assert not deadline.ship_here(
            remote, "ArmDock"
        ), "stored ship is still in the world"
        loaded = deadline.hangar(remote, "load ArmDock")
        assert "Loaded " in loaded, loaded
        print(
            "PASS: waiting job causes the bot ship to be stored and loaded", flush=True
        )
    finally:
        with tr.Second():
            rig.stop_client()
        rig.stop_client()
        rig.stop_server()
    return 0


if __name__ == "__main__":
    sys.exit(main())
