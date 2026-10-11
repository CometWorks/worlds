# Octahedron server plugin

A Magnetar server plugin with the rules only the Octahedron world uses, and
experiments that move to Essentials or another public plugin once they prove
generic. It lives next to the world and has no repository, hub entry or release
of its own: a server compiles it from a checkout of this repository.

Server only. It was made from `server-plugin-template`, without the client
project and the template's shared plumbing: PluginSdk provides the config,
logging and chat commands.

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
checkout's `config/octahedron.cfg` on every node of a cluster.

## Loading it on a cluster

Dev folders don't work everywhere. Magnetar's managed preparation, which
Quasar's cluster setup runs, refuses dev-folder builds because a manifest
commit can't prove a working tree's contents, and the cluster CLI's release
mode loads compiled local plugins only. For those, `package.sh <folder>` builds
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
