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
| `config/` | inputs of the world: MES mod list and settings, Relative Top Speed config; the server's Essentials config |
| `server/` | the server's world, seed 42 |
| `ORES.md` | ore statistics of `server/` |
| `MES.md` | the MES settings and what spawns where |

## One seed per world

A world and its ore mod are built with the same seed. The seed decides
everything random: entity ids, the asteroids' places, sizes, rotations and
seeds, and where the ore deposits sit on EarthLike and the Moon. The layout
(planets, stations, lanes, the ring's shape) and the rules (settings, mods,
which ore goes where) are the same for every seed, and the same seed always
gives the same files.

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
  and on the equator, and one 30 km above the Moon toward the nearest station.
  Every station's up is its planet's north. Each sits in a 200 m admin safe
  zone, owned by the "Octahedron Authority" NPC faction, and players can't
  damage or edit it. The stations start empty, apart from 10,000 kg of
  uranium ingots in each large reactor, a full safe zone block (75 zone chips)
  and the gas in the tanks. North Station, the one with the lane to the Moon,
  shares its inventories and production with everyone.
- Makes the stations the NPC faction's trade stations: the faction is a Trader
  with a bank account, and each station grid has a station entry, so the
  economy stocks its store and contract blocks. The game's own trade factions
  and their procedural stations are off (`GenerateFactionsOnStart` false).
  The entries name no safe zone (`SafeZoneEntityId` 0); with one, the economy
  would delete the station grid when no player is near. That leaves the
  Services Terminal's repair and salvage without grids to list.
- Places one Trade Lane along each of the 12 edges and between the Moon
  station and its nearest station, 13 in all. A lane goes both ways: each end
  is an entry toward the other. Every end is a one-block grid with a Trade Lane
  Computer in a 100 m radius admin safe zone, 1000 m from its station.
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
  `ORES.md` counts the asteroids per 15 degrees around it.
- The asteroids are the game's own procedural ones, saved as seed and size only
  (a `.vx2` of about 100 bytes each), so the game generates their shape and
  ores. The ice ones are the largest plain vanilla asteroid shape
  (`SuperRock_001`, 1024 m) filled with ice. The game's own procedural
  asteroids are off (`ProceduralDensity` = 0).
- Writes the mod configs into the world's `Storage` folder: Relative Top Speed
  (`config/relative-top-speed.xml`) and MES (`config/storage`).

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
- EarthLike keeps its ore rules; it already has gold and silver and no uranium
  or platinum.
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
  450 km, needs a plugin (SE1-0119).
- In-game scripts are off until the script whitelist exists.
- Radiation is on (planets, weather, blocks); solar radiation (`SolarRadiationIntensity`)
  and hunger (`FoodConsumptionRate`) stay at 0.
- Every economy tick the game refills each trade station: fuel, batteries,
  gas, ice in the gas generators, and loot (credits included) in every empty
  cargo container, which it then hides. On the server the Essentials plugin
  replaces that with `config/essentials.cfg`: loot only into the one
  faction-only "Station Loot" cargo terminal per station, and no ice, so
  North Station's shared cargo and oxygen generators can't be farmed. A world
  without the plugin, like the design world, has the game's refill.
- The asteroid ore settings are vanilla's (DepositsCountCoefficient 2,
  DepositSizeDenominator 30, HarvestRatioMultiplier 1), not the Star System
  scenario's sparser 1, 60 and 0.8. See `ORES.md`.
