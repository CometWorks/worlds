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
itself for the stations it simulates: at once when a station comes to the node,
whether at its start or with a partition it gets later, then every
`EconomyTickInSeconds`. With `StationRefillEnabled` off, that is the game's own
refill.

### Station connectors

With `StationConnectorsTrade` on, the connectors of every trade station are kept
trading and shared with nobody, not even the station owner's faction, which every
player is in (OCT faction). A ship docked to a
trading connector gets no gas or conveyor access from the station, and no power
from the game (see Station charging). Before,
the connectors were shared with everyone, so a player could open one's terminal,
turn trading off and charge and fuel a ship from the station for free. Every 5
seconds each connector found otherwise is put right, and a ship docked to one that
didn't trade is let go first. Players dock just the same: that needs no access to
the connector. On a cluster each node does this for the stations it simulates.

### Station connector release

With `StationConnectorRelease` on, the plugin decides when a station's connector
lets go of a docked ship. The game's own auto unlock let go of every ship 10
minutes after it docked, whoever was aboard; the plugin sets it to 0 on every
station connector it finds. Every `StationConnectorCheckSeconds` (60) it looks
at each docked ship:

- A player who is online, inside the station's admin safe zone, and is one of
  the ship's biggest owners or in a biggest owner's faction keeps it docked.
  OCT, which every player is in, doesn't count as a shared faction.
- With none of them there, a countdown of `StationConnectorCountdownSeconds`
  (600) starts, and the ship is let go at its end. One of them coming back stops
  it; leaving again starts it over.
- While no connector of the station is free, the ship docked there the longest
  gets the countdown even with its owners there, and its biggest owners online
  are told by chat. A connector freeing up stops it again.

The biggest owners are those of the ship's largest grid. Both the docked time
and the countdown count simulation time, and the connector keeps them in its
storage, so they are saved with the world and go with the station to another
cluster node in a handover. A node that just got a station counts its first
check from zero. On a cluster each node does this for the stations it simulates.

### Station charging

With `StationDockCharging` on, a ship docked at a trade station's trading
connector charges from the station. The game passes no power through a trading
connection: the grids keep separate resource distributors, and joining them would
open the station's conveyors and terminal too. So the plugin moves the energy
itself, once a second. Each battery of the docked ship that is on and not set to
discharge gets its input power (12 MW for a large battery) until it is full. The
station pays for it with uranium from its working reactors, at the rate they burn
it, and then from its batteries.

The station has a fuse. While the docked ships want more power than the station
has to spare (its sources' maximum output less what its own blocks draw), the ship
that wants the most stops charging, then the next, until the rest fit. The ship's
owner gets a chat message. Charging stays off for that ship until every connector
of the station that held it lets go; docking again resets the fuse. A ship's
grids on rotors, pistons or its own connectors count as one ship. On a cluster
each node charges at the stations it simulates.

With `StationDockHydrogen` on (SE-1387), the docked ship's H2 tanks are filled
too, but not for free. Once the tanks that are on lack 1 % or more of their
capacity, the plugin fills them and bills the ice a gas generator would need for
that hydrogen at the station's Store price:

    SC = ceil(liters / ShipyardHydrogenLitersPerIce x ice price), at least 1

with 20 L of H2 per kg of ice, the vanilla H2/O2 generator's ratio, and the ice
price OCT asks at that station's Store, in whole SC per kg. A full small H2 tank
of a large grid, 1,000,000 L, is 50,000 kg of ice: 150,000 SC at 3 SC per kg. The
owner of the ship's biggest grid pays OCT and gets a chat message. Short of money,
the tanks get what the balance buys, the whole balance, and the owner hears so
once per docking. Without ice for sale at the Store there is no hydrogen. Only the
money moves: the Store's ice stock stays. A tank turned off gets nothing, so a
player can opt out. Shipyards are no stations: a ship docked at a shipyard
connector gets neither power nor hydrogen from the plugin.

### Station patches

A kept world's stations change in place through station patches (SE-1234,
policy note SE-1238). A patch names a station or shipyard grid and a few ops by
grid cell:

| Op | Does |
| --- | --- |
| `set:X,Y,Z:Expected:enabled=0` (or `1`) | turns the block off or on |
| `set:X,Y,Z:Expected:name=Text` | renames it |
| `set:X,Y,Z:Expected:data=Text` | sets its Custom Data |
| `del:X,Y,Z:Expected` | removes the block |
| `add:X,Y,Z:Type/Subtype:Forward:Up[:Name]` | adds a block with its min at the cell, owned and shared like the grid's main owner's blocks, in the color of a block next to it |
| `swap:X,Y,Z:Expected:Type/Subtype` | replaces the block in place, keeping its orientation, name, owner, sharing, color and skin |

Ops are separated by `;`. `Expected` is the block the cell must hold, as
`Type/Subtype` or `Subtype`; any cell of a larger block names it. Directions are
`Forward`, `Backward`, `Left`, `Right`, `Up` and `Down`. A name or Custom Data
can't contain `;`, and the name of an added block can't contain `:`. A patch id has letters, digits,
`.`, `_` and `-`.

The plugin checks every op before it applies any, each seeing the grid as the ops
before it leave it, and refuses the whole patch with the first problem:

- a cell that doesn't hold the expected block, or a setting the block doesn't have;
- an added block on a taken cell, of an unknown definition or another grid size,
  or with no mount neighbor once the patch is done (it would split off at the
  next removal);
- a patch that removes a block and would leave the grid in pieces: at any
  removal the game splits off every group of blocks not joined to the largest by
  mount points, so islands that were there before must go or be joined back by
  the same patch;
- removing a connector a ship is docked to;
- changing a shipyard's cargo containers, connectors, rotors or welders while it
  has an order, running or queued.

Then it applies the ops on the game thread through the game's raze and build
success paths the shipyard uses (the station's safe zone refuses the game's own
block removal), and adds the id to the grid's ledger, a storage key saved with
the grid. A patch the ledger has is skipped, so it applies once, wherever the
grid goes in handovers, restarts and cluster stops. Each applied patch logs a line
`Station patch APPLIED <id> on <grid>` with the block-list diff, and the admin gets
the same diff as the reply.

- `!oct patch apply <id> "<grid>" "<ops>"` (admin) applies a patch now, on the
  process that simulates the grid; on a cluster that is the node holding the
  station.
- `!oct patch check <id> "<grid>" "<ops>"` runs the checks only.
- A chat command has 200 characters at most: split a longer patch into several,
  or stage it.
- `!oct patch status "<grid>"` answers the block count, a hash of the block list,
  the ledger and the islands.

