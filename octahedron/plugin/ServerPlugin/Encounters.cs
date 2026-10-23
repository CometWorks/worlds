using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ServerPlugin;

// Spawns the encounters MES spawned in Octahedron, with MES's rules: per-player timers, the
// spawn type the player's place allows, the eligible spawn groups (threat score, planet,
// air, gravity, reputation, world variables, uniqueness), a mod picked by its number of
// eligible groups and a group by its frequency, then MES's placement, caps and timeouts.
// Numbers are MES's defaults (Configuration/Config*.cs), with Octahedron's lunar cargo ships
// off and no cleanup. The grids come from the plugin's own blueprints, so clients need no
// mods. Each cluster node spawns for the players it simulates.
internal static class Encounters
{
    private const int StepTicks = 600;
    private const double Step = 10;

    // An NPC grid this spawner made, in the grid's storage: "<type>|<group>|<code>"
    public static readonly Guid StorageKey = new("5B0E7C65-7F43-4D59-8B0A-1E2C9A0F2F47");

    private static readonly Random Rng = new();

    private static string Text(Vector3D v) => $"{{X:{v.X:F0} Y:{v.Y:F0} Z:{v.Z:F0}}}";

    private static readonly Dictionary<long, Watched> Players = new();
    private static readonly List<(Vector3D Position, EncounterType Type, double Time)> Recent =
        new();
    private static readonly List<BossSignal> Signals = new();
    private static readonly List<(MyCubeGrid Grid, Vector3D End)> Cargo = new();
    private static double now;
    private static long tick;
    private static bool loaded;

    private sealed class Watched
    {
        public double SpaceCargo = 300;
        public double PlanetCargo = 300;
        public double Encounter = 60;
        public double Installation = 60;
        public double Boss = 1200;
        public double EncounterCooldown;
        public double InstallationCooldown;
        public double BossCooldown;
        public Vector3D? EncounterCheck;
        public Vector3D? InstallationSpawn;
    }

    private sealed class BossSignal
    {
        public EncounterGroup Group;
        public EncounterCondition Condition;
        public EncounterType Type;
        public Vector3D Position;
        public double Expires;
        public List<(long Identity, int Gps)> Gps = new();
    }

