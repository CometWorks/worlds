# Octahedron

The world of the first public test cluster. EarthLike and its Moon from the
vanilla Star System scenario, 7 stations, Trade Lanes between them and an
asteroid ring. The design and the reasons behind it are in the workspace note
`se1/notes/public-test-cluster/NOTE.md`; the work is ticket SE1-0114.

## Building the world

```bash
python3 make_ore_mod.py
python3 build_world.py <output folder> --seed <N> [--ore-mod <Workshop id>]
```

Both use only the Python standard library. They read the game's content and the
station blueprint from the Steam folders on Linux
(`~/.steam/steam/steamapps`), so Steam must have the game and the blueprint
([3816368704](https://steamcommunity.com/sharedfiles/filedetails/?id=3816368704))
downloaded.

`build_world.py` starts from a copy of the Star System scenario:

- Removes every planet but EarthLike and the Moon.
- Applies the world settings and the mod list (`SETTINGS` and `MODS` in the
  script, MES and its encounter mods from `config/mes-mods.xml`).
- Places the 6 stations of the octahedron 80 km above EarthLike and one 30 km
  above the Moon, toward the nearest station. Every station's up is its
  planet's north. Each sits in a 250 m admin safe zone, owned by the
  "Octahedron Authority" NPC faction, and players can't damage or edit it.
- Places two parallel Trade Lanes along each of the 12 edges and between the
  Moon station and its nearest station. Every endpoint is a one-block grid with
  a Trade Lane Computer in a 100 m safe zone, 1000 m from its station.
- Places 400 asteroids in a ring 60 km above the equator, and 4 ice asteroids
  on the ring halfway between the equatorial stations. They are the game's own
  procedural asteroids, saved as seed and size only (a `.vx2` of about 100
  bytes each), so the game generates their shape and ores. The ice ones are a
  vanilla prefab shape filled with ice. Procedural asteroids are off
  (`ProceduralDensity` = 0).
- Writes the mod configs into the world's `Storage` folder: Relative Top Speed
  (`config/relative-top-speed.xml`) and MES (`config/storage`).

The seed only changes the asteroid ring. The stations, lanes and every entity
id other than the asteroids' come out the same for any seed.

| World | Seed |
|---|---|
| Server | 1 |
| Workshop copy | TBD, any other seed |

## OctahedronOres

`make_ore_mod.py` writes the ore mod into `OctahedronOres/` (ignored here, mods
live in their own repositories). It copies the game's own definitions and
changes only these:

- Moon: no gold, silver or platinum. The ore map values of its gold, which
  cover about as many pixels as its magnesium, become uranium 50 to 100 m deep.
- Asteroids and meteorites: no gold, silver or uranium.
- EarthLike stays vanilla; it already has gold and silver and no uranium or
  platinum.

Rerun it after a game update that changes the planet or voxel material
definitions. Until the mod is on the Workshop, the world lists it as a local
mod (`--ore-mod 0`), which a multiplayer game refuses.

## Notes

- The game forces `WorldSizeKm` to 0 for scenarios with planets, and its world
  edge is a cube anyway. The 1000 km world radius needs a plugin.
- In-game scripts are off until the script whitelist exists.
