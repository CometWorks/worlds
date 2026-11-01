# Deploying Octahedron

How to run the Octahedron cluster on a Linux host under a user account (here
`cluster`), hosted by Quasar. The design is in the workspace note
`se1/notes/public-test-cluster/NOTE.md`; the work is ticket SE1-0114.

Written on 2026-10-11 from the Quasar and cluster documentation and the local
cluster bench. Nobody has deployed it this way yet. Steps that name a Quasar page
or button come from `quasar/Docs` (`QuickStart.md`, `LinuxDeploymentAndUpdates.md`,
`ClusterGuidedSetupPlan.md`, `ClusterConversion.md`); check the labels against the
running version.

## Before the first deployment

These are not done yet. Each one changes what gets deployed.

1. **OctahedronOres on the Workshop.** A server can't load a local mod
   ("Local mods are not allowed in multiplayer"). See
   [Publishing OctahedronOres](#publishing-octahedronores).
2. **Cluster 0.1.13 or later.** It has two fixes found on the bench with this
   world (SE1-0114 work log, 2026-10-11):
   - [CometWorks/cluster#47](https://github.com/CometWorks/cluster/pull/47):
     every station has a safe zone block, and up to cluster 0.1.12 a station's
     partition fails every handover on the block's two countdown fields.
   - [CometWorks/cluster#48](https://github.com/CometWorks/cluster/pull/48):
     after a voxel journal compaction, the edits to the pre-placed asteroids are
     lost on a fresh node, and a restarted node stops on a journal gap.
3. **`MaxPlayers` and `TotalPCU`.** The game gives each player
   `TotalPCU / MaxPlayers` PCU. The world sets `MaxPlayers` 255 and `TotalPCU`
   255,000,000, so 1,000,000 PCU each. For another player cap, change
   `MAX_PLAYERS` in `build_world.py` and set both values in the Quasar config
   profile to match.
4. **The Octahedron plugin (`plugin/`, SE1-0133).** It isn't on the hub.
   Quasar compiles it from a dev folder, a clean checkout of this repository
   on the Quasar host, and pins it to the checkout's HEAD commit. That needs
   Magnetar 2.4.3.4 or later (see "Loading it on a cluster" in
   `plugin/README.md`). It does the station refill and the refill containers
   (`config/octahedron.cfg`). Without it a cluster's stations get no refill at
   all: the economy tick runs only on the World Authority, which has no grids.
   A standalone server without the plugin runs the game's own economy refill:
   loot in every empty cargo container, ice in the gas generators, and no Free
   Tools top-up.
5. **World edge (SE1-0119).** The Octahedron plugin brakes ships from 450 km
   and stops them at 500 km; the world keeps `WorldSizeKm` 0. Without the
   plugin (item 4) the world has no edge.

## The host

Checked on 2026-10-10: Debian 13, 32 cores, 124 GiB RAM, no swap, 270 GB free
on the root filesystem, `dotnet`, `docker` and `python3` installed. The home
folder of `cluster` is empty.

As root, once:

```bash
loginctl enable-linger cluster
apt-get install -y util-linux python3
```

Without lingering, the user's services stop at logout, and the cluster with them.

Every node of the cluster loads the whole world: 2.3 to 2.5 GiB per node right
after the load on the bench, without players; more once players build. Count on the WA plus the regular
nodes, and keep at least 8 GiB free; the host has no swap.

## Ports

| Port | Protocol | Open to |
|---|---|---|
| player port P (the server port chosen in the wizard, e.g. 27016) | UDP | everyone |
| P+16 (Cluster Registry admin), P+100.. and P+200.. (nodes), P+164 and P+264 (WA) | TCP/UDP | this host only |
| 8080 (Quasar web UI) | TCP | nobody; use an SSH tunnel |

Only the player port is public. On a single host the cluster talks over
loopback, so the firewall needs one rule:

```bash
# nftables (Debian's default); adapt if the host uses something else
nft add rule inet filter input udp dport 27016 accept
```

Reach the Quasar UI through a tunnel from your own machine:

```bash
ssh -L 8080:127.0.0.1:8080 cluster@ai
```

## Install Quasar

As `cluster`, from the latest release of CometWorks/quasar:

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh --start
```

`install.sh` asks before installing .NET 10 if it is missing. It installs the
user service `quasar.service`.

Set the first administrator before signing in with Steam (the SteamID64 of the
admin; repeat the mapping for each admin):

```bash
cat > ~/.local/share/Quasar/rbac.json <<'EOF'
{
  "subjectRoleMappings": [
    { "provider": "Steam", "subject": "YOUR_17_DIGIT_STEAM_ID", "roles": ["admin"] }
  ],
  "claimRoleMappings": [],
  "policyOverrides": {}
}
EOF
```

Open `http://localhost:8080` through the tunnel and sign in. In Settings,
Updates, save a GitHub token without scopes, so update checks and plugin
downloads don't hit GitHub's rate limit. Quasar installs and updates the
Dedicated Server (SteamCMD, app 298740) and Magnetar itself; wait until the
Updates page shows both.

## The world

Build it on any Linux machine with the game and the station blueprint
downloaded (see `README.md`), with the OctahedronOres Workshop id:

```bash
uv run make_ore_mod.py --seed 42
python3 build_world.py server --seed 42 --ore-mod <OctahedronOres Workshop id>
uv run check_sun_clearance.py server
```

Commit `server/`, then copy it to the host. **The folder must be named
`Octahedron`**, the world's session name: mods read their world storage from
`Saves/<SessionName>`, and the Relative Top Speed and MES configs in
`Storage/` are only found there.

```bash
rsync -a server/ cluster@ai:Octahedron/
```

In Quasar, import `~/Octahedron` as a world template (World Templates page, or
the Create From Template dialog), then create a config profile from that
template. The profile takes the session settings and the 43 mods from the
world's `Sandbox_config.sbc`. In the profile:

- Online mode Public, server name and world name `Octahedron`, a description,
  and the message of the day from `config/motd.txt`. A local Magnetar server
  takes the same three values in `SpaceEngineers-Dedicated.cfg` (`ServerName`,
  `WorldName`, `MessageOfTheDay`). The world's session name is `Octahedron` too.
- Programmable block scripts need the Scripter role (`EnableScripterRole`, set
  by `build_world.py`). Admins promote players to it; how players earn it is not
  decided yet.
- `MaxPlayers` and `TotalPCU` as decided above.
- Administrators: the SteamID64s of the admins.
- Plugins, from the MagnetarHub: Essentials, Hangar, Multigrid Projector,
  Bugfixes. Quasar adds the cluster plugins and Direct Transport itself.
- The Octahedron plugin, from its dev folder (item 4 above). Before that,
  clone this repository on the host at the commit to deploy and add its
  `octahedron/plugin` folder, with `Octahedron.xml`, under Configs, Developer.
- Leave the mod order as the world has it; Quasar may add dependency mods it
  finds missing (Definition Extension API is already listed before Radio
  Spectrometry).

## Create the cluster

Dashboard, **Create Cluster**, **Create and deploy**:

1. **Host enrollment:** Quasar enrolls this machine. It needs the user's systemd
   session and lingering; the enrollment page warns if lingering is off.
2. **Configure and deploy:** the Octahedron world template and its profile, this
   host as the Cluster Gateway and World Authority host machine, the player port,
   and the number of regular nodes. Start with 4: the world has 5 stations and
   2,000 asteroids, and partitions move between nodes as players spread out.
3. Quasar installs DS and Magnetar, stages the latest cluster release whose
   PluginSdk pin matches Magnetar, converts a copy of the world with
   MagnetarWorld, and leaves the cluster stopped.
4. In the cluster's plugin configuration editor, set the plugin configs: Hangar
   with `AdminSafeZonesOnly` on (ships are stored and retrieved only at the stations),
   Multigrid Projector as its defaults suit, Essentials as the cluster
   needs. The Octahedron plugin takes the values in `config/octahedron.cfg`:
   Deploy Octahedron stages them before the first start (`--wipe`), by hand
   the same editor does.
   The Performance plugin takes `config/performance.cfg`, which runs programmable
   block scripts on worker threads: Deploy Octahedron stages it on every update,
   and its smoke check fails on a node that doesn't report the per-thread
   instruction counter.
5. **Start.** Watch the nodes come up on the cluster page.

Then, once every node runs cluster 0.1.8 or later, raise the Gateway's
lookahead speed cap to the world's top speed (see "Speed and the cluster" in the
note), from the cluster admin page or the cluster CLI:

```bash
cluster config set lookahead.maxSpeed=360
```

It is stored in the registry and survives restarts.

## Check it

- Join with a vanilla client through the server browser or `ai:27016`.
- On the respawn screen all 5 stations are listed; spawning at one puts you in
  its medical room.
- The stations' Store blocks show OCT's stock and prices within seconds of the
  start, and the 2 % tax on both sides.
- A Trade Lanes trip between two stations arrives.
- The cluster page shows every partition placed and no handover failures in the
  Gateway log (`Automatic handover check failed`); with the safe zone fix the
  station partitions move like any other.

## Backups

Quasar's scheduled cluster backups capture only a stopped cluster with a clean
shutdown proof; online backups of a running cluster are not implemented yet
(SE1-0034, `se1/notes/cluster-online-backup.md`). Until they are:

- Schedule a short daily stop, backup and start from the cluster's Backups
  section, or stop and **Back up** by hand.
- **Save world** on the cluster page saves every partition while it runs, which
  bounds what a crash loses to about a minute.
- Backups go to Quasar's `BackupDirectory/Clusters`. Copy them off the host.

## Updates

- Quasar updates its web UI and launcher from GitHub releases on its own
  (Settings, Updates).
- Cluster releases go through the cluster's guided update on its page; a
  deployment keeps its frozen runtime until then.
- A game update needs a DS update (Quasar's **Check Dedicated Server**), a
  Magnetar build for it, and then a cluster release built against both.
- After a game update that changes the planet or ore definitions, rerun
  `make_ore_mod.py --seed 42` and upload the mod again under the same
  Workshop item.

## Publishing OctahedronOres

A person with the Steam account that will own the item does this, on Windows or
Linux with the game installed:

1. `uv run make_ore_mod.py --seed 42 ~/.config/SpaceEngineers/Mods/OctahedronOres`
   (on Windows, `%APPDATA%\SpaceEngineers\Mods\OctahedronOres`). It writes the
   planet and asteroid definitions, the ore maps and the `OmniArcTools` block
   tag, about 14 MB.
2. Start the game, open a world's Mods screen, select OctahedronOres under the
   local mods and press **Publish**. Visibility: Public (servers and players
   download it). Title "OctahedronOres", description from the README's
   OctahedronOres section.
3. Note the Workshop id from the item's URL. The game writes `modinfo.sbmi` into
   the mod folder; keep it, the next upload from the same folder updates the same
   item.
4. Rebuild the world with `--ore-mod <id>`, commit `server/`, and record the id in
   the README and the SE1-0114 ticket.

The offline design world for players gets a mod of its own, built with its own
seed, so its ore maps don't give away the server's.
