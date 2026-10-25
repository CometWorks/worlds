# Octahedron server plugin

A Magnetar server plugin with the rules only the Octahedron world uses, and
experiments that move to Essentials or another public plugin once they prove
generic. It lives next to the world and has no repository, hub entry or release
of its own: a server compiles it from a checkout of this repository.

Server only. It was made from `server-plugin-template`, without the client
project and the template's shared plumbing: PluginSdk provides the config,
logging and chat commands.

## Rules

### Economy stations

Every economy tick (`EconomyTickInSeconds`, 600 s by default) the game refills
each NPC trade station grid. It fills reactors with fuel, charges batteries,
fills gas tanks, adds 10,000 ice to every gas generator, and fills turrets with
ammo. It also puts the station type's loot (tools, datapads, Space Credits) into
every empty cargo container and cryo chamber, and hides them all from inventory
screens. Anyone could empty a shared container and find new loot after the next
tick.

With `StationRefillEnabled` on, the plugin's refill replaces the game's. Each
part has its own switch, and loot goes only into the cargo containers and cryo
chambers named `StationLootContainerName` (case-insensitive). Only those are
hidden; other containers keep their setting. With the name empty, nothing gets
loot or is hidden. Off, the game's own refill runs unchanged.

On a cluster the economy tick runs only on the World Authority, which has no
grids, so the game never refills a station there. Each node runs the refill
itself for the stations it simulates: once when it starts, then every
`EconomyTickInSeconds`. With `StationRefillEnabled` off, that is the game's own
refill.

### Refill containers

`RefillContainers` keeps blocks stocked with fixed items, independent of the
economy. Each rule names the blocks (case-insensitive), the seconds between
refills, and the items with the minimum amount of each. Every interval, each
matching block with an inventory (a cargo container, a reactor and so on) is
topped up to those amounts; items above the minimum and other items stay as
they are. A matching battery is charged full; its rule needs no items. Only
blocks owned by an NPC are refilled, so players can't name their own after a
rule and farm it.

Items are written as `Type/Subtype`, for example the Elite tools
`PhysicalGunObject/Welder4Item`, `PhysicalGunObject/AngleGrinder4Item` and
`PhysicalGunObject/HandDrill4Item`, the bottles `OxygenContainerObject/OxygenBottle`
and `GasContainerObject/HydrogenBottle`, or the suit battery pack
`ConsumableItem/Powerkit`. An unknown item is logged once and skipped. Bottles
are added full, and bottles of a rule's item that aren't full are removed from
the container at each refill.

### World edge