    public static void Update()
    {
        if (!Plugin.Config.EncountersEnabled || !Sync.IsServer || MySession.Static?.Ready != true)
            return;
        if (!loaded)
        {
            loaded = true;
            EncounterData.Load();
        }
        if (tick++ % StepTicks != 0 || EncounterData.Groups.Count == 0)
            return;
        now += Step;
        Recent.RemoveAll(r => now - r.Time > 900);
        UpdateCargo();
        UpdateSignals();

        var online = new HashSet<long>();
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            var character = player.Character;
            if (character == null || character.IsDead || !Plugin.Simulated(character))
                continue;
            online.Add(player.Identity.IdentityId);
            var position = character.PositionComp.GetPosition();
            if (!Players.TryGetValue(player.Identity.IdentityId, out var watched))
                Players[player.Identity.IdentityId] = watched = new Watched
                {
                    EncounterCheck = position,
                    InstallationSpawn = position,
                };
            Watch(watched, Spot.At(position));
        }
        foreach (var gone in Players.Keys.Where(k => !online.Contains(k)).ToList())
            Players.Remove(gone);
    }

    private static void Watch(Watched w, Spot spot)
    {
        w.EncounterCooldown -= Step;
        w.InstallationCooldown -= Step;
        w.BossCooldown -= Step;

        // The cargo ship timers run wherever the player is; an attempt in the wrong place is lost
        if ((w.SpaceCargo -= Step) <= 0)
        {
            w.SpaceCargo = Rng.Next(780, 1021);
            if (!spot.InGravity)
                TrySpawn(EncounterType.SpaceCargoShip, spot);
        }
        if ((w.PlanetCargo -= Step) <= 0)
        {
            w.PlanetCargo = Rng.Next(780, 1021);
            if (spot.InGravity)
                TrySpawn(
                    spot.Altitude <= 6000
                        ? EncounterType.AtmosphericCargoShip
                        : EncounterType.GravityCargoShip,
                    spot
                );
        }
        if ((w.Encounter -= Step) <= 0)
        {
            w.Encounter = 60;
            if (
                !spot.InGravity
                && w.EncounterCooldown <= 0
                && (
                    w.EncounterCheck == null
                    || Vector3D.Distance(w.EncounterCheck.Value, spot.Position) >= 15000
                )
            )
            {
                w.EncounterCheck = spot.Position;
                if (TrySpawn(EncounterType.SpaceRandomEncounter, spot))
                    w.EncounterCooldown = 300;
            }
        }
        if ((w.Installation -= Step) <= 0)
        {
            w.Installation = 60;
            if (
                spot.InGravity
                && spot.Altitude <= 6000
                && w.InstallationCooldown <= 0
                && (
                    w.InstallationSpawn == null
                    || Vector3D.Distance(w.InstallationSpawn.Value, spot.Position) >= 6000
                )
                && TrySpawn(EncounterType.PlanetaryInstallation, spot)
            )
            {
                w.InstallationSpawn = spot.Position;
                w.InstallationCooldown = 300;
            }
        }
        if ((w.Boss -= Step) <= 0)
        {
            w.Boss = 1200;
            if (w.BossCooldown <= 0 && TrySpawn(BossType(spot), spot))
                w.BossCooldown = 600;
        }
    }

    private static EncounterType BossType(Spot spot) =>
        !spot.InGravity ? EncounterType.BossEncounterSpace : EncounterType.BossEncounterAtmo;

    // The MES spawn type flags a group needs for a spawn type at a place
    private static EncounterType Accepted(EncounterType type) =>
        type is EncounterType.BossEncounterSpace or EncounterType.BossEncounterAtmo
            ? type | EncounterType.BossEncounterAny
            : type;

    // Caps of a type around the player: (grids, radius)
    private static (int Count, double Radius) Cap(EncounterType type) =>
        type switch
        {
            EncounterType.SpaceCargoShip => (15, 15000),
            EncounterType.SpaceRandomEncounter => (10, 25000),
            EncounterType.PlanetaryInstallation => (10, 15000),
            EncounterType.AtmosphericCargoShip or EncounterType.GravityCargoShip => (2, 20000),
            _ => (6, 25000),
        };

    // Timeouts: (spawns, radius) within 900 s, none for bosses
    private static (int Count, double Radius) Timeout(EncounterType type) =>
        type switch
        {
            EncounterType.SpaceCargoShip => (4, 10000),
            EncounterType.SpaceRandomEncounter => (2, 25000),
            EncounterType.PlanetaryInstallation => (2, 25000),
            EncounterType.AtmosphericCargoShip or EncounterType.GravityCargoShip => (2, 10000),
            _ => (int.MaxValue, 0),
        };

    public static bool TrySpawn(
        EncounterType type,
        Spot spot,
        string onlyGroup = null,
        bool force = false
    )
    {
        var accepted = Accepted(type);
        var capKind = accepted.HasFlag(EncounterType.BossEncounterAny)
            ? EncounterType.BossEncounterAny
            : type;
        if (!force)
        {
            var (cap, capRadius) = Cap(type);
            if (CountSpawned(capKind, spot.Position, capRadius) >= cap)
                return false;
            var (timeout, timeoutRadius) = Timeout(type);
            if (
                Recent.Count(r =>
                    r.Type == type && Vector3D.Distance(r.Position, spot.Position) < timeoutRadius
                ) >= timeout
            )
                return false;
        }

        var pick = Pick(type, spot, onlyGroup, force);
        if (pick == null)
            return false;
        var (g, c, f) = pick.Value;

        if (accepted.HasFlag(EncounterType.BossEncounterAny))
            return StartSignal(g, c, type, spot);
        var prefabs = c.SelectPrefabs(g.Prefabs, Rng);
        if (!Placement.Find(type, prefabs, c, spot, out var matrix, out var end))
            return false;
        if (!Spawn(g, c, f, type, prefabs, matrix, end))
            return false;
        Recent.Add((spot.Position, type, now));
        if (type == EncounterType.PlanetaryInstallation)
            CountInstallation(spot.Planet, c.InstallationSize);
        return true;
    }

    // The spawn group an attempt of a type at a place picks, with its condition and owner
    private static (EncounterGroup, EncounterCondition, string)? Pick(
        EncounterType type,
        Spot spot,
        string onlyGroup,
        bool force
    )
    {
        var accepted = Accepted(type);
        Threat.Clear();
        var eligible =
            new List<(EncounterGroup Group, EncounterCondition Condition, string Faction)>();
        foreach (var group in EncounterData.Groups)
        {
            if (onlyGroup != null && group.Name != onlyGroup)
                continue;
            if (group.Persistent != null && !Allowed(group, group.Persistent, spot, out _, true))
                continue;
            foreach (var condition in group.Conditions)
            {
                if ((condition.TypeFlags & accepted) == 0)
                    continue;
                if (!Allowed(group, condition, spot, out var faction, false))
                    continue;
                if (
                    type == EncounterType.AtmosphericCargoShip
                    && !condition.SkipAirDensityCheck
                    && !spot.AirTravel
                )
                    continue;
                // A failed chance roll moves on to the group's next condition, as in MES
                if (!force && !RollChance(condition, type))
                    continue;
                eligible.Add((group, condition, faction));
                break;
            }
        }
        if (type == EncounterType.PlanetaryInstallation)
            eligible = InstallationPool(eligible, spot.Planet);
        return Select(eligible);
    }

    // Threat scores of this attempt by range, as MES caches a grid's for 5 s
    private static readonly Dictionary<float, float> Threat = new();

    private static bool RollChance(EncounterCondition c, EncounterType type)
    {
        var chance = c.Chance(type);
        return chance >= c.ChanceCeiling || Rng.Next(0, (int)c.ChanceCeiling + 1) <= chance;
    }

    // MES's common spawn conditions (Spawning/SpawnConditions.cs)
    private static bool Allowed(
        EncounterGroup group,
        EncounterCondition c,
        Spot spot,
        out string faction,
        bool persistent
    )
    {
        faction = null;
        var planetName = spot.Planet?.Generator?.Id.SubtypeName;
        var whitelist = EncounterCondition.List(c.PlanetWhitelist);
        if (whitelist.Length > 0 && !whitelist.Contains(planetName))
            return false;
        if (EncounterCondition.List(c.PlanetBlacklist).Contains(planetName))
            return false;
        if (
            c.PlanetRequiresAtmo
            && !c.TypeFlags.HasFlag(EncounterType.GravityCargoShip)
            && spot.Air <= 0
        )
            return false;
        if (c.MaxGravity >= 0 && spot.Gravity > c.MaxGravity)
            return false;
        if (c.UniqueEncounter && Variable("Octahedron-Encounter-Unique-" + group.Name))
            return false;
        if (EncounterCondition.List(c.SandboxVariables).Any(v => !Variable(v)))
            return false;
        if (EncounterCondition.List(c.FalseSandboxVariables).Any(Variable))
            return false;
        if (c.ThreatMinimum > 0 || c.ThreatMaximum > 0)
        {
            if (!Threat.TryGetValue(c.ThreatRange, out var threat))
                Threat[c.ThreatRange] = threat = ThreatScore.Around(spot.Position, c.ThreatRange);
            if (c.ThreatMinimum > 0 && threat < c.ThreatMinimum)
                return false;
            if (c.ThreatMaximum > 0 && threat > c.ThreatMaximum)
                return false;
        }
        var terrain = EncounterCondition.List(c.TerrainTypes);
        if (terrain.Length > 0 && !terrain.Contains(spot.Terrain))
            return false;
        if (!string.IsNullOrEmpty(c.RemoteControlCode) && !CodeAllowed(c, spot.Position))
            return false;
        if (persistent && c.Types == "")
            return true;

        faction = Faction(group, c);
        if (faction == null)
            return false;
        if (c.ReputationRange > 0 && !ReputationAllowed(c, faction, spot.Position))
            return false;
        return true;
    }

    // The owner MES gives a group: FactionOverride, the condition's FactionOwner (SPRT by
    // default), or for base-game groups a listed faction or a random NPC faction of a listed
    // type. Null if the world has no such faction.
    private static string Faction(EncounterGroup group, EncounterCondition c)
    {
        var factions = MySession.Static.Factions;
        if (group.IsBaseGame)
        {
            var tags = EncounterCondition
                .List(group.BaseGameFactionTags)
                .Where(t => factions.TryGetFactionByTag(t) != null)
                .ToList();
            if (group.BaseGameFactionTypes != null)
            {
                var types = EncounterCondition.List(group.BaseGameFactionTypes);
                tags.AddRange(
                    factions
                        .GetAllFactions()
                        .Where(f =>
                            f.IsEveryoneNpc()
                            && !f.AcceptHumans
                            && types.Contains(f.FactionTypeString)
                        )
                        .Select(f => f.Tag)
                );
            }
            return tags.Count == 0 ? null : tags[Rng.Next(tags.Count)];
        }
        var tag = group.FactionOverride ?? c.Faction ?? "SPRT";
        if (tag == "Nobody")
            return tag;
        return factions.TryGetFactionByTag(tag) != null ? tag : null;
    }

    private static bool ReputationAllowed(EncounterCondition c, string tag, Vector3D position)
    {
        var faction = MySession.Static.Factions.TryGetFactionByTag(tag);
        if (faction == null)
            return false;
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            var character = player.Character;
            if (
                character == null
                || Vector3D.Distance(character.PositionComp.GetPosition(), position)
                    > c.ReputationRange
            )
                continue;
            var reputation = MySession
                .Static.Factions.GetRelationBetweenPlayerAndFaction(
                    player.Identity.IdentityId,
                    faction.FactionId
                )
                .Item2;
            if (reputation >= c.ReputationMinimum && reputation <= c.ReputationMaximum)
                return true;
        }
        return false;
    }

    // Stations register a code; a group may need one of them near or none too near
    private static bool CodeAllowed(EncounterCondition c, Vector3D position)
    {
        var nearest = double.MaxValue;
        foreach (var grid in Spawned())
            if (grid.Code == c.RemoteControlCode)
                nearest = Math.Min(nearest, Vector3D.Distance(grid.Position, position));
        if (c.RemoteControlCodeMinDistance > -1 && nearest < c.RemoteControlCodeMinDistance)
            return false;
        if (c.RemoteControlCodeMaxDistance > -1 && nearest > c.RemoteControlCodeMaxDistance)
            return false;
        return true;
    }

    // MES's installation size: small, medium at a chance that grows with every small one on
    // the planet, large at a chance that grows with every medium one; a size with no group
    // falls to the next (Spawning/SpawnGroupCollection.cs, SelectRandomSpawnGroup)
    private static List<(EncounterGroup, EncounterCondition, string)> InstallationPool(
        List<(EncounterGroup Group, EncounterCondition Condition, string Faction)> eligible,
        MyPlanet planet
    )
    {
        List<(EncounterGroup, EncounterCondition, string)> Of(string size) =>
            eligible
                .Where(e => e.Condition.InstallationSize == size)
                .Select(e => (e.Group, e.Condition, e.Faction))
                .ToList();
        var small = Of("Small");
        var medium = Of("Medium");
        var large = Of("Large");
        var size = "Small";
        if (small.Count == 0)
            size = "Medium";
        else if (Rng.Next(0, 101) < Counter(planet, "Medium", 15))
            size = "Medium";
        if (size == "Medium" && medium.Count == 0)
            size = "Large";
        else if (size == "Medium" && Rng.Next(0, 101) < Counter(planet, "Large", 5))
            size = "Large";
        if (size == "Large" && large.Count == 0)
            size = medium.Count > 0 ? "Medium" : "Small";
        return size switch
        {
            "Small" => small,
            "Medium" => medium,
            _ => large,
        };
    }

    private static void CountInstallation(MyPlanet planet, string size)
    {
        if (size == "Small")
            SetCounter(planet, "Medium", Counter(planet, "Medium", 15) + 15);
        if (size == "Medium")
        {
            SetCounter(planet, "Medium", 15);
            SetCounter(planet, "Large", Counter(planet, "Large", 5) + 15);
        }
        if (size == "Large")
            SetCounter(planet, "Large", 5);
    }

    private static int Counter(MyPlanet planet, string size, int start) =>
        MyAPIGateway.Utilities.GetVariable<int>(CounterName(planet, size), out var value)
            ? value
            : start;

    private static void SetCounter(MyPlanet planet, string size, int value) =>
        MyAPIGateway.Utilities.SetVariable(CounterName(planet, size), value);

    private static string CounterName(MyPlanet planet, string size) =>
        $"Octahedron-Encounter-{planet.EntityId}-{size}";

    // A mod first, weighted by its number of eligible groups (1 up to 10, 2 up to 19, then 3),
    // then one of its groups weighted by frequency (MES Config-General UseModIdSelection and
    // UseWeightedModIdSelection, both on by default)
    private static (EncounterGroup, EncounterCondition, string)? Select(
        List<(EncounterGroup Group, EncounterCondition Condition, string Faction)> eligible
    )
    {
        if (eligible.Count == 0)
            return null;
        var mods = eligible.GroupBy(e => e.Group.Mod).ToList();
        var mod = Weighted(
            mods,
            m =>
                m.Count() <= 10 ? 1
                : m.Count() < 20 ? 2
                : 3
        );
        var pick = Weighted(
            mod.ToList(),
            e => Math.Max(1, (int)Math.Round(e.Group.Frequency * 10))
        );
        return (pick.Group, pick.Condition, pick.Faction);
    }

    private static T Weighted<T>(IList<T> items, Func<T, int> weight)
    {
        var total = items.Sum(weight);
        var roll = Rng.Next(total);
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll < 0)
                return item;
        }
        return items[^1];
    }

    // The group's prefabs at their offsets in the spawn matrix, as MES's PrefabSpawner does it
    private static bool Spawn(
        EncounterGroup group,
        EncounterCondition condition,
        string faction,
        EncounterType type,
        List<EncounterPrefab> prefabs,
        MatrixD matrix,
        Vector3D? end
    )
    {
        var owner =
            faction == "Nobody"
                ? 0
                : MySession.Static.Factions.TryGetFactionByTag(faction)?.FounderId ?? 0;
        var options =
            SpawningOptions.RotateFirstCockpitTowardsDirection | SpawningOptions.SpawnRandomCargo;
        if (condition.DisableDampeners)
            options |= SpawningOptions.DisableDampeners;
        if (group.IsBaseGame)
        {
            options |= SpawningOptions.ReplaceColor;
            if (type == EncounterType.PlanetaryInstallation)
                options |= SpawningOptions.UseGridOrigin | SpawningOptions.UseOnlyWorldMatrix;
        }
        if (type == EncounterType.SpaceRandomEncounter && group.IsBaseGame)
            options |= SpawningOptions.UseGridOrigin;

        var spawned = 0;
        foreach (var prefab in prefabs)
        {
            // MES's dummy prefab only carries the behavior that spawns the rest
            if (prefab.Blueprint.StartsWith("(NPC-MES) Dummy"))
                continue;
            var name = EncounterData.PrefabName(prefab.Blueprint, condition.ForceStaticGrid);
            if (name == null)
                continue;
            var offset = new Vector3D(prefab.X, prefab.Y, prefab.Z);
            var start = Vector3D.Transform(offset, matrix);
            var velocity = Vector3.Zero;
            Vector3D? prefabEnd = null;
            if (end != null)
            {
                prefabEnd = end.Value + Vector3D.TransformNormal(offset, matrix);
                velocity = (Vector3)Vector3D.Normalize(prefabEnd.Value - start) * prefab.Speed;
            }
            var grids = new List<IMyCubeGrid>();
            var code =
                condition.RemoteControlCodeMinDistance > -1 ? condition.RemoteControlCode : "";
            var data = $"{type}|{group.Name}|{code}";
            var target = prefabEnd;
            var speed = prefab.Speed;
            MyAPIGateway.PrefabManager.SpawnPrefab(
                grids,
                name,
                start,
                (Vector3)matrix.Forward,
                (Vector3)matrix.Up,
                velocity,
                Vector3.Zero,
                null,
                options,
                owner,
                false,
                () => Spawned(grids, data, group, target, speed)
            );
            spawned++;
            foreach (var variable in EncounterData.Variables(prefab))
                MyAPIGateway.Utilities.SetVariable(variable, true);
        }
        if (spawned == 0)
            return false;
        if (group.Conditions.Any(c => c.UniqueEncounter))
            MyAPIGateway.Utilities.SetVariable("Octahedron-Encounter-Unique-" + group.Name, true);
        Plugin.Log.Info(
            $"Encounter {type} {group.Name} ({group.ModName}) for {faction} at {Text(matrix.Translation)}"
        );
        return true;
    }

    private static void Spawned(
        List<IMyCubeGrid> grids,
        string data,
        EncounterGroup group,
        Vector3D? end,
        float speed
    )
    {
        foreach (var g in grids)
        {
            if (g is not MyCubeGrid grid)
                continue;
            grid.Storage ??= new MyModStorageComponent();
            grid.Storage[StorageKey] = data;
            Plugin.Log.Info(
                $"Encounter grid {grid.EntityId} '{grid.DisplayName}', {grid.BlocksCount} blocks, at {Text(grid.PositionComp.GetPosition())}"
            );
            if (group.EraseScripts)
                foreach (
                    var pb in grid.GetFatBlocks<Sandbox.Game.Entities.Blocks.MyProgrammableBlock>()
                )
                    ((IMyProgrammableBlock)pb).ProgramData = "";
        }
        if (end == null || grids.FirstOrDefault() is not MyCubeGrid main)
            return;

        // Without MES's behaviors a cargo ship flies its path on the game's autopilot, or
        // drifts along it, dampeners off, when it has no remote control
        var remote = main.GetFatBlocks<Sandbox.Game.Entities.MyRemoteControl>()
            .FirstOrDefault(r => r.IsFunctional);
        if (remote != null)
        {
            var ap = (Sandbox.ModAPI.Ingame.IMyRemoteControl)remote;
            ap.ClearWaypoints();
            ap.AddWaypoint(end.Value, "Path end");
            ap.FlightMode = Sandbox.ModAPI.Ingame.FlightMode.OneWay;
            ap.SpeedLimit = Math.Max(speed, 10);
            ap.SetCollisionAvoidance(true);
            ap.SetAutoPilotEnabled(true);
        }
        else
            main.Components.Get<MyEntityThrustComponent>()?.DampenersEnabled = false;
        Cargo.Add((main, end.Value));
    }

    // A cargo ship at the end of its path with no player within 1.2 km leaves, as MES's
    // cargo ship behavior makes it do (Behavior/CargoShip.cs)
    private static void UpdateCargo()
    {
        for (var i = Cargo.Count - 1; i >= 0; i--)
        {
            var (grid, end) = Cargo[i];
            if (grid.MarkedForClose || grid.Closed)
            {
                Cargo.RemoveAt(i);
                continue;
            }
            var position = grid.PositionComp.GetPosition();
            if (Vector3D.Distance(position, end) > 500 || PlayerWithin(position, 1200))
                continue;
            Cargo.RemoveAt(i);
            if (!Plugin.Simulated(grid))
                continue;
            Plugin.Log.Info(
                $"Encounter cargo ship {grid.DisplayName} reached its path's end and left"
            );
            foreach (
                var member in grid.GetGridGroup(GridLinkTypeEnum.Logical)
                    .GetGrids(new List<IMyCubeGrid>())
            )
                ((MyCubeGrid)member).Close();
        }
    }

    private static bool PlayerWithin(Vector3D position, double range) =>
        MySession
            .Static.Players.GetOnlinePlayers()
            .Any(p =>
                p.Character != null
                && Vector3D.Distance(p.Character.PositionComp.GetPosition(), position) <= range
            );

    // Boss encounters start as a GPS signal 6–8 km away for every player within 25 km; the
    // boss spawns 2.5–4 km from it once a player comes within 300 m (MES BossEncounters)
    private static bool StartSignal(
        EncounterGroup group,
        EncounterCondition condition,
        EncounterType type,
        Spot spot
    )
    {
        if (!Placement.BossSignal(spot, out var position))
            return false;
        var signal = new BossSignal
        {
            Group = group,
            Condition = condition,
            Type = type,
            Position = position,
            Expires = now + 1200,
        };
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            if (
                player.Character == null
                || Vector3D.Distance(player.Character.PositionComp.GetPosition(), spot.Position)
                    > 25000
            )
                continue;
            var gps = (MyGps)
                MyAPIGateway.Session.GPS.Create(
                    "Boss Encounter",
                    $"{group.ModName}: a signal of unknown origin",
                    position,
                    showOnHud: true,
                    temporary: true
                );
            gps.GPSColor = Color.Red;
            MyAPIGateway.Session.GPS.AddGps(player.Identity.IdentityId, gps);
            signal.Gps.Add((player.Identity.IdentityId, gps.Hash));
        }
        Signals.Add(signal);
        Recent.Add((spot.Position, type, now));
        Plugin.Log.Info(
            $"Encounter boss signal {group.Name} ({group.ModName}) at {Text(position)}"
        );
        return true;
    }

    private static void UpdateSignals()
    {
        for (var i = Signals.Count - 1; i >= 0; i--)
        {
            var s = Signals[i];
            var triggered = PlayerWithin(s.Position, 300);
            if (!triggered && now < s.Expires)
                continue;
            Signals.RemoveAt(i);
            foreach (var (identity, hash) in s.Gps)
                MyAPIGateway.Session.GPS.RemoveGps(identity, hash);
            if (!triggered)
                continue;
            var spot = Spot.At(s.Position);
            var faction = Faction(s.Group, s.Condition);
            if (faction != null && Placement.Boss(spot, out var matrix))
                Spawn(
                    s.Group,
                    s.Condition,
                    faction,
                    s.Type,
                    s.Condition.SelectPrefabs(s.Group.Prefabs, Rng),
                    matrix,
                    null
                );
        }
    }

    private static int CountSpawned(EncounterType type, Vector3D position, double radius) =>
        Spawned()
            .Count(g => (g.Type & type) != 0 && Vector3D.Distance(g.Position, position) <= radius);

    private static IEnumerable<(EncounterType Type, string Code, Vector3D Position)> Spawned()
    {
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.Storage == null
                || !grid.Storage.TryGetValue(StorageKey, out var data)
            )
                continue;
            var parts = data.Split('|');
            if (parts.Length < 3 || !Enum.TryParse<EncounterType>(parts[0], out var type))
                continue;
            if (type is EncounterType.BossEncounterSpace or EncounterType.BossEncounterAtmo)
                type |= EncounterType.BossEncounterAny;
            yield return (type, parts[2], grid.PositionComp.GetPosition());
        }
    }

    private static bool Variable(string name) =>
        MyAPIGateway.Utilities.GetVariable<bool>(name, out var value) && value;

    // For !oct encounters <type> <n>: the share of each mod in n attempts here, without
    // spawning, caps or timeouts
    public static IEnumerable<string> Sample(EncounterType type, Vector3D position, int count)
    {
        var spot = Spot.At(position);
        var mods = new Dictionary<string, int>();
        var none = 0;
        for (var i = 0; i < count; i++)
        {
            if (Pick(type, spot, null, false) is not var (group, _, _))
            {
                none++;
                continue;
            }
            mods[group.ModName] = mods.GetValueOrDefault(group.ModName) + 1;
        }
        yield return $"{type}, {count} attempts, {none} with no eligible group:";
        foreach (var (mod, n) in mods.OrderByDescending(m => m.Value))
            yield return $"{mod}: {100.0 * n / count:F1} %";
    }

    // For !oct encounters: what would spawn here, and what is around
    public static IEnumerable<string> Describe(Vector3D position)
    {
        var spot = Spot.At(position);
        yield return $"{EncounterData.Groups.Count} spawn groups. Here: "
            + (
                spot.InGravity
                    ? $"{spot.Planet?.Generator?.Id.SubtypeName}, altitude {spot.Altitude:F0} m, "
                        + $"{spot.Gravity:F2} g, air {spot.Air:F2}, terrain {spot.Terrain}"
                    : "space"
            )
            + $", threat {ThreatScore.Around(position, 5000):F0} within 5 km.";
        var types = spot.InGravity
            ? new[]
            {
                spot.Altitude <= 6000
                    ? EncounterType.AtmosphericCargoShip
                    : EncounterType.GravityCargoShip,
                EncounterType.PlanetaryInstallation,
                EncounterType.BossEncounterAtmo,
            }
            : new[]
            {
                EncounterType.SpaceCargoShip,
                EncounterType.SpaceRandomEncounter,
                EncounterType.BossEncounterSpace,
            };
        foreach (var type in types)
        {
            var accepted = Accepted(type);
            var names = EncounterData
                .Groups.Where(g =>
                    (g.Persistent == null || Allowed(g, g.Persistent, spot, out _, true))
                    && g.Conditions.Any(c =>
                        (c.TypeFlags & accepted) != 0 && Allowed(g, c, spot, out _, false)
                    )
                )
                .Select(g => g.Name)
                .ToList();
            var (cap, radius) = Cap(type);
            yield return $"{type}: {names.Count} eligible, {CountSpawned(type, position, radius)}/{cap} spawned within {radius / 1000:F0} km";
        }
    }
}
