"""The mass a taxi pickup offer reports for a docked cab, on the test server, without a client.

The cab is a large grid with a piston and a wheel suspension, a battery and a cargo container, out in
space. tests/cab-mass-probe adds the piston head and the wheel, locks the cab's connectors to a small
static station and to a parked ship of heavy armor, and writes the masses the server has for the cab.
The offer's mass (Taxi.CabMass) must be the cab's grid with its piston head and wheel: not the main grid
alone, not the parked ship's, and not the rigid body's mass, which the station weld spoils. Then 5,000
kg of ice goes into the cab's cargo: the offer keeps its first measurement, as the cab's mass is cached.

    OCT_TASK=taxi-cab-mass uv run --no-project --with httpx python tests/test_taxi_cab_mass.py
"""

from __future__ import annotations

import os
import sys

os.environ.setdefault("OCT_TASK", "taxi-cab-mass")
import rig  # noqa: E402
from test_client import log  # noqa: E402

PROBE = (
    "cab-mass-probe",
    rig.PLUGIN / "tests" / "cab-mass-probe",
    "CabMassProbe.xml",
    "cab-mass-probe",
)
OUTPUT = rig.SERVER_DATA / "cab-mass-probe.txt"
AT = (250_000.0, 0.0, 0.0)
CELL = 2.5
POWER = "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"


def at(x: int, y: int, z: int) -> tuple[float, float, float]:
    return (AT[0] + CELL * x, AT[1] + CELL * y, AT[2] + CELL * z)


def connector(pos, forward: str) -> str:
    return rig.block(
        "MyObjectBuilder_ShipConnector",
        "Connector",
        pos,
        "<Enabled>true</Enabled>",
        forward=forward,
    )


def battery(pos) -> str:
    return rig.block(
        "MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", pos, POWER
    )


def world_grids() -> list[str]:
    cab = rig.grid(
        "Taxi Mass Cab",
        None,
        [
            connector((0, 0, 0), "Left"),  # to the station, at -X
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 1)),
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (0, 0, 2)),
            battery((0, 0, 3)),
            connector((0, 0, 4), "Backward"),  # to the parked ship, at +Z
            rig.block(
                "MyObjectBuilder_CargoContainer", "LargeBlockSmallContainer", (0, 1, 1)
            ),
            rig.block(
                "MyObjectBuilder_ExtendedPistonBase",
                "LargePistonBase",
                (0, 1, 2),
                "<Enabled>true</Enabled>",
            ),
            # The wheel: a suspension at the foot of an arm at -X, its wheel below it. A wheel must
            # clear every block of the cab, or the game doesn't place it
            rig.block(
                "MyObjectBuilder_MotorSuspension",
                "Suspension3x3",
                (0, -5, 2),
                "<Enabled>true</Enabled>",
                forward="Right",
                up="Down",
            ),
        ]
        + [
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (-1, -y, 2))
            for y in range(0, 6)
        ],
        position=AT,
    )
    station = rig.grid(
        "TM Station",
        None,
        [
            connector((0, 0, 0), "Right"),
            battery((-1, 0, 0)),
            rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (-2, 0, 0)),
        ],
        position=at(-1, 0, 0),
    ).replace("<IsStatic>false</IsStatic>", "<IsStatic>true</IsStatic>")
    parked = rig.grid(
        "TM Parked",
        None,
        [connector((0, 0, 0), "Forward"), battery((0, 0, 1))]
        + [
            rig.block(
                "MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", (0, 0, z)
            )
            for z in range(2, 6)
        ],
        position=at(0, 0, 5),
    )
    return [cab, station, parked]


def probe() -> dict[str, str]:
    """when + key -> value, e.g. "docked offer_mass" -> "12345.6" """
    if not OUTPUT.exists():
        return {}
    result = {}
    for line in OUTPUT.read_text(encoding="utf-8").splitlines():
        key, _, value = line.rpartition(" ")
        result[key] = value
    return result


def main() -> int:
    failures = []

    def check(ok: bool, what: str) -> None:
        log(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    rig.stop_server()
    rig.prepare_world(world_grids())
    rig.write_plugin_config()
    rig.prepare_server(extra_dev_folders=[PROBE])
    OUTPUT.unlink(missing_ok=True)
    rig.start_server()
    log("server ready")
    rig.wait_until(
        lambda: "done" in OUTPUT.read_text() if OUTPUT.exists() else False,
        300,
        "the probe's measurements",
    )
    text = OUTPUT.read_text(encoding="utf-8")
    log("probe:\n" + text)
    check("error" not in text, "the probe ran without an error")
    lines = text.splitlines()
    check(
        "docked connector Connected to TM Station" in lines
        and "docked connector Connected to TM Parked" in lines,
        "the cab's connectors are locked to the station and the parked ship",
    )
    check("docked tops 2 of 2" in lines, "the piston head and the wheel are attached")

    p = {k: float(v) for k, v in probe().items() if _number(v)}
    main_shape = p["docked main_shape"]
    subgrids = p["docked subgrid_shapes"]
    offer = p["docked offer_mass"]
    check(subgrids > 0, f"the piston head and the wheel weigh {subgrids:.1f} kg")
    check(
        abs(offer - (main_shape + subgrids)) < 0.01,
        f"offer {offer:.1f} kg = cab {main_shape:.1f} + head and wheel {subgrids:.1f}",
    )
    check(
        offer < main_shape + subgrids + p["docked parked_shape"] - 1,
        f"the parked ship's {p['docked parked_shape']:.1f} kg is not in the offer",
    )
    check(
        abs(p["docked physics_mass"] - offer) > 1,
        f"the rigid body's mass, {p['docked physics_mass']:.1f} kg, docked, is not the cab's",
    )
    added = p["after cargo main_shape"] - main_shape
    check(added > 100, f"the ice added {added:.1f} kg to the cab's shape mass")
    check(
        p["after cargo offer_mass"] == offer,
        "the offer keeps the cab's first measurement (cached)",
    )

    log("PASS" if not failures else f"FAIL {len(failures)}: {failures}")
    return 1 if failures else 0


def _number(text: str) -> bool:
    try:
        float(text)
        return True
    except ValueError:
        return False


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        rig.stop_server()