The world is a sphere: `WorldEdgeCenter` and `WorldEdgeRadius` (Octahedron: EarthLike's
center, the world origin, and 500 km). From `WorldEdgeBrakingRadius` (450 km) outward,
the outward component of every grid's and character's velocity is capped. The cap is
`WorldEdgeBrakingSpeed` (360 m/s, the world's top speed) at the braking radius and falls
to zero at the world radius:

    cap = WorldEdgeBrakingSpeed * ((WorldEdgeRadius - distance) / (WorldEdgeRadius - WorldEdgeBrakingRadius)) ^ WorldEdgeBrakingExponent

The exponent 0.5 gives a constant deceleration, about 1.3 m/s² with these values: a
ship at 360 m/s stops just at 500 km, a ship at 100 m/s starts braking 3.9 km before
it. The inward and sideways components are left alone, so a ship can always turn
back. Beyond the world radius the cap turns negative and pulls a body back, at 1 m/s
per meter beyond it and at most 10 m/s; a ship thrusting outward at the edge settles
within about a meter of it.

Nothing is deleted. The rule doesn't use the game's `WorldSizeKm` or
`MySession.IsInsideWorld`: the world keeps `WorldSizeKm` at 0, since above 0 the game
also kills characters and deletes grids that drop out of a Havok broadphase. On a
cluster each node brakes the grids and characters in the partitions it owns.

`!oct edge` (admin) lists the grids and characters in the braking zone with their
distance, outward speed and the limit there.

### Starter ship

Players don't get a respawn ship. A player who stands within `StarterShipTableReach`
(10 m) of a station's hologram table (an NPC-owned `LargeBlockConsole` projector)
switched on and with a blueprint loaded into it types `!oct ship`, or
`!oct ship <table name>` to pick one table of several, and the blueprint is built
as a ship. The stations' tables start switched off, and a projector that is off
holds no blueprint for the server.

- It must be a single grid of at most `StarterShipMaxShipPcu` (10,000) PCU, and
  of no more than `GroupPcuLimit`.
- Only a player with less than `FreeBuildPcuLimit` (10,000) PCU gets one; the
  others are told to build at the station's shipyard. It counts the blocks the
  player built, on every cluster node through the cluster plugin's PCU ledger,
  and the player's ships in Hangar, read from Hangar's storage when the Hangar
  plugin is loaded. The ledger knows only the grids some node has loaded, so a
  player's grids in offline cluster partitions don't count yet.
- It appears past the station's safe zone, `StarterShipClearance` (50 m) beyond
  its edge, on the table's side of the station if there is room there, else
  turned about the station's up. Its main cockpit (else any cockpit, control seat
  or remote control, else the grid's forward) faces away from the station and its
  up is the station's up.
- It is built as a projector would build it: empty cargo, no fuel or gas,
  nobody seated, scripts as the table got them. With `StarterShipChargeBatteries`
  its batteries are full. The player owns every block and has built it, so it
  counts toward their PCU.
- It costs nothing. A player gets one every `StarterShipCooldownMinutes` (15);
  each cluster node counts that on its own, and a restart forgets it.
- The player gets a GPS point on the ship, named after it: cyan, always shown on
  the HUD, and kept (not a temporary one). Only that player has it.

On a cluster the node that owns the station's partition builds the ship; that is
the node the player is on while standing at the table.

While starter ships are on, a blueprint loaded into a station table gets its
hologram fitted to the table by the server. The game only scales a blueprint to
fit the 2.5 m block and leaves its grid origin at the block's center. The free
space is the table block's own cell above the table's top: the cell above it is
taken, by a column on the stations. The plugin scales the whole blueprint, all its
grids, uniformly to fit that space, from `StarterShipHologramTableTop` (0.33 m
below the block's center) to the top of the block, with
`StarterShipHologramMargin` (5 cm) kept free on every side, and centers it there.
It turns the blueprint so that the up and forward of the main grid's main cockpit,
or of any cockpit, control seat or remote control, point along the table's up and
forward. A blueprint without one keeps its main grid's own up and forward. It sets
the projector's offset, rotation and scale, which the server sends to every
client. The offset moves in whole scaled cubes and the game keeps it within 50 of
them, so a blueprint longer than about 150 m may sit a little off center.
`!oct holograms` (admin) fits every station table again, for example after a
config change.

### Projection limits

The server welds a projected block without checking the world's block limits.
The game's welders check them before they ask; ToolCore's tools, the OmniArc
welders among them, don't. An OmniArc welder could print a projection with more
OmniArc tools than a tag limit like `OmniArcTools` allows, or past the player's
PCU.

With `ProjectionLimitsEnabled` on, the server checks every projected block before
building it, for the player the block will count toward (its `BuiltBy`): block
type or tag limits, PCU, blocks per player and grid size, the check the game's
welders make. A block over a limit isn't built, and the player gets a red
notification, the same one at most every 10 seconds. ToolCore takes a block's
first component from the tool's inventory before it asks, so a refused block
gives it back; the game's welders pay only for the blocks they build.

On a cluster the check counts the player's blocks on every node, through the
cluster plugin's PCU ledger. Blocks built by an NPC, like those an NPC-owned
shipyard welder welds, count toward no player and aren't checked.

Multigrid Projector builds projected blocks through the same request, so they are
checked the same way. Its own limit, `ProjectionPcuLimitPercent`, applies when a
blueprint is loaded and is counted apart from the player's PCU, so nothing is
counted twice.

### PCU per ship or station

A ship or station may have at most `GroupPcuLimit` (60,000) PCU: all grids
joined by rotors, pistons, hinges and wheel suspensions (the game's mechanical
group), not the ones docked by connectors. The game itself caps PCU only per
player. The plugin refuses, the way the game refuses a block over the player's
PCU, anything that would take a group past the cap, and tells the player in red
(the same message at most every 10 seconds):

- placing blocks by hand, one or an area of them;
- building a projected block, by any welder, the OmniArc ones and Multigrid
  Projector's builds included;
- welding a block to its functional level, where it goes from 1 PCU to its full
  PCU;
- adding a top part to a rotor, piston, hinge or suspension, and attaching a top
  part, unless both grids are in the group already;
- two merge blocks merging their grids: the two stay locked but apart;
- pasting grids, onto a grid or as new grids, per mechanical group of the
  pasted grids; a starter ship is checked by its own rule above.

The PCU of a group is what the game counts on its grids, the PCU shown in the
terminal's Info tab, so a vanilla projector's reservation (one PCU per projected
block) is part of it; Multigrid Projector doesn't reserve on the grid.

Blocks built by nobody or by an NPC, like the stations' blocks, and admins with
"Ignore PCU limit" on are not capped. A group already over the cap, pasted by an
admin for example, keeps what it has. The game reattaches the top parts it
already had, after a load for example, without the check. 0 turns the cap off.

On a cluster the partitions keep a mechanical group together, so the node that
owns a group sees all of it.

### Shipyard welding in safe zones

The station safe zones allow no welding, and each one holds the station's
shipyard. Its OmniArc welders run on ToolCore, which obeys the zone like the
game's tools do. The plugin lets a shipyard grid, a grid named `<station>
Shipyard` and owned by the Octahedron Authority NPC, weld and build projected
blocks in a zone. Every other tool stays bound by the zone: hand tools, ship
welders, ToolCore tools on players' grids, and grinding with any tool, the
shipyard's included.

The game asks a zone either about an entity, sometimes naming who acts on it, or
about a box, naming nobody. The plugin allows welding when the entity is a
shipyard grid or one of its blocks (ToolCore asks about its own grid every tick),
when a shipyard's tool asks to build a projected block (the zone check of the
grid and of the new block's box), and when a block is welded with components
from a shipyard tool's inventory. ToolCore first asks the projector whether it
can build a block, and that answer checks the block's box too. Any mod gets past
that check, since the build request after it names the tool. ToolCore takes a
block's first component before it asks to build it, so a mod tool whose request
builds nothing gets the component back, as with the projection limits.

All of it runs only after the zone refused welding. There is no switch of its
own; `Enabled` turns it off with the other rules. The server welds, and clients
without the plugin see the welded blocks.

### Shipyard orders

A player who stands by a station's hologram table with a blueprint loaded into it
types `!oct order`, or `!oct order <table name>`, and the station's shipyard builds
the ship for them, for at most `ShipyardOrderMinutes` (60; 0 turns orders off).

- It is free, so only a player with less than `FreeBuildPcuLimit` (10,000) PCU
  may order, counted as for the starter ship, and the blueprint may have at most
  `GroupPcuLimit` PCU, the cap of a ship.
- The ship must fit the shipyard's free space, the largest box between its
  welders free of its blocks, turned so that its main cockpit points along the
  shipyard. The space must be empty: a ship left there has to fly out first.
- One order per shipyard. Another player is told whose ship it builds and
  when it is free at the latest.
- The ship starts as a static grid of one of its blocks, built complete and the
  player's, centered in the free space. Next to it, outside the ship's bounds, an
  NPC-owned projector and battery named `Shipyard Order` project the blueprint
  (Multigrid Projector builds its subgrids). The components of the other blocks
  go into the shipyard's cargo containers, and its OmniArc welders are switched
  on through their terminal switches. Clients don't see the beams in a
  no-welding safe zone: ToolCore turns the tools off there on the client, which
  doesn't have the shipyard exception above (SE1-0259).
- Every projected block welded onto the ship or its subgrids is the player's,
  owner and `BuiltBy`, so it counts toward the player's PCU and the projection
  limits check it for them.
- The order ends when every block is complete, or when the time is up. The
  welders go off, the projector and battery are removed with the projection,
  what is left of the loaded components is taken out of the shipyard's
  containers and welders, and the ship, finished or not, becomes the player's
  dynamic ship. The player gets a chat message either way.

The order is kept in the world, in the projector's custom data, so it goes on
after a restart. Every 10 seconds the node that simulates a shipyard checks it,
so on a cluster the node owning the station's partition runs the order.

### Money

With `MoneyEnabled` on, the account of the stations' NPC faction, OCT
(`MoneyAuthorityFactionTag`), is real money. The game moves the balance of every
faction with stations 40 % of the way to 1e9 SC at every economy tick; the plugin stops
that for OCT. New money then comes from two places only: a subsidy of
`MoneySubsidyPerActivePlayer` (5,000 SC) per active player a day, and a top-up of
OCT's account to `MoneyOctFloor` (2M SC) whenever it drops below it, checked every
minute. A new player account starts with `MoneyStartingBalance` (25,000 SC) instead of
the game's 10,000. Player factions still start with nothing.

A player who was online at any time in the last `MoneyActiveHours` (24) hours is
active; the plugin keeps a "last seen" time for each player.

- At 00:00 UTC every player account pays `MoneyWealthTaxBasisPoints` (100, that is
  1 %) of its balance above `MoneyPlayerTaxThreshold` (200k SC), and every player
  faction the same share of its balance above `MoneyFactionTaxThreshold` (1M SC). NPC
  identities and NPC factions pay nothing. The tax is held in a pool, outside any
  account.
- At the top of every UTC hour the plugin pays out an even part of the held tax (what
  is held divided by the hours left in the day, so the last hour pays the rest), a
  24th of the daily subsidy per active player, and OCT's balance above `MoneyOctCap`
  (20M SC). The sum is split equally between the active players; what the division
  leaves stays held. An hour with nobody active pays nothing: the tax stays held and
  OCT keeps its balance.

The server log gets a line for every payout, one for the day's tax (from players and
from factions) and one when a day closes with its totals: what was paid out in how
many payouts, split into tax, subsidy and OCT's part, OCT's top-ups, the tax still
held, OCT's balance and the money in all accounts. All of it is integer math in SC
with overflow checks; an overflow stops the money rules before any balance changes. A
server that was down collects one day's tax and makes one payout when it comes back,
however many it missed.

The first time the rules run in a world, OCT's balance is cut to its cap without
being paid out: the world starts OCT with the game's 1e9 SC.

The rules run where the bank accounts are kept: on a plain server, or on the World
Authority of a cluster, whose bank the nodes forward every change to. The plugin must
be loaded there too, as a Quasar cluster does. The state (the tax day, the last
payout hour, the held tax, the day's totals and when each player was last online) is
a world variable (`Octahedron-Money`) there, so it is saved with the accounts and goes
back with them when a save is restored. The new account balance applies on every
node.

`!oct money` (admin) shows OCT's balance, the caller's, the money in all accounts and
the state. `!oct payday` (admin) makes the hour's payout at once, `!oct taxday`
collects the day's tax and closes the day at once. On a cluster node they answer that
the rules run on the World Authority.

### Container drops

The game's drop pods (world setting `EnableContainerDrops`) come in two kinds.
Each drop a player is due is a personal "Unknown Signal" (`Container_MK-1` to
`26`) only that player sees, or, in multiplayer with another player online, a
"Strong Unknown Signal" (`LargeContainer_Mk-1` to `15`) everyone sees, with a 5 %
chance. The personal ones spawn wherever the players fly, and on a cluster they
chain partitions together.

With `ContainerDropsPersonal` off (the default) the game still rolls the kind,
but a personal drop finds no spawn position, so the game counts it as not
spawned and logs nothing else; the plugin logs `Personal container drop
(Unknown Signal) refused`. Strong drops are left alone. On a cluster the World
Authority decides who is due a drop and the node that owns the player spawns it,
so the rule acts there.

`!oct drop` (admin) spawns a personal drop for the caller the way the game's
timer does, `!oct drop strong` a strong one, so the rule can be checked by hand.

### MES spawn pattern

With `MesGateEnabled` on, the plugin patches Modular Encounters Systems (MES, Workshop
1521905890) at runtime so that its NPC spawns leave room for the cluster's
partitions. This is MesCluster (CometWorks/mod-mes, DF-582), folded into this plugin
(DF-706); it contains no MES code, since MES may not be republished modified, and the
world keeps loading MES from the Workshop.

MES spreads NPC grids evenly along the players' flight paths. On a cluster those grids
chain into huge partitions that can't be split, balanced or taken offline separately.

- Spawn pattern. Space is cut into Voronoi cells around jittered lattice sites
  (`MesCellSize`, 40 km, `MesJitter` 0.5, `MesSeed` 1321). Only the core of a cell is
  spawnable: the cell shrunk by half the gap from every face. Cores are at least the
  gap apart (`MesGap`, or `MesGapSyncDistances` 4 x the world's sync distance when it
  is 0: 12 km at a 3 km sync distance, the cluster's partition split distance), so the
  NPCs of one core form partitions of their own. Cores are convex, so a cargo ship
  whose path starts and ends in one core stays in it.
- Density limits. No spawn where `MesMaxNpcGridsInRadius` (12) NPC grids are already
  within `MesDensityRadius` (15 km), and none once there are `MesMaxClusterNpcGrids`
  (300). On a cluster that publishes its NPC census (`CometWorks.Cluster.NpcCensus.v1`,
  DF-611) the counts are cluster-wide. The density count includes offline partitions,
  so a returning player meets the old pocket instead of a new one. The cluster cap counts
  only the NPC grids online on a node (`CometWorks.Cluster.NpcCensus.Online.v1`,
  DF-712): offline partitions keep their NPC grids, and counting them filled the cap
  within an hour in cluster run 4. Without the census the counts cover this server.
  The 10-minute spawn summary logs the census totals (all and online).
  An accepted spawn counts for 30 s before its grids show up.
- How. A Harmony postfix on MES's `PathPlacements.GetSpawnPlacement` checks the
  placement MES computed. If it doesn't fit, MES computes up to
  `MesPlacementAttempts` (16) more, with all of its own obstruction checks, and the
  first that fits is used; if none does, MES skips the spawn. Static encounters are
  left alone, and so are the spawning types in `MesExemptSpawnTypes` (creatures).
- Guard. Before patching, the plugin checks the MES version (`MesAcceptedVersions`)
  and a hash of the instructions of the MES code it relies on
  (`MesAcceptedMethodHashes`). Every start logs both. On a mismatch it logs a
  Critical line and MES runs unpatched. After reviewing an MES update, add the new
  version and hash to the config.
- The cluster's NPC sweep reads the gap bands from `CometWorks.ModMes.InGap.v1`, which
  the plugin publishes, and removes unvisited NPC grids there sooner.

The pattern settings (`MesCellSize`, `MesGap`, `MesGapSyncDistances`, `MesJitter`,
`MesSeed`) are read once, when MES is patched: every node of a cluster must use the
same ones, and each logs them with a pattern fingerprint, which must be equal on all
nodes. The other options apply at once. Each spawn the gate decides logs
`spawn placed` or `spawn refused`, and a summary every 10 minutes.

### Encounters

On hold since 2026-10-24 (SE-653): the world runs MES and its encounter mods again,
and `EncountersEnabled` is off. The code, blueprints and tests stay for when the work
resumes (SE-654: the game's AI blocks for the NPC ships, SE-655: MES's behaviors).
Don't switch it on in a world that still loads MES: both would spawn.

With `EncountersEnabled` on, the plugin spawns the NPC encounters that Modular
Encounters Systems (MES) and its 12 encounter mods spawn in Octahedron, by MES's
rules, from blueprints shipped in `Encounters/`. The server spawns the grids and
clients get them like any other grid, so players need none of those mods.
`../MES.md` describes the rules as MES applies them; the plugin follows that
document, with the differences listed below.

`Encounters/` is written by `tools/export_encounters.py` from the mods in the local
Workshop folder: `SpawnGroups.xml` holds the 320 spawn groups the MES timers could
spawn, with their spawn conditions and prefabs, `Blueprints/` one gzipped blueprint
per prefab (936), and `Factions.xml` the mods' 11 NPC factions, which
`../build_world.py` writes into the world. Rerun the script after an encounter mod
changes.

Every blueprint has only blocks the base game defines, so no grid spawns with a
block swapped for another or missing. A blueprint loses MES's three service
terminals (research, suit upgrade and shipyard), which work only with MES, if every
grid stays in one piece without them. A prefab with any other non-vanilla block is
left out: MES's proprietary reactors, NPC thrusters and RivalAI remote controls, and
other mods' blocks (shields, modded weapons, desks and couches saved as remote
controls). That drops 47 prefabs, and the 41 spawn groups whose main prefab is one of
them, among them 10 of the 13 Trade Operators Coalition groups (their stations have
proprietary reactors) and 26 of Wasteland Encounters' 82 wrecks.

What it does, as MES does it with its default settings and Octahedron's:

- Each online player has timers, stepped every 10 seconds. The player's place picks
  the spawn type: in space, space cargo ships (780 s after joining, then every
  780–1020 s), random encounters (checked every 60 s once the player is 15 km from
  the last check, 300 s apart) and space bosses; in gravity up to 6 km above the
  ground, atmospheric cargo ships, planetary installations (checked every 60 s once
  the player is 6 km from the last one, 300 s apart) and planet bosses; higher in
  gravity, gravity cargo ships. Bosses are tried every 1200 s. Lunar cargo ships stay
  off.
- A spawn group is eligible when one of its conditions allows the spawn type and the
  place: planet whitelist and blacklist, air for the groups that need it, maximum
  gravity, the threat score of the player grids around, the player's reputation with
  the owner faction, world variables (the Xenvakian `Mod-Enable`), unique encounters
  that spawned before, the terrain type for the base game's outposts, and the
  distance to the Trade Operators Coalition's stations. Its chance tag is rolled.
  The owner faction must exist in the world.
- The spawn picks a mod first, weighted 1, 2 or 3 by its number of eligible groups,
  then one of its groups weighted by frequency. Installations first roll their size:
  small, medium at 15 % plus 15 % per small one on the planet, large at 5 % plus
  15 % per medium one.
- Placement, caps and timeouts are MES's: cargo ship paths 10–15 km long past the
  player, outside gravity, clear of grids and asteroids; random encounters 8–12 km
  away; installations 3–6 km away (farther for medium and large ones) on ground
  level within 2.5 m; at most 15 space cargo ships within 15 km, 10 encounters
  within 25 km, 10 installations within 15 km and 2 planetary cargo ships within
  20 km; at most 4, 2, 2 and 2 spawns of a type within 900 s around a place; no
  spawn at all while the node holds 100 encounter grids.
- Cleanup: an encounter grid with no player within 15 km for 900 s is removed, and
  a player coming back within 15 km starts the time over. The groups MES tags
  `IgnoreCleanupRules` (Abandoned Settlements, some Wasteland outposts) stay.
- A boss starts as a red GPS signal 6–8 km away for every player within 25 km; the
  boss spawns 2.5–4 km from it once a player comes within 300 m. The signal goes
  after 1200 s.
- The grids go through the game's prefab spawner with the options MES uses, owned
  by the faction's founder. The plugin marks each spawned grid in its storage, which
  is what the caps and the cleanup go by. A removal takes the grid and the encounter
  grids joined to it by rotors, pistons or hinges, not ships docked to it.

What MES did that the plugin doesn't:

- MES's AI behaviors (RivalAI): the NPC ships' combat, patrols, retreats, chat,
  reinforcements and despawn timers, the Abandoned Settlements spawner grids that
  build a settlement around them, and the groups only other NPCs spawn. A cargo ship
  flies to the end of its path on the game's autopilot when it has a remote control,
  else it drifts there with its dampeners off, and leaves once nobody is within
  1.2 km of it there. Turrets fire on their own as in the base game.
- MES's grid changes at spawn: weapon randomization, block replacement, armor to
  modules, damaged wrecks, NPC thrusters, faction colors and names. The grids
  spawn as their prefabs are.
- The Xenvakian probes set `Mod-Enable` when they spawn; the Final Boss doesn't clear
  it when destroyed.
- Counters live on each node: the timers, timeouts, cleanup times and boss signals
  start over after a restart. Unique encounters and the installation counters are world
  variables and stay.

`!oct encounters` (admin) shows the place the caller stands in, the threat score
there, the number of eligible groups per spawn type and the encounters spawned
nearby. `!oct encounters <type> [count]` makes that many spawn attempts of a type
there without spawning anything and lists each mod's share, to compare with the
shares in `../MES.md`. `!oct encounter <type> [spawn group]` (admin) spawns one
there without caps, timeouts or chance rolls.

The 780 s first cargo ship, the 100 grid cap and the cleanup are the settings MES had
in Octahedron for the cluster (`../MES.md`, "MES on a cluster"). As MES did, each
node spawns for the players it simulates, and counts and cleans up the encounter
grids it holds, measuring distance to its own players. A grid's cleanup time starts
over on a node it moves to.

## Loading it on a Magnetar server

Magnetar compiles the plugin from source at startup when it is registered as a
local dev folder. In the server's Magnetar config folder (`-config`, the one
holding `Sources` and `Profiles`):

1. `Sources/sources.xml`, under `LocalPluginSources`, points at this folder and
   its manifest:

   ```xml
   <LocalPlugin>
     <Name>octahedron</Name>
     <Folder>/srv/worlds/octahedron/plugin</Folder>
     <File>Octahedron.xml</File>
     <Enabled>true</Enabled>
   </LocalPlugin>
   ```

2. The profile in use (`Profiles/Current.xml`) enables it by the manifest's
   `<Id>`:

   ```xml
   <DevFolder>
     <LocalFolderConfig>
       <Id>F7FB710B-7B41-45D6-B05C-A01DDE0FEE3A</Id>
       <DebugBuild>false</DebugBuild>
     </LocalFolderConfig>
   </DevFolder>
   ```

3. `../config/octahedron.cfg` goes into the server's instance folder (the
   Dedicated Server's `-path`, where `SpaceEngineers-Dedicated.cfg` is) as
   `Octahedron.cfg`, copied or symlinked. The plugin only reads it; a
   change made in Quasar's config editor lasts until the server restarts.

Magnetar compiles the `.cs` files `git ls-files` lists in the folder (tracked
and untracked, not ignored) under the manifest's `SourceDirectories`. Without
`git` it logs a warning and takes every `.cs` file outside `bin` and `obj`.
Updating the checkout and restarting the server deploys a new version: Magnetar
recompiles when the sources change.

`!oct reload` (admin) rereads the config without a restart.

`OCTAHEDRON_CONFIG`, when set, names the config file instead, for example the
checkout's `config/octahedron.cfg` on every node of a cluster run by the
cluster CLI.

The manifest names `Encounters` as the plugin's asset folder, and Magnetar tells the
plugin where it is: for a dev folder, the checkout's own `Encounters`. A Quasar
cluster's preparation (`-prepareManaged`) exports it with the build.
`OCTAHEDRON_ENCOUNTERS`, when set, names the folder instead.

## Loading it on a cluster

A Quasar cluster loads it as a dev folder too, with Magnetar 2.4.3.4 or later.
On the Quasar host, add this folder of a `worlds` checkout, with `Octahedron.xml`,
as a dev folder (Configs, Developer panel) and select the plugin in the
cluster's config profile. Cluster setup and every profile staging compile it
once, as a release build, and pin it to the checkout's HEAD commit. The nodes
run that build and need no checkout. Preparation fails if the checkout has
modified, staged or untracked files. To deploy a newer commit, update the
checkout, choose **Stage profile changes** for the cluster, then apply the
pending update from Maintenance. Quasar's content update monitoring doesn't
see new commits of a dev folder.

On a Quasar cluster the plugin gets its config from Quasar, not from a file:
set it in the cluster's plugin configuration editor to the values in
`../config/octahedron.cfg`. `OCTAHEDRON_CONFIG` and `Octahedron.cfg` in the
instance folder are ignored there.

The cluster CLI (`se1/cluster`, also the test bench) loads compiled local
plugins only. For it, `package.sh <folder>` builds
`<folder>/Octahedron/Octahedron.dll` with an `Octahedron.xml` that names this
repository and the commit it was built from, the layout of a Magnetar local
plugin. The checkout must be clean.

With the cluster CLI the plugin runs on the regular nodes; the World Authority
simulates no grids:

```bash
CLUSTER_EXTRA_PLUGINS=node:/path/to/out/Octahedron python3 cli/cluster.py \
    --node-env OCTAHEDRON_CONFIG=/path/to/worlds/octahedron/config/octahedron.cfg \
    --node-env OCTAHEDRON_ENCOUNTERS=/path/to/worlds/octahedron/plugin/Encounters <command>
```

## Tests

`tests/rig.py` runs an isolated Magnetar server with a copy of the world, the
mods and asteroids left out, and this working copy as a dev folder (or a
`package.sh` bundle as a local plugin), plus a headless client with the Remote plugin that joins it. Ports, ids and folders
come from a test slot of the workspace (`se/testing/parallel-test-work`). See its
docstring.

`tests/test_client.py` checks the rules with them: grids flying out of the
world (`tests/edge_grids.py`) and the character on its jetpack, the starter
ship from a blueprint loaded into the hologram tables, and its limits with a
ship in Hangar (a Hangar working copy runs on the server), and the PCU cap per
ship: pasting, a rotor base's new head and placing a block, each refused at the
cap and allowed under it.
`tests/check_save.py` then reads the saved world: where the edge grids ended up,
the refilled blocks and the starter ships. `tests/test_projection_limits.py` runs
the server alone, with OmniArc and ToolCore: an OmniArc welder welds a projection
of more light armor blocks than the world's limit, with the check off, on with
Multigrid Projector and on without it, and the PCU cap per ship stopping it.
`tests/test_group_pcu.py` runs the server alone too: a ship welder welding
blocks to their functional level, two grids merging and a rotor attaching a
head, each stopped by the cap, next to a copy built by nobody that isn't.
`tests/test_shipyard_zone.py` restricts the zones as the world does, then checks
with the client that Alpha Station's shipyard welds a projection, while in the
same zone a player's hand welder and grinder, ship welder and grinder, and an
OmniArc welder on a player's grid named like a shipyard do nothing, and that the
player's tools work outside the zone. It runs with the plugin's rules on, on
with the zones holding every test grid from the start, and off.
`tests/test_shipyard_order.py` orders a ship at a station table with the client
and checks the saved world after the shipyard finished it, then lets a bigger
order run out after a server restart; with `OCT_SHOTS` it also takes
screenshots of the welding from among the welders.
`tests/test_free_build.py` checks the free-build limits at their defaults with
the client and a ship in Hangar: a starter ship blueprint over 10,000 PCU is
refused, and a player who reaches 10,000 PCU gets no more starter ships or
shipyard orders.
`tests/test_money.py` checks the money rules with the client: OCT cut to its cap at
the first run, a new account's balance, no economy top-up of OCT while the rules are
on (and the game's with them off), the tax by `!oct taxday`, two payouts by
`!oct payday`, OCT topped up to its floor, the day's totals and the state read back
after a restart. `OCT_WORLD_SOURCE` points the rig at a world built by
`build_world.py` instead of `octahedron/server`. `tests/money/MoneyTests` checks the
arithmetic of the tax and the payout alone
(`dotnet run -c Release --project tests/money/MoneyTests`).
`tests/mes/` holds MesCluster's tests for the MES spawn gate:
`CellPatternTests` checks the cell pattern against a brute-force definition
(`dotnet run -c Release --project tests/mes/CellPatternTests`); `ds/run.py PLAN [cfg]`
runs a standalone server with MES, a few encounter mods and a probe plugin that fires
MES spawn requests, and `ds/check.py gate|guard` judges it; `cluster/mes_cluster.py`
runs the same with the cluster's NPC census on a local test cluster;
`check_placements.py LOG...` checks the gate's decisions in any server logs.
`tests/test_encounters.py` checks the encounters with a client that has no mods:
the timer's first space cargo ship, each spawn type spawned by command in space, on
EarthLike, high above it and on the Moon, the client seeing the spawned grids, and
the mods' shares of 2,000 spawn attempts per type and place.
`tests/test_hologram.py` takes screenshots of fitted holograms, a blueprint with
a cockpit and one without, from the side, the front and above, on a bench of two
tables in open space; it needs a rendering client.

## Building locally

Only needed for an IDE or to check that it compiles; servers compile it
themselves.

```bash
dotnet build ServerPlugin/ServerPlugin.csproj
```

The Dedicated Server and Magnetar folders are auto-detected as in the template;
override them in `Directory.Build.props.user`.