A grid can also be replaced by a new blueprint, in place. The plugin compares the
blueprint the grid was built from with the new one, by block min cell, and turns
the differences into ops. It compares the two blueprints rather than the live grid,
so what the game changed since (charge, inventories, timers, the plugin's panels)
is left alone:

- a block only in the old blueprint is removed, one only in the new one added, one
  of another type or orientation removed and added;
- a block whose saved settings differ is built again from the new blueprint where it
  is: terminal properties, LCD texts and other components, color and skin, and the
  mod storage keys of a mod's blocks or settings. The rebuilt block keeps the live
  block's owner, inventories, timers and Store offers, and the mod storage keys the
  new blueprint doesn't change;
- a block whose name, Custom Data or on/off alone changed is set in place, keeping
  its entity;
- the grid's `DestructibleBlocks`, `Editable`, `Immune` and `IsRespawnGrid`, and its
  own mod storage keys, are set in place. A blueprint that changes the grid's name,
  size or static flag is refused.

Each op is checked against the live grid like any patch: the cell must hold the old
blueprint's block, turned the same way, with its min there. On top of that a block
can't be removed or rebuilt while a subgrid hangs on it, or while a player's blueprint
is in a hologram table. Grid and block entity ids stay, except for the rebuilt blocks,
so faction station entries, partitions, the market (kept by station name on the World
Authority) and the shipyard's queue don't change. Only the blueprint's largest grid is
compared; subgrids are left as they are.

- `!oct patch diff <id> "<grid>" <from.sbc> <to.sbc>` checks a replacement and lists
  its ops; `!oct patch replace` with the same arguments applies it. The blueprint
  files are found next to the config file, else in the world's `Storage/Octahedron`
  folder, unless their path is absolute.
- A staged entry with `From` and `To` instead of `Ops` replaces the grid the same way
  when a process loads it.

The plugin builds added and changed blocks from `To` as they are in the file, so
both blueprints must be prepared the way `build_world.py` puts that grid into a
world: the station's color, the names, share modes and "Anyone can use" it sets,
emptied programs and inventories. Raw blueprints would bring blocks in unprepared.
`station_patch.py` makes the pair. `From` is the blueprint and `build_world.py` of
the commit the live world was built from, `To` those of the working tree, so a
change to the preparation becomes part of the patch too:

```sh
python station_patch.py 2026-12-station-v2 "Beta Station" --since 04f52f6
python station_patch.py 2026-12-station-v2 --stations --since 04f52f6
```

It writes `2026-12-station-v2-from.sbc` and `-to.sbc` to `config/storage/Octahedron`
and prints the staged entry. Each station needs its own pair: the preparation depends
on the station's name. `--stations` makes one for every station built from the station
blueprint, the id ending in the station's first word (`2026-12-station-v2-beta`). A
grid named `<station> Shipyard` is that station's shipyard. Both files keep only the
main grid and the blocks that differ between them, which is all the plugin compares,
so a pair is a few kB. `config/storage` goes into a new world's `Storage` folder, and
Deploy Octahedron stages it into a kept world's (SE-1235), where the plugin finds the
pair.

`--since` is the commit the live world was built from. A blueprint change that only
matters for new worlds (like the station connectors' `AutoUnlockTime` of SE-1237, which
the plugin sets on a kept world at run time) is better left out of a patch: diff
against a later commit, as long as the blocks the patch changes are the same there.
The deploy checks the From blocks against the cluster's source world before its stop.

`StationPatches` in the config is the staged list, shipped with
`octahedron.cfg`. Every process applies its patches, in the list's order, to the
grids it simulates whose ledger lacks them, within 5 seconds of loading them. On a
cluster that is whichever node loads the station's partition, also one that wakes
up later. A refused staged patch is logged, holds back the later patches of its
grid, and is tried again every minute. In `octahedron.cfg`:

```xml
<StationPatches>
  <StationPatchEntry>
    <Id>2026-11-lobby-light</Id>
    <Grid>Alpha Station</Grid>
    <Ops>swap:3,2,-1:LargeBlockHalfTrofferLight:InteriorLight/LargeBlockLight_1corner</Ops>
  </StationPatchEntry>
  <StationPatchEntry>
    <Id>2026-12-station-v2</Id>
    <Grid>Beta Station</Grid>
    <From>2026-12-station-v2-from.sbc</From>
    <To>2026-12-station-v2-to.sbc</To>
  </StationPatchEntry>
</StationPatches>
```

Once a process has gone through the staged patches of a grid it loaded, it logs the
grid's status and what its station answers, for the deploy's report (SE-1235):

```
Station patch STATUS Alpha Station 81057932184256363 blocks=1984 hash=… ledger=2026-11-08-lounge-lcds-alpha islands=0
Station patch ANSWERS Alpha Station: store 61 offers, market 48 items, queue 0 orders and ships
```

again after a patch applies there. A market or queue that doesn't answer yet is asked
again every 5 s, six times at most.

After a wipe the stations are built from the new blueprint, and the staged pairs
rebuild their blocks to what they already are. Entries can be dropped from the list
once every kept world they were for has been wiped.

Patches don't change grid ids, partitions or names the plugin keys on: don't
rename a station's hologram tables, buttons, panels or arm rotors with them.
Moving grids, and new or removed stations, still need a wipe (see SE-1238). New
grids that aren't stations, like lane computers, go in through world patches.

### World patches

A world patch adds grids to a kept world once (SE-1358). It's for what a station
patch can't do, since that only changes grids the world already has. The first one
adds the space elevators' four lane computers to the worlds built before them.

`world_patch.py` builds the world from the commit the live world was built from and
from the working tree, with the same seed, and writes the grids only the new one has
(by name) to a blueprint in `config/storage/Octahedron`, at their world positions:

```
python world_patch.py 2026-11-12-space-elevators --since 70fcefb --seed 2
```

`WorldPatches` in the config is the list, applied in order:

```xml
<WorldPatches>
  <WorldPatchEntry>
    <Id>2026-11-12-space-elevators</Id>
    <Grids>2026-11-12-space-elevators.sbc</Grids>
  </WorldPatchEntry>
</WorldPatches>
```

The world's ledger is the world variable `Octahedron-WorldPatches`, kept by the
server or by a cluster's World Authority. A plain server creates the grids of a
patch the ledger lacks itself, skipping any grid whose name a loaded grid already
has, and adds the id. On a cluster the World Authority, which simulates nothing,
sends the patch to the first regular node by name, always with the same operation
id. The node creates the grids, and the cluster puts each into a partition by its
position: one near a station joins the station's partition, one far from
everything gets a partition of its own. When the node's answer says APPLIED, the
World Authority adds the id to the ledger. A patch no node took is tried again a
minute later. If the answer is lost (a timeout), the node may have added the grids,
so the World Authority puts `?<id>` in the ledger and doesn't send the patch again.
It logs `World patch UNKNOWN`, and an admin checks the world, then removes `?<id>`
from the variable to send it again.

`build_world.py` writes every id of the config's `WorldPatches` into a new world's
ledger, because that world has the grids already. At its first check after a start the
ledger's keeper logs `World patch LEDGER <ids>`, and each patch it adds logs `World
patch APPLIED <id>: <n> grids: <name> <entity id>, ...` on the node and the World
Authority. Deploy Octahedron reports from those lines.

### Grid moves

A grid move turns and moves a station's shipyard of a kept world to where a world
built now has it, once (SE-1369). Neither a station patch, which changes blocks, nor a
world patch, which adds grids, can do that. The first ones turn the shipyards of Alpha,
Gamma, North and South Station about their center line, so a bottom beam no longer
hides the middle of the build space from the lobby.

`world_patch.py` writes a `GridMoveEntry` for each shipyard that has another place or
facing in the world built from the working tree than in the one built from `--since`,
and leaves out grids an earlier world patch adds:

```
python world_patch.py 2026-11-13-shipyard-turns --since 70fcefb --seed 2
```

```xml
<GridMoves>
  <GridMoveEntry>
    <Id>2026-11-13-shipyard-turns-gamma</Id>
    <Grid>Gamma Station Shipyard</Grid>
    <Position>-112119.536872,81.137552,83892.854914</Position>
    <Forward>0.957285,0.184106,-0.222958</Forward>
    <Up>-0.07497,0.902762,0.423557</Up>
  </GridMoveEntry>
</GridMoves>
```

The process that simulates the shipyard teleports it, with its rotor heads, once it
is idle: no job running or coming, no grid in its build space, none docked to it,
nobody in its bounds or where it goes, and no other grid where it goes. Until then it
logs `Grid move WAITING <id> on <grid>: <why>` and tries again a minute later. The id
goes into the grid's station patch ledger, so a grid moves once wherever it goes. A
shipyard that is where its entry puts it already, as in a world built with the move,
only gets the id: `Grid move THERE`. Each move logs `Grid move APPLIED <id> on <grid>:
moved <m> m, turned <deg> deg`, and every process logs `Grid move READY` at its first
check. Moves happen seconds after a start, before players are back after a deploy.

### Shared NPC blocks

Whoever a block is shared with can also change it: the game's ownership check
guards a block's settings as well as its use. `build_world.py` keeps a station's
store, ATM, vending machine, contract block, doors and button panel unshared and
turns on their "Anyone can use", which lets every player use them
without a terminal. A door the blueprint has off, like the engineering section's
airlocks, gets no "Anyone can use": players can neither open it nor switch it on.
The medical room, hologram table, seats and Free Tools boxes
have no such option: the game lets only those it shares them with heal there, load
a blueprint, sit or reach the inventory. So they are shared with everyone, as are
the shipyard's medical room and lights.

With `NpcBlocksLocked` on, the server refuses a change to any block an NPC shares
with everyone from any player, OCT's members too: settings and on/off, name,
custom data, LCD texts, toolbars, button names, a medical room's spawn name, the
projector's offsets, and new block groups on the NPC's grid. An admin with "Use
terminals" on (Alt+F10) is let through, as by the game. Everything else stays open:
the inventories, the medical room's healing and refills and its wardrobe, sitting in
a seat, and loading a blueprint into a hologram table and switching the table on or
off. A refused setting of a shared block is sent back to the player's client. A
refused name or text, and a refused change to a block that isn't shared, for which
the game sends nothing back, stay on that client only until it reconnects. The
server logs the first refusal of each kind per player and block. In a world built
before "Anyone can use", the store, contracts, doors and buttons are still shared
with everyone, and the option covers them too.

### OCT faction

With `NewPlayersJoinOct` on, a new player joins OCT's faction (`MoneyAuthorityFactionTag`)
when the game makes their identity: the server sends the game's join request and its
acceptance, as a scenario's medical room does. On a cluster the World Authority makes the
identity when the Gateway admits the player, so it sends them
([SE-1012](https://tasks.cometworks.se/tasks/1903)). Players relate to each other only through
factions, so this makes every player an ally of every other one and of the stations: green
markers, and turrets don't fire at them
([SE-975](https://tasks.cometworks.se/tasks/1866)). A player can leave OCT; OCT accepts no
request to join it again. Players who were in the world before keep their faction.

OCT's leaders are the players whose Steam IDs `OctLeaders` lists, Viktor on the Octahedron
([SE-1015](https://tasks.cometworks.se/tasks/1906)): they join OCT as leaders, everyone else as a
plain member. The game makes an admin who joins a faction of only NPCs its leader, so without this
the first admin to join a fresh world would lead OCT and could kick or promote players and change
OCT's settings. OCT's NPC stays its founder. A player who is already in OCT keeps their rank.

The game shares whatever a player builds with their faction. With `OctBuildsPrivate` on, a
block an OCT member places, welds from a projection or welds to working order, and a ship a
shipyard or a contract gives them, starts as No share instead. Placing a block runs on
every machine, and each one gives it faction share there, so the server follows with the
game's ownership change, which reaches every client; welding to working order sets the owner
on the server only, and the plugin changes the share before that. No client plugin is
needed. Players can still share a block with the faction themselves, which in OCT is
everyone. A pasted grid keeps the shares it was saved with. Players in other factions keep
the game's faction share. Where the plugin asks whether two players are teammates (the
shipyard's welders, contract abuse) OCT doesn't count as a team (`OctFaction`).

`tests/test_oct_faction.py` checks it in game with the client: a new player joins OCT and
their placed blocks are No share, a player who makes their own faction keeps faction share
(`--own-faction`), and with the options off nothing changes (`--no-oct`).

Only OCT's leaders can take money out of its account, so a member's deposit would be money
gone ([SE-1160](https://tasks.cometworks.se/tasks/2060)). The game's faction screen offers every
member the Deposit button, and only a client plugin could grey it out, so with `OctNoDeposits`
on the server refuses the deposit of a member who isn't a leader and tells them why. Nothing
else pays into OCT's account but the stations' stores, the fees and what the money rules move:
the `[B]` members are the trader bots, players, and what they pay is what they buy.

With `OctGridsNoGrinding` on, nobody but the owner grinds a grid of OCT's NPC or of a player
whose Steam ID `OctProtectedSteamIds` names (`first-last` ranges, comma separated). The
Octahedron leaves that list empty, so players can grind the trader bots' ships
([SE-1282](https://tasks.cometworks.se/tasks/2198)); before, it held the bots' range
`76561202000000001-76561202000009999`. A grid counts when its owner owns the most blocks on it or on
a grid it is mechanically joined to. The stations' safe zones forbid grinding anyway; this
covers OCT's grids everywhere else, so players can't take them apart for
components to sell. Every grinder takes a block apart through `MySlimBlock.DecreaseMountLevel`
on the server, a hand grinder, a ship grinder and ToolCore's tools, so the plugin refuses it
there and the player hears why. Grids of the encounters' NPCs and of other players stay open.

`tests/test_oct_protection.py` checks both with the client: the faction screen's Deposit moves
no money, a hand grinder does nothing to OCT's and a bot's hull but grinds a player's and a
pirate's, and after `!oct reload` with the options off the same deposit and grinders work.

### OCT reputation

With `ReputationEnabled` on, every OCT member has a reputation, counted in SC
([SE-1283](https://tasks.cometworks.se/tasks/2199)). It starts at `ReputationStart` (10,000) and
`!oct rep` shows it; `!oct rep add <amount> [player name]` (admin) changes it.

- **Down:** grinding, shooting, blowing up or ramming a grid that another member owns most of
  costs the value lost: the block's components at their reference prices (`MarketPrices`, else
  the game's minimal price / 10, the prices the stores and the market start from) times the share
  of the block's integrity lost. "Owns most of" is the game's `BigOwners`, the owners of the most
  ownable blocks; a grid with none, like a rotor top of armor, counts with the largest grid of its
  mechanical group. Grids shared with OCT count as their owner's. OCT's NPC and the trader bots
  are members too, so taking a station block or a trader's ship apart costs.
- **Up:** a deposit into OCT's account adds the SC paid, so 1 SC paid makes up for 1 SC of grid
  taken apart. Sharing a block with OCT in its terminal adds its value, unsharing takes it off
  again, and so does taking apart a block of your own that you share, so sharing can't be farmed.
- **Free:** grids the member owns most of, grids of no one and of other factions, and the first
  ram in `ReputationRamGraceSeconds` (an hour). A ram is the collision damage one member's ship,
  the faster of the two, does to other members' grids until `ReputationRamGapSeconds` (10) pass
  without one; its pilot pays. The trader bots (`ShipyardBotSteamIds`) have no reputation.

Below zero the member is told. Below `ReputationKickBelow` (-10,000) a countdown of
`ReputationKickAfterSeconds` (an hour) starts, with a warning at its start and at
`ReputationWarnSeconds` (30, 10 and 5 minutes) before its end. Back above, it stops and starts
over in full next time. At its end the member is removed from OCT and told why; their reputation
stays as it was. The countdown counts the time the server runs, whether the member is online or not.

The reputations live where the faction and the bank accounts do: on a plain server, or on the
World Authority of a cluster, in the world variable `Octahedron.Reputation`, saved with them. The
countdowns run there. The nodes, which see the damage, deposits and shares, send their changes
every second as a global command, which the World Authority runs once even when a node sends
it again after a timeout. So the reputation follows the player to any node and through restarts.

`OctNoDeposits` refuses a member's deposit, so with it on, paying in can't raise the reputation;
`config/octahedron.cfg` has it off since reputation is on there
([SE-1285](https://tasks.cometworks.se/tasks/2201)). A grid taken over with Essentials' `!ess claim`
changes no reputation: the claim doesn't go through the share box, and the grid is then the
claimer's own.
ToolCore's tools, like the OmniArc Destro, grind with no player at them (`identityId` 0), so the
tool block's owner pays, the way a weapon block's owner pays for its damage. ToolCore names the
tool block to the game's damage system just before it grinds, and the plugin takes it from there.

`tests/reputation/ReputationTests` checks the countdown, its warnings, the forgiven ram and the
save without a game. `tests/test_reputation.py` checks it with the client and
`tests/rep-probe`, a client plugin that sends a block terminal's share box, the ownership claim
and the faction screen's Deposit: grinding, the share, a deposit, two rams and the removal with a
20 s countdown. A second run of the server alone, with ToolCore and OmniArc, has a member's OmniArc
Destro grind another member's hull and checks that its owner pays. `tests/reputation_cluster.py` checks it on a cluster: a change on
one node reaches the World Authority once, the reputation is the same after a handover and a warm
start, and the warnings and the removal reach the player on the node they moved to.

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
are added full. Each refill first removes every oxygen and hydrogen bottle that
isn't full from the block, whatever its rule lists, then tops it up. A player
can leave an empty or partly used bottle in the Free Tools box and take a full
one.

An item can name several ids separated by commas, like
`PhysicalGunObject/Welder2Item,PhysicalGunObject/Welder3Item`. Their amounts count
together toward the minimum, and each one missing is one of them at random.

The item `Datapad/Datapad` gives out lore data pads from `DataPads` instead
(SE-1308): each one missing is a configured pad at random that the block doesn't
hold yet. Only pads whose title is in `DataPads` count toward the minimum, so a
player's own pad left in the box stays and doesn't fill it. A pad's text can
hold `{GPS:StorageName:Label}`, which becomes a GPS line (the game's data pad
screen offers "Create GPS" for it) of the center of the voxel map with that
storage name, read from the loaded world, so it always matches the world the
server runs. A pad whose voxel map the world doesn't have is never given out,
and the missing map is logged once. Octahedron's Free Tools boxes keep one pad
each, with scraps of the rare ore distribution and the 4 ice asteroids of the
ring, and a taken pad is replaced after 10 minutes (a second Free Tools rule).

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
back. A grid or character found beyond the world radius, by whatever means it got
there, is moved straight back toward the center onto the world sphere (1 m inside it),
a grid with everything attached to it, and loses its outward speed.

Nothing is deleted. The rule doesn't use the game's `WorldSizeKm` or
`MySession.IsInsideWorld`: the world keeps `WorldSizeKm` at 0, since above 0 the game
also kills characters and deletes grids that drop out of a Havok broadphase. On a
cluster each node brakes the grids and characters in the partitions it owns.

`!oct edge` (admin) lists the grids and characters in the braking zone with their
distance, outward speed and the limit there.

Jump drives keep to the braking radius as well. A jump whose target lies beyond it is
shortened to where the jump line crosses that sphere, and the ship arrives there. A
ship already beyond the braking radius may jump sideways or inward, but never farther
out than it is: its jump stops where the line crosses the sphere of its own distance,
so a jump straight outward goes nowhere and the game refuses it as too short. The
player gets a chat message either way. The server shortens the target when the jump
is requested and again when the ship arrives, which also catches jumps that scripts
and mods start, on a single server and on every cluster node.

### Starter ship

Players don't get a respawn ship. A player who stands within `StarterShipTableReach`
(10 m) of a station's hologram table (an NPC-owned `LargeBlockConsole` projector)
switched on and with a blueprint loaded into it types `!oct ship`, or
`!oct ship <table name>` to pick one table of several, and the blueprint is built
as a ship. The stations' tables start switched off, and a projector that is off
holds no blueprint for the server.

- It must be a single grid of at most `StarterShipMaxShipPcu` (10,000) PCU, and
  of no more than `GroupPcuLimit`.
- Only a player with less than `FreeBuildPcuLimit` (60,000) PCU gets one; the
  others are told to build at the station's shipyard. It counts the blocks the
  player built, on every cluster node through the cluster plugin's PCU ledger,
  and the player's ships in Hangar, read from Hangar's storage when the Hangar
  plugin is loaded. The ledger knows only the grids some node has loaded, so a
  player's grids in offline cluster partitions don't count yet.
- It appears past the station's safe zone, `StarterShipClearance` (50 m) beyond
  its edge, on the table's side of the station if there is room there, else
  turned about the station's up. Its main cockpit (else the first block that flies
  the ship, else the grid's forward) faces away from the station
  and its up is the station's up.
- It is built as a projector would build it: empty cargo, no fuel or gas,
  nobody seated, scripts as the table got them. With `StarterShipChargeBatteries`
  its batteries are full. The player owns every block and has built it, so it
  counts toward their PCU.
- It costs nothing. A player gets one every `StarterShipCooldownMinutes` (15);
  each cluster node counts that on its own, and a restart forgets it.
- An admin with the game's "Ignore PCU limits" on (Alt+F10) gets one without
  the cool-down and whatever PCU they have, as for shipyard orders below.
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
or of the first cockpit, control seat or remote control, point along the table's up
and forward. Only a block that flies the ship counts: never a cryo chamber, a
passenger seat or furniture. A blueprint without one keeps its main grid's own up
and forward. It sets
the projector's offset, rotation and scale, which the server sends to every
client. The offset moves in whole scaled cubes and the game keeps it within 50 of
them, so a blueprint longer than about 150 m may sit a little off center.
`!oct holograms` (admin) fits every station table again, for example after a
config change.
A cluster node builds a handover's grids on parallel threads, so a table's new
blueprint can arrive off the game thread. `tests/table-hologram/TableHologramTests`
calls the plugin's hook from 16 threads at once and checks that every table is queued
for fitting
(`dotnet run -c Release --project tests/table-hologram/TableHologramTests`).

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

### Target Neutrals

With `ClearTargetNeutralsEnabled` on, a new turret or custom turret controller, modded
ones too, starts with Target Neutrals off however it is built: placed by hand, as the
first block of a new grid, welded from a projection by any tool, pasted, as a starter
ship or by a shipyard. Ships and stations then shoot only at those whose reputation
makes them enemies, and players can turn their weapons off around stations.
Searchlights keep their setting, they don't shoot.

A prefab ship gets the same when it is made for a player: one bought from a store
block and a respawn ship. Ships NPCs own keep their design, like encounter ships and
wrecks.

It happens once. A player who turns the setting back on keeps it: a grid loaded from a
save or moved to another cluster node isn't built, so it isn't cleared again. Grids
already in a world keep what they have.

### No gravity drives

With `NoGravityDrivesEnabled` on (the default), artificial mass and space ball blocks
don't react to gravity generators, so a gravity drive doesn't move its ship. Characters,
loose objects and natural gravity on grids work as before.

The server also turns these blocks off. When a working one on a ship is in the field of a
working generator, on the same grid or another, all of them on that grid go off within
about 170 ms, and go off again if a player turns one back on. The generators stay on.
Nothing turns the blocks back on: they stay off when the field goes, and a player can then
turn them on, which holds until they are in a field again. Static grids keep theirs, they
can't move. The player gets no message, the server's log names the grid. An off block adds
no mass to its grid.

A client has no patch and would otherwise fly its own copy of the ship while the server's
grid sleeps: the pilot of a parked ship would see it creep away without limit. The ship's
grid stays awake for 5 s after the blocks go off, so that the server's states correct what
the client's copy did until then.

The game's Warfare 3 update brings the rule as a world setting. Until then it applies on
game version 1.210.* only; on any other version the patches aren't applied, and the log
says at load which way it went. [SE-882](https://tasks.cometworks.se/tasks/1769) removes
it and turns the world setting on.

### Clang drives

With `ClangDrivesEnabled` on (the default), the server switches a ship off when its speed
changes in a way the forces on it don't explain: the power switch and every battery,
reactor, engine and other power producer of the ship go off, whatever state each one was in. This stops clang drives, and a ship that brakes by ramming a station or another ship in
a safe zone. The log names the grid, its owner, the acceleration left unexplained, the one
measured and the one allowed, and the owner gets a chat message, at most one a minute. A ship over the limit that
one of the exceptions below leaves on gets a log line too, with the reason. Nothing switches
the ship back on; the player can, with Y.

- The ship is the grids joined by rotors, pistons, hinges, connectors and landing gear. Its
  speed is their common center of mass's, so parts moving inside the ship cancel out.
- The speed change is taken over `ClangDriveWindowFrames` (6, 100 ms) of game time, so a
  single spike counts only for its share, and server lag changes nothing.
- Every force the game or a mod applies to a ship counts toward explaining it: thrusters
  with their dampeners and overrides, Relative Top Speed's drag above the cruise speed,
  explosions and hits, artificial mass. So does natural gravity. Artificial gravity doesn't
  pull grids, only artificial mass does. A clang drive's push comes from its parts' contacts,
  which are no such forces (SE-1157). Nor is a wheel's steering push: it pushes the ship alone,
  against whatever the wheel touches, the ship's own hull too.
- What these leave unexplained may be `ClangDriveMinAcceleration` (3 m/s²), plus
  `ClangDriveFactor` (2) minus one times the thrust of the ship's strongest direction at
  full override divided by its mass, plus the natural gravity, which the ground may be
  holding up. Every built thruster counts, on or off, as effective as it is at that place,
  and as it was up to a second before: a ship ramming a station may lose its thrusters in
  the hit. The mass is each grid's own with its cargo, so grids welded by a locked connector
  aren't counted twice. Ships slower than `ClangDriveMinSpeed` (20 m/s) both before and after
  the window are left alone.
- Outside safe zones a crash doesn't count: a ship whose grid touched an asteroid, a planet
  or another grid within the last second is left on. Its own grids touching each other, the
  clang, count, and so do a floating item or a loose grid inside the ship's bounds: cargo
  rattling in a bay. Inside a safe zone a crash counts, but only for the faster of two ships
  that hit each other: the one rammed is left on. A ship left on is left alone for five
  seconds more, the aftermath of its hit: its wreck's pieces keep bumping into each other.
- A velocity set from code isn't the ship's doing: a Trade Lane pulling the ship in or
  dropping it out, a teleport or jump, a grid splitting off, the game capping a ship's speed,
  or any other mod. A merge block's pull toward the block it merges with is a write too, but it
  counts: it changes both grids' speeds by the same amount whatever their masses, which is what
  a merge block clang drive runs on. No window
  with such a write in it counts, nor one with the ship's physics switched off. The server
  never takes a ship's velocity from a client, so contacts and thrusters, a clang drive's
  means, are never writes.
- Ships with a static grid (one docked or landed on a station), NPC ships, ships in a
  shipyard's build space and ships already switched off are left alone. A ship is an NPC's
  only when none of its grids is a player's: a player's tug towing an NPC wreck is checked.
  It is off when none of its power producers is on. The power switch doesn't count: it cuts
  no power, and a ship switched off with Y runs on any battery turned back on in its terminal.

On a cluster each node checks the ships it simulates.

### Drift stop

Havok puts a body to sleep only at rest, so a wreck or debris with no power keeps drifting, awake, until
something stops it. It costs game thread time, and on a cluster its partition can't go offline. With
`DriftStopEnabled` on (off by default), the server stops such a ship and its subgrids when they have no power,
nobody sits in them, no gravity, natural or artificial, acts on them, and nothing joins them to a station:

- With no player within the world's sync distance, a group up to `DriftStopMaxSpeed` (20 m/s) stops at once,
  one group a frame. It falls asleep, and its partition can go offline.
- With a player within sync distance, only a group up to `DriftStopMaxSpeedNearPlayer` (1 m/s) is slowed, and
  only after it drifted for `DriftStopSeconds` (60). It slows down by `DriftStopDeceleration` (0.1 m/s²), one
  group a frame in turn, until it stops.
- A group a player is closer to than `DriftStopPlayerMeters` (100 m) is never touched: they may be working
  on it or pushing it.

Each stop is logged with the grid's name, id, block count and speed. On a cluster each node does this for the
grids it simulates, with its own online players.

### PCU per ship or station
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
"Ignore PCU limit" on are not capped, nor is the ship a shipyard builds for such
an admin, on whichever node builds it. A group already over the cap, pasted by an
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

### Shipyard tool speed

The station shipyards' OmniArc welders weld `ShipyardToolSpeed` (2) times as fast
as their default, and their deconstructors take a block apart in the time those
welders build it: on twice as many blocks at once, with 3/2 the speed times the
block's `DisassembleRatio`, which the game divides a grinder's work by (3 for
heavy armor). ToolCore's definitions are shared by every tool of a type, so the
plugin patches where ToolCore welds and grinds on the server.

Only a tool on a shipyard grid, named `<station> Shipyard` with blocks of the
Octahedron Authority NPC, that is neither editable nor destructible is faster.
`build_world.py` sets both, and only an admin can change them, so a tool a player
builds, grinds off, merges in or takes over keeps its default speed. A player's
turn at a shipyard keeps its welders fast: they are still the shipyard's.

### Shipyard orders

A player loads a blueprint into a station's hologram table (`Console Block -
Station ShipyardProjection`). The table's large screen (`ShipyardQuoteSurface`, 1)
shows the main message: free or paid, what is wrong if anything, and to press the
button. The first screen of the billboard next to it (`ShipyardDetailsPanelName`,
`Billboard LCD Ship Build Details`) keeps its first line and lists the ship's bill
of materials under it; a station from before the billboard shows the list on the
table's screen until the world is wiped. The billboard's second screen, how to
order, is the blueprint's own text, as are the station's other signs.

A player who loads a blueprint holds the table: it is their block, shared with
nobody, so nobody else can load over it or remove the blueprint. Loading another
restarts `ShipyardTableMinutes` (3). The blueprint is cleared, the table goes back
to OCT and is open to everyone once the ship is in the queue, after those minutes,
or when the player leaves the sphere around the station's grids or this server (a
log-off, or a handover to another node on a cluster). A blueprint found on an open
table after a restart or a move of the station to this node has no known loader:
the first player to press gets it.

The first button of the button panel next to the table
(`ShipyardConfirmButtonName`, `Button Panel - Station ShipyardConfirm`) acts by who
presses it, whoever holds the table:

- In a window of their turn (below) the press starts the build: the blueprint they hold
  if any, else their queued paid order. Without either, in the last part of a turn, it
  only says when the turn ends. When the start is refused, a second press within 10 s
  gives the turn up, as `!oct shipyard done` does, also after the station moved to
  another node.
- With another build or repair slot of theirs in the shipyard's queue or running, the
  press cancels it. It does nothing to the table. A running build is canceled only by a
  second press within 10 s; the first press says how far it is and what the second
  does. A paid build's ship is handed over unfinished, as when its time runs out, and
  what is left of it stays in the containers for the window. A free build's ship and
  leftovers are removed, and the free build doesn't count: no cool-down starts.
  Holding a blueprint during their repair slot, the press ends the slot and orders the
  blueprint. A free one is ordered at once only when the shipyard's containers are
  empty; else the slot's window to take what is in them comes first.
- Else, holding a blueprint the shipyard accepts, the press orders the ship. That
  clears the blueprint and the list, and the screen tells the player a second press
  cancels. It first shows the quote and asks for another press when the screen doesn't
  show the presser's quote of this blueprint at these terms, so a build that turned
  from free to paid is never ordered unseen. A blueprint it doesn't accept gets the
  reason.
- Else the press books a repair slot.

A queued order stays when its owner leaves: a free build is built and handed over as
usual, a paid one gets its turn and leaves the queue if nobody presses, and the owner
can cancel it when back.
`ShipyardOrderMinutes` (60; 0 turns orders off) is how long a build runs at most.
`ShipyardStationOrderMinutes` (`Moon Station=240`) gives some stations' shipyards
their own limit, as `Station name=minutes` separated by `;`: the Moon Shipyard builds
the big ships.

A repair slot is an entry "Repair" in the shipyard's queue, without a blueprint or a
payment. It takes a place in the queue, and a player has one entry at a time at any
shipyard, a build or a slot. When its turn comes the shipyard's welders go on, and its containers, welders
and connectors become the player's, shared with their faction unless that is OCT
(`ShipyardTurn`).
The reactors and everything else stay OCT's, and the connectors are not in trading
mode. The player docks a ship with the components to one of the connectors, which are
on the outside of the frame, and the welders take them through the conveyors to weld
what is in the frame, projected blocks of the player's own repair projector too. Prepare
the ship, components and projector before booking.

The slot ends when no building or welding happened for `ShipyardRepairIdleMinutes` (5;
0 turns slots off), or at the player's next press. It ends like a build: the welders go
off and the player has the last window of their turn (below) to take what is in the
containers and welders, which is over at once if they are empty. The running slot
is known from the queue, so a restart or a move of the station to another node picks it
up, with the idle time starting over.

The plugin gives the button a toolbar item (its own On switch) so that clients
offer it; the item never runs. `!oct order [table name]` (admin) presses the button
of the nearest table, `!oct table remove [table name]` (admin) removes the blueprint
of a table nobody else holds and `!oct table show [table name]` (admin) prints its screens.

- A ship is a free build for a player with less than `FreeBuildPcuLimit`
  (60,000) PCU, counted as for the starter ship, once `FreeBuildCooldownMinutes`
  (10) have passed since their last free build ended, and when none of its blocks
  needs a component `FreeBuildExcludedComponents` names (subtypes with `*`, by
  default `PrototechFrame`, which every prototech block needs) or is named by
  `FreeBuildBlockedBlocks` (type/subtype with `*`, by default none). All three are
  in `config/octahedron.cfg` too, because a running cluster keeps the values it has
  for options that file doesn't name. The cool-down starts when a free build ends with
  the ship handed over, finished or not; a canceled order or build, or one whose ship
  was removed, starts none. The plugin keeps the times in the world variable
  `Octahedron-FreePrints`, on a cluster in the World Authority's world: the node
  ending an order sends the time to every process, and a starting node asks the
  World Authority for the list. A player gets one free ship at a time.
- An admin with the game's "Ignore PCU limits" on (Alt+F10) gets every ship as a
  free build, whatever PCU they own, during a cool-down, with blocked blocks and
  over `GroupPcuLimit`, so a shipyard build can be tested again and again. Their
  builds start no cool-down, and the server log says so. The other rules stay:
  one free ship at a time, one order per queue, the shipyard's space and
  containers. The server that takes the order checks the flag, on a cluster the
  node the player is on, and the order keeps it in the queue and the build's
  projector: the node building the ship may not have the admin's settings.
- Any other ship is a paid build, paid from the player's own account. The quote
  lists each component of every block, bought from OCT's stock at the station on
  the Store's terms. On top comes the labor fee, `ShipyardLaborFeePer100Pcu` (0) SC
  per 100 PCU of the blueprint, rounded up, paid to OCT on every build. At 0 there
  is no fee, and the quote and the messages don't mention one. The materials also
  include the gas to fill the ship's H2 tanks to `ShipyardHydrogenFillPercent`
  (100 %) and its O2 tanks to `ShipyardOxygenFillPercent` (50 %): from the tanks of
  the player's ships docked at the shipyard or the station's connectors first, each
  keeping half its capacity, then ice (`ShipyardHydrogenLitersPerIce`, 20 L of H2
  per kg, and `ShipyardOxygenLitersPerIce`, 10 L of O2 per kg, as a gas generator
  makes them) in those ships' cargo containers and the shipyard's, then ice from the
  Store, which the plugin moves without conveyors. `MarketMinStock` keeps 100 t of
  ice at a Store, 2,000,000 L of H2. When the station has too little ice, the order
  goes ahead and the tanks get less, the H2 tanks first. Nothing is paid or moved
  when the order goes into the queue. An order queued before the O2 fill (SE-1288)
  bought ice for the H2 tanks only, and its O2 tanks stay empty.
- A free ship's H2 and O2 tanks are filled to the same percents, so a ship with
  only hydrogen thrusters can fly out. When it is done the player's docked ships give what
  they can in the same way, and OCT gives the rest for free, as it does the
  components, whatever the Store has.
- The queue of each shipyard holds at most `ShipyardQueueMax` (6) orders, the one
  being built included, one per player: more don't fit the queue panel above the
  table. A full queue shows on the table's screen and refuses the press.
- Human players' orders go before the bots' (SE-1207), the bots being the Steam IDs
  `ShipyardBotSteamIds` names (`first-last` ranges, comma separated; the trader bots'
  on the Octahedron). A human player's order goes ahead of every bot order waiting.
  A bot's build in progress, or its turn before the build started, is canceled when a
  human player orders, and so is the last bot order waiting when the human player's
  would make the queue longer than `ShipyardQueueMax`. A queue full of human players'
  orders refuses a new one and cancels nothing. The node that simulates the shipyard
  ends a canceled bot order at its next check, as the bot's own cancel would, and tells
  the bot "a player's order took its place", so it orders again: a free build's ship is
  removed and the build doesn't count toward the cool-down; a paid build gets its labor
  fee back and its components are sold for it. Nothing was paid for a waiting order.
  Orders from before keep their places and count as human players'. The
  station's queue panels (`ShipyardQueuePanelName`, `Text Panel - Station
  ShipyardQueue`) show it, and which ship blocks the shipyard with the time it has
  left to leave, once its owner was told. The first entry says how far it is, then
  whose ship it is: `1. 53% (46 mins) Viktor 'IMDC Europa Carrier' (44,181 PCU)`
  while it is built, the share of its blocks the projector counts built (all
  subgrids, by Multigrid Projector), or `loading parts (8 mins)`, `taking what is
  left (8 mins)`, `breaking down`, `repairing`, `taking parts` or `waiting`. The
  others are just `2. Viktor 'Ship' (1,234 PCU)`, a repair slot `(repair)`. Each
  entry wraps to two lines of 46 columns
  and is cut after that, a blocking ship's name before its time, so a full queue keeps
  font size 0.57.
- The ship must fit the shipyard's free space, the largest box between its
  welders free of its blocks, turned so that its main cockpit points along the
  shipyard or, when it only fits another way, by right angles from there,
  its components the shipyard's containers, and it may have at most
  `GroupPcuLimit` PCU, the cap of a ship, unless an admin who ignores PCU limits
  ordered it. The quote checks that; an order that
  doesn't fit when its turn comes ends with everything given back. When a
  blueprint with subgrids is too big, the message also suggests blueprinting the
  ship again in an offline game: an old blueprint can have its subgrids out of
  place.
- The game's client damages a blueprint with subgrids on its way to the table: it
  remaps the entity ids of each grid on its own, so a rotor, piston or hinge base
  points at no block of its head's grid, and the ship would come out wrong.
  Multigrid Projector on the client remaps them together. The server can't see
  the plugin, so it takes a blueprint with subgrids as Multigrid Projector's only
  when every base points at a block of the blueprint, and at least one does. It
  checks that when the blueprint arrives, before the game starts showing it on
  the server: that remaps the same ids on a worker thread, and a check during it
  refused an intact blueprint, most often a player's first (SE-1248). A refusal
  is logged. It clears any other blueprint with subgrids off the table after
  `ShipyardTableSettleSeconds` (10) and tells the player in chat to use the blueprint's Steam Workshop link or id with
  `!bp <link or id> [table name]` (or `!oct workshop`), which needs no plugin, or
  the Multigrid Projector plugin of Pulsar. The wait is for the clients: each
  starts showing the blueprint on a worker thread when it arrives, and a removal
  that comes while that runs breaks it, with "Blueprint can't be found" or a
  hologram that stays and hides the next blueprint (SE-1195, SE-1196). One found on a table later, after a
  restart for example, is refused by the table's screen and the button the same
  way. With `!bp` the server loads its own copy into the table, the hologram the
  clients show and the ship the shipyard builds, so no client sends it. The
  node the player is on downloads the item anonymously with SteamCMD
  (`ShipyardSteamCmd`; empty: Quasar's managed SteamCMD in
  `ManagedRuntime/Tools/SteamCMD` above the server's data folder, else `steamcmd`
  on the PATH), because a cluster node has no Steam connection of its own for the
  game's Workshop calls. The blueprint is kept by item and version (the Steam Web
  API's `time_updated`) in `ShipyardWorkshopCache` (empty: `OctahedronBlueprints`
  next to Quasar's SteamCMD, else in the server's data folder), which the servers
  on a host share, so each version is downloaded once. Entries unused for 7 days
  are removed, checked once a day. A hidden item has no public version and is
  downloaded every time. It loads the blueprint into the nearest table as if the
  player had loaded it: ids remapped together, the main grid at the origin, and
  programmable blocks' scripts dropped unless the player may use scripts, as the
  game does with a client's blueprint. The table must be free or the player's,
  and the player within `StarterShipTableReach` of it, both when typing the command
  and when the download is done.
  Multigrid Projector before 0.9.10 sent no ids, so its blueprints with subgrids
  are cleared too.
- The quote leaves out the blocks of DLCs the player doesn't own and the blocks
  of mods the world doesn't load, and names them by DLC or by subtype. The server
  has no definition for a mod's block, so no projection shows it and no welder
  could build it: the build would stall without it (SE-1086). A main grid left
  with no blocks can't be ordered.
- The next order starts when no block of another ship is in the shipyard's free
  space: a ship left there has to fly out first. The space ends at the walls, so
  a ship docked at the connectors outside them is not in the way, and neither the
  connectors nor the conveyors leading to them are in the space. The ship is built on the free space's center line:
  a ship shorter than 1/4 of the shipyard's length around the 1/3 point nearer the
  station, where it shows better from the lobby; a longer one in the middle. It is
  built from
  a support arm (below) on a shipyard with arm rotors. A shipyard without them
  starts the ship as a static grid of one of its blocks, built complete and the
  player's, with an NPC-owned projector and battery named `Shipyard Order` next
  to it, outside the ship's bounds. Either way the projector projects the
  blueprint (Multigrid Projector builds its subgrids), the components go into the
  shipyard's cargo containers (a paid build's are there already), and its
  OmniArc welders are switched on through their terminal switches. The welders
  take the components from the containers like any welder, so a free build gets
  its components loaded too, and what is left is removed at the end; no tool has
  to skip the component cost. While a build runs the plugin also tops up the
  shipyard's reactors to 1000 uranium each, at OCT's cost: without power the
  welders stop. Floating objects in the shipyard main grid's bounding box are
  removed while a build runs. Before a build starts, grids with a block in that
  box that no player owns are removed too, and so are players' wrecks: grids with no power
  and no cockpit or remote control in their whole mechanical group. Characters,
  players' other grids and the shipyard's own grids stay; those still block the
  next build until they leave. In a no-welding safe zone ToolCore on the clients,
  which don't have the shipyard exception above, would turn the tools off and
  show no beams (SE-441): while a build runs, the plugin tells the clients every
  10 s that the zones don't hold the shipyard grid, and puts it back at the end.
  The server's own zones are left alone. A client that gets the shipyard streamed
  in during a build (it comes back in range, joins or crosses to this node) still
  turns the tools off before that message comes, and ToolCore tells it only when
  the server's switch changes, and only if it knows the client has the tools. After a
  cluster handover that moves the shipyard to another node with its players, the new
  node's ToolCore knows of none of them (SE-1115). So at every 10 s check of a build
  the plugin sends the switch and target tracking itself to every client the server
  has given the shipyard grid, however it got there: in range all along, joined,
  respawned, streamed in or brought along by a handover (SE-782, SE-963, SE-1126).
  ToolCore is the mod's own code: this reaches its session
  and packet by reflection and does nothing if they are gone.
- While an order runs, the shipyard's welders work only on its ship: the grids its
  projector builds, those of the subgrids included, and what it projects (SE-1122).
  They weld nothing else in their reach, such as a player's ship or projection
  outside the build space. A free build's welders are OCT's, and before this they
  built a player's missile printer parked by the Moon Shipyard with the order's
  components, so the ship came up short and was removed. The plugin patches ToolCore
  on the server for this (`ShipyardOwnBlocks`).
- A build whose ship gains no integrity for 60 s has stalled. The plugin first
  places the projected blocks that only a collision with another part of the ship
  keeps the welders from, welds them from the containers and watches the physics
  for 10 s: a ship that shakes (clang) is removed and refunded, else the build goes
  on (SE-940). Then it finishes the blocks the welders left though the containers
  hold their components, which happens on big ships: it welds the built ones and
  places and welds the projected ones that pass the build check, up to 200 at each
  10 s check while the welders make no progress, and the build goes on (SE-953).
  When nothing is left to finish, the build stops. A paid build short of
  components in the shipyard first waits `ShipyardTopUpMinutes` (5) for a top-up and
  tells the player. Plain blocks, armor and decorations, are those of the plain
  `CubeBlock` type; every block of its own type, lights, panels and conveyors included,
  is functional. Only the main grid decides. When it misses only plain blocks, at most
  10% of them, the ship is delivered as usual. When it misses a functional block or more
  plain ones, the ship is removed: a free build doesn't count, a paid build's components
  go into the containers and the labor fee comes back. An unfinished subgrid is no reason
  to remove a ship, as a subgrid's hitboxes may just intersect the ship's blocks: the ship
  is delivered without what is missing, as a finished build. A free build counts and its
  cool-down starts, what is left of its components is removed. What is left of a paid
  build stays in the containers for the last part of the turn and is then sold, and the
  labor fee is kept. The player gets a chat message either way, naming what is missing.
- Every projected block welded onto the ship or its subgrids is the player's,
  owner and `BuiltBy`, so it counts toward the player's PCU and the projection
  limits check it for them.
- The order ends when every block is complete, or when the time is up. The
  welders go off, the projector and battery are removed with the projection,
  the hydrogen and oxygen go into the ship's H2 and O2 tanks (on a paid build the ice
  of what doesn't fit is sold at the Store, with a warning when none does), and the ship,
  finished or not, becomes the player's dynamic ship. The player gets a chat
  message either way. After a free build what is left of the components is removed
  and the shipyard is free at once; a paid build goes on to the last part of its
  turn.

#### A player's turn

A paid build, a towed grid's breakdown and a repair slot come with a turn at the
shipyard. A free build has none: the blocks stay OCT's and only its welders go on.

Without a job a shipyard is off: its connectors and welders are switched off, and its
cargo containers and cargo terminals, which have no switch, are OCT's, so nobody gets
in. Those conveyor ports lock with their block's ownership, and the conveyors are
closed off with the connectors. That holds from the world's build, and the plugin puts it right at
the first look at the queue after a start, for a world from before the rule, leaving a
shipyard with a running job alone. The reactors, batteries and refill stations are
never switched: the reactors charge the batteries, and a refill station needs only
power, so it works for anyone at any time.

A job doesn't need its player. They can log off or move to another node, in a build
or a repair slot. The welders stay theirs, and for a player who isn't connected the
game's DLC question, which only Steam could answer, counts as yes. On a cluster a
shipyard with something in its queue keeps its partition online, which would otherwise go
offline with nobody within 8 km, and may go offline again once it is free. A cluster stop
leaves such a partition out of what it takes offline, so the job goes on after the start with
nobody there; a cluster that can't, from before SE-904, needs a visit first. The shipyard's
chat messages find the player on any node. One who is logged off misses them.

- At the queue's front the player gets the shipyard's cargo containers, welders and
  connectors, shared with their faction unless that is OCT, connectors and welders on and connectors
  not trading. The reactors and batteries stay OCT's, and conveyors can't pull
  uranium out of a reactor. OCT keeps most of the blocks, so it stays the grid's big
  owner and the grid a shipyard.
- While the welders are the player's and no order runs, as in a repair slot, they work
  only on the player's blocks and their faction's, OCT not counting as one: a block's owner, or for a block without one who built it, and for a
  projection the projector's owner. The order's own projector counts as theirs. Another
  player's ship in the shipyard is neither targeted nor welded, and its projection isn't
  built. The plugin patches ToolCore on the server for this, so a client may still draw
  a beam at such a block (`ShipyardOwnBlocks`).
- They get `ShipyardTurnMinutes` (10) to load components, and a chat message saying
  so. The details screen lists what is missing. The press starts the build: the
  Store supplies what is missing, into the containers, and the labor fee is paid. The press is refused when the player can't pay all of it, when
  it doesn't fit the containers, or when a ship is in the way. The refusal says why
  and that a second press within 10 s cancels the order and ends the turn; the server
  log and the World Authority's log record it. If they don't press, the order leaves
  the queue. While it builds they and their faction can put
  components in and take them out.
- After the build, the breakdown or the repair slot they get another
  `ShipyardTurnMinutes` to take what is left. In either window a blueprint they load
  and press starts at once on the same turn, with a new window, using what is in the
  containers and buying the rest. A free ship can't: they end the turn first.
- The turn ends at the deadline, once the containers, welders and connectors are
  empty, or with `!oct shipyard done`. Every connector unlocks, a docked ship floats
  free and its owner is told, and the blocks are OCT's and off again. What is left is sold to
  the station on the Store's terms: OCT's bid, tax in it, or the item's base price
  less tax if it has none, into OCT's stock. The money goes to the player, with a chat
  message.
- A ship in the build space holds up the next order. A ship an order built has that
  deadline also with no order waiting, from when the queue is empty. Its owner is told, and after
  `ShipyardClearMinutes` (5) a ship an order built, until it first leaves the build
  space, goes into the Hangar of the player who ordered it (Hangar 1.2.0 or later). The
  entry counts as stored at this station, so it comes back free here. With no free slot
  it is removed with all that is in it, and the player gets back the SC the build took:
  the Store's price and the labor fee, less what the ice the ship didn't take sold for,
  and for an unfinished ship the share of its blocks that were built. A free ship is
  just removed. Components the player brought aren't paid for. A ship Hangar can't take
  (over its entry limits, or no Hangar) and any other grid are moved out over the
  shipyard. A restart starts that wait over.
- A towed grid over the player's limits is broken down at once on arrival: each block
  yields the components it really has, and its cargo comes along. With the shipyard
  free that is into the containers, with a turn for the player and the last window;
  what doesn't fit is sold at once, the cheapest item first. With the shipyard busy
  all of it is sold on the same terms, and nobody's build is disturbed. `!oct shipyard
  tow <station> <grid id>` does it by hand, for tests.
- A grid a tow gave a player gets a chat warning to move it and is safe from the sweep
  for `ShipyardTowGraceMinutes` (10), then counts as any wreck. `!oct shipyard given
  <grid id>` does it by hand, for tests. The grace is on the node, a restart or a move
  to another node ends it.

The turn and its deadline are in the queue, the
blocks' owner in the world, so a turn goes on after a restart or a move of the
station's partition.

#### Support arm

The shipyard's rotors named in `ShipyardArmRotors` ("Advanced Rotor Top Arm
Large", "... Top Arm Small", "... Bottom Arm Large", "... Bottom Arm Small") carry
the support arm of a build on their rotor heads. The ship's main grid is built
onto the head's grid, so only the rotors whose head has the ship's grid size
take part. The plugin plans an arm on each and takes the best:

- It starts with a pipe end on the head and runs in pipes to the ship, around the
  cells every block of the blueprint will take, the subgrids included, and around
  the shipyard. A turn costs `ShipyardArmTurnCost` (3) pipes, so the arm trades
  length against turns. The pipes are plain unit-tested code
  (`Builds/ArmPlanner.cs`): an A* search over cells and the direction of travel.
- Its last pipe is a tee, and the arm ends in a new projector (`Shipyard Order`,
  owned by the station's NPC) next to a block of the ship's main grid whose face
  mounts to the projector's. The projection is anchored on that block. If no such
  spot can be reached, the projector takes the place of the lightest block that
  `ShipyardArmGiveUpBlocks` names (armor and plating); the ship lacks that block
  at the end and the player is told to build it.
- With a connector of the ship's main grid within reach, the tee carries a
  connector facing it and the projector hangs on a support block next to it. The
  ship's connectors are tried by how many blocks with an inventory or conveyor
  touch them, in a chain. The projector has to touch a ship block beside the
  ship's connector, so a connector sticking out of the hull with nothing beside
  it gets a cap. Without one the tee's branch ends in a cap.
- The pieces are `ShipyardArmLargeBlocks` and `ShipyardArmSmallBlocks`, each
  turned so its mount points face its neighbors in the arm.

The arm is planned on a worker thread, one plan at a time. The game thread only
copies what the plan needs of the shipyard and places the arm when the build
starts. A press while its plan runs goes on when the plan is done, usually a
fraction of a second later, and an order waiting for its plan stays in the queue
with nothing taken yet. A plan that has no arm within `ShipyardArmPlanSeconds` (5)
is refused: the press gets the reason, a queued order ends with everything given
back, and the same ship isn't planned again for 5 minutes. Nothing is placed for a
plan until its build starts, so a server stopping or an order canceled in the
meantime leaves nothing behind.

The quote checks that an arm can be planned; when none fits, the server log says
why for each rotor once. At build start the arm's blocks are
placed from the rotor head and built at once with the arm's components, and the
head's grid turns static for the build. The arm's components go into the
shipyard's containers on top of the ship's, for free: OCT pays for the arm. When
every block of the arm is complete, the plugin switches the projector on and the
projection appears. Should an arm block still be incomplete with no progress for
30 s, the plugin welds it with the components in the containers. When the
ship is complete, or its time is up, the arm's projector is switched off and its
projection cleared, and the ship leaves the arm as the player's dynamic ship. With
`ShipyardRepasteShips` (on) it leaves as Essentials' `!fixship` re-pastes a ship:
the plugin saves the ship's blocks and subgrids, removes them from the arm and
pastes them anew as one dynamic ship, with no grid split. A split (the option
off) leaves the clients a copy of the ship they can sit in but not fly; SE-746
tracks why. With a connector the arm's connector locks onto the ship's;
docked within `ShipyardArmDockSeconds` (1), the ship's H2 and O2 tanks get the
build's gas. Without a dock the plugin fills the tanks itself. Either way, and for
a ship built without an arm too, `ShipyardChargeBatteries` (on) charges the new
ship's batteries fully when it is handed over (SE-1387). Its jump drives stay
empty. Then the plugin hands the ship over and takes the arm down at
once, never the rotor head itself; the shipyard starts its next build after
that. An idle shipyard takes down anything an NPC built that is left on its
rotor heads. An unfinished ship that is delivered leaves the arm's projector
behind, and it comes down with the arm. A ship built without an arm loses its
scaffold projector the same way. The shipyard loads what the arm's blocks still
need, without the first component each frame already has. What the arm didn't
use of them is removed with the ship's leftovers, and only the ship's go on to
the player's turn. The `!oct shipyards` admin command lists the shipyards, their arm
rotors and the running builds with their arm.

`ShipyardBotHangarDelaySeconds` stores a completed, re-pasted bot ship in its
orderer's Hangar after that many seconds if another job is waiting, once the
authority records the finished order. Zero disables it. With no waiting job, or
if the Hangar cannot take the ship, the owner collects it from the shipyard.
The bot is told either way, also when the station has gone to another cluster node
during the delay; only a ship that is gone gets no word. A restart during the delay
forgets the pending ship, and the bot hears nothing more about it.
Octahedron sets the delay to one second.
`!oct shipyard strip <station> <item>` takes an item out of a shipyard's
containers and welders, so tests can stall a build.
`!oct shipyard clear [station]` (admin, on the node that simulates the station)
without a station clears the one whose hologram table is in the admin safe zone the
admin stands in, and asks for the name anywhere else or in a zone with several. It
empties a shipyard's queue the way each player's own cancel or `!oct shipyard done`
would: waiting orders leave the queue, a running build is canceled (a paid one's
unfinished ship is handed over, a free one's removed), a repair slot ends, and every
turn ends at the next check with what is left sold for its player. The players are
told an admin did it, and the reply lists each entry.

The ship's PCU cap (`GroupPcuLimit`) leaves out the shipyard and the arm, which
are in one mechanical group with the ship while it is built.

The queues live with the market, on a plain server or the
World Authority, saved as the sandbox variable `Octahedron.Builds`, the
blueprints included. A running build is kept in the world, in the projector's
custom data, so it goes on after a restart. Every 10 seconds the node that
simulates a shipyard checks it and starts the next order, so on a cluster the
node owning the station's partition runs the builds and asks the World
Authority for the queue.

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

### Offline characters

A character left alive at a station with its player offline would keep its space
credits out of reach of the wealth tax. After `OfflineCharacterMinutes` (15) offline,
a player's character inside the bounding sphere of a trade station's grid is removed
from the world. Standing or seated makes no difference, in a seat of the station or of a
ship, a cryo chamber included, and the seat is left free. It is the character's own
position that counts: outside the sphere it stays, even in a ship reaching into it, and
so does a dead character. Coming back in time changes nothing and starts the time over.

The space credits in its inventory go to the player's bank account without any tax, as
they are the player's own money, and they are not kept as items. The rest of the
inventory is kept in the world variable `Octahedron.OfflineCharacters`. The player's
next character gets it on top of what it spawns with, past the inventory's limit if it
has to, and a chat message says so. Only the inventory is kept: health, oxygen and
hydrogen start as on any spawn.

On a cluster the World Authority keeps the times and the inventories and pays the
bank, and the node holding a body asks it what to do every 30 seconds. So it survives
restarts and a player can come back on any node.

- The node removes a body only once a recoverable global save holds its inventory and
  deposit: the World Authority asks the cluster's `cluster.global-save` command, which
  answers once such a save holds what was done before it. A cluster older than 0.2 has
  no such command: the World Authority logs an error at its start and bodies stay. A
  body a dead node brings back is known by its entity id: it goes without a second
  deposit. A player who gets into such a body first finds it emptied within seconds and
  gets the kept inventory instead.
- A new character gets a kept inventory only once a save knows it is on its way, so a
  World Authority restored from an older save doesn't hand it out twice. Like every
  other inventory change, what a node hasn't saved yet is lost if the node dies in the
  minute after that.
- A body usually goes offline with its partition 3 minutes after the player left, well
  before its time is up. The World Authority then asks the gateway to load the
  partition, a few a minute, for players it saw within 200 m of a station's position when
  they left; the node goes by the sphere and leaves a body outside it where it is. Bodies
  already there when this first runs have no such record: they wait for their partition
  to load for another reason, or for their player.

`!oct offline` (admin) shows on the server or the World Authority who was last online
when, and every kept inventory.

### Station market

With `MarketEnabled` on, each OCT station's Store block is its market: the game's
own store screen, with OCT's real stock and prices. Players buy and sell there with
money from and to their own account, moving items to and from their docked ships or
inventory. Nobody can keep items at a station. A station is a faction station entry
of the world, by its name (`Alpha Station`), so a new station blueprint keeps its
stock as long as its entry keeps the name.

- OCT (`MoneyAuthorityFactionTag`) is the only seller and buyer. Its catalog is the
  same at every station: the items matching `MarketCatalog` but not
  `MarketCatalogExcluded` with a reference price, the game's minimal price / 10
  (`MarketPrices` sets one where the game has none). The Inhibitor Chip
  (`Component/InhibitorChip`, MES loot worth 1,000,000 SC by its minimal price) is
  excluded, so no Store sells or buys it and a paid build can't buy one: a blueprint
  that needs it is refused as more than the station has for sale. The value is in
  `config/octahedron.cfg` too, because a running cluster keeps the values it has
  for options that file doesn't name.
- With `MarketCatalogBuildableOnly` on, the catalog also leaves out every ingot and
  component players can't make: one that no public production block (refinery,
  assembler, survival kit) has a blueprint for. That covers the parts of MES's
  NPC-only blocks, like Proprietary Circuits and the Proprietary Thrust Component,
  so nobody can weld a proprietary jump drive or gyroscope from Store parts. Ores
  stay. The server logs what the rule leaves out. Prototech items stay out by
  `MarketCatalogExcluded`, whatever blueprints they have. Restocks and acquisition contracts skip anything out of the catalog, even
  when a stock list names it, and stock a saved world already has of it stays in
  the book, unsold.
- It sells at `MarketOctSellPercent` (100 %) of the reference plus
  `MarketTaxPercent` (2 %), and buys at `MarketOctBuyPercent` (100 %) less the tax,
  until it holds `MarketOctMaxStockTonnes` (200 t) of the item there. Selling
  something and buying it back so costs twice the tax. The Store's prices are whole
  SC: rounded to the nearest, at least 1 SC, a bid at least 1 SC below the ask, so
  on a very cheap item the rounding costs more than the tax. An item worth under
  about 1 SC is sold but not bought.
- The stock is real: it goes down with each purchase and up with each sale, and
  what the Store shows is what is there. Once an hour, on the UTC hour, OCT brings
  every item that is completely out back to the station's minimum: `MarketMinStock`
  for all stations, with `MarketStationStock` for a station's own profile (empty
  for now), never over the 200 t. Anything not completely out is left alone. Its
  acquisition contracts ask for what it lacks of these lists (`Market.Missing`).
- Players must mine the metals of `MarketPlayerMinedMetals` (gold, silver,
  platinum and uranium). OCT never restocks their ores and ingots, nor any
  component whose blueprint needs those ingots (Thrust, Superconductor, Reactor,
  Gravity Generator and others; the server logs the list), and its acquisition
  contracts don't ask for them, whatever the two stock lists name. OCT still buys
  them from players and sells what it bought. Free shipyard builds need no
  components, so the rule doesn't touch them, nor the Free Tools boxes.
- `MarketSpecialties` names what each station buys at `MarketSpecialtyPercent`
  (120 %) less the tax, up to `MarketSpecialtyDailyTonnes` (50 t) a UTC day per
  station; past that OCT pays its normal price. Such an item sells there at no less,
  plus the tax, so nobody can sell back at the station what they just bought. Alpha:
  platinum and magnesium, Gamma: iron, nickel and gravel, Beta: gold, silver and
  organics, Delta: cobalt and silicon, Moon: uranium, ore and ingots alike. North
  and South have none.
- OCT's prices are per station and drift with the volume: the net tonnes bought of
  an item at a station (sales count negative) raise its price there, doubling it
  at `MarketPriceDriftTonnes` (100 t), or lower it the same way, at most
  `MarketPriceDriftMax` (2) times either way. Half of the drift wears off in
  `MarketPriceDriftHalfLifeHours` (24 h).
- OCT sells zone chips at `ZoneChipStations` (Moon Station only) for
  `ZoneChipPrice` (1,000 SC) each, a fixed price with no tax or drift. A chip keeps
  a player safe zone up for an hour (the game's safe zone block takes 1 chip every
  60 minutes). OCT doesn't buy them, and they stay out of its catalog
  (`MarketCatalogExcluded`). Its stock there is topped up to `ZoneChipStock` (100)
  every minute.
- Reputation changes no prices: the game's discount for friends of the seller is
  off on OCT's Stores, or a friend could buy cheap and sell back with profit.
- The game's generator of store items stays off for OCT. Players' own store blocks
  work as before.

The plugin writes the store items of the stations this process simulates every
15 s and after each trade; the game's own store code does the trade, with the
money, the items and its checks. A trade then tells the book, which the server
keeps, on a cluster the World Authority: nodes send it as a cluster-wide command,
once per operation id, and get the station's rows back. So on a cluster two nodes
may sell the same last items within a moment, and the stock floors at 0 on the
book. The market is saved with the world, as the sandbox variable
`Octahedron.Market`: the stock, the day's specialty purchases and the price drift.
Every change is logged.

Admin commands, for tests and support: `!oct market stations`,
`!oct market show <station> [all|item]`, `!oct market set <station> <item> <units>`,
`!oct market tick [force]` (restock what is out and top up the chips; force as on a
new hour and day) and `!oct market stores [tick]` (the store items of OCT's stations,
after the game's economy tick).

### Hangar fee

The cluster runs Hangar with `config/hangar.cfg`: a ship comes back only where it was saved, or at the nearest spot clear of the other
grids' bounding spheres, so it counts as the same station and there is no fee. A ship the
shipyard's deadline stored comes back outside the station.

With Hangar 1.1.0 or later on the server, set to `AdminSafeZonesOnly`, players store
and retrieve their ships only inside a station's safe zone, the world's only admin
safe zones, and Hangar records the zone on each stored ship. A ship comes back free at
the station it was stored at. At another station it costs `HangarRetrieveFeePer10Pcu`
(1) SC per 10 PCU, rounded up, paid to OCT, with no wait, and the ship keeps its
inventory. A player who can't pay is told the price and the
ship stays stored. The plugin hooks Hangar by reflection, so it still runs without
Hangar, or with an older one, where retrieval is free.

### Taxi

Cabs carry players between the stations for a fare (SE-1279). A cab is a ship whose
name starts with `TaxiGridName` (Taxi) with someone in a seat that controls it: the
driver, a sebot taxi driver (cluster-regression `sebot/tools/trader/taxi.py`) or a
player. Whoever sits down in one of the cab's other seats is told privately where it
goes: every station but the one it is at, and the fare. `!oct taxi <station>` (a name
or its start, any case), or the station whispered to the driver (`/w "[B] Taxi Moon Station"`,
then `alpha`), takes `TaxiFare` (1000) SC from the passenger through the bank, as
the Hangar fee does, and pays it to the driver, who hears where to go. The fare is not
refunded when the passenger gets out before the cab arrives. The driver also hears
when a passenger gets in and out, by name, from `Taxi`:
`Passenger in: NAME`, `Fare paid by NAME. Destination: STATION`, `Passenger out: NAME`.
The first one to sit down picks the destination and pays; whoever else sits down while
the cab waits rides along to the same station for free (when the first one gets out
before paying, the next one seated picks).

What a seat's passenger was asked and paid is kept in the seat's custom data, so a
cluster node that takes over the cab's partition neither asks again nor misses the
passenger leaving. The passenger seats and the O2/H2 generator of a bot's cab
(`ShipyardBotSteamIds`) are opened to everyone, so a passenger can take or fill a
bottle there; the shipyard builds a ship with its owner's faction share.

Whispered to a driver, `call` calls that driver's cab to where the sender is, as
`!oct taxi call` does with the nearest free cab. A whispered station name counts only from
someone sitting in that driver's cab. The driver's words don't care about case,
punctuation or the word "station", and forgive a typo in a short word and two in a longer
one (`TaxiWords.cs`, tested offline by `tests/taxi-words`: `dotnet test`); two equally
close answers, or anything else, get a reply saying what the driver understands.
Whispers to other players and global chat are left alone, except GPS points.

A GPS point instead of a station (SE-1344): a GPS link (`GPS:name:X:Y:Z:...`, as the game
shares it) whispered to the driver, or said in any chat by the first passenger before paying,
pays the ride to that point. It costs what a call from that point would (`TaxiFares.CallRide`:
`TaxiCallFare` within `TaxiCallRange` of the nearest station or Earth end, plus
`TaxiCallFarePerKm` beyond), and is refused when the passenger can't pay it all. A point within
3 km of a station is that station's regular fare. The driver hears `Fare paid by NAME.
Destination: GPS X Y Z`, flies there as to a caller (above it on a planet), and waits for the
passenger to get out. The ride counts as arrived within 1 km of the point.

`!oct taxi call` calls a cab to where the caller is, out in space only (cabs don't fly
into gravity yet): the nearest one with a driver, nobody else in it and no other call,
waiting at a station or on its way home empty. The driver hears `Call from NAME at X Y
Z` and comes, through the lanes as far as they lead and straight on from there. When the
caller sits down in that cab, they pay the call fare to the driver, or all they have when
that is less, and the driver hears the usual `Fare paid by`. The ride goes to the nearest
station, or up a space elevator (`TaxiElevators`) to its station when the elevator's Earth
end is nearer. The fare is `TaxiCallFare` (1,000) SC within `TaxiCallRange` (5 km) of that
station or Earth end, plus `TaxiCallFarePerKm` (100) SC for each whole km beyond it
(`TaxiFares.cs`, tested offline by `tests/taxi-fares`: `dotnet test`). A call from within 3 km of a
station is a regular fare instead: the caller is asked where to go and pays `TaxiFare`,
as if they had sat down in the cab at that station. A caller who dies before pays
`TaxiCancelFee` (100) SC and the call ends: the driver hears `Call cancelled: NAME`. A
cab that doesn't come costs nothing: the call ends without a fee after `TaxiCallTimeout`
(1800) seconds, or when the cab or its driver is gone. Open calls live in the process,
so on a cluster a call reaches only the cabs of the caller's node.

A paid ride that doesn't get there is refunded in full, from the driver to the one who
paid: when the passenger dies on the way, the driver leaves the controls or dies, or the
cab is gone. A passenger who gets out early and alive gets no refund. A driver who can't
reach a caller says `!oct taxi giveup`: the call ends at once, without a fee. On a
cluster a cab's paid rides go with it to the node it flies into (the grid handover), a
call stays open on the caller's node while its cab is on another one, and a give-up and
the messages to a driver reach every node.

### Police

Police bots defend the stations (SE-1384). An officer is a bot (`ShipyardBotSteamIds`)
that said `!oct police duty <station>`, flying a ship named `PoliceGridName*` (Police),
the "Raptor - Ion" in the station's color (cluster-regression `sebot/tools/trader/police.py`).

- Weapon damage (bullets, rockets, explosions, mines) to any grid or character within
  `PoliceAlertRange` (2 km) of a station's center makes the attacker a target: the grid of
  the weapon block or missile launcher that did it, with every grid mechanically connected
  to it. Damage by a police ship, by a station grid and by hand weapons raises no alert.
- Every `PoliceReportSeconds` (2) the officers on duty at that station hear, privately from
  `Police`, `Target KEY at X Y Z moving VX VY VZ grids ID,... weapons N`, until the target is
  clear: `Clear KEY: WHY`, once all its grids are beyond `PoliceChaseRange` (3 km) of the
  station, it has no working weapon block, or it is gone.
- A police ship of an officer on duty docked at a station's connector (where the officers
  park) gets its hydrogen tanks filled, and its guns and its cargo (`PoliceCargoAmmo` of the volume, 0.5)
  filled with its guns' ammo, free, every second. A service station is to do this later.
- `!oct police list` (admins) lists the officers on duty and the targets.

Officers and targets live in the process: on a cluster each node knows the officers who went
on duty there and the stations it simulates. The bots say it again now and then.

### Contracts

With `ContractsEnabled` on, OCT (`MoneyAuthorityFactionTag`) posts the stations'
contracts in the game's own contract system and the game's contract generator stays
off. They are station contracts: every contract block of the station lists them, and
players take and hand them in there as usual. OCT pays every reward. The game pays the
reward out when a contract finishes, and the plugin takes the same amount from OCT's
account; a contract that fails or runs out costs OCT nothing. There is no collateral.
Every minute the plugin fills each station up again:

- Hauling: `ContractHaulingPerStation` (3) open package deliveries to a random other
  station. A delivery pays `ContractHaulingBaseReward` (2,000 SC) plus
  `ContractHaulingRewardPerKm` (20 SC) per km between the stations, and has
  `ContractHaulingMinutes` (20) plus a minute for every 3 km to arrive. The package
  is the game's: it goes into the player's inventory when they take the contract.
- Acquisitions: for each item of the station's minimum stock (`MarketMinStock`) that
  OCT holds less of there, OCT posts one acquisition for the rest,
  less what open and taken acquisitions bring already, at most
  `ContractAcquisitionMaxAmount` (5,000) items, paying `ContractAcquisitionPricePercent`
  (100 %) of the market's reference price. The player delivers the items at the
  station's contract block from their docked ship or inventory, and they go into
  OCT's stock there, on the Store, not into the station's containers.
- Bounties: up to `ContractBountiesPerStation` (3) on the NPC grids within
  `ContractBountyRangeKm` (30 km) of the station with a threat score (MES's, as the
  spawn gate counts it) of at least `ContractBountyMinThreat` (50), the highest
  first, at `ContractBountyRewardPerThreat` (20 SC) per point. OCT's own grids and the
  wreck factions' are no targets. A bounty is done when the target's threat score
  drops to a quarter of what it was when posted, or the target stops being an NPC
  grid, or it is gone while the player who took the bounty is within 5 km of where
  it was last seen; gone otherwise, it fails. A bounty nobody took is withdrawn when
  its target is gone. `ContractBountyMinutes` (120) to do it.
- Salvage: up to `ContractSalvagePerStation` (2) on derelicts within the bounty
  range: movable grids of at least `ContractSalvageMinBlocks` (20) blocks that belong
  to nobody or to a wreck faction (`ContractSalvageFactionTags`, `WRECKS`). It pays
  `ContractSalvageRewardPerTonne` (100 SC) per tonne. Delivered when the derelict is
  within `ContractSalvageRadius` (150 m) of the station's shipyard grid, center to
  center, with the player who took it within 5 km and nobody in its seats. The plugin
  then finishes the contract and gives the player the derelict, with its rotor and
  piston parts, on top of the reward. A tug on a connector stays as it was, and so does
  another player's ship on a rotor. A grid with no block that can have an owner, armor
  only, stays ownerless until the player adds one. `ContractSalvageMinutes` (180) to
  tow it. A derelict already in that radius gets no contract: there is nothing to tow.

- Wrecks, on a cluster: `ContractWreckContracts` (5) open salvage contracts on wrecks
  left in offline partitions, wherever they are, each picked at random from the
  `ContractWreckPool` (10) biggest by blocks and posted at the nearest station with a
  shipyard. A wreck is a grid that belongs to nobody with no working terminal block
  (armor and decor), at once, or any grid without power once its partition has been
  offline for `ContractWreckOfflineDays` (7); a visit brings the partition online and
  starts that time anew. No power means no battery with charge, no reactor or engine
  with fuel, and no working solar panel or wind turbine. That includes a player's ship
  or base, static or not. Stations, OCT's grids, Trade Lane Computers and a grid someone
  sits in are never wrecks.
  The plugin knows them from what nodes reported and, for a partition that went offline
  without a report, from the rows the gateway keeps of it, by physical group, as they
  were at the freeze (a cluster newer than 0.1.25; an older one has only the reports). A
  row read back from a partition's save has too little to tell, so it is no wreck until
  the partition has been online again. A row can't tell a seat in use, a safe zone or who
  built the grid: the node's report tells once the partition wakes, and the contract is
  withdrawn or, if it is the hunter's own disowned ship, fails.
  A wreck from a row pays by the mass of its blocks, without cargo or tank fill.
  A static wreck pays by the mass of its blocks. It is done like a ship: ground
  down, or cut free of the voxels, converted to a ship and towed; only its big owner can
  convert a grid, so a static wreck that still has an owner needs that first.
  Done when towed to the shipyard as above, which also gives the player the grid, or
  ground down to a quarter of its blocks with the player within 5 km (the plugin scraps
  the rest), or gone with them within 5 km. Its owner powering it up fails the contract,
  or withdraws it if nobody took it; the player who took it may repair it themselves.
  It pays per tonne as above; the time limit is `ContractSalvageMinutes` plus a minute
  for every 3 km.
- A grid given to a player, with a contract or without, has Target Neutrals off on its
  weapons from then on.
- Any movable NPC grid, of any faction but OCT and no station, joined by a connector or
  landing gear to a player's ship and brought within `ContractSalvageRadius` of a
  shipyard becomes that player's, with its rotor and piston parts, if they are within
  5 km. No contract is needed and nothing is paid. A contract nobody took on it is
  withdrawn; a grid under a contract someone took goes that contract's way.
- The PCU limits apply to every grid a tow would give, with or without a contract: the
  player's PCU limit (`TotalPCU` per player, 1,000,000 on Octahedron, against what they
  have built on all the cluster's nodes) and `GroupPcuLimit` (60,000). Up to 10% over
  their limit the grid is theirs, and chat tells them they can't build blocks until they
  are back under it. More than 10% over, or over `GroupPcuLimit`, a contract still pays
  but the nearest station breaks the grid down at once, as told under [A player's
  turn](#a-players-turn), and chat says why. A grid given by a tow is safe from the
  shipyard's clearing for `ShipyardTowGraceMinutes` (10), and chat tells them to move it.
- A grid in an enabled safe zone gets no bounty, salvage or wreck contract, and an open
  contract nobody took on it is withdrawn. A contract someone took goes on, because a
  shipyard is inside its station's zone. In an offline partition the zone counts as it
  was when the partition went offline.
- Against abuse, for salvage and wrecks: a contract on a grid its taker or their faction
  (not OCT) owned or mostly built when it was posted fails at once, unpaid, so nobody sells their
  own ship to OCT by disowning it. It also fails when a third player comes to own the
  target. A grid once given away never gets another contract. The given grid's blocks
  count against the new owner's limits, not the old owner's.

The targets, the grids last reported and the grids given away are world variables
(`Octahedron-Contracts`, `Octahedron-Contract-Grids`, `Octahedron-Contract-Given`); the
contracts themselves are saved by the game. The plugin checks them every 30 seconds.

The contract system is kept on the World Authority of a cluster, so the plugin posts
there; the nodes carry the deliveries, and OCT's payment and an acquisition's stock
happen where the contract finishes, through the World Authority. The World
Authority has no grids. Every minute each node reports the candidates among the grids it
simulates and, every 30 seconds while there are any, the contracts' targets, on the topic
`contracts/report`; the answer names the targets to watch, the grids to scrap and the
grids to give to a player, and chat for players, which every node gets once and the
one with the player delivers. The give order goes to all nodes and the one holding the grid
carries it out; it repeats until a report shows the new owner, also after a handover or
a World Authority restart. The World Authority
decides from those rows, which outlast a grid's partition going offline. It reads the
offline partitions and the time they went offline from the gateway's
`GET /registry/partitions/offline`, once a minute, with the cluster's join token. A
target counts as gone once its node has reported without it for 3 minutes, or stayed
silent for 15: a handover or a node restart doesn't end a contract. A partition that
went offline before it ran this plugin isn't known until it wakes once.

`!oct contracts` (admin) lists OCT's contracts at every station and counts the
others, the grids and wrecks it knows and the reports served; `!oct contracts post` checks and posts at once
(run on the World Authority of a cluster). `!oct contract accept <id> [identity]`,
`deliver <id> [fill]` and `complete <id>` take, hand in (from the caller's
inventory; `fill` adds an acquisition's items first) and complete (a bounty or
salvage) a contract as the caller, for tests and support.

### Leaderboard

The station panels named `LeaderboardBlockName` ("Curved LCD Panel - Station Lounge
Leaderboard", 2 side by side in every station's lounge) rank the factions by their value in SC: the cash in
the faction's bank account, with the money held for its market orders, plus its grids.
They also show each faction's PCU. A grid is the faction's when its biggest owner is a
member. Its value is what its blocks are built of, their mounted components, plus its
inventories, each item at the stations' price: the middle of OCT's bid and ask as
the station's market shows them (the specialty price while the day's quota lasts),
averaged over the stations that trade it. Items OCT doesn't trade count for nothing.
Only factions players made are on it: OCT, the economy's NPC factions and those of mods
like MES are left out (a faction with a type other than player made, or without a human
member). With `LeaderboardOctPlayers` on (off by default) OCT's players are ranked too,
one by one by name with their own bank account and grids, and the board's title drops
"FACTION". The panels show the first `LeaderboardRows` (15)
in Monospace at `LeaderboardFontSize` (0.45), 45 characters a line.

Every `LeaderboardIntervalSeconds` (600) the server counts the world's grids, a few
per tick (2 ms at most), and then writes the board to the panels. The log has the
count: `Leaderboard: counted N grids in T ticks, X ms of work`. On a cluster every
node counts the grids it simulates: it asks the World Authority for the prices, sends
it every grid's value, PCU and biggest owner, and gets the ranked factions back. The
World Authority keeps each grid's latest count, whichever node made it, in its world
(the `Octahedron-Leaderboard-Grids` variable), and sums them per faction by the
owners' factions as they are now. A grid in an offline partition, as the gateway's
`/registry/partitions/offline` lists them, counts as it was last counted before it
went offline, until it wakes and its node counts it again; this survives a cluster
stop. A grid its node has reported without for two intervals and two minutes (the
gateway's table is read every minute), and in no offline partition, is gone; so are the grids of a node silent for that and one interval more. The World
Authority adds the bank accounts and the market's held money, and logs each ranking:
`Leaderboard: N grids from node, ranking G grids, O in offline partitions`. A
partition that went offline before the World Authority kept grids counts once it
wakes. Grids stored in Hangar don't count.

`!oct leaderboard` (admin) counts now and lists every faction with its cash, grids
and PCU; on a cluster, run it on a node.

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

On a cluster at most `drops.maxActive` drops are out at a time for all players
together (`config/cluster.cfg`, 5). A drop due while that many are out is
skipped, and the World Authority also deletes the oldest drop GPS markers over
the cap (SE-1146).

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
- Guard. Before patching, the plugin hashes what the gate relies on: the instructions
  and signatures of `GetSpawnPlacement` and `CalculateSpawn`, the `PathDetails` fields
  and the `SpawningType` names. The hash must be in `MesAcceptedMethodHashes`;
  otherwise it logs a Critical line and MES runs unpatched. The MES version is checked
  against `MesAcceptedVersions` only for the log: a new version with an accepted hash
  is patched, with a Warning. Every start logs both. When the hash changes, review
  the MES change, then add the new hash (and version) to the defaults and to
  `config/octahedron.cfg`: a keep-world deploy changes only the options that file
  names.
- The cluster's NPC sweep reads the gap bands from `CometWorks.ModMes.InGap.v1`, which
  the plugin publishes, and removes unvisited NPC grids there sooner.

The pattern settings (`MesCellSize`, `MesGap`, `MesGapSyncDistances`, `MesJitter`,
`MesSeed`) are read once, when MES is patched: every node of a cluster must use the
same ones, and each logs them with a pattern fingerprint, which must be equal on all
nodes. The other options apply at once. Each spawn the gate decides logs
`spawn placed` or `spawn refused`, and a summary every 10 minutes.

### MES reputation triggers

THIE's trigger `THIE-Bullet-Reputation-Loss-TriggerA` takes 5 reputation for any damage
to its ships, collisions included, with no cooldown, and from every member of the
attacker's faction. Every player is in OCT, so one player bumping a THIE ship cost all
of them 5 per damage event (SE-1294). With `MesReputationFix` on (the default):

- A damage trigger of any MES ship that changes the attacker's reputation ignores
  collision (`Deformation`) and `Environment` damage, and fires at most once per
  `MesReputationCooldownMs` (1000) on that ship. Triggers with a longer cooldown of
  their own keep it. The trigger is fixed the first time its ship takes damage, so
  ships from an older save get it too.
- A reputation change that MES would apply to every member of the attacker's faction
  stays with the attacker when that faction is OCT. Members of other factions are
  still charged together, as MES does.
- With `MesReputationOctExempt` on (the default), damage an OCT member does to an MES
  ship changes nobody's reputation, whatever the damage type: MES spawns the ships OCT
  players are meant to hunt. Players outside OCT keep MES's rules with the two
  changes above.

How: Harmony prefixes on MES's `TriggerSystem.ProcessDamageTriggerWatchers`,
`FactionHelper.ChangeDamageOwnerReputation` and
`FactionHelper.ChangePlayerReputationWithFactions`. If MES no longer has the members
they use, the plugin logs a Critical line and MES runs unpatched. Turning the option
off at runtime leaves triggers that were already fixed as they are until the next start.

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

A Quasar cluster loads it as a dev folder too, with Magnetar 2.4.3.9 or later.
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
cluster setup starts from the plugin's compiled defaults. Deploy Octahedron
(`scripts/deploy-octahedron/deploy.py` in the workspace) stages the values of
`../config/octahedron.cfg` over them: with `--wipe` before the new cluster's first
start, and in every later update of that cluster. An update keeps the saved values
while the config's schema (option names, types, descriptions) stays the same; when
it changes, the values go back to the defaults, and the deploy stages the file
again. By hand, use the cluster's plugin configuration editor or
`POST /api/v1/clusters/{name}/plugin-configuration-preparation`. `OCTAHEDRON_CONFIG`
and `Octahedron.cfg` in the instance folder are ignored there.

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

The cabs (SE-1279) are tested from the other side, by sebot taxi drivers and a passenger bot on a bench
server: cluster-regression `sebot/tools/trader/taxitest.py`.

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
`tests/test_gravity_drive.py` pilots ships with a gravity drive, and with
`--game-version` checks the other side of the version gate.
`tests/gravity_drive_cluster.py` pilots them on a cluster node.
`tests/test_clang_drive.py` runs one server without a client, with Relative Top Speed from
`OCT_WORKSHOP` and tests/clang-probe pushing ships beyond their thrust as a clang drive would:
pushed at 3x, 1.5x and 0.8x, at 3x through AddForce as a mod pushes, an NPC's ship, one-frame
speed jumps of 3 s, 0.1 s and 0.3 s of thrust, a ship at full override, ramming a wall at
50 m/s in a safe zone and outside, a ship without thrusters ramming, a ship at rest rammed in
the zone, a heavy ship falling toward the planet, the holes of SE-1024 (a loose block in a
cage, a gravity generator, a player's grid holding a bigger NPC one), DIS-0059's false
positives (full reverse at the boost ceiling, a missile thrusting two ways at 355 m/s),
Viktor's "Clang with Piston" contraption with a thruster added, and with `CLANG_BLUEPRINTS`
two clang drives from the Workshop, a sliding door one and a merge block one.
`tests/clang_drive_cluster.py` checks a speed jump, a ram in a safe zone and a crash
outside on a cluster node, without a client.
`tests/test_taxi_cab_mass.py` runs one server without a client: tests/cab-mass-probe docks a
cab with a piston head and a wheel at a static station, with a ship docked to it too, and
checks the mass a taxi pickup offer reports. It counts the cab and its head and wheel, not
the docked ship, and stays the same after the cab takes on cargo.
`tests/test_target_neutrals.py` builds blocks with Target Neutrals on in each way
with the client, welding them through shipyard orders, with the rule off too, and a
test mod's turret whose definition has it on. It buys ships at a store block and
respawns in one, through the tests/neutrals-probe plugin, and spawns one for an NPC,
which keeps it. It reads the setting on the client and in the saved world, across a
restart.
`tests/target_neutrals_cluster.py` does it on a cluster with a shipyard order, a paste
and blocks placed by hand, and keeps a player's later choice through handovers and
a cluster restart.
`tests/test_shipyard_zone.py` restricts the zones as the world does, then checks
with the client that Alpha Station's shipyard welds a projection, while in the
same zone a player's hand welder and grinder, ship welder and grinder, and an
OmniArc welder on a player's grid named like a shipyard do nothing, and that the
player's tools work outside the zone. It runs with the plugin's rules on, on
with the zones holding every test grid from the start, and off.
`tests/test_shipyard_own_blocks.py` gives one player a repair slot with a ship of theirs and
one of another player's in the shipyard, each with a damaged block and a projection: the
welders repair the slot's player's ship and leave the other alone until that player joins
their faction.
`tests/test_shipyard_tool_speed.py` runs the server alone, with OmniArc and ToolCore, and
measures how much an OmniArc welder and deconstructor weld and grind in a minute on rigs far
from the stations: a shipyard grid of OCT's, one in a player's turn, a player's grid, a
player's grid named like a shipyard with a block of OCT's, and a player's grid made neither
editable nor destructible.
`tests/test_shipyard_align.py` orders 150 m ships that only fit along the shipyard,
one behind a cryo chamber facing sideways and one with its only cockpit sideways,
and short ones that fit: one with its cockpit along the shipyard, which stays so, one
whose cockpit comes after a cryo chamber, a passenger seat and a bed, one whose
sideways bed is flagged as main, and one with nothing that flies the ship, which
keeps the grid's forward. It checks how they lie in the saved world. `OCT_ALIGN_BP` adds a real `bp.sbc`.
`tests/test_shipyard_placement.py` checks in the saved world where ships end up
along the shipyard: short ones at the 1/3 point nearer the station, at pole and
equatorial stations, the widest small grid ship among them, and a ship of 1/4 of
the length in the middle. With the arm's time limit forced to 0 it checks that a
press is refused and that a queued paid order ends with its reason and everything
given back.
`tests/test_shipyard_sight.py` loads a world built by `build_world.py` with the sight
probe (`tests/sight-probe`), a server plugin that casts rays through the game's
physics from the lobby spots of `check_sun_clearance.py` to each shipyard's free
build space, windows letting them through. It needs no client, prints how many
spots see the center and the 1/3 points and, with `-v`, what blocks each ray, and
fails when an equatorial station's center is in sight of fewer than half its spots.
`tests/test_shipyard_order.py` orders a ship at a station table with the client
(the quote, then the order) and checks the saved world after the shipyard finished it, then lets a bigger
order run out after a server restart; with `OCT_SHOTS` it also takes
screenshots of the welding from among the welders.
`tests/test_shipyard_beams.py` checks the welders' beams as a client sees them
during a long build: the client and the server run the `tests/beam-probe` test
plugin, which logs once a second what ToolCore says about the shipyard's tools. The
client goes beyond sync distance and comes back, then leaves the game and joins
again; all welders must shoot on it afterwards and some must hit, also once only
integrity is left. `OCT_BEAM_MIX=1` builds functional blocks too, `OCT_BEAM_ZONE=allow`
is the control with zones that allow welding, and `OCT_BEAM_OFFLINE=1` stops the server
mid-build and loads its save offline. `tests/shipyard_beams_cluster.py` does the same
check on a test cluster, with a trip to a station on the other node.
`OCT_BEAM_STEPS=handover` has the server forget which clients have the tools before the
order, as a node does after a handover that brought the client along. With the
OctahedronMod's filament tools (`OCT_SHIPYARD_STATION=Moon`) the client must draw their
filaments too.
`tests/panel-text/PanelTextTests` checks the queue panel's layout without the game
(`dotnet run -c Release --project tests/panel-text/PanelTextTests`) and
`tests/test_queue_panel.py` on a real station.
`tests/builds/BuildTests` checks the queues without the game
(`dotnet run -c Release --project tests/builds/BuildTests`): the labor fee, quotes
from OCT's stock, paid orders and their turns, the purchase at the start all or none,
hydrogen, the sale of what a turn leaves, towed grids, free orders, the queue, human
players' orders before the bots', where a bot's finished ship goes, the formats,
blueprints in parts, and that no money
is made or lost. `tests/test_shipyard_human_priority.py` checks in game, with a
second client as the bot, that a human player's order cancels the bot's running free
build (its ship removed, the free build not counted), pushes the bot's waiting order
out of a full queue, and that a queue full of human players' orders refuses another
one. `tests/test_shipyard_turn.py` checks a
player's turn in game at Alpha Station: who owns what, the window, the purchase, the
sale, overflow, a ship in the way, a tow reaching the shipyard, restarts;
`tests/shipyard_turn_cluster.py` does the same on a cluster with moves of the station's
partition. `tests/test_shipyard_docked.py` checks what is in a shipyard's way: a ship docked
at its connectors isn't, a block of a ship in the free space is. `tests/test_shipyard_off.py` checks that a shipyard is off without a job and on only
for the slot's player and faction, and a build and a repair slot going on after the player left;
`tests/shipyard_off_cluster.py` does that with the player on another node and the station's
partition free to go offline, and `tests/shipyard_stop_cluster.py` with a cluster stop and start
in the middle of a build, with nobody connected. `tests/test_shipyard_hydrogen.py` checks where a
new ship's hydrogen and oxygen come from, on free and paid builds, and
`tests/shipyard_hydrogen_cluster.py` does that on a cluster; both, and the other tests written for
half full tanks, pin `ShipyardHydrogenFillPercent` 50 and `StationDockHydrogen` off through
`rig.CONFIG_PINS`. `tests/test_refill_rules.py` checks the SE-1387 rules in game at Alpha: a new
ship's full battery and H2 tank and empty jump drive, the hydrogen a docked ship buys at the Store's
ice price, a partial fill with little money, nothing for a tank turned off or barely short, nothing
at the shipyard; `tests/dock-hydrogen/DockHydrogenTests` checks the bill's arithmetic
(`dotnet run -c Release --project tests/dock-hydrogen/DockHydrogenTests`). `tests/test_bottle_swap.py` leaves a half full and an empty bottle in Alpha's
Free Tools box for full ones and checks the next refill takes them out and tops the box up, also with a rule that
lists no hydrogen bottles; `tests/bottle_swap_cluster.py` does that on a cluster. `tests/test_data_pads.py` checks that no lore pad tells more than one rare ore or ice asteroid and that together they tell all, then in game that every Free Tools box at Alpha keeps one pad, a taken one comes back as another, every pad turns up, the ice pads' GPS lines are the asteroids' centers in the save, and a player's own pad stays (`--offline` runs only the first part). `tests/test_paid_builds.py` checks paid builds in game at Alpha Station with
the shipyard's mods: the quote and the order, the hydrogen from a docked ship's tank into
the new ship's, the payment and OCT's stock, a press while it builds, an order queued
behind the finished ship and canceled after a restart, and a free order canceled with
the button itself.
`tests/test_shipyard_left_out.py` checks what a ship is built without: a player gets
only the blocks of the DLCs they own, built by the station's welders, and a paid ship
with a grid no welder can place is delivered without it, the grid named and its
components refunded. An unfinished subgrid, with a block intersecting the main grid and
a battery short of parts, doesn't remove the ship, free or paid; the same battery on the
main grid still does. `OCT_BLUEPRINT` and the case `blueprint` order a real ship's
`bp.sbc` free.
`tests/test_table_rules.py` runs two players at one table on a world built by
`build_world.py`: the screens, one press to order and one to cancel, the loader's
sole access, removing another player's blueprint, a full queue and an owner who
logs off; with `OCT_SHOTS` it also takes screenshots of the screens.
`tests/test_table_watchers.py` has three players with Multigrid Projector at one
table: a blueprint the shipyard refuses (`OCT_BLUEPRINT`, two grids that only
connectors join) gives the player watching no "Blueprint can't be found" box and
leaves no hologram once cleared, and one it takes shows for the watcher and for a
player who joins later. The `tests/table-probe` test plugin on the server and the
clients logs any change to the table's clipboard while its activation runs.
`tests/test_first_load_mgp.py` checks that a player's first blueprint with a rotor
from Multigrid Projector after joining is taken. The `tests/remap-probe` test plugin
on the server holds the activation's id remap after the rotor's base until the
server has handled the blueprint, so every run hits the timing that refused it.
`tests/arm/ArmTests` checks the support arm's path planning without the game
(`dotnet run -c Release --project tests/arm/ArmTests`): pipes around the ship and
obstacles, the turn cost, docking, the cap, giving up an armor block, no plan when
nothing is reachable and the reason logged for it. `tests/test_support_arm.py`
checks the arm in game on a world built by `build_world.py`: a large ship docked to
the arm's connector across a server restart, the same ship ending in a cap with the
admin fill, and a small ship on a small rotor; each time the arm and its projector
are gone afterwards and the rotor heads stay. Without the restart it also checks
that the whole arm is complete when the projector turns on, that the client sees
the shipyard's welders shoot, and that the client sees the finished ship dynamic
and owned by the player who ordered it. A build fails as soon as no arm or ship
block completes for 90 s. Two more builds stall on purpose: one missing only
2 of its 33 armor blocks is delivered, one missing 4 of them and one missing its
cockpit are removed and refunded. The player sits in the docked and the capped
ship's cockpit and flies the docked one. A ship ordered by an admin who ignores
PCU limits, over a lowered `GroupPcuLimit` and with a blocked block, is refused
without the flag and free with it, and is still welded and free with the flag
turned off once its build started, as on a cluster node without the admin's
settings.
`tests/test_free_build.py` checks the free-build rule with the client and a ship in
Hangar: a starter ship blueprint over 10,000 PCU is refused, a player who reaches
60,000 PCU gets no more starter ships and a paid quote at the shipyard, a ship with
superconductors is free and one with a prototech frame paid, and each of
`FreeBuildPcuLimit`, `FreeBuildExcludedComponents` and `FreeBuildBlockedBlocks`
changes the quote.
`tests/test_ignore_pcu.py` checks the "Ignore PCU limits" exemption with the
client: with the flag on, a free build over `FreeBuildPcuLimit` that starts no
cool-down, a free quote during a cool-down and a starter ship past both limits;
with it off, the same quotes are paid and the starter ship is refused.
`tests/test_money.py` checks the money rules with the client: OCT cut to its cap at
the first run, a new account's balance, no economy top-up of OCT while the rules are
on (and the game's with them off), the tax by `!oct taxday`, two payouts by
`!oct payday`, OCT topped up to its floor, the day's totals and the state read back
after a restart. The rig builds its world with
`build_world.py --seed 42` into `~/.se-test/<task>-world` and builds it again when the
generator, `config/` or `blueprints/` change; `OCT_WORLD_SOURCE` points it at another
world instead. `tests/test_money_restart.py` checks
without a client that a restart pays no hour twice and that hours missed while down
make one payout. `tests/money/MoneyTests` checks the arithmetic of the tax, the payout,
a whole day of payouts and Hangar's retrieval fee alone
(`dotnet run -c Release --project tests/money/MoneyTests`).
`tests/shipyard_cluster.py` orders a ship on a cluster from a client with Multigrid
Projector. A cluster's plugin messages carry 64 KiB at most, so a blueprint goes to and
from the World Authority in parts; `tests/mech_blueprint.py bp.sbc big` writes a ship
that needs them and still builds fast.
`tests/shipyard_workshop_cluster.py` loads blueprints into a cluster's table: a ship
without subgrids from a client without Multigrid Projector; `!bp` away from a table,
refused; one with subgrids from
such a client, and a copy of it without its bases' links, both cleared off the table
with the prompt, then loaded with `!bp` by link and by id (the second time from the
cache), the client's hologram checked, and built; the same from a client with the
plugin; and a `!bp` ship with DLC blocks for a player who owns none, built without
them. Blocks of mods the world doesn't load must be named by the order and left out.
A built ship must have the blueprint's grids and blocks, and the order must end with
it ready. See its docstring.
`tests/wa_economy_cluster.py` runs the money rules and the free-build cool-down on a
local test cluster, with the World Authority keeping both: see its docstring.
`tests/test_shipyard_deadline.py` checks a ship left in the build space past
`ShipyardClearMinutes`, with a Hangar working copy (`OCT_HANGAR_REPO`) on the server: stored
for its orderer and counted once in their PCU, free to load at the station, removed and refunded
with the slot taken, a free ship just removed, a ship that left and came back moved out as any
grid, the same with Hangar off, restarts in the wait and after a refund, and a ship stored
with no next order in the queue.
`tests/shipyard_deadline_cluster.py` does it on a cluster: the node stores, another node loads,
the World Authority refunds, over node restarts in the wait and a graceful stop.
`tests/hangar_where_saved_cluster.py` checks `config/hangar.cfg` on a cluster: back at the
saved spot from another node, at the nearest clear spot with another grid's sphere there on
another node, and a ship from the deadline clear of the station.
`tests/test_hangar_fee.py` checks the Hangar fee with the client and a Hangar working
copy (`OCT_HANGAR_REPO`) on the server holding ships stored at the stations: a load
outside the safe zones refused, the same station free, another station 100 SC for
1,000 PCU paid to OCT, a fee the player can't pay refused, and a fee of 0 free.
`tests/mes/` holds MesCluster's tests for the MES spawn gate:
`CellPatternTests` checks the cell pattern against a brute-force definition
(`dotnet run -c Release --project tests/mes/CellPatternTests`); `ds/run.py PLAN [cfg]`
runs a standalone server with MES, a few encounter mods and a probe plugin that fires
MES spawn requests, and `ds/check.py gate|guard` judges it (with `ds/new-version.cfg`,
`check.py gate-new-version`: an unlisted MES version with an accepted hash is patched);
`cluster/mes_cluster.py`
runs the same with the cluster's NPC census on a local test cluster;
`check_placements.py LOG...` checks the gate's decisions in any server logs.
`ds/plan-reputation.txt` checks the MES reputation rules: an OCT player and a player
of another faction each ram a THIE Atlas and hit it with bullets. `ds/check_reputation.py`
judges it: `fix` with `ds/reputation.cfg` (the defaults), `attacker` with
`ds/reputation-attacker.cfg` (`MesReputationOctExempt` off), and `off` with
`ds/reputation-off.cfg`, the old behavior the test catches.
`tests/test_encounters.py` checks the encounters with a client that has no mods:
the timer's first space cargo ship, each spawn type spawned by command in space, on
EarthLike, high above it and on the Moon, the client seeing the spawned grids, and
the mods' shares of 2,000 spawn attempts per type and place.
`tests/patches/PatchTests` checks station patches without the game
(`dotnet run -c Release --project tests/patches/PatchTests`): the patch format,
the checks of a whole patch before any op applies (a bad last op refuses the
whole patch), the mount neighbor of added blocks and the island check, swaps that
keep a grid whole, and vetoes; and the blueprint diff: which change becomes which
op, mod storage keys, grid settings, and no ops for the station blueprint written
another way. `tests/station_patch_cluster.py` runs them on a local test cluster with
an admin client (`tests/edit-probe` reports the grids from whichever node holds
them): patches applied with `!oct patch apply` on Alpha Station and its shipyard,
the refusals, a docked ship and a paid build, then a node restart and a cluster stop
and start; Alpha Station replaced by an edited copy of its blueprint (a light's
radius, an LCD's text and mod storage, a Rotary Airlock door, a container with
items, a name, a grid setting), both copies prepared by `station_patch.py`, with
the market unchanged; and staged patches in
`StationPatches` for Beta Station, ops and a blueprint, applied once by the node
that loads it.
`tests/test_world_patch.py` checks world patches on a plain server: a world built
from before the space elevators gets the patch's lane computers once, a restart adds
none, and a world built now lists the patch and keeps its own computers.
`tests/world_patch_cluster.py` does the same on a local test cluster (WA and two
nodes): the World Authority has a node add the grids, both log them, the grids are in
partitions, a stop and start adds nothing, and the admin client finds one grid of
each name at its place.
`tests/market/MarketTests` checks the market's bookkeeping without the game
(`dotnet run -c Release --project tests/market/MarketTests`): OCT's prices with the
tax and rounding, stock, the hourly restock and profiles, specialty and drift, the
zone chips, build purchases and sales, the formats, and that no money is made or lost.
`tests/test_store.py` checks the market in game: the client's store probe
(`tests/store-probe`) sends the requests of the game's store screen to Alpha's and
Moon's Store blocks, the same as a player's. It covers the shown stock and prices,
buying and selling, the refusals, drift, the specialty and its quota, OCT's limit, the
restock and profiles, the tax, a friend of OCT, the zone chips, and the stock after a
restart. `tests/station-probe` is the server plugin of test commands several tests
share (`station_tests.py`): it docks ships, sets factions and reputation and pays
accounts. `tests/console-probe` runs chat commands as the server's console for the
tests on a cluster (`console_probe.py`). `tests/world_config_cluster.py` buys and sells
on a node of a cluster, with the stock on the World Authority.
`tests/test_contracts.py` checks the contracts with the client's admin commands: three
hauling contracts and an acquisition at every station and none of the game's, a bounty
on an NPC grid and salvage of a derelict placed near Alpha Station, a hauling and an
acquisition handed in and paid by OCT with the items in OCT's stock, the derelict
finished at the shipyard by the plugin with the derelict given to the player, a player's own
ownerless block refused, the bounty completed, and after a restart the same open contracts. `tests/contracts/ContractTests` checks the rewards, time limits
and amounts alone (`dotnet run -c Release --project tests/contracts/ContractTests`), and
the rules of bounties, salvage and wrecks against a clock the test sets, the offline week
both ways among them. `tests/contracts_cluster.py` runs them on a local cluster; `clock`
in the console probe's plan moves the clock that ages offline partitions.
`tests/leaderboard/LeaderboardTests` checks the leaderboard's arithmetic, ranking,
text and wire formats alone (`dotnet run -c Release --project tests/leaderboard/LeaderboardTests`).
`tests/test_leaderboard.py` checks it in game with the client and the station probe: two
players' factions and no NPC ones ranked, a faction's cash, a docked ship's value and
PCU against the game's block definitions and OCT's prices at every station, the station panels and their refresh, and how long a count of the
world takes. `tests/leaderboard/leaderboard_cluster.py` checks it on a local test
cluster with the World Authority and a node, and that the grids of offline partitions
keep counting, across a warm start too, until they wake or are deleted.
`tests/offline-characters/ParkingTests` checks the bookkeeping of offline characters
alone (`dotnet run -c Release --project tests/offline-characters/ParkingTests`).
`tests/test_offline_characters.py` checks them with the client and the character probe
in `tests/offline-characters/probe`, `tests/offline_characters_cluster.py` on a local
test cluster: a partition load, a warm start, and a node or the World Authority killed
right after the inventory is kept.
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
