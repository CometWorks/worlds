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

An item can name several ids separated by commas, like
`PhysicalGunObject/Welder2Item,PhysicalGunObject/Welder3Item`. Their amounts count
together toward the minimum, and each one missing is one of them at random.

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

### Shipyard orders

A player loads a blueprint into a station's hologram table (`Console Block -
Station ShipyardProjection`). The table's large screen (`ShipyardQuoteSurface`, 1)
shows the main message: free or paid, what is wrong if anything, and to press the
button. The first screen of the billboard next to it (`ShipyardDetailsPanelName`,
`Billboard LCD Ship Build Details`) keeps its first line and lists the ship's bill
of materials under it; a station from before the billboard shows the list on the
table's screen until the world is wiped. The billboard's second screen, how to
order, comes from `build_world.py`.

One press of the first button of the button panel next to the table
(`ShipyardConfirmButtonName`, `Button Panel - Station ShipyardConfirm`) orders the
ship from the station's shipyard. That clears the blueprint and the list, and the
screen tells the player a second press cancels; it does so while the build hasn't
started. A press also shows the quote first and asks for another when the screen
doesn't show the presser's quote of this blueprint at these terms, so a build that
turned from free to paid is never ordered unseen.

While a blueprint is on the table only the player who loaded it can order it or
cancel an order; anyone else's press is refused, their own queued order included.
Anyone can remove the blueprint with the Remove button of the table's terminal or
load another one over it, which frees the table. An empty table's button cancels
the presser's own order. A blueprint found on the table after a restart or a move
of the station to this node has no known loader: the first player to press gets it.

The plugin gives the button a toolbar item (its own On switch) so that clients
offer it; the item never runs. `!oct order [table name]` (admin) presses the button
of the nearest table and `!oct table remove [table name]` (admin) removes its
blueprint. The table's blueprint is cleared once the ship is in the queue, when the
player who loaded it leaves the sphere around the station's grids or this server (a
log-off, or a handover to another node on a cluster), and `ShipyardTableMinutes`
(15) after it was loaded. A queued order stays when its owner leaves: it is built
and handed over as usual, its payment is already in its build ledger, and the owner
can cancel it when back if it hasn't started.
`ShipyardOrderMinutes` (60; 0 turns orders off) is how long a build runs at most.

- A ship is a free build for a player with less than `FreeBuildPcuLimit`
  (10,000) PCU, counted as for the starter ship, once `FreeBuildCooldownMinutes`
  (10) have passed since their last free build ended, and when it has none of
  the blocks `FreeBuildBlockedBlocks` names (prototech blocks, large ion
  thrusters and jump drives). The cool-down starts when a free build ends with
  the ship handed over, finished or not; a canceled order or one whose ship was
  removed starts none. The plugin keeps the times in the world variable
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
- Any other ship is a paid build. The payer is the player, or their faction when
  their last kiosk request had the Faction switch on, which needs the trading
  rank. The quote lists each component of every block with where it comes from:
  the payer's ledger at the station first (free), then bought from the cheapest
  player listings and OCT, as the market's Buy does. On top comes the labor fee,
  `ShipyardLaborFeePer10Pcu` (1) SC per 10 PCU of the blueprint, rounded up,
  paid to OCT on every build. The materials also include the hydrogen to fill
  the ship's H2 tanks to `ShipyardHydrogenFillPercent` (50 %): from the H2 tanks
  of the payer's ships docked at the station's connectors first, the rest as
  ice (`ShipyardHydrogenLitersPerIce`, 20 L per kg) from the ledger and the
  market. When the station has too little ice, the order goes ahead and the
  tanks get less.
- On order the plugin moves the components from the payer's ledger into a build
  ledger (an uncounted reservation `build <id>` of the payer at the station),
  buys the rest into it at the quoted price (a higher price refuses the order
  and shows the new quote), takes the fee and queues the order. Canceling
  releases the build ledger to the payer's ledger, the bought components and the
  hydrogen's ice included, and OCT pays the fee back.
- The queue of each shipyard holds at most `ShipyardQueueMax` (6) orders, the one
  being built included, one per player: more don't fit the queue panel above the
  table. A full queue shows on the table's screen and refuses the press. The
  station's queue panels (`ShipyardQueuePanelName`, `Text Panel - Station
  ShipyardQueue`) show it, and which ship blocks the shipyard.
- The ship must fit the shipyard's free space, the largest box between its
  welders free of its blocks, turned so that its main cockpit points along the
  shipyard, its components the shipyard's containers, and it may have at most
  `GroupPcuLimit` PCU, the cap of a ship, unless an admin who ignores PCU limits
  ordered it. The quote checks that; an order that
  doesn't fit when its turn comes ends with everything given back.
- The next order starts when the shipyard's free space is empty: a ship left
  there has to fly out first. The ship is built on the free space's center line:
  a ship shorter than 1/4 of the shipyard's length around the 1/3 point nearer the
  station, where it shows better from the lobby; a longer one in the middle. It is
  built from
  a support arm (below) on a shipyard with arm rotors. A shipyard without them
  starts the ship as a static grid of one of its blocks, built complete and the
  player's, with an NPC-owned projector and battery named `Shipyard Order` next
  to it, outside the ship's bounds. Either way the projector projects the
  blueprint (Multigrid Projector builds its subgrids), the components go into the
  shipyard's cargo containers, from the build ledger for a paid build, and its
  OmniArc welders are switched on through their terminal switches. The welders
  take the components from the containers like any welder, so a free build gets
  its components loaded too, and what is left is removed at the end; no tool has
  to skip the component cost. While a build runs the plugin also tops up the
  shipyard's reactors to 1000 uranium each, at OCT's cost: without power the
  welders stop. Floating objects in the shipyard main grid's bounding box are
  removed while a build runs. Before a build starts, grids in that box that no
  player owns are removed too, and so are players' wrecks: grids with no power
  and no cockpit or remote control in their whole mechanical group. Characters,
  players' other grids and the shipyard's own grids stay; those still block the
  next build until they leave. In a no-welding safe zone ToolCore on the clients,
  which don't have the shipyard exception above, would turn the tools off and
  show no beams (SE-441): while a build runs, the plugin tells the clients every
  10 s that the zones don't hold the shipyard grid, and puts it back at the end.
  The server's own zones are left alone. A client that gets the shipyard streamed
  in during a build (it comes back in range, joins or crosses to this node) still
  turns the tools off before that message comes, and ToolCore tells it only when
  the server's switch changes. So the plugin sends each such client the switch itself
  at the next 10 s check and once more at the one after (SE-782), and nothing to the
  clients it already told. ToolCore is the mod's own code: this reaches its session
  and packet by reflection and does nothing if they are gone.
- A build whose ship gains no integrity for 60 s stops. Plain blocks, armor and
  decorations, are those of the plain `CubeBlock` type; every block of its own
  type, lights, panels and conveyors included, is functional. When the ship
  misses only plain blocks, at most 10% of them, it is delivered as usual, and a
  paid build's payer gets those missing pieces' components back, not the labor
  fee. When it misses a functional block or more plain ones, the ship is
  removed and the build refunded: every component it took and the labor fee go
  back to the payer, and a free build doesn't count. The player
  gets a chat message either way.
- Every projected block welded onto the ship or its subgrids is the player's,
  owner and `BuiltBy`, so it counts toward the player's PCU and the projection
  limits check it for them.
- The order ends when every block is complete, or when the time is up. The
  welders go off, the projector and battery are removed with the projection,
  what is left of the loaded components is taken out of the shipyard's
  containers and welders (back into the payer's ledger for a paid build), a paid
  build's hydrogen goes into the ship's H2 tanks (the ice of what doesn't fit
  goes back, with a warning when none does), and the ship, finished or not,
  becomes the player's dynamic ship. The player gets a chat message either way.

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
docked within `ShipyardArmDockSeconds` (1), the ship's H2 tanks get the build's
hydrogen and its batteries are charged. Without a dock the plugin fills the
tanks itself. Then the plugin hands the ship over and takes the arm down at
once, never the rotor head itself; the shipyard starts its next build after
that. An idle shipyard takes down anything an NPC built that is left on its
rotor heads. An unfinished ship that is delivered leaves the arm's projector
behind, and it comes down with the arm. A ship built without an arm loses its
scaffold projector the same way. What
the arm didn't use of its components is removed with the ship's leftovers, and
only the ship's go back to the ledger. The `!oct shipyards` admin command lists
the shipyards, their arm rotors and the running builds with their arm.
`!oct shipyard strip <station> <item>` takes an item out of a shipyard's
containers and welders, so tests can stall a build.

The ship's PCU cap (`GroupPcuLimit`) leaves out the shipyard and the arm, which
are in one mechanical group with the ship while it is built.

The queues and build ledgers live where the ledgers do, on a plain server or the
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

### Station ledgers

Each trade station keeps a warehouse ledger: how much of each item every player
and every faction owns there. The items exist only in the ledger, not in a
container. A station is a faction station entry of the world, by its name
(`Alpha Station`), so a new station blueprint keeps its ledger as long as its
entry keeps the name. The kiosks, the market and paid builds use the ledgers;
players have no command for them.

- An owner's items are free or reserved. A reservation has an id, holds items
  of one owner and is released back to them, given to another owner (a sale) or
  taken out (a build uses them). Market listings reserve items, build orders
  put their components into a reservation.
- A player may keep `LedgerPlayerLimitTonnes` (100 t) at each station, a faction
  `LedgerFactionLimitTonnes` (100 t), by the items' mass. Free items and listing
  reservations count; a build's reservation doesn't, since a big ship needs more
  components than that. A change that would put the owner over the limit is
  refused, so are taking more than there is, a fraction of a component, and an
  unknown item, station or owner. Leftovers of a build come back even past the
  limit; the owner then can only take items out until under it.
- Changes come in batches that apply all or none, so a sale can move the items
  and the payment together.

The server keeps the ledgers, on a cluster the World Authority. Other nodes send
their changes there as cluster-wide commands, each run once per operation id, so
a node that lost the answer sends the same change again without applying it
twice. The ledgers are saved with the world, as the sandbox variable
`Octahedron.Ledgers` in `Sandbox.sbc`, and on a cluster with the World
Authority's global saves. Every applied change is logged.

Admin commands, for tests and support (`!oct help` for the details):
`!oct ledger stations`, `show`, `deposit`, `withdraw`, `transfer`, `reserve`,
`fill`, `release`, `give` and `take`. An owner is `me`, a player's name,
`faction:TAG`, or `P<identity id>` / `F<faction id>`; a station is its name or
the start of it, and amounts are in units:

```
!oct ledger deposit Alpha me Ingot/Iron 1000
!oct ledger transfer Alpha me faction:OCT Component/SteelPlate 10
!oct ledger show Alpha
```

### Station kiosks

Players use the station services on the station's kiosk, a touch screen; the
chat commands are for admins. A kiosk is a text panel on a station's grids named
`KioskBlockName` (`LCD Panel - Station Lounge StationStorefront`, one per
station in the current blueprint). The station blueprints must keep the kiosks
clear of the landing pads, so landing ships hit neither the screen nor the
player in front of it.

The screens come from the Octahedron Kiosk client mod (`../kiosk-mod`), built on
TouchScreenAPI (Workshop 2668820525); both are in the world's mods, the kiosk
mod as Workshop 3827916260. The plugin selects the mod's screen script, `KioskScript`, on every kiosk. The mod
only draws and sends the player's requests as mod messages; the plugin checks
each one and does the work:

- The player must stand within `KioskReachMeters` (5 m) of the kiosk, and makes
  one request at a time, at most one every `KioskCooldownSeconds` (0.5 s).
- Deposit and withdraw move items between a docked ship and the ledger. A ship
  is docked when a connector of the station holds it; the kiosk lists every such
  ship the player has inventory access to, by the game's ownership and share
  rules, and works on the one they pick. Its items are those in the cargo
  containers and connectors of its mechanical group (rotors, pistons and hinges
  included) that the player may open; withdrawn items go into the cargo
  containers first. Only what fits is withdrawn. Gas bottles are not taken.
- From hand works on the player's character inventory instead of a ship.
- The Faction switch works on the faction's ledger. It needs a rank allowed to
  trade: leaders and the founder, or every member when a leader or the founder
  turned that on at the kiosk. The setting is kept with the ledgers.
- Transfer moves items between the player's and their faction's ledger at the
  station. Any member gives; taking needs the trading rank.

Items leave the ship before they enter the ledger, and the ledger gives them
before they arrive, so nothing is ever in both. A refused deposit puts the items
back. A withdrawal that no longer fits when it arrives goes back into the
ledger. If the World Authority of a cluster doesn't answer a deposit, the items
stay taken and the operation id is logged for an admin. Each client draws its
own screen, so several players can use one kiosk at once.

`!oct kiosks` (admin) lists the kiosks the plugin found, with their station and
screen script.

### Station market

With `MarketEnabled` on, the kiosks' Market tab trades items in the station ledgers
for money in the bank accounts. The operator trades for their own ledger and
account, or with the Faction switch and the trading rank for the faction's. Items
and money move in one step: a sale that can't move both moves neither.

- Buy takes the cheapest offers at the station first and OCT's stock at its price
  among them; Sell goes to the best orders first, then to OCT. Nobody trades with
  their own listings. Every sale between players pays `MarketFeePercent` (5 %) of
  its price to OCT.
- Offer lists items of the ledger at a price per unit; they stay reserved in the
  ledger and still count toward its limit. Order lists items wanted at a price; the
  money for them leaves the account until the order is filled, canceled or expires.
  A price is in SC or a percent of the station's reference price ("80%"). Listings
  expire after `MarketListingDays` (7), which gives the items or the money back. A
  player or faction may have `MarketMaxListings` (20) listings at a station. An
  order is passed over while its owner's ledger has no room for the items.
- OCT (`MoneyAuthorityFactionTag`) is the market maker of last resort. Its catalog
  is the same at every station: the items matching `MarketCatalog` but not
  `MarketCatalogExcluded` with a reference price, the game's minimal price / 10
  (`MarketPrices` sets one where the game has none). It sells what its ledger at the
  station holds at `MarketOctSellPercent` (140 %) of the reference and buys at
  `MarketOctBuyPercent` (60 %) until it holds `MarketOctMaxStockTonnes` (200 t) of
  the item there. Its ledger has no other limit.
- Each UTC day OCT adds `MarketDailyStock` (20 t of components and basic
  materials) to its ledger at every station, up to the same 200 t. Its
  acquisition contracts ask for what it lacks of that list (`Market.Missing`).
- `MarketSpecialties` names what each station buys at `MarketSpecialtyPercent`
  (80 %), up to `MarketSpecialtyDailyTonnes` (50 t) a UTC day per station; past
  that OCT pays its normal price. Alpha: platinum and magnesium, Gamma: iron,
  nickel and gravel, Beta: gold, silver and organics, Delta: cobalt and silicon,
  Moon: uranium, ore and ingots alike. North and South have none.
- OCT's prices are per station and drift with the volume: the net tonnes bought of
  an item at a station (sales count negative) raise its price there, doubling it
  at `MarketPriceDriftTonnes` (100 t), or lower it the same way, at most
  `MarketPriceDriftMax` (2) times either way. Half of the drift wears off in
  `MarketPriceDriftHalfLifeHours` (24 h).
- OCT sells zone chips at `ZoneChipStations` (Moon Station only) for
  `ZoneChipPrice` (1,000 SC) each, a fixed price that doesn't drift. A chip keeps a
  player safe zone up for an hour (the game's safe zone block takes 1 chip every
  60 minutes). OCT doesn't buy them, and they stay out of its catalog
  (`MarketCatalogExcluded`). Its ledger there is topped up to `ZoneChipStock` (100)
  chips every minute and before each purchase, so one purchase takes at most that
  many. Players may still list chips at any station.
- OCT's stations have no generated store items: the plugin stops the game's
  generator for OCT and empties what its stations had. Players' own store blocks
  work as before.

The market is kept with the ledgers, by the server or the World Authority of a
cluster, as the sandbox variable `Octahedron.Market`: the listings, the money held
for orders, the day's specialty purchases and the price drift. Nodes send their
requests there as cluster-wide commands, once per operation id. Every trade is
logged.

Admin commands, for tests and support: `!oct market show <station> [owner] [all]`,
`!oct market do <buy|sell|offer|order|cancel> <station> <owner> <item|listing>
[amount] [price]`, `!oct market tick [force]` (expire listings, and with force
restock OCT now), `!oct market stores [tick]` (the game's store items of OCT's
stations, after an economy tick) and `!oct market age <listing> <days>`.

### Hangar fee

With Hangar 1.1.0 or later on the server, set to `AdminSafeZonesOnly`, players store
and retrieve their ships only inside a station's safe zone, the world's only admin
safe zones, and Hangar records the zone on each stored ship. A ship comes back free at
the station it was stored at. At another station it costs `HangarRetrieveFeePer10Pcu`
(1) SC per 10 PCU, rounded up, paid to OCT: the labor fee of building it, with no wait,
and the ship keeps its inventory. A player who can't pay is told the price and the
ship stays stored. The plugin hooks Hangar by reflection, so it still runs without
Hangar, or with an older one, where retrieval is free.

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
- Acquisitions: for each item of the market's daily stock (`MarketDailyStock`) that
  OCT's ledger at a station holds less of, OCT posts one acquisition for the rest,
  less what open and taken acquisitions bring already, at most
  `ContractAcquisitionMaxAmount` (5,000) items, paying `ContractAcquisitionPricePercent`
  (100 %) of the market's reference price. The player delivers the items at the
  station's contract block from their docked ship or inventory, and they go into
  OCT's ledger there, not into the station's containers.
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
  center; the plugin then finishes the contract and scraps the derelict.
  `ContractSalvageMinutes` (180) to tow it.

The bounties' and salvage's targets are a world variable (`Octahedron-Contracts`); the
contracts themselves are saved by the game. The plugin checks them every 10 seconds.

The contract system is kept on the World Authority of a cluster, so the plugin posts
there; the nodes carry the deliveries, and OCT's payment and an acquisition's ledger
deposit happen where the contract finishes, through the World Authority. The World
Authority has no grids, so a cluster gets no bounties or salvage.

`!oct contracts` (admin) lists OCT's contracts at every station and counts the
others; `!oct contracts post` checks and posts at once. `!oct contract accept <id>`,
`deliver <id> [fill]` and `complete <id>` take, hand in (from the caller's
inventory; `fill` adds an acquisition's items first) and complete (a bounty or
salvage) a contract as the caller, for tests and support.

### Leaderboard

The station panels named `LeaderboardBlockName` ("Curved LCD Panel - Station Lounge
Leaderboard", 4 at every station) rank the factions by their value in SC: the cash in
the faction's bank account, with the money held for its market orders, plus its grids.
They also show each faction's PCU. A grid is the faction's when its biggest owner is a
member. Its value is what its blocks are built of, their mounted components, plus its
inventories, each item at the stations' price: the middle of OCT's bid and ask as
the station's market shows them (the specialty price while the day's quota lasts),
averaged over the stations that trade it. Items OCT doesn't trade count for nothing.
OCT and the NPC factions are left out. The panels show the first `LeaderboardRows` (15)
in Monospace at `LeaderboardFontSize` (0.45), 45 characters a line.

Every `LeaderboardIntervalSeconds` (300) the server counts the world's grids, a few
per tick (2 ms at most), and then writes the board to the panels. The log has the
count: `Leaderboard: counted N grids in T ticks, X ms of work`. On a cluster every
node counts the grids it simulates: it asks the World Authority for the prices, sends
it the totals per faction and gets the ranked factions back, summed over the latest
totals of every node. The World Authority adds the bank accounts and the market's
held money, and forgets a node's totals after three intervals without news. Grids
stored in Hangar don't count.

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
`tests/test_shipyard_placement.py` checks in the saved world where ships end up
along the shipyard: short ones at the 1/3 point nearer the station, at pole and
equatorial stations, the widest small grid ship among them, and a ship of 1/4 of
the length in the middle. With the arm's time limit forced to 0 it checks that a
press is refused and that a queued paid order ends with its reason and everything
given back.
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
`tests/builds/BuildTests` checks the queues and build ledgers without the game
(`dotnet run -c Release --project tests/builds/BuildTests`): the labor fee, quotes
from the ledger, listings and OCT, paid orders all or none, cancel, start and finish
with leftovers and hydrogen, free orders, the queue, the formats, and that no money
is made or lost. `tests/test_paid_builds.py` checks paid builds in game at Alpha
Station with the shipyard's mods and the kiosk mods: the quote and the order, the
hydrogen from a docked ship's tank into the new ship's, the payment, a press while
it builds, a faction-paid order queued behind the finished ship and canceled after
a restart, and a free order canceled with the button itself.
`tests/test_table_rules.py` runs two players at one table on a world built by
`build_world.py`: the screens, one press to order and one to cancel, the loader's
sole access, removing another player's blueprint, a full queue and an owner who
logs off; with `OCT_SHOTS` it also takes screenshots of the screens.
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
`tests/test_free_build.py` checks the free-build limits at their defaults with
the client and a ship in Hangar: a starter ship blueprint over 10,000 PCU is
refused, and a player who reaches 10,000 PCU gets no more starter ships and a
paid quote at the shipyard.
`tests/test_ignore_pcu.py` checks the "Ignore PCU limits" exemption with the
client: with the flag on, a free build over `FreeBuildPcuLimit` that starts no
cool-down, a free quote during a cool-down and a starter ship past both limits;
with it off, the same quotes are paid and the starter ship is refused.
`tests/test_money.py` checks the money rules with the client: OCT cut to its cap at
the first run, a new account's balance, no economy top-up of OCT while the rules are
on (and the game's with them off), the tax by `!oct taxday`, two payouts by
`!oct payday`, OCT topped up to its floor, the day's totals and the state read back
after a restart. `OCT_WORLD_SOURCE` points the rig at a world built by
`build_world.py` instead of `octahedron/server`. `tests/test_money_restart.py` checks
without a client that a restart pays no hour twice and that hours missed while down
make one payout. `tests/money/MoneyTests` checks the arithmetic of the tax, the payout,
a whole day of payouts and Hangar's retrieval fee alone
(`dotnet run -c Release --project tests/money/MoneyTests`).
`tests/wa_economy_cluster.py` runs the money rules and the free-build cool-down on a
local test cluster, with the World Authority keeping both: see its docstring.
`tests/test_hangar_fee.py` checks the Hangar fee with the client and a Hangar working
copy (`OCT_HANGAR_REPO`) on the server holding ships stored at the stations: a load
outside the safe zones refused, the same station free, another station 100 SC for
1,000 PCU paid to OCT, a fee the player can't pay refused, and a fee of 0 free.
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
`tests/ledgers/LedgerTests` checks the ledgers' bookkeeping without the game
(`dotnet run -c Release --project tests/ledgers/LedgerTests`): limits, reservations,
batches that apply all or none, and the saved and wire formats.
`LedgerTests` also checks the kiosks' requests, replies and trading rules.
`tests/test_kiosks.py` checks the kiosks with the client and both mods loaded:
the probe plugin in `tests/kiosks/probe` docks ships at Alpha Station and sets
the player's faction rank, and the client sends the kiosk mod's requests with
its admin command `/kiosk`, through the same mod messages as the touch screens.
It covers reach, the ship list by access, deposit and withdraw with a ship and
from hand, the ledger limit, the Faction switch, Transfer and the members
setting; not the touch input itself.
`tests/market/MarketTests` checks the market's bookkeeping without the game
(`dotnet run -c Release --project tests/market/MarketTests`): OCT's prices, stock,
specialty and drift, buying and selling through listings and OCT all or none, the
fee, limits, cancel and expiry, the zone chips, the formats, and that no money is
made or lost.
`tests/test_market.py` checks the market in game with the client and both mods,
through the kiosk mod's `/kiosk` requests at Alpha Station: OCT's daily stock and
prices, the game's store items gone, buying from OCT, Alpha's specialty and its
quota, a faction offer and a player's order with the fee, the trading rank, the
listing limit and expiry, and the market after a restart.
`tests/test_zone_chips.py` checks the zone chips in game the same way: OCT sells
them at Moon Station for 1,000 SC and not at Alpha, the stock is topped up, a
bought chip goes from the ledger into the player's hands, and the game's safe zone
block takes 1 chip per 60 minutes.
`tests/test_ledgers.py` checks them in game with the client's admin commands, the
game's item masses and the config, then restarts the server and finds them as they
were saved.
`tests/test_contracts.py` checks the contracts with the client's admin commands: three
hauling contracts and an acquisition at every station and none of the game's, a bounty
on an NPC grid and salvage of a derelict placed near Alpha Station, a hauling and an
acquisition handed in and paid by OCT with the items in OCT's ledger, the derelict
finished at the shipyard by the plugin, the bounty completed, and after a restart the
same open contracts. `tests/contracts/ContractTests` checks the rewards, time limits
and amounts alone (`dotnet run -c Release --project tests/contracts/ContractTests`).
`tests/leaderboard/LeaderboardTests` checks the leaderboard's arithmetic, ranking,
text and wire formats alone (`dotnet run -c Release --project tests/leaderboard/LeaderboardTests`).
`tests/test_leaderboard.py` checks it in game with the client and the kiosk probe: two
players' factions and no NPC ones ranked, a faction's cash, a docked ship's value and
PCU against the game's block definitions and OCT's prices at every station, money held
for a market order, the station panels and their refresh, and how long a count of the
world takes. `tests/leaderboard/leaderboard_cluster.py` checks it on a local test
cluster with the World Authority and a node.
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
