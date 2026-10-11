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
    --node-env OCTAHEDRON_CONFIG=/path/to/worlds/octahedron/config/octahedron.cfg <command>
```

## Tests

`tests/rig.py` runs an isolated Magnetar server with a copy of the world, the
mods and asteroids left out, and this working copy as a dev folder (or a
`package.sh` bundle as a local plugin), plus a headless client with the Remote plugin that joins it. Ports, ids and folders
come from a test slot of the workspace (`se/notes/parallel-test-work`). See its
docstring.

## Building locally

Only needed for an IDE or to check that it compiles; servers compile it
themselves.

```bash
dotnet build ServerPlugin/ServerPlugin.csproj
```

The Dedicated Server and Magnetar folders are auto-detected as in the template;
override them in `Directory.Build.props.user`.
