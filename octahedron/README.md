# Octahedron

The world of the first public test cluster. EarthLike and its Moon from the
vanilla Star System scenario, 7 stations, Trade Lanes between them and an
asteroid ring. The design and the reasons behind it are in the workspace note
`se1/notes/public-test-cluster/NOTE.md`; the work is ticket SE1-0114.

| File | What it is |
|---|---|
| `build_world.py` | builds the world |
| `make_ore_mod.py` | builds the OctahedronOres mod for a seed |
| `ore_stats.py` | writes the ore statistics of a world and its mod |
| `config/` | inputs of the world: MES mod list and settings, Relative Top Speed config; the Octahedron server plugin's config (`octahedron.cfg`) |
| `plugin/` | the Octahedron server plugin, which servers load from this folder (see its `README.md`) |
| `server/` | the server's world, seed 42 |
| `ORES.md` | ore statistics of `server/` |
| `MES.md` | the MES settings and what spawns where |
| `DEPLOY.md` | how to deploy the cluster with Quasar |

## One seed per world

A world and its ore mod are built with the same seed. The seed decides
everything random: entity ids, the asteroids' places, sizes, rotations and
seeds, and where the ore deposits sit on EarthLike and the Moon. The layout
(planets, stations, lanes, the ring's shape) and the rules (settings, mods,
which ore goes where) are the same for every seed, and the same seed always
gives the same files.

The asteroids take the first entity ids, and each one is placed by its own
generator, seeded from the world seed and its name. Adding, moving or removing
stations and other grids leaves their ids, places and shapes as they are. A new
lane moves only the asteroids within 1 km of it, and the few placed after them
that would then overlap.

The server runs seed 42. The world players get to design ships and stations
offline is built with another seed and its own ore mod, so the ores they find
there don't lead them to the server's.

```bash
uv run make_ore_mod.py --seed 42
python3 build_world.py server --seed 42 [--ore-mod <Workshop id>]
uv run ore_stats.py server OctahedronOres ORES.md --seed 42
```

