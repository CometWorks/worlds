"""Writes bp.sbc of a ship with a rotor: the cockpit's toolbar turns a battery on the rotor's head on and off
and its block group, so it needs one id map for both grids (SE-772). With "big" a dozen containers have names
that don't compress, so the order is over the 64 KiB of a cluster's plugin message but builds as fast (SE-805).

    python mech_blueprint.py bp.sbc [big]
"""

import base64
import random
import sys

import rig

HEAD_Y = 2.5 + 0.421
ID = 777000772000200
BASE, TOP, COCKPIT, BATTERY = ID, ID + 1, ID + 2, ID + 3
GROUP = "Head Batteries"

blocks = [
    rig.block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", (x, 0, z))
    for x in range(3)
    for z in range(6)
]
blocks.append(
    rig.block(
        "MyObjectBuilder_MotorStator",
        "LargeStator",
        (1, 1, 3),
        f"<EntityId>{ID + 4}</EntityId><TopBlockId>{ID + 5}</TopBlockId>",
    )
)
blocks.append(
    rig.block(
        "MyObjectBuilder_Cockpit",
        "LargeBlockCockpit",
        (1, 1, 0),
        f"<EntityId>{COCKPIT}</EntityId>"
        "<Toolbar><ToolbarType>Character</ToolbarType>"
        '<SelectedSlot xsi:nil="true" /><Slots>'
        '<Slot><Index>0</Index><Item /><Data xsi:type="MyObjectBuilder_ToolbarItemTerminalBlock">'
        f"<Action>OnOff</Action><BlockEntityId>{BATTERY}</BlockEntityId></Data></Slot>"
        '<Slot><Index>1</Index><Item /><Data xsi:type="MyObjectBuilder_ToolbarItemTerminalGroup">'
        f"<Action>OnOff</Action><BlockEntityId>{COCKPIT}</BlockEntityId><GroupName>{GROUP}</GroupName></Data></Slot>"
        "</Slots></Toolbar>",
    )
)
if sys.argv[2:] == ["big"]:
    noise = random.Random(805)
    blocks += [
        rig.block(
            "MyObjectBuilder_CargoContainer",
            "LargeBlockSmallContainer",
            (x, 1, z),
            f"<CustomName>{base64.b64encode(noise.randbytes(9000)).decode()}</CustomName>",
        )
        for x in (0, 2)
        for z in range(6)
    ]
base = rig.grid("Mech Ship", BASE, blocks, tag="CubeGrid")
head = rig.grid(
    "Mech Head",
    TOP,
    [
        rig.block(
            "MyObjectBuilder_MotorRotor",
            "LargeRotor",
            (0, 0, 0),
            f"<EntityId>{ID + 5}</EntityId>",
        ),
        rig.block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            (0, 1, 0),
            f"<EntityId>{BATTERY}</EntityId>",
        ),
    ],
    position=(2.5, HEAD_Y, 7.5),
    tag="CubeGrid",
).replace(
    "</CubeBlocks>",
    f"</CubeBlocks><BlockGroups><MyObjectBuilder_BlockGroup><Name>{GROUP}</Name>"
    "<Blocks><Vector3I><X>0</X><Y>1</Y><Z>0</Z></Vector3I></Blocks>"
    "</MyObjectBuilder_BlockGroup></BlockGroups>",
)
with open(sys.argv[1], "w", encoding="utf-8") as out:
    out.write(
        '<?xml version="1.0"?><Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Mech Ship" /><DisplayName>Tester</DisplayName>'
        f"<CubeGrids>{base}{head}</CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>"
    )
