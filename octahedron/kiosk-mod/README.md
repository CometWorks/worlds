# Octahedron Kiosk

The client mod of the Octahedron stations' kiosks: the touch screens on which
players store items in the station warehouses and share them with their
faction, and trade on the station's market. The Octahedron server plugin
(`../plugin`, "Station kiosks" and "Station market" in its README) owns the
ledgers and the market and does every operation; this mod draws the screens and
sends the player's requests to it.

It is built on [TouchScreenAPI](https://steamcommunity.com/sharedfiles/filedetails/?id=2668820525)
by Adriano Lima ([GitHub](https://github.com/adrianulima/TouchScreenAPI)),
which projects a cursor onto the LCD where the player looks. The world needs
both mods. `Data/Scripts/OctahedronKiosk/Lima` holds the API's client files,
copied unchanged from the TouchScreenAPI Workshop item as its author asks
consuming mods to do.

## What is in it

- `KioskTSS.cs`: the screen script `Octahedron_Kiosk`. The server plugin selects
  it on every kiosk (its `KioskScript` option), so the blueprints need no change.
- `KioskApp.cs`: the screens. Storage moves items between the docked ship you
  pick, or From hand your character, and your ledger or, with the Faction switch,
  the faction's. Transfer gives items to the faction and, with the trading rank,
  takes them; leaders also set whether every member may trade. Market shows
  OCT's prices and stock and the best offer and order of every item; Buy and Sell
  trade the chosen amount at once, Offer and Order list it at the chosen percent of
  the reference price, and My listings cancels them.
- `KioskSession.cs`: the mod messages to and from the plugin (channel 40683),
  and `/kiosk`, the same requests as a chat command for admins and tests.
- `KioskState.cs`: reads the plugin's answers. The format is the plugin's
  `KioskProtocol.cs`; change both together.

`/kiosk <state|deposit|withdraw|give|take|members|market|buy|sell|offer|order|cancel>
[faction] [hand|ship=ID] [item=Type/Subtype] [amount=N|all] [price=SC|N%]
[id=listing] [kiosk=ID]` works on the nearest kiosk within 20 m and prints the
answer into the chat, for example `/kiosk deposit item=Ingot/Iron amount=100` or
`/kiosk offer item=Ingot/Iron amount=50 price=90%`.

## Building and testing

The game compiles `Data/Scripts` when the mod loads. `KioskMod.csproj` is for
the IDE and compile checks against the game's mod assemblies:

```bash
dotnet build octahedron/kiosk-mod/KioskMod.csproj -p:Bin64=<game Bin64>
```

`octahedron/plugin/tests/test_kiosks.py` loads it as a world mod on a test
server and client and checks the requests end to end.

## Publishing

The mod has no Workshop item yet. Publishing it, and adding it and
TouchScreenAPI to the world's mods, waits for a person: upload the folder with
`Data`, `metadata.mod` and a thumbnail (not `bin`, `obj` or the project file).
