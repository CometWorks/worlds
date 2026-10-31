using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Plugins;
using VRageMath;

namespace MesClusterProbe;

/// <summary>
/// Test hooks of the MES Cluster tests. Runs the plan in the file named by MESPROBE_PLAN once the session
/// is ready, one line per step, and appends what it saw to MESPROBE_OUT. Lines appended to the plan later
/// run as they arrive, so a test can drive it step by step. On a cluster it runs only on the node named
/// by MESPROBE_NODE (CLUSTER_NODE_ID), and "{node}" in MESPROBE_OUT is replaced by the node id.
///   spawn TYPE COUNT X Y Z SPREAD   COUNT MES spawn requests of SpawningType TYPE, each from a "player"
///                                   position drawn uniformly within SPREAD metres of (X, Y, Z)
///   wait SECONDS                    let the game run (prefabs spawn over several frames)
///   census LABEL                    one line per NPC grid of this process: census LABEL id x y z inGap name
///   count LABEL X Y Z RADIUS        what MES Cluster's census slot answers: count LABEL n (n/a without it)
///   owners LABEL                    one line per top-level grid: owners LABEL id name [owner:npc:identity:faction,...]
///   save                            saves the world (as the DS would), writes "saved" when the files are written
///   freeze                          stops every NPC-owned grid and makes it a station, so that no moving NPC keeps
///                                   its neighbours' partitions online (writes "frozen N")
///   eligible LABEL TYPE WHERE N     the spawn groups MES finds eligible at N positions, with no forcing, as for a
///                                   player there: eligible LABEL TYPE group [factions]. WHERE is a planet's name
///                                   (points 1 m above its surface) or X,Y,Z (points within 2 km of it)
///   spawn-group TYPE GROUP WHERE    one unforced MES spawn of this group at the first eligible position of 40:
///                                   spawned TYPE GROUP True|False
///   stores LABEL                    one line per top-level grid with store or contract blocks: stores LABEL id name
///                                   store=N contract=N items=N station=true|false faction=TAG
///   factions LABEL                  one line per faction: factions LABEL TAG type stations=N
///   census-off                      removes the cluster's census slot (falsification runs: MES Cluster counts this
///                                   process only)
///   done                            writes "done" and stops
/// Calls SpawnRequest.CalculateSpawn by reflection (forceSpawn: no MES timeouts; adminSpawn: no MES
/// area caps, so only MES Cluster's own limits apply).
/// </summary>
public class Probe : IPlugin
{
    private string planPath;
    private string[] plan;
    private long planLength = -1;
    private int step;
    private DateTime resumeAt = DateTime.MaxValue;
    private StreamWriter output;
    private readonly Random random = new Random(1321);

    public void Init(object gameInstance)
    {
        var planPath = Environment.GetEnvironmentVariable("MESPROBE_PLAN");
        var outPath = Environment.GetEnvironmentVariable("MESPROBE_OUT");
        var node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
        var only = Environment.GetEnvironmentVariable("MESPROBE_NODE");
        if (planPath == null || outPath == null || (only != null && node != only))
            return;
        this.planPath = planPath;
        plan = Array.Empty<string>();
        output = new StreamWriter(outPath.Replace("{node}", node ?? "standalone"), append: true)
        {
            AutoFlush = true,
        };
    }

