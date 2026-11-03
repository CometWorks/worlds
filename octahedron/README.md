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
| `check_sun_clearance.py` | sweeps the Sun over a day: can it strobe on a station? |
| `config/` | inputs of the world: MES mod list and settings, Relative Top Speed config; the Octahedron server plugin's config (`octahedron.cfg`); the Hangar plugin's options (`hangar.cfg`); the Performance plugin's options that differ from its defaults (`performance.cfg`) |
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
three read the game's content from the Steam folder on Linux
(`~/.steam/steam/steamapps`), so Steam must have the game downloaded. The
station and shipyard blueprints are in `blueprints/`.

## The world

`build_world.py` starts from a copy of the Star System scenario:

- Removes every planet but EarthLike and the Moon.
- Applies the world settings and the mod list (`SETTINGS`, `BLOCK_LIMITS` and
  `MODS` in the script, MES and its encounter mods from `config/mes-mods.xml`).
- Places the 6 stations of the octahedron 80 km above EarthLike, over the poles
  and on the equator, and one 15 km from the Moon's center, just outside its
  gravity (12.3 km). The octahedron is turned about the poles (by 36.8 degrees
  from EarthLike's own forward and right) so that the outward normal of its
  North, Delta, Alpha face points as near to the Moon as it can: the Moon lies
  over the bisector of Delta and Alpha Station, 12 degrees from that normal.
  Moon Station stays where it was before the turn, between the directions to
  where Delta and Alpha Station were then. The 6
  Earth stations are placed with EarthLike's north as up and their front toward
  its axis, each with its shipyard. The 4 equatorial ones are then turned
  together with their shipyards about the safe zone's center, so the deck lies
  parallel to the asteroid ring (up along the ring's axis, its north side, 25
  degrees from north) and the front faces EarthLike in the ring's plane, with
  the shipyard behind, out in space. Otherwise the sun shines sideways through
  the deck all day: bad shadows and unreadable LCDs. North and South Station
  stay level with the equator, both with their up toward EarthLike: North
  Station is upside down, so its lobby looks down on the ring. The
  lanes don't turn. Moon Station's up is square to the line between EarthLike and the
  Moon, the one such direction nearest to where Delta Station was before the
  turn. Its shipyard is placed as if it faced away from the Moon, then the
  station alone is turned 180 degrees about its up: it faces the Moon, its
  lobby toward EarthLike. Delta Station, whose up leaned 25 degrees away from
  EarthLike, is turned 50 degrees further with its shipyard, so its up leans
  25 degrees toward it, like Beta Station's: the lobby sees the planet, and the
  deck is still tilted against the Sun. Each station sits in a 250 m admin safe zone, owned by the
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
  away from EarthLike, and on the Moon side of Moon Station. The lanes leave
  the stations the other way and still start from the zone's center.
  - The shipyard's side faces the lobby, rolled 30 degrees about its long axis
    with its top toward the station, lifted 50 m along the station's up and
    turned 17.5 degrees about it, so more of the build volume shows past its
    near wall and the wall's middle beam doesn't hide its center. Gamma
    Station's shipyard is turned the other way (`SHIPYARD_YAW_REVERSED`): its
    turn for the Sun, below, went against the 17.5 degrees and put the beam
    back in front of the center.
  - The station and its shipyard are centered on the safe zone together and
    spread apart as far as their block bounds stay 10 m inside the zone's edge:
    100 m between them, the farthest block 227 m from the zone's center.
  - Then the shipyard is raised (`SHIPYARD_RAISE`) along its own up, the side
    with its cargo containers: 30 m at the equatorial stations; Moon Station's
    125 m along the station's up. Each is then pulled straight toward its
    station by as little as keeps its block bounds 10 m inside the zone's edge
    again. Their farthest blocks end up 211 to 221 m from the zone's center,
    and 92 m or more from the station's blocks.
  - The pole stations' shipyards follow Viktor's placement at South Station in
    game (`POLE_SHIPYARD`, in the station's own axes): off the side of the
    lobby with the ship build holo table, above its floor and on EarthLike's
    side, turned so the windows look down into the middle of the shipyard.
    North Station, upside down, sits like South Station, so its shipyard takes
    the same place. Along that direction the pair is centered on the zone and
    spread apart as far as it fits 10 m inside the edge: 257 m between their
    centers, the farthest block 224 m from the zone's center, the shipyard's
    nearest block 140 m from the station's.
  - Its blocks are shared with everyone but its 4 large reactors and 72
    batteries. Those start with 10 uranium ingots each and full charge, and on
    the server the Octahedron plugin tops them back up every 10 minutes
    (`config/octahedron.cfg`). Only 10 ingots, since players get at the reactors
    while renting the shipyard.
  - Its connectors and welders start off (`SHIPYARD_OFF`): the plugin turns them
    on for a job. The reactors, batteries and refill stations stay on.
  - Each station has its own color (`STATION_COLORS`): the shipyard's red frame
    gets a skin and color, the station's purple walls and furniture only the
    color, at 70 % brightness. North red, South blue, Beta green and Delta
    orange painted metal; Alpha black and Gamma cyan carbon fiber; Moon purple
    concrete.
  - The Sun never goes behind a station's own shipyard: see "The Sun" below.
    For that the line between Alpha, Gamma, North and South Station and their
    shipyards is turned 33, 33, 12 and 12 degrees about the zone's center
    (`SHIPYARD_SUN_TURN`), both keeping their orientation, and Moon Station and
    its shipyard turn 22 degrees as one unit (`MOON_SUN_TURN_DEGREES`).
- Makes the stations the NPC faction's trade stations: the faction is a Trader
  with a bank account, and each station grid has a station entry, so the
  economy stocks its store and contract blocks. The game's own trade factions
  and their procedural stations are off (`GenerateFactionsOnStart` false,
  `TradeFactionsCount` 0), and MES spawns no economy outposts or Trade Operators
  Coalition stations (`NpcSpawnGroupBlacklist` in `config/storage`, `MES.md`).
  The entries name no safe zone (`SafeZoneEntityId` 0); with one, the economy
  would delete the station grid when no player is near. That leaves the
  Services Terminal's repair and salvage without grids to list.
- Places 11 Trade Lanes: one along each edge of the equatorial square
  (Alpha-Beta-Gamma-Delta), from Beta and Delta Station to both North and South
  Station, and from Moon Station to the three stations of the face toward the
  Moon: Delta, North and Alpha. The Moon lanes pass 129 km (Delta), 138 km
  (North) and 130 km (Alpha) from EarthLike's center, clear of its gravity, and
  15 km from the Moon's; the lanes between two EarthLike stations pass 99 km
  from its center. No two lanes come closer than 0.8 km, and only near a
  station they share; there the closest gates of two lanes, placed the way
  Trade Lanes MP places them, are 654 m apart (Alpha-Delta and Delta-North). A
  lane goes both ways: each end is an entry toward the other. The lanes of the
  equatorial square run 200 m north of the stations' plane, on the side the
  lobbies look at, and the ends of North Station's Moon lane are moved too, 150 m
  south at North and 160 m north at the Moon, to keep their gates out of the
  Sun's way (see "The Sun" below). Every end is a
  one-block grid with a Trade Lane Computer, 1000 m from its station (1500 m at
  the Earth stations for the Moon lanes, clear of their other lane ends),
  indestructible and not editable. Its Custom Data ends with a Target GPS at
  the other end's computer, so a computer the cluster loads before its partner
  still knows where its lane leads. `GpsMarkers:false` in it keeps the lanes
  off the players' GPS lists ([DF-716](https://tasks.cometworks.se/tasks/1530);
  needs Trade Lanes MP with that switch, older versions ignore the line). The
  ends have no safe zone; the lane's
  rings have no physics. The two rings of a tunnel sit side by side, level with
  the equator, on the lanes of the equatorial square and from Delta and Alpha
  Station to the Moon, and one above the other on the others (to the poles and
  from North Station to the Moon). Each ring of a tunnel goes one way.
  Around the equatorial square and around the polar ring (Beta, North, Delta,
  South), the inner track runs Alpha, Beta, Gamma, Delta and Beta, North, Delta,
  South, the outer one the other way, so a ship going around stays on its track
  at every station. A Moon lane follows the circle of its Earth station: the
  equator's at Delta and Alpha, the polar ring's at North. A ship that turns off
  toward the Moon keeps its side: Delta to Moon to Alpha takes the same track
  as Delta to Alpha, and North to Moon the same as North to Delta.
- Places 2,000 asteroids in a flat band 45 to 75 km above EarthLike (105 to
  135 km from its center) and 1 km thick, evenly over its area, and 4 ice
  asteroids 120 km out, halfway between the equatorial stations. The band stays
  out of EarthLike's gravity (it ends about 103 km from the center), clear of
  Alpha and Gamma Station at 140 km, and every asteroid stays 1 km from where
  the Trade Lanes would run without the Sun's shift, at least 0.8 km from the
  lanes themselves. Its axis is EarthLike's magnetic axis as the Aurora plugin
  (se-aurora) draws it when the sun is where the world starts it, the north
  pole tilted 25 degrees (the plugin's default `MagneticAxisTilt`) away from
  the sun, then turned about the poles together with the octahedron. So the
  band keeps its place among the stations, tilted against the equator: its
  nearest asteroids are 6 to 9 km from Alpha and Gamma Station, but about
  58 km from Beta and Delta. It no longer lines up with the plugin's axis at
  the start, which is 36.8 degrees around from it; that axis follows the sun as
  it moves anyway, the band doesn't.
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

## The Sun

A fast flicker of light is a health risk for photosensitive players. It happens
when the Sun passes behind something with gaps, as seen from a station: a Trade
Lanes gate or the station's own shipyard
([SE-809](https://tasks.cometworks.se/tasks/1691),
[SE-720](https://tasks.cometworks.se/tasks/1597)).

The Sun circles about the world's Y axis, EarthLike's north (`sun_axis`), with
its rays always level. So over a day the rays to a station stay inside a flat
slab through it, and what stays out of that slab can't shade it at any time.
The lanes are moved by as little as keeps every gate's bounding sphere out of the
slab of every station's bounding sphere (`EQUATOR_LANE_NORTH`,
`NORTH_MOON_LANE_NORTH`). `build_world.py` fails when a shipyard's blocks reach
into the slab of its station's bounding sphere.

```bash
uv run check_sun_clearance.py <world folder>
```

sweeps a built world independently of those rules: 7,200 Sun positions over the
day, every gate and shipyard block against each station. It prints the
smallest clearance in meters and degrees per station, how many of 26 lobby spots
see the shipyard's center and its corners past the blocks, and how far the
farthest block is from the zone's center. It exits with 1 when the Sun can reach
a station through one of them, or a block leaves the zone. Its sight counts take
each block as its first cell only, so they can miss a beam in the way: at Gamma
Station it counted 18 of 25 spots where the game had none (SE-934). The
plugin's `tests/test_shipyard_sight.py` counts them with the game's physics.

The clearances are for the ray that casts shadows. Other stations aren't counted:
one passes in front of the Sun once a day at about 200 km, which doesn't flicker.
The asteroid ring isn't counted either: its asteroids can still pass in front of
the Sun as seen from the equatorial stations. Nor is what a gate does to a shipyard: the
equatorial square's gates can shade the four shipyards on that plane, though not
their stations.

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
  players, so the world sets `MaxPlayers` 255 (`MAX_PLAYERS`) and `TotalPCU`
  255,000,000. The scenario's `MaxPlayers` 4 would have meant 250,000 PCU each and
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
- The stations' 8 connectors trade and are shared with OCT's faction only, so a
  docked ship gets no power, hydrogen or conveyor access from the station and
  no player can turn trading off. Trading needs no access to them. On the
  server the Octahedron plugin keeps them that way (`StationConnectorsTrade`),
  in a world from before the rule too.
- Every station has 4 "Free Tools" cargo terminals (the blueprint's hidden
  toolboxes by its "Free tools!" signs, renamed and shown in inventory screens)
  shared with all players. On the server, the Octahedron plugin's
  `RefillContainers` rule in `config/octahedron.cfg` tops each up every minute
  to 3 each of welders, grinders and drills, each one Enhanced or Proficient at
  random (the two tiers above the basic tool), 1 pair of binoculars (the
  Binoculars mod, which has no tiers), 3 full oxygen and 3 full
  hydrogen bottles, and 3 suit battery packs (Powerkit). Each refill first takes
  out the bottles that aren't full, so a player can swap a used bottle for a
  full one there. "Station Loot" is the blueprint's tools, bottles and ammo
  container.
- Binoculars work in a safe zone that forbids shooting, though the game counts them as a
  rifle. Clients have no plugin, so the plugin's `BinocularsInZones` tells them the zone
  doesn't hold a character holding binoculars. The server's own check doesn't change.
- The asteroid ore settings are vanilla's (DepositsCountCoefficient 2,
  DepositSizeDenominator 30, HarvestRatioMultiplier 1), not the Star System
  scenario's sparser 1, 60 and 0.8. See `ORES.md`.