`build_world.py` needs only the Python standard library. `make_ore_mod.py` and
`ore_stats.py` declare their packages (numpy, pillow, scipy) for `uv run`. All
three read the game's content and the station blueprint from the Steam folders
on Linux (`~/.steam/steam/steamapps`), so Steam must have the game and the
blueprint ([3816368704](https://steamcommunity.com/sharedfiles/filedetails/?id=3816368704))
downloaded.

## The world

`build_world.py` starts from a copy of the Star System scenario:

- Removes every planet but EarthLike and the Moon.
- Applies the world settings and the mod list (`SETTINGS`, `BLOCK_LIMITS` and
  `MODS` in the script, MES and its encounter mods from `config/mes-mods.xml`).
- Places the 6 stations of the octahedron 80 km above EarthLike, over the poles
  and on the equator, and one 15 km from the Moon's center, just outside its
  gravity (12.3 km), between the directions to Delta and Alpha Station. The 6
  Earth stations are placed with EarthLike's north as up, then each is tilted together with its shipyard about its safe
  zone's center, so its deck lies parallel to the asteroid ring (up along the
  ring's axis, 25 degrees from north). Otherwise the sun shines sideways
  through the deck all day: bad shadows and unreadable LCDs. The lanes don't
  tilt. Moon Station's up is square to the line between EarthLike and the
  Moon, the one such direction nearest to Delta Station, and it faces away
  from the Moon. Each station sits in a 250 m admin safe zone, owned by the
  "Octahedron Authority" NPC faction, and players can't damage or edit it. The
  zone allows only drilling and landing gear locks: no welding, grinding or
  block placing, also not by the shipyard's welders until the Octahedron plugin
  makes an exception for them (SE1-0204). The stations start empty, apart from
  10,000 kg of uranium ingots in each large reactor, a full safe zone block (75
  zone chips) and the gas in the tanks. The station's programmable block is
  emptied and off: the blueprint's Automatic LCDs 2 script isn't needed
  (SE1-0121).
- Places a shipyard next to every station (`blueprints/omni-arc-beam-shipyard.sbc`,
  24 OmniArc beam welders around an open frame), in view of the station's
  lobby, its pressurized back half: straight behind the equatorial stations,
  away from EarthLike, 45 degrees between behind and away from EarthLike at
  the pole stations, and on the Moon side of Moon Station. The lanes leave the
  stations the other way and still start from the zone's center.
  - The shipyard's side faces the lobby, rolled 30 degrees about its long axis
    with its top toward the station, lifted 50 m along the station's up and
    turned 17.5 degrees about it, so more of the build volume shows past its
    near wall and the wall's middle beam doesn't hide its center.
  - The station and its shipyard are centered on the safe zone together and
    spread apart as far as their block bounds stay 10 m inside the zone's edge:
    100 m between them (less at the pole stations), the farthest block 227 m
    from the zone's center.
  - Its blocks are shared with everyone but its 4 large reactors and 72
    batteries. Those start with 10 uranium ingots each and full charge, and on
    the server the Octahedron plugin tops them back up every 10 minutes
    (`config/octahedron.cfg`). Only 10 ingots, since players get at the reactors
    while renting the shipyard.
  - Each station has its own color (`STATION_COLORS`): the shipyard's red frame
    gets a skin and color, the station's purple walls and furniture only the
    color, at 70 % brightness. North red, South blue, Beta green and Delta
    orange painted metal; Alpha black and Gamma cyan carbon fiber; Moon purple
    concrete.
- Makes the stations the NPC faction's trade stations: the faction is a Trader
  with a bank account, and each station grid has a station entry, so the
  economy stocks its store and contract blocks. The game's own trade factions
  and their procedural stations are off (`GenerateFactionsOnStart` false).
  The entries name no safe zone (`SafeZoneEntityId` 0); with one, the economy
  would delete the station grid when no player is near. That leaves the
  Services Terminal's repair and salvage without grids to list.
- Places 6 Trade Lanes: one along each edge of the equatorial square
  (Alpha-Beta-Gamma-Delta), and from Moon Station to Delta and to North
  Station. South Station has no lane. The North-Moon lane passes 138 km from
  EarthLike's center, clear of its gravity, and 15 km from the Moon's; no two
  lanes come closer than 1.4 km, and only near a station they share. A lane
  goes both ways: each end is an entry toward the other. Every end is a
  one-block grid with a Trade Lane Computer, 1000 m from its station (1500 m at
  Delta and North Station for the Moon lanes, clear of their other lane ends),
  indestructible and not editable. The ends have no safe zone; the lane's rings
  have no physics. The two rings of a tunnel sit side by side, level with
  EarthLike's equator, but one above the other on the North-Moon lane, so
  pilots can tell the Moon's two lanes apart.
- Places 2,000 asteroids in a flat band 45 to 75 km above EarthLike (105 to
  135 km from its center) and 1 km thick, evenly over its area, and 4 ice
  asteroids 120 km out, halfway between the equatorial stations. The band stays
  out of EarthLike's gravity (it ends about 103 km from the center), clear of
  Alpha and Gamma Station at 140 km, and every asteroid stays 1 km from the
  Trade Lanes. Its plane is perpendicular to EarthLike's magnetic axis as the
  Aurora plugin (se-aurora) draws it when the sun is where the world starts it:
  the north pole tilted 25 degrees (the plugin's default `MagneticAxisTilt`)
  away from the sun. So the band is tilted against the equator: its nearest
  asteroids are 6 to 9 km from Alpha and Gamma Station, but about 58 km from
  Beta and Delta. The plugin's axis follows the sun as it moves, the band doesn't.
  The ring's asteroids are dealt out evenly to 24 sectors of 15 degrees,
  counted from Alpha Station's direction, 83 or 84 in each; `ORES.md` counts
  them per sector.
- The asteroids are the game's own procedural ones, saved as seed and size only
  (a `.vx2` of about 100 bytes each), so the game generates their shape and
  ores. The ice ones are the largest plain vanilla asteroid shape
  (`SuperRock_001`, 1024 m) filled with ice. The game's own procedural
  asteroids are off (`ProceduralDensity` = 0).
- Writes the mod configs into the world's `Storage` folder: Relative Top Speed
  (`config/relative-top-speed.xml`) and MES (`config/storage`: no lunar cargo
  ships, and no cleanup, so NPC grids stay; see `MES.md`).

`server/` is the script's output as is, never loaded in a game: the first load
converts it to the binary format and stocks the stores. It
lists OctahedronOres as a local mod for now. Once the mod is on the Workshop,
rebuild it with `--ore-mod <id>`; neither a server nor a Workshop world can use
a local mod. The `thumb.jpg` is still the Star System scenario's.

## OctahedronOres

`make_ore_mod.py` writes the ore mod into `OctahedronOres/` (ignored here, mods
live in their own repositories). It copies the game's own definitions and ore
maps and changes only these:

- Moon: no gold, silver or platinum. The ore map values of its gold, which
  cover about as many pixels as its magnesium, become uranium 50 to 100 m deep.
- Asteroids and meteorites: no gold, silver or uranium.
- Platinum yields 3 times and uranium 5 times the ore per mined voxel
  (`MinedOreRatio` 3 and 1.5). Platinum is only in asteroids and uranium only
  on the Moon, so this triples the belt's platinum and quintuples the Moon's
  uranium.
- EarthLike keeps its ore rules; it already has gold and silver and no uranium
  or platinum.
- Not about ores: the `OmniArcTools` block tag (`Data/BlockTags_Octahedron.sbc`),
  the six OmniArc tool blocks. The world limits blocks by tag (`LimitBlocksBy`
  Tag) and allows 4 of the tag per player, whatever the mix of welders, drills,
  deconstructors and rings. The limit counts built blocks, turned on or off.
- EarthLike's and the Moon's ore maps (the blue channel of their `*_mat.png`)
  are redrawn from the seed: every deposit moves to a random free spot on the
  same cube face, with its shape and ores. Terrain and biomes stay as they are.
  The mod carries both planets' definitions, unchanged for EarthLike, because
  the game reads a planet's maps from the mod that defines it.

The mod is about 14 MB, nearly all of it EarthLike's ore maps. Rerun it after a
game update that changes the planet or voxel material definitions or maps.

## Notes

- `WorldSizeKm` is 0. The vanilla world edge is a cube that deletes whatever
  crosses it, and while it is set, anything that drops out of a Havok physics
  cluster's broadphase is killed or deleted. In the asteroid ring that happened
  to characters on short teleports. The 500 km world radius, braking from
  450 km, is the Octahedron plugin's world edge (`config/octahedron.cfg`).
- In-game scripts are on with the Scripter role: only Scripters and above can
  edit a programmable block's code, and grids pasted or projected by anyone
  else lose their scripts. Anyone can still run the scripts already on a block.
- Progression (research) is off: every block is available from the start.
- The Services Terminal's ship storage is off (`GridStorageMaxPerPlayer` 0). On
  the cluster a stored ship stays in the world folder of the node that stored
  it, no other node can return it, and it is lost when that node restarts
  (SE1-0126). The Hangar plugin parks ships instead.
- PCU limits are per player, 1,000,000 each. The game gives every player
  `TotalPCU / MaxPlayers` and a dedicated server admits at most `MaxPlayers`
  players, so the world sets `MaxPlayers` 100 (`MAX_PLAYERS`) and `TotalPCU`
  100,000,000. The scenario's `MaxPlayers` 4 would have meant 250,000 PCU each and
  4 players. On a cluster each node counts the same, and the cluster plugin's PCU
  ledger adds up a player's PCU over the nodes; grids in Hangar are not in the
  world, so they don't count. On the server the Octahedron plugin also checks
  the limits when a projected block is welded, which ToolCore's tools (OmniArc)
  don't.
- Radiation is on (planets, weather, blocks); solar radiation (`SolarRadiationIntensity`)
  and hunger (`FoodConsumptionRate`) stay at 0.
- Every economy tick the game refills each trade station: fuel, batteries,
  gas, ice in the gas generators, and loot (credits included) in every empty
  cargo container, which it then hides. On the server the Octahedron plugin
  replaces that with `config/octahedron.cfg`: loot only into the one
  faction-only "Station Loot" cargo terminal per station, and no ice. A world
  without the plugin, like the design world, has the game's refill. The
  server reads the file as `Octahedron.cfg` from its instance folder; the
  options it leaves out keep their defaults.
- Every station has 4 "Free Tools" cargo terminals (the blueprint's hidden
  toolboxes by its "Free tools!" signs, renamed and shown in inventory screens)
  shared with all players. On the server, the Octahedron plugin's
  `RefillContainers` rule in `config/octahedron.cfg` tops each up every minute
  to 3 of each Elite tool (welder, grinder, drill), 3 full oxygen and 3 full
  hydrogen bottles, and 3 suit battery packs (Powerkit). "Station Loot" is the blueprint's tools,
  bottles and ammo container.
- The asteroid ore settings are vanilla's (DepositsCountCoefficient 2,
  DepositSizeDenominator 30, HarvestRatioMultiplier 1), not the Star System
  scenario's sparser 1, 60 and 0.8. See `ORES.md`.