    public void Update()
    {
        if (planPath == null || MySession.Static is not { Ready: true })
            return;
        if (step >= plan.Length)
        {
            long length = new FileInfo(planPath).Length;
            if (length == planLength)
                return;
            planLength = length;
            plan = File.ReadAllLines(planPath)
                .Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))
                .ToArray();
            if (step >= plan.Length)
                return;
        }
        if (resumeAt == DateTime.MaxValue)
            resumeAt = DateTime.UtcNow.AddSeconds(30); // MES initialises in its first session updates
        if (DateTime.UtcNow < resumeAt)
            return;

        var words = plan[step++].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (words[0])
            {
                case "spawn":
                    Spawn(words);
                    break;
                case "wait":
                    resumeAt = DateTime.UtcNow.AddSeconds(D(words[1]));
                    break;
                case "census":
                    Census(words[1]);
                    break;
                case "count":
                    CountSlot(words);
                    break;
                case "owners":
                    Owners(words[1]);
                    break;
                case "save":
                    MyAsyncSaving.Start(() => output.WriteLine("saved"));
                    break;
                case "freeze":
                    Freeze();
                    break;
                case "eligible":
                    Eligible(words);
                    break;
                case "spawn-group":
                    SpawnGroup(words);
                    break;
                case "stores":
                    Stores(words[1]);
                    break;
                case "factions":
                    Factions(words[1]);
                    break;
                case "census-off":
                    AppDomain.CurrentDomain.SetData("CometWorks.Cluster.NpcCensus.v1", null);
                    output.WriteLine("census-off");
                    break;
                case "done":
                    output.WriteLine("done");
                    break;
            }
        }
        catch (Exception e)
        {
            output.WriteLine("error " + e.ToString().Replace('\n', ' '));
        }
    }

    public void Dispose() => output?.Dispose();

    private void Spawn(string[] words)
    {
        var assembly = MyScriptManager.Static.Scripts.Values.First(a =>
            a.GetType("ModularEncountersSystems.Spawning.SpawnRequest") != null
        );
        var request = assembly.GetType("ModularEncountersSystems.Spawning.SpawnRequest");
        var spawningType = assembly.GetType("ModularEncountersSystems.Spawning.SpawningType");
        var calculate = request.GetMethod(
            "CalculateSpawn",
            BindingFlags.Public | BindingFlags.Static
        );
        var type = Enum.Parse(spawningType, words[1]);
        int count = int.Parse(words[2]);
        var center = new Vector3D(D(words[3]), D(words[4]), D(words[5]));
        double spread = D(words[6]);

        for (int i = 0; i < count; i++)
        {
            var position =
                center
                + spread
                    * new Vector3D(
                        random.NextDouble() * 2 - 1,
                        random.NextDouble() * 2 - 1,
                        random.NextDouble() * 2 - 1
                    );
            var args = calculate
                .GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray();
            args[0] = position;
            args[1] = "MesClusterProbe";
            args[2] = type;
            args[3] = true; // forceSpawn
            args[4] = true; // adminSpawn
            var result = calculate.Invoke(null, args);
            output.WriteLine($"request {words[1]} {F(position)} {result}");
        }
    }

    private static Assembly Mes =>
        MyScriptManager.Static.Scripts.Values.First(a =>
            a.GetType("ModularEncountersSystems.Spawning.SpawnRequest") != null
        );

    private Vector3D Position(string where)
    {
        var parts = where.Split(',');
        if (parts.Length == 3)
        {
            return new Vector3D(D(parts[0]), D(parts[1]), D(parts[2]))
                + 2000
                    * new Vector3D(
                        random.NextDouble() * 2 - 1,
                        random.NextDouble() * 2 - 1,
                        random.NextDouble() * 2 - 1
                    );
        }
        var planet = MyEntities
            .GetEntities()
            .OfType<MyPlanet>()
            .First(p => p.Generator.Id.SubtypeName.StartsWith(where));
        var direction = Vector3D.Normalize(
            new Vector3D(
                random.NextDouble() * 2 - 1,
                random.NextDouble() * 2 - 1,
                random.NextDouble() * 2 - 1
            )
        );
        var far = planet.PositionComp.GetPosition() + direction * planet.MaximumRadius;
        var surface = planet.GetClosestSurfacePointGlobal(ref far);
        return surface + direction;
    }

    // SpawnGroupManager.GetSpawnGroups with no forcing: group name -> factions it could spawn for
    private System.Collections.Generic.Dictionary<string, string> EligibleAt(
        string type,
        Vector3D position
    )
    {
        var assembly = Mes;
        var get = assembly
            .GetType("ModularEncountersSystems.Spawning.SpawnGroupManager")
            .GetMethod("GetSpawnGroups", BindingFlags.Public | BindingFlags.Static);
        var collection = Activator.CreateInstance(
            assembly.GetType("ModularEncountersSystems.Spawning.SpawnGroupCollection")
        );
        var environment = Activator.CreateInstance(
            assembly.GetType("ModularEncountersSystems.World.EnvironmentEvaluation"),
            position
        );
        var args = get.GetParameters()
            .Select(p => p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        args[0] = Enum.Parse(
            assembly.GetType("ModularEncountersSystems.Spawning.SpawningType"),
            type
        );
        args[1] = "MesClusterProbe";
        args[2] = environment;
        args[3] = "";
        args[4] = collection;
        get.Invoke(null, args);
        var factions = (System.Collections.IDictionary)
            collection.GetType().GetField("ValidFactions").GetValue(collection);
        var found = new System.Collections.Generic.Dictionary<string, string>();
        foreach (
            var group in (System.Collections.IEnumerable)
                collection.GetType().GetField("SpawnGroups").GetValue(collection)
        )
        {
            var name = (string)group.GetType().GetField("SpawnGroupName").GetValue(group);
            found[name] = factions.Contains(name)
                ? string.Join(",", (System.Collections.Generic.IEnumerable<string>)factions[name])
                : "";
        }
        return found;
    }

    private void Eligible(string[] words)
    {
        var all = new System.Collections.Generic.SortedDictionary<string, string>();
        for (int i = 0; i < int.Parse(words[4]); i++)
        {
            foreach (var pair in EligibleAt(words[2], Position(words[3])))
                all[pair.Key] = pair.Value;
        }
        foreach (var pair in all)
            output.WriteLine($"eligible {words[1]} {words[2]} {pair.Key} [{pair.Value}]");
        output.WriteLine($"eligible-end {words[1]} {words[2]} {all.Count}");
    }

    private void SpawnGroup(string[] words)
    {
        var assembly = Mes;
        var calculate = assembly
            .GetType("ModularEncountersSystems.Spawning.SpawnRequest")
            .GetMethod("CalculateSpawn", BindingFlags.Public | BindingFlags.Static);
        bool result = false;
        for (int i = 0; i < 40 && !result; i++)
        {
            var position = Position(words[3]);
            if (!EligibleAt(words[1], position).ContainsKey(words[2]))
                continue;
            var args = calculate
                .GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray();
            args[0] = position;
            args[1] = "MesClusterProbe";
            args[2] = Enum.Parse(
                assembly.GetType("ModularEncountersSystems.Spawning.SpawningType"),
                words[1]
            );
            args[4] = true; // adminSpawn: eligibleNames applies, no area caps. The blacklist still does.
            args[5] = new System.Collections.Generic.List<string> { words[2] };
            result = (bool)calculate.Invoke(null, args);
        }
        output.WriteLine($"spawned {words[1]} {words[2]} {result}");
    }

    private void Stores(string label)
    {
        var text = new StringBuilder();
        var stations = MySession
            .Static.Factions.SelectMany(f => f.Value.Stations)
            .Select(s => s.StationEntityId)
            .ToList();
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.Closed
                || MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid
            )
                continue;
            var blocks = grid.GetFatBlocks();
            var stores = blocks.OfType<MyStoreBlock>().ToList();
            int contracts = blocks.Count(b =>
                b.BlockDefinition.Id.TypeId.ToString().EndsWith("ContractBlock")
            );
            if (stores.Count == 0 && contracts == 0)
                continue;
            var owner = grid.BigOwners.FirstOrDefault();
            string tag = MySession.Static.Factions.TryGetPlayerFaction(owner)?.Tag ?? "none";
            text.AppendLine(
                $"stores {label} {grid.EntityId} {grid.DisplayName?.Replace(' ', '_')} store={stores.Count} "
                    + $"contract={contracts} items={stores.Sum(s => s.PlayerItems.Count)} "
                    + $"station={stations.Contains(grid.EntityId)} faction={tag}"
            );
        }
        output.Write(text.ToString());
        output.WriteLine($"stores-end {label}");
    }

    private void Factions(string label)
    {
        foreach (var faction in MySession.Static.Factions.Select(f => f.Value))
            output.WriteLine(
                $"factions {label} {faction.Tag} {faction.FactionType} stations={faction.Stations.Count}"
            );
        output.WriteLine($"factions-end {label}");
    }

    private void Census(string label)
    {
        var inGap =
            AppDomain.CurrentDomain.GetData("CometWorks.ModMes.InGap.v1")
            as Func<double, double, double, bool>;
        var text = new StringBuilder();
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.Closed
                || MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid
            )
                continue;
            var owners = grid.BigOwners;
            if (owners.Count == 0 || owners.Any(o => !MySession.Static.Players.IdentityIsNpc(o)))
                continue;
            var c = grid.PositionComp.WorldAABB.Center;
            string gap =
                inGap == null ? "na"
                : inGap(c.X, c.Y, c.Z) ? "gap"
                : "core";
            text.AppendLine(
                $"census {label} {grid.EntityId} {F(c)} {gap} {grid.DisplayName?.Replace(' ', '_')}"
            );
        }
        output.Write(text.ToString());
        output.WriteLine($"census-end {label}");
    }

    private void Freeze()
    {
        int frozen = 0;
        foreach (var entity in MyEntities.GetEntities().ToList())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.Closed
                || grid.IsStatic
                || grid.Physics == null
            )
                continue;
            var owners = grid.BigOwners;
            if (owners.Count == 0 || owners.Any(o => !MySession.Static.Players.IdentityIsNpc(o)))
                continue;
            grid.Physics.LinearVelocity = Vector3.Zero;
            grid.Physics.AngularVelocity = Vector3.Zero;
            grid.ConvertToStatic();
            if (grid.IsStatic)
                frozen++;
        }
        output.WriteLine($"frozen {frozen}");
    }

    private void Owners(string label)
    {
        var text = new StringBuilder();
        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.Closed
                || MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid
            )
                continue;
            var owners = grid.BigOwners.Select(o =>
            {
                var identity = MySession.Static.Players.TryGetIdentity(o);
                var faction = MySession.Static.Factions.TryGetPlayerFaction(o);
                return $"{o}:{(MySession.Static.Players.IdentityIsNpc(o) ? "npc" : "-")}:{identity?.DisplayName?.Replace(' ', '_') ?? "?"}:{faction?.Tag ?? "-"}";
            });
            text.AppendLine(
                $"owners {label} {grid.EntityId} {grid.DisplayName?.Replace(' ', '_')} [{string.Join(",", owners)}]"
            );
        }
        output.Write(text.ToString());
        output.WriteLine($"owners-end {label}");
    }

    private void CountSlot(string[] words)
    {
        var census =
            AppDomain.CurrentDomain.GetData("CometWorks.Cluster.NpcCensus.v1")
            as Func<double, double, double, double, int>;
        string n =
            census == null
                ? "n/a"
                : census(D(words[2]), D(words[3]), D(words[4]), D(words[5]))
                    .ToString(CultureInfo.InvariantCulture);
        output.WriteLine($"count {words[1]} {n}");
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string F(Vector3D v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} {2:F1}", v.X, v.Y, v.Z);
}
