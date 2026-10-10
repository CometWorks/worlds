# MES on Octahedron

How Modular Encounters Systems (MES) is set up on the Octahedron world, what it spawns where, how the spawns follow the players' threat score, and what looks risky. For the server admins.

Worked out on Linux on 2026-10-10 from MES 2.74.05 (workshop 1521905890) and the spawn group definitions of the 15 mods in `config/mes-mods.xml`, as downloaded under `~/.steam/steam/steamapps/workshop/content/244850/`. Not tested in game. Figures marked *estimate* come from the MES rules and a simulation of its selection code, not from a running server.

Source paths below are relative to the MES script folder, `1521905890/Data/Scripts/ModularEncountersSystems/`.

## 1. Settings

### Files

MES keeps one XML file per area in the world's `Storage/1521905890.sbm_ModularEncountersSystems/`. It creates every missing file with the defaults of its config class on the first load, and a file that sets only some values gets the class defaults for the rest. We ship only `Config-SpaceCargoShips.xml` (from `config/storage/`).

| File | Class (`Configuration/`) | Controls | Ours |
|---|---|---|---|
| `Config-General.xml` | `ConfigGeneral` | mod weighting, global NPC cap, threat handicap, difficulty, global blacklists | default |
| `Config-SpaceCargoShips.xml` | `ConfigSpaceCargoShips` | space and lunar cargo ships | `MinLunarSpawnHeight` = 0 |
| `Config-RandomEncounters.xml` | `ConfigRandomEncounters` | space random encounters | default |
| `Config-PlanetaryCargoShips.xml` | `ConfigPlanetaryCargoShips` | atmospheric and gravity cargo ships | default |
| `Config-PlanetaryInstallations.xml` | `ConfigPlanetaryInstallations` | surface installations | default |
| `Config-BossEncounters.xml` | `ConfigBossEncounters` | boss encounters | default |
| `Config-ConfigDroneEncounters.xml` (MES's spelling) | `ConfigDroneEncounters` | drone encounters | default |
| `Config-Creatures.xml` | `ConfigCreatures` | creature spawns | default |
| `Config-OtherNPCs.xml` | `ConfigOtherNPCs` | cleanup of NPCs spawned by other NPCs (reinforcements) | default |
| `Config-Combat.xml` | `ConfigCombat` | combat/peace phases (off) | default |
| `Config-Grids.xml`, `Config-CustomBlocks.xml`, `Config-Progression.xml` | | NPC grid tweaks, MES blocks, suit upgrades | default |

Every spawn type file also has `SpawnTypeBlacklist` (spawn group names or mod ids), `SpawnTypePlanetBlacklist` and `PlanetSpawnFilters`, all empty. These are the tools to switch off single groups or mods per spawn type. In game, an admin can change a value with `/MES.Settings.<Area>.<Field>.<Value>` (for example `/MES.Settings.SpaceCargoShips.MinLunarSpawnHeight.0`), and `/MES.GESAP` lists the groups eligible at the admin's position.

### Our one override

`MinLunarSpawnHeight` = 0 (default 3500) turns lunar cargo ships off. MES offers them when the point `MinLunarSpawnHeight` above the player is outside gravity (`World/EnvironmentEvaluation.cs`, `SetEligibleSpawns`). With 0, that point is the player's own position, which is in gravity whenever the check runs. So no space cargo ship spawns inside either planet's gravity.

### Spawn types: when MES tries and where it places them

Each online, living player has their own timers (`Spawning/WatchedPlayer.cs`), stepped every 10 s (`PlayerWatcherTimerTrigger`). The player's position picks the spawn type (`Spawning/SpawnRequest.cs`, `World/EnvironmentEvaluation.cs`).

| Spawn type | Player must be | When MES tries | Where it spawns |
|---|---|---|---|
| Space cargo ship | outside gravity | 300 s after the player joins, then every 780–1020 s | a straight path 10–15 km long that passes 2–4 km from the player; the start is ≥1 km from any grid; the path must not touch the nearest planet's gravity sphere or any asteroid's bounding box within 20 km |
| Random encounter | outside gravity | every 60 s, but only once the player is ≥15 km (straight line) from where the last check passed; 300 s cooldown after a spawn | 8–12 km from the player, ≥3 km from any grid and from any asteroid's center; moved to the opposite side if the point is in the nearest planet's gravity |
| Atmospheric cargo ship | in gravity, ≤6 km altitude | own timer: 300 s after joining, then every 780–1020 s | a path 10–13 km long at 1.7–2.5 km altitude, 3–5 km from the player, never below 900 m altitude; air density ≥0.7 at both ends unless the group sets `SkipAirDensityCheck` |
| Gravity cargo ship | in gravity, >6 km altitude | the same timer as atmospheric | a path 10–13 km long at the player's altitude ±1.7–2.5 km, 3–5 km away, start ≥1.2 km from any grid |
| Planetary installation | in gravity, ≤6 km altitude | every 60 s, but only after the player moved ≥6 km over the surface since the last spawn; 300 s cooldown after a spawn | 3–6 km from the player (2 km farther for medium, 4 km for large) on flat ground (±2.5 m), ≥2.5 km from any grid |
| Boss encounter | anywhere | every 1200 s; 600 s cooldown after a spawn | a GPS signal 6–8 km away, sent to every player within 25 km and kept for 1200 s; the boss spawns 2.5–4 km from it once a player gets within 300 m |
| Drone encounter | anywhere | every 30 s | nothing: the only drone groups are four admin-only MES debug groups |
| Creatures | ≤150 m altitude | every 900–1200 s | nothing: the only creature groups (Abandoned Settlements bots) are admin-only and need the AiEnabled mod; vanilla wolves and spiders are off in the world settings |
| Lunar cargo ship | | | off (our override) |

Installation size: MES first offers a small one and rolls for medium with a chance that starts at 15 % and grows by 15 % with every small installation on that planet; a medium one rolls for large starting at 5 %, +15 % per medium (`Spawning/SpawnGroupCollection.cs`, `Spawning/PrefabSpawner.cs`). The counters are per planet and shared by all players. In the long run that is about 62 % small, 27 % medium and 11 % large (*estimate*, simulated).

### Caps and cleanup

| Spawn type | Max of the type around the player | Timeout (spawns / radius / time since the last one) | Cleanup (default values) |
|---|---|---|---|
| Space cargo ship | 15 grids within 15 km | 4 / 10 km / 900 s | at the end of its path when nobody is within 1 km, or when the nearest player is >30 km away |
| Random encounter | 10 grids within 25 km | 2 / 25 km / 900 s | after 3600 s in total with the nearest player >50 km away (the timer does not reset) |
| Atmospheric and gravity cargo ship | 2 grids within 20 km | 2 / 10 km / 900 s | at the end of its path, or when the nearest player is >25 km away |
| Planetary installation | 10 grids within 15 km | 2 / 25 km / 900 s | after 3600 s with the nearest player >50 km away; the timer resets when a player comes back |
| Boss encounter | 6 grids within 25 km | none | when the nearest player is >50 km away |

`CleanupUnpoweredOverride` is on for every type but random encounters. The code (`Watchers/Cleaning.cs`) applies the `CleanupUnpowered*` values (25 km / 900 s, or 20 km for space cargo ships) to powered grids and the normal ones to unpowered grids, which looks inverted. Cleanup only removes MES-spawned grids (`OnlyCleanNpcsFromMes`), so the stations are safe from it, and it skips groups tagged `IgnoreCleanupRules`.

### Global settings in `Config-General.xml`

- `UseMaxNpcGrids` = false: no global NPC limit (`MaxGlobalNpcGrids` = 50 is not used). Only the per-area caps above apply.
- `UseModIdSelectionForSpawning` and `UseWeightedModIdSelection` = true. For each attempt MES collects the eligible groups, picks a mod first, weighted 1 if it has ≤10 eligible groups, 2 for 11–19 and 3 for ≥20, then a group in that mod weighted by the group's `Frequency` (`Spawning/SpawnGroupCollection.cs`, `SelectSpawnGroupSublist`). A group's chance tags (`SpaceCargoShipChance` and the like) are rolled while collecting, so a 20 % group is in the pool one attempt in five.
- `ThreatReductionHandicap` = 0, `Difficulty` = 1. No group in these mods uses the difficulty tags.
- Combat phases are off (`Config-Combat.xml`), so the 78 groups tagged `UseCombatPhase` spawn normally.

### Vanilla encounters

- The two Suppress Vanilla mods (888457124, 888457381) stop MES from spawning the base game's cargo ships and random encounters (`Spawning/SpawnGroupManager.cs`, `ApplySuppression`). The world settings also turn the game's own cargo ships, encounters and drones off.
- The base game's planetary encounters are not suppressed. MES turns every base-game group with `IsPlanetaryEncounter` into a small planetary installation (`GetOldSpawnGroupDetails`), and `EnablePlanetaryEncounters` = false does not stop MES. 28 of them fit EarthLike and 13 the Moon (Appendix B). Their suppressor mod, 3796756949, is not in our list.
- MES does not spawn the base game's global encounters. `GlobalEncounterCap` = 0 keeps the game from spawning them.

## 2. What spawns where

### Locations

| Code | Where the player is | Spawn types |
|---|---|---|
| S | outside both gravity wells: all 7 stations, the Trade Lanes, the asteroid ring, anywhere ≥103 km from EarthLike's center or >2.8 km above the Moon | space cargo ships, random encounters, space bosses |
| EL | EarthLike gravity, ≤6 km altitude | atmospheric cargo ships, installations, bosses |
| EH | EarthLike gravity, 6–43 km altitude | gravity cargo ships, bosses |
| M | Moon gravity (≤2.8 km altitude) | installations, the atmospheric cargo ships that skip the air check, bosses |

The spawn's own position follows the player's location type, with the margins of the previous section: space spawns never start in gravity, and planet spawns stay within a few km of the player.

The previous note (`se1/notes/mes-earth-moon-spawns`) says the Moon gets installations only. That misses two things: the 15 cargo groups with `SkipAirDensityCheck` (marked "flies on the Moon" in Appendix A) pass as atmospheric cargo ships there, because the tag also skips the "planet needs air" rule and the air checks on the path. Their path at 1.7–2.5 km above the Moon mostly clears the 900 m minimum despite the curvature (*estimate*). And the 13 vanilla planetary encounters above.

### How often, per player (*estimate*)

| Player's situation | What MES tries | Rate |
|---|---|---|
| In space, parked or flying | space cargo ship | one per 13–17 min (about 4 an hour), but no more than 4 per 15 min within 10 km for all players together |
| In space, travelling | random encounter | one per 15 km travelled, at most one per ~6 min, no more than 2 per 15 min within 25 km |
| On EarthLike below 6 km | atmospheric cargo ship | about 4 an hour, at most 2 alive within 20 km |
| On EarthLike or the Moon, moving | installation | one per 6 km of surface travel, at most one per ~6 min, no more than 2 per 15 min within 25 km |
| On EarthLike above 6 km | gravity cargo ship | about 4 an hour |
| Anywhere, threat ≥1500 within 15 km | boss signal | one per 20 min, 30 min after a successful one |

The cargo ship timers keep running in the wrong place: a space cargo attempt while the player is on a planet is lost, and the other way round. Attempts can also fail on pathing or because no group is eligible; these rates are upper bounds.

### What spawns, by mod

494 MES spawn groups in 14 mods, plus the vanilla planetary encounters. 330 can be picked by the timers above. The other 164 are spawned only by other NPCs (reinforcements, settlement parts and so on), are admin-only, or are never eligible. Shares are the chance that an attempt of that type at that location picks the mod. The first number is for threat ≈10 (a new player), the second for threat ≈800, both before the Xenvakian Empire is unlocked (*estimate*, simulated with the selection code; assumptions below).

| Mod | Type, location | Groups | Threat gate | Share | Notes |
|---|---|---|---|---|---|
| Parallax | space cargo S | 13 | none | 23 → 18 % | hostile (`Enemies`) |
| Imber | space cargo S | 12 | none | 22 → 18 % | hostile |
| Independent Contractors | space cargo S | 18 | none | 22 → 18 % | hostile |
| Terran Heavy Industries | space cargo S | 8 | none (Fafnir cruiser 1500+, 5 % chance) | 11 → 9 % | neutral traders and miners |
| Trade Operators Coalition | space cargo S | 2 | none | 10 → 9 % | neutral; Wandering Merchant |
| Xenvakian Empire | space cargo S | 1 at first, then 7 per reputation tier | none for the probe, 250–550 for warships | 11 → 10 % | progression, see below |
| Pravda | space cargo S | 9 | 350 / 700 / 1000 within 10 km | 0 → 9 % | neutral warships |
| Space Pirates Enhanced | space cargo S | 4 | 720–1500 | 0 → 9 % | hostile |
| Weyland-Yutani | random encounter S | 24 | none | 41 → 36 % | hostile remote stations |
| Terran Heavy Industries | random encounter S | 3 | Space Station 250+ | 15 → 13 % | |
| Trade Operators Coalition | random encounter S | 5 | none; ≥100 km from another TOC station | 15 → 12 % | trade stations |
| Wreckage | random encounter S | 3 | none | 14 → 13 % | derelict wrecks, 67 prefabs |
| Imber | random encounter S | 1 (Drone Patrol) | none | 14 → 11 % | hostile drones |
| Space Pirates Enhanced | random encounter S | 4 | 195–325 | 0 → 13 % | raiding outposts and stations |
| Abandoned Mining Facility | random encounter S | 1 | none, 5 % chance | 1 % | |
| Parallax, Imber, Independent Contractors | boss S/EH/EL/M | 2 + 2 + 3 | 1500+ within 15 km | only at 1500+ | Incon's three only in space |
| Independent Contractors | gravity cargo EH | 12 | none | 50 % | |
| Parallax / Imber | gravity cargo EH | 6 / 2 | none | 26 / 24 % | |
| Xenvakian Empire | gravity cargo EH | 2 per tier | 250 / 300 | ~20 % once unlocked | |
| Independent Contractors | atmospheric cargo EL | 12 | none | 29 → 24 % | |
| Imber, Parallax, THI, TOC, Xenvakian | atmospheric cargo EL | 6, 6, 4, 2, 15 | mostly none | 13–15 % each | |
| Space Pirates Enhanced | atmospheric cargo EL | 2 | 730 / 1400 | 0 → 13 % | |
| Wreckage | atmospheric cargo EL/M | 2 | none, 10 % chance | 2 % | |
| Parallax, Imber, Incon, THI Baldrin, Wreckage | atmospheric cargo M | 15 | none (SPRT Stinging Adversary 1400+) | ~24 % each, Wreckage 3 % | the skip-air groups |
| Vanilla planetary encounters | small installation EL/M | 28 / 13 | none | 28 → 20 % | economy outposts, pirate bases, wrecks |
| Abandoned Settlements v2 | small installation EL/M | 18 | four bands 0–5 … 60+ | 15 → 10 % | QARC settlements |
| Wreckage, Imber, Parallax, Incon | small installation EL/M | 4, 4, 5, 2 | none | 14–15 → 10 % each | |
| Terran Heavy Industries | small installation EL | 2 | 250+ | 0 → 10 % | Buffalo needs ice or snow |
| Weyland-Yutani | small / medium / large installation EL | 5 / 6 / 7 | 280–1500 | 0 → 10–20 % | hostile; drill suppressors |
| Space Pirates Enhanced | small / medium / large installation EL | 2 / 1 / 1 | 358–455 | 0 → 10–20 % | |
| Wasteland Encounters V2 | medium installation EL/M | 82 | none | 60 → 42 % of mediums | derelict wrecks and outposts |
| Parallax, Imber | medium installation EL/M | 1, 3 | none | 20 → 14 % each | |
| Independent Contractors, Imber, TOC | large installation EL/M | 1, 1, 5 | none; TOC ≥25 km from another TOC station | ~33 → 20 % each | Atom Heart Station, Fortress Tower |
| Xenvakian Empire | installations EL/M, orbital platform S | 2 + 1 per aggressive tier | 200 / 400 | only after reputation drops | |

Assumptions behind the shares: the player stands on grass or soil (vanilla encounters and the Buffalo check the terrain type), no Trade Operators Coalition station is near (TOC cargo ships need one within 25 km, TOC stations need none within 25 or 100 km), player reputation with the Xenvakian Empire is the default −1000, and the threat score is the same for every check range. Weights within each mod are in Appendix A.

#### Xenvakian Empire

The Xenvakian groups are a progression. All but two need the world-wide sandbox flag `Mod-Enable`. The two unlocked ones, Probe Unit Atmo and Ion (Passive), spawn once per world (`UniqueEncounter`); a probe sets `Mod-Enable` 1–2 s after it spawns. Then the Passive tier unlocks (warships from threat 250 up). Each tier needs a player within 15 km whose reputation with TXVE is in its range: Passive −1001…−1000 (the default), then Lightly, Moderately, Highly aggressive and Armageddon down to −1500 as players fight them. Only the aggressive tiers bring installations, orbital platforms and probes. The Final Boss (space, threat ≥700 within 5 km) clears `Mod-Enable` when destroyed. Since the probes cannot spawn again, that ends the Xenvakian spawns for the world unless an admin sets the flag again.

## 3. Threat score

### How MES computes it

A spawn group with `[UseThreatLevelCheck:true]` compares a threat score against `ThreatScoreMinimum` and `ThreatScoreMaximum` (`Spawning/SpawnConditions.cs`, `CheckCommonSpawnConditions` and `GetThreatLevel`). The score is the sum over every grid within `ThreatLevelCheckRange` (default 5 km, some mods use 10 or 15 km) of the spawn check position, which is the triggering player's position. Only player-owned grids count (NPC grids only with `ThreatIncludeOtherNpcOwners`, which no group sets), from every player, not just the triggering one. Characters count for nothing. A value ≤0 switches that side of the check off. `ThreatScorePlanetaryHandicap`, which would raise the gates in gravity, is unused here, as are the difficulty tags. Groups without the tag ignore threat entirely. The gates are minimums: a high-threat player still gets the ungated groups, the gated ones just join the pool.

Per grid (`Entities/EntityEvaluator.cs`, `GridTargetValue`, cached 5 s):

score = (block points + max power output in MW / 10 + block count / 100 (integer division) + bounding box diagonal in m / 4) × 0.75 if static × 2.5 large grid or 0.5 small grid × 0.7

| Functional block | Points |
|---|---|
| antenna / beacon | 4 / 3 |
| cockpit, seat, remote control | 0.5 |
| thruster | 1 |
| rotor, piston, hinge | 1 |
| power producer (reactor, battery, solar, H2 engine) | 0.5 × (1 + inventory fill) |
| cargo container | 0.5 × (1 + fill) |
| drill, welder, grinder | 2 × (1 + fill) |
| refinery, assembler, gas generator | 2 × (1 + fill) |
| gravity generator, artificial mass | 2 × (1 + fill) |
| fixed weapon / turret | 5 / 7.5, × (1 + fill) |
| jump drive | 10 |
| medical room, survival kit | 10 |
| Nanobot Build and Repair (also counts as a welder) | 15 |
| shield block | 15 |

Modded blocks count double (weapons, turrets and gravity blocks four times). One large-grid Nanobot is worth about 60 threat on its own, one 300 MW large reactor about 50 through its power output alone.

### Example scores (*estimate*)

| Grids within range | Score |
|---|---|
| player on foot, no grids | 0 |
| new player's small-grid miner: 120 blocks, 8 thrusters, 2 drills, antenna, 2 small batteries | ~10 |
| basic large-grid miner: 300 blocks, 12 thrusters, 3 drills, survival kit, antenna | ~90 |
| armed large ship: 1500 blocks, 24 thrusters, 4 turrets, 2 guns, jump drive, 2 small reactors, 6 batteries | ~270 |
| large static base: 6000 blocks, 10 turrets, 12 production blocks, 30 containers, 2 large reactors, 20 batteries, a Nanobot | ~560 |
| that base plus three parked armed ships | ~1,370 |

### Who meets what

| Threat around the player | Typical situation | Space (S) | EarthLike surface (EL) | EarthLike high (EH) | Moon (M) |
|---|---|---|---|---|---|
| 0–5 | on foot, no grids within 5 km | all ungated cargo: Parallax, Imber, Incon, THI, TOC merchant, the Xenvakian probe; ungated encounters: WLYC remote stations, THI, TOC stations, Wreckage, Imber drones | AS2 band 0 (pods, respawn), vanilla outposts, Wreckage, Imber/Parallax/Incon outposts; Wasteland mediums; Incon/Imber/TOC large | Incon/Parallax/Imber gravity cargo | as EL without Weyland-Yutani, SPRT and THI; skip-air cargo |
| 5–60 | new player's small ship, small outpost | same | AS2 band 1 (5–40), then band 2 (40+) | same | same |
| 60–200 | basic large ship | same | AS2 band 3 (60+ within 10 km) | same | same |
| 200–700 | armed large ship, mid-game base | + SPRT raiding outposts and stations (195–325), THI Space Station (250), Pravda Derzky/Mako/Moskit (350), Xenvakian warships (250–550, once unlocked) | + Weyland-Yutani bases (280–640), SPRT facilities (358–455), THI Mammoth and Buffalo (250) | + Xenvakian Cobra and Rakshi | unchanged |
| 700–1500 | base with a few warships, or a busy hub | + Pravda Gaede/Nideggen (700) and Gnevny/Graf Spee/Prinz Eugen/Weser (1000), SPRT Drifting Pursuant (720), Xenvakian Final Boss (700 within 5 km) | + SPRT Drifting Pursuant (730), Weyland-Yutani large bases (960) | unchanged | unchanged |
| ≥1500 | end-game base with a fleet within 15 km | + boss signals (Parallax, Imber, Incon), SPRT Stinging Adversary (1400), Light and Heavy Raider (1440, 1500), THI Fafnir (5 %) | + Parallax and Imber bosses, Weyland-Yutani 1480/1500 bases, SPRT Stinging Adversary | + Parallax and Imber bosses | + Parallax and Imber bosses, Stinging Adversary |

What this means for the theme:

- A new player meets hostile warships from the start. In space about two thirds of the cargo ships (Parallax, Imber, Incon, all `Enemies`) are not gated by threat at all, and the Imber Drone Patrol is one random encounter in seven.
- Difficulty barely grows with the player. At threat 1600 the gated Pravda and SPRT ships are still only ~18 % of space cargo ships; the rest is the same pool a new player gets. Most of the gated content is on EarthLike's surface (Weyland-Yutani, SPRT) and in the boss signals.
- The score belongs to the place, not the player. Everyone at a station shares the sum of all player grids parked within 5–15 km, so a new player at a busy hub gets the hub's threat. Players start on stations.

## 4. Things that look wrong or risky

1. **Vanilla planetary encounters are on.** Without mod 3796756949 (Suppress Vanilla Planetary Installations) MES spawns 28 base-game outposts on EarthLike and 13 on the Moon, including military watchtowers and pirate strongholds owned by the economy and pirate factions. They are 20–28 % of all small installations. Add the mod, or blacklist them in `Config-PlanetaryInstallations.xml`, if we don't want them.
2. **Hubs fill up with NPCs.** The space cargo timer runs for every player parked at a station. A busy station can get 4 cargo ships per 15 min within 10 km, up to 15 alive within 15 km, on paths 2–4 km from the players there. MES keeps spawn starts ≥1 km (cargo), ≥2 km (boss signal), ≥2.5 km (installation) or ≥3 km (encounter) from any grid, so nothing spawns inside the 200 m station or 100 m lane safe zones, but paths and AI-driven ships can fly through them, and MES's placement code never looks at safe zones. Hostile ships right outside a station's zone meet new players there.
3. **Trade Lanes trigger random encounters.** A lane trip covers far more than 15 km, so the next 60 s check after arriving is eligible: a static encounter 8–12 km from the arrival station. These are only cleaned up after an hour with nobody within 50 km, which never happens around a used station, so they pile up to the cap of 10 grids within 25 km around each busy station.
4. **No global NPC limit.** `UseMaxNpcGrids` is off. With many players the NPC grid count is bounded only by the per-area caps. Worth turning on with a `MaxGlobalNpcGrids` the cluster can carry. How MES counts and cleans up on a cluster where each node sees only its own players and grids was not checked.
5. **Some installations are never removed.** 27 installation groups carry `IgnoreCleanupRules` (18 Abandoned Settlements, 9 Wasteland), and the settlement parts their behaviors spawn carry it too. They accumulate on EarthLike and the Moon for good. The powered/unpowered cleanup values also look swapped in MES (section 1).
6. **High-threat spawns at hubs.** Bosses need 1500 within 15 km, which a few parked fleets at a station reach. Boss signals then go to every player within 25 km, new ones included.
7. **Xenvakian Final Boss can end the mod early.** Once unlocked, any spot with threat ≥700 within 5 km in space can get it, and destroying it stops Xenvakian spawns for the world.
8. **Suppressor blocks.** 18 Weyland-Yutani bases on EarthLike get MES drill suppressors. Of the Xenvakian groups, 36 get jetpack suppressors, 8 drill suppressors, and the Final Boss also a Nanobot suppressor. That cuts into practical planet mining and into the Nanobot Build and Repair mod near those NPCs.
9. **The asteroid ring holds back space spawns (*estimate*).** Random encounters need ≥3 km to every asteroid's center, which almost no point inside the 4 km wide band has, so they spawn above, below or beside it. Cargo paths whose line would cross any asteroid's bounding box within 20 km are rejected, so fewer cargo ships run along the ring. Not wrong, but the ring will be quieter than open space. Alpha and Gamma Station are ~19 km from the ring, so this also affects their surroundings.
10. **Moon flyers.** The 15 skip-air cargo groups fly at 1.7–2.5 km over the Moon, just under its 2.8 km gravity edge. Correct for MES, but it contradicts the earlier note.
11. **Imber's two space stations never spawn.** Delphos Supply Outpost and Perios Station use the condition group `MES-SpawnConditionsGroup-RandomEncounterStation`, which MES 2.74.05 does not define. Harmless.
12. **Abandoned Mining Facility brings its own voxels** (`RemoveVoxelsIfGridRemoved`). Its ores were not checked against the world's ore rules (uranium on the Moon only, platinum in asteroids only). It is ~1 % of random encounters.

Checked and fine: space spawns cannot start inside EarthLike's or the Moon's gravity (cargo paths that touch the gravity sphere are rejected, encounter points are moved to the other side), so the stations and the ring 17 km outside EarthLike's gravity edge only get space content, and nothing spawns on the planets because of a player at the stations.

## Appendix A: timer-spawned groups

Codes: SC space cargo, RE random encounter, AC atmospheric cargo, GC gravity cargo, PI-S/M/L small/medium/large installation; locations S, EH, EL, M as in section 2. Threat is the minimum score (5 km range unless noted), "–" for no check. Weight is the group's `Frequency`. Groups with escort variants (Parallax, Imber, Incon, Pravda) spawn with escorts 20 % of the time.

### Wreckage (WRECKS)

| Group | Spawns as | Threat | Weight | Notes |
|---|---|---|---|---|
| Wrecks-Mobile-Both | AC EL/M; RE S | – | 5 | AC 10 % chance |
| Wrecks-SG-Rover-Planetary | AC EL/M | – | 5 | AC 10 % chance |
| Wrecks-Static-Both | PI-S EL/M; RE S | – | 5 | |
| Wrecks-Static-Locked-Planetary | PI-S EL/M | – | 1 | |
| Wrecks-Static-Planetary | PI-S EL/M | – | 5 | |
| Wrecks-Static-Planetary-Underground | PI-S EL/M | – | 1 | |
| Wrecks-Static-Space | RE S | – | 5 | |

### Weyland-Yutani (WLYC)

All installations need air (EarthLike only).

| Group | Spawns as | Threat | Weight |
|---|---|---|---|
| WLYC-Onyx-Ultra-Compact-Pod, -Survival-Shelter, -Oasis-Starter-Base, -Echo-Canyon-Habitable-Outpost | PI-S EL | 280 | 5 |
| WLYC-Rax-Nygahl-Station | PI-S EL | 420 | 5 |
| WLYC-OctaHab-1 | PI-M EL | 420 | 5 |
| WLYC-Pogo-Base, -Pogo-Base-MK2, -Proteus-Station, -Dark-Finger-of-Death, -Substrata-Refuge | PI-M EL | 640 | 5 |
| WLYC-Gas-Z-Gaseous-Production-Facility | PI-L EL | 640 | 5 |
| WLYC-Vortex-Energy-Solutions-MK2, -Rattlesnake-Flats-Outpost | PI-L EL | 960 | 5 |
| WLYC-Pegasus-Trading-Station, -Centurion-Smelting-and-Refining-Facility | PI-L EL | 1480 | 5 |
| WLYC-Nexus-Galaxia, -Oberon-Nuclear-Generating-Station | PI-L EL | 1500 | 5 |
| WLYC-Remote-Station-Alpha … -Omega (24) | RE S | – | 5 |

### Terran Heavy Industries (THIE, neutral)

| Group | Spawns as | Threat | Weight | Notes |
|---|---|---|---|---|
| THIE-202-AS-Atlas, THIE-205-ASC-Atlas | AC EL; SC S | – | 5 | |
| THIE-631-Elephant | RE S; SC S | – | 5 | |
| THIE-651-AMS-Baldrin | AC EL/M | – | 5 | flies on the Moon |
| THIE-830-A-Thrush | AC EL | – | 5 | |
| THIE-614-S-Caterpillar-Piston-Autominer, THIE-662-MS-Taurus-Miner, THIE-842-S-Prison-Ship, THIE-UNC-Dover-Supertanker | SC S | – | 5 | |
| THIE-910-Jumpdrive-Station | RE S | – | 5 | |
| THIE-Space-Station | RE S | 250 | 5 | |
| THIE-142-MS-Fafnir-Cruiser | SC S | 1500 | 5 | 5 % chance |
| THIE-36-Mammoth-Parking-Bay | PI-S EL | 250 | 5 | not on the Moon |
| THIE-521-Buffalo-Ice-Harvester | PI-S EL | 250 | 5 | ice or snow terrain only |

### Space Pirates Enhanced (SPRTE)

| Group | Spawns as | Threat | Weight |
|---|---|---|---|
| SPRT-Raiding-Outpost-MK2 / -Station-MK2 / -Complex-MK2 / -Station-MK3 | RE S | 195 / 228 / 260 / 325 | 5 |
| SPRT-Salvage-Facility-MK2, SPRT-Remote-Drone-Facility-MK2 | PI-S EL | 358, 423 | 5 |
| SPRT-Ore-Handling-Facility-MK2 | PI-M EL | 390 | 5 |
| SPRT-District-Headquarters-MK2 | PI-L EL | 455 | 5 |
| SPRT-Drifting-Pursuant-MK2 | AC EL | 730 | 5 |
| SPRT-Drifting-Pursuant-MK3 | SC S | 720 | 5 |
| SPRT-Stinging-Adversary-MK2 | AC EL/M; SC S | 1400 | 5 |
| SPRT-Light-Raider / SPRT-Heavy-Raider | SC S | 1440 / 1500 | 5 |

### Trade Operators Coalition (TRADE, neutral)

| Group | Spawns as | Weight | Notes |
|---|---|---|---|
| TOC-SpawnGroup-WanderingMerchant-EV | SC S | 3 | |
| TOC-SpawnGroup-WanderingMerchant-EA | AC EL | 3 | |
| TOC-SpawnGroup-CargoShip | AC EL; SC S | 1 | only within 25 km of a TOC station |
| TOC-SpawnGroup-BrazierDockTradeStation, -GSX5010ChronoStation, -NanyangStation, -OrbitalTradeStation, -PoseidonStation | RE S | 3 | no other TOC station within 100 km; set up their own safe zone |
| TOC-SpawnGroup-PlanetaryLogisticsOutpost, -PlanetaryTradeStationMkI, -TheTower, -TheHangar, -LittleCity | PI-L EL/M | 5 | no other TOC station within 25 km |

### Pravda (PRAVDA, neutral)

All space cargo ships, threat within 10 km.

| Group | Threat | Weight |
|---|---|---|
| Pravda-SpawnGroup-Mako, -Derzky, -Moskit | 350 | 5, 4, 4 |
| Pravda-SpawnGroup-Gaede, -Nideggen | 700 | 3, 3 |
| Pravda-SpawnGroup-GrafSpee, -PrinzEugen, -Gnevny, -Weser | 1000 | 2, 2, 1, 1 |

### Parallax (PARALLAX)

| Group | Spawns as | Threat | Weight |
|---|---|---|---|
| Parallax-SpawnGroup-Blunderbuss, -Thunderhelm | Boss S/EH/EL/M | 1500 (15 km) | 1 |
| Parallax-SpawnGroup-WardOfLothian, -ObanTideMk2, -Shadow, -SpiritOfLomond | AC EL/M; GC EH; SC S | – | 5, 4, 4, 3 |
| Parallax-SpawnGroup-Eidolon, -Triton | AC EL; GC EH; SC S | – | 3, 2 |
| Parallax-SpawnGroup-Lightstar, -VerdisQuo, -Pioneer, -Buzzard, -HammersledII, -Phantom, -BasicShuttle | SC S | – | 4, 4, 3, 2, 2, 2, 1 |
| Parallax-SpawnGroup-SecurityOutpost, -MilitaryInstallation, -Bunker, -FuelingStation, -ExpeditionBasePod | PI-S EL/M | – | 5, 4, 2, 2, 1 |
| Parallax-SpawnGroup-FrontierOutpost | PI-M EL/M | – | 4 |

### Independent Contractors (INCON)

All weight 1 except the two outposts.

| Group | Spawns as | Threat |
|---|---|---|
| Incon-SpawnGroup-AtomHeart, -SCRescisco, -Simplicity02 | Boss S | 1500 (15 km) |
| Incon-SpawnGroup-GremlinLite, -GremlinXL, -SuperGremlin, -TheClangingGremlin, -SS11Harpy | AC EL/M; GC EH; SC S | – |
| Incon-SpawnGroup-AS1StormFire, -GremlinAssault, -IntruderIIIEscortCarrier, -RWIMuskratSARShuttle, -SS1SerenityMK2, -TheScutlerMk7, -WMIHunchbackDropship | AC EL; GC EH; SC S | – |
| Incon-SpawnGroup-BCCorpP100Proton, -GeminiMkI, -GopherIITransport, -RWIAmazonian, -RWIPackmule, -Ubumboo | SC S | – |
| Incon-SpawnGroup-DBSLookoutTower (weight 5), -DBSObservationOutpost (2) | PI-S EL/M | – |
| Incon-SpawnGroup-AtomHeartStation | PI-L EL/M | – |

`Incon-SpawnGroup-SS1SerenityMK2` is defined twice in the mod; the game keeps one.

### Imber (IMBER)

| Group | Spawns as | Threat | Weight |
|---|---|---|---|
| Imber-SpawnGroup-ImpulseFrigate, -InstinctFrigate | Boss S/EH/EL/M | 1500 (15 km) | 1 |
| Imber-SpawnGroup-Monarch, -AquilaStrikeCorvette | AC EL/M; GC EH; SC S | – | 4, 2 |
| Imber-SpawnGroup-TransportCourier, -SupplyTransport, -MobileDrillingPlatform, -SkyFactory | AC EL | – | 5, 4, 3, 2 |
| Imber-SpawnGroup-ExplosivesShipment, -GalvusHeavyCorvette, -ArciosTorpedoCorvette, -ProcessingShip, -VitrusAssaultCorvette, -VycusAssaultCorvette, -EgonTorpedoFrigate, -GravityNetMammon, -VariusUtilityShuttle, -AnicetusHeavyFrigate | SC S | – | 4, 4, 3, 3, 3, 3, 2, 2, 2, 1 |
| Imber-SpawnGroup-DronePatrol | RE S | – | 1 |
| Imber-SpawnGroup-DefenseInstallation, -OreProcessingOutpost, -ScoutingOutpost, -SupplyCache | PI-S EL/M | – | 5, 4, 4, 4 |
| Imber-SpawnGroup-FighterDockingStation, -ResourceProcessingStation, -TerritoryDefenseStation | PI-M EL/M | – | 2 |
| Imber-SpawnGroup-FortressTower | PI-L EL/M | – | 1 |

### Xenvakian Empire (TXVE)

Every group needs `Mod-Enable` and a reputation tier (section 2), except the two probes marked unique.

| Ship or station | Spawns as | Threat | Tiers |
|---|---|---|---|
| Probe Unit Atmo / Ion (Passive) | AC EL / SC S | – | unique, needs reputation −1100…−1000, no `Mod-Enable` |
| Probe Unit Atmo / Ion | AC EL / SC S | – | aggressive tiers |
| Cobra MK3, Rakshi-Class Ion Frigate | AC EL; GC EH; SC S | 250, 300 | all |
| Fast Armed Response Corvette S / M, Makuta-Class Cruiser, Teridax-Class Destroyer, Dreadnaught | SC S | 350, 400, 450, 500, 550 | all |
| Refueling Depot, Heavy Defense Platform | PI-S EL/M | 200, 400 | aggressive tiers |
| Heavy Orbital Defense Platform | RE S | 400 | aggressive tiers |
| Final Boss Encounter | Boss S | 700 | needs `Mod-Enable` only |

### Abandoned Settlements v2 (QARC)

Small installations on EarthLike and the Moon. The spawner grids build the settlement through their behaviors.

| Band | Groups (weight) | Threat |
|---|---|---|
| B0 | AS2-B0-Respawn (50), -SpacePod (30), -MoonDropPod (40), -SARSpawner (40) | 0–5 |
| B1 | AS2-B1-ClaimSpawner (50), -MaintenanceSpawner (40), -SecuritySpawner (40), -SurveySpawner (40) | 5–40 |
| B2 | AS2-B2-CampSpawner (60), -ServicesSpawner (40), -ProcessingSpawner (40) | 40+ |
| B3 | AS2-B3-MineSpawner (40), -ResearchSpawner (10), -ProcessingSpawner (10), -CampSpawner (40), -TownSpawner-1/2/3 (40 each) | 60+ within 10 km |

### Wasteland Encounters V2 (SCRP, 9 owned by SPRT)

82 medium installations on EarthLike and the Moon, no threat check, weights 2–8: [NPC Outpost] New Horizons, FOB Medium, FOB small, Communication station; [NPC Wreck - Dusty] Tempest MK1, Ceverus, MPC-2, MPC-3 Riot RAM, MPC-4, Ketar MK1, Blood Star, Liberty, Skywarden, Roach, Buzzard, Aggressor, Mantis MK2P3, Prowler, Raptor, Thornback Ray, Jaeger, Javelin, Uppercut, Valkyrie, Corvette 1, Corvette 2, Guardian, Gunboat, Sabretooth, Sentinel, Turtle, Vanguard, Zephyrus, EVE Tempest, Habitat 1, Lioness, Loki, Mantis MK3P1, Raptor1.4, Tortoise, Undercut, Land Lord XL, Widowmaker; [NPC Wreck] Factory 1, Factory 2, Crusader, Assault Ship, Doodle bug, Gnat, Mule, Medium Housing Block, Medium Housing Block 2, Small Housing Block, Tall Housing Block, Tall Housing Block Damaged, A-200 Scarab, LX-22 Ravager, LX-40 Gazelle, LX-50 Cutter, LX-100 Bastion, S-20 Reliant, S-40 Prospero, S-400 Carack, X-300 Scutum, Antaios, A-50 Clipper, A-100 Albatros, B-2 Kaiman, S-1 Petrel, S-2 Kite, S-3 Rapier, S-4 Gladius, S-50 Taurus, S-150 Pavo, S-500 Themis, S-600 Ilios, S-700 Scylla, X-20; Odin, Kratos, Office V1, Office V2.

### Abandoned Mining Facility

MES Abandoned Mining Facility: RE S, weight 10, 5 % chance, brings its own voxels.

## Appendix B: vanilla planetary encounters

From `Content/Data/SpawnGroups_PlanetaryEncounters.sbc`, as MES uses them: small installations, weight 1, only on the listed terrain. The outposts go to a random NPC faction of their type (builder, miner, trader, military or pirate), the wrecks to SPRT.

| Planet | Groups |
|---|---|
| EarthLike, most surfaces | P1 Supply Post, P2 Storage Depot, P3 Manufacturing Facility (builder); P4 Processing Plant, P5 Prospecting Station, P6 Ore Processing Facility (miner); P7 Field Research Outpost, P8 Shipping Platform, P9 Regional Headquarters (trader); P10 Military Barracks, P11 Vanguard Outpost, P12 Military Watchtower (military); P13 Pirate Relay Post, P14 Salvage Yard, P15 Pirate Stronghold (pirate); P28 Logistics Post, P29 Wrecked Hopper, P30 Abandoned Farmstead |
| EarthLike, snow | P16/P18/P20/P22 frozen rover, shuttle, explorer, colony ship; P26 frozen hauler wreck |
| EarthLike, sand and soil | P17/P19/P21/P23 rusted rover, shuttle, explorer, colony ship; P26 rusted hauler wreck |
| Moon, moon soil | `_Moon` versions of P1–P12; P27 Trade Runner Wreck |
