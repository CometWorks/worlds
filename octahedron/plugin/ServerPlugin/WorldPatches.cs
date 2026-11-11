using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ServerPlugin;

/// <summary>
/// Grids added to a kept world once, like the space elevators' lane computers (SE-1358): what a
/// station patch can't do, as it only changes grids the world has. The WorldPatches of the config
/// are applied in order by the process keeping the world's ledger, a world variable: a plain server
/// creates the grids itself; a cluster's World Authority, which simulates nothing, has one regular
/// node create them, and the cluster puts each grid into a partition by its position. A world
/// build_world.py makes lists every patch of its config in the ledger already: it has the grids.
///
/// A request the node may have carried out without the World Authority hearing back (a timeout,
/// a lost answer) marks the patch "?" in the ledger and isn't sent again: the grids may be there,
/// and twice would be worse than none. The log says so, and an admin checks the world.
/// </summary>
internal static class WorldPatches
{
    private const string VariableName = "Octahedron-WorldPatches";
    private const string Topic = "world-patch";
    private const int CheckSeconds = 5;
    private const int RetrySeconds = 60;

    // The cluster refuses a request with a longer timeout as Invalid
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static DateTime nextCheck;
    private static IDisposable handler;
    private static bool logged;

    // The World Authority's request in flight and what it asked for
    private static Task<PluginResult> pending;
    private static string pendingId;
    private static readonly Dictionary<string, (DateTime Next, string Why)> refused = new();

    // A node's answers by operation id, so a repeated request creates nothing again
    private static readonly Dictionary<Guid, string> answered = new();

    private static PluginClusterClient Cluster => Plugin.Cluster;

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextCheck || MySession.Static?.Ready != true)
            return;
        nextCheck = now.AddSeconds(CheckSeconds);
        if (Cluster != null)
        {
            if (!Cluster.Context.Available)
                return;
            handler ??= Cluster.RegisterHandler(Topic, Serve);
        }
        if (!Stations.IsAuthority)
            return;
        if (pending != null)
        {
            if (pending.IsCompleted)
                Answered();
            return;
        }

        var ledger = Ledger();
        if (!logged)
        {
            // For the deploy's report: what this world had before this start
            logged = true;
            Plugin.Log.Info($"World patch LEDGER {string.Join(",", ledger.OrderBy(i => i))}");
        }
        foreach (var entry in Plugin.Config.WorldPatches ?? [])
        {
            if (ledger.Contains(entry.Id) || ledger.Contains("?" + entry.Id))
                continue;
            if (refused.TryGetValue(entry.Id, out var last) && now < last.Next)
                return;
            var error = PatchFormat.Check(entry.Id, entry.Grids);
            if (error != null)
            {
                Refuse(entry.Id, error);
                return;
            }
            if (Cluster == null)
            {
                var result = Apply(entry.Id, entry.Grids);
                Plugin.Log.Info($"World patch {result}");
                if (result.StartsWith("APPLIED"))
                    Record(entry.Id);
                else
                    Refuse(entry.Id, result);
            }
            else
            {
                pendingId = entry.Id;
                pending = Send(entry);
            }
            // One at a time, in order
            return;
        }
    }

    // The World Authority: the patch to the first regular node
    private static async Task<PluginResult> Send(WorldPatchEntry entry)
    {
        var nodes = await Cluster.NodesAsync().ConfigureAwait(false);
        var node = nodes
            ?.Where(n => n.Role == "Regular")
            .OrderBy(n => n.Node, StringComparer.Ordinal)
            .FirstOrDefault();
        if (node == null)
            return PluginResult.Failure(PluginResultCode.Unavailable);
        return await Cluster
            .RequestAsync(
                new PluginTarget
                {
                    Kind = PluginTargetKind.Node,
                    Node = node.Node,
                    Incarnation = node.Incarnation,
                },
                Topic,
                Encoding.UTF8.GetBytes(entry.Id + "\n" + entry.Grids),
                OperationId(entry.Id),
                Timeout
            )
            .ConfigureAwait(false);
    }

    private static void Answered()
    {
        var id = pendingId;
        var reply = pending.IsFaulted
            ? PluginResult.Failure(PluginResultCode.Timeout)
            : pending.Result;
        pending = null;
        var text = Encoding.UTF8.GetString(reply.Payload ?? []);
        switch (reply.Code)
        {
            case PluginResultCode.Success when text.StartsWith("APPLIED"):
                Record(id);
                Plugin.Log.Info($"World patch {text}");
                break;
            case PluginResultCode.Success:
                Refuse(id, text);
                break;
            // Not delivered: nothing was created
            case PluginResultCode.Unavailable
            or PluginResultCode.Fenced
            or PluginResultCode.Capacity
            or PluginResultCode.NotFound
            or PluginResultCode.Invalid
            or PluginResultCode.Unsupported:
                Refuse(id, $"no node took it ({reply.Code})");
                break;
            default:
                Record("?" + id);
                Plugin.Log.Error(
                    $"World patch UNKNOWN {id}: the node's answer was {reply.Code}; it may have added the grids. "
                        + $"Not sent again: check the world, then remove '?{id}' from the world variable {VariableName} to send it again"
                );
                break;
        }
    }

    // A regular node, on the game thread: the World Authority's patch
    private static Task<byte[]> Serve(PluginMessage message)
    {
        if (!answered.TryGetValue(message.OperationId, out var result))
        {
            var parts = Encoding.UTF8.GetString(message.Payload ?? []).Split('\n');
            result =
                parts.Length != 2 ? "REFUSED: bad request"
                : Stations.IsAuthority ? $"REFUSED {parts[0]}: sent to the World Authority"
                : Apply(parts[0], parts[1]);
            Plugin.Log.Info($"World patch {result}");
            if (result.StartsWith("APPLIED"))
                answered[message.OperationId] = result;
        }
        return Task.FromResult(Encoding.UTF8.GetBytes(result));
    }

    /// <summary>Creates the grids of the patch file that no loaded grid has the name of.</summary>
    private static string Apply(string id, string file)
    {
        try
        {
            // Read here, not by the game's path: its serializer only takes files under Mods
            var path = StationPatches.BlueprintPath(file);
            if (!File.Exists(path))
                return $"REFUSED {id}: no file {file} next to the config or in Storage/{Plugin.Name} of "
                    + $"{MySession.Static?.CurrentPath} or {Plugin.LoadedFrom}";
            if (
                !MyObjectBuilderSerializer.DeserializeXML(
                    File.ReadAllBytes(path),
                    out MyObjectBuilder_Definitions definitions,
                    out _
                ) || definitions.ShipBlueprints is not { Length: > 0 }
            )
                return $"REFUSED {id}: the game can't read {file}";
            var grids = definitions.ShipBlueprints.SelectMany(b => b.CubeGrids).ToList();
            var loaded = MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(g => !g.Closed)
                .Select(g => g.DisplayName)
                .ToHashSet();
            var skipped = grids.Where(g => loaded.Contains(g.DisplayName)).ToList();
            var added = grids.Except(skipped).ToList();
            MyEntities.RemapObjectBuilderCollection(added);
            var ids = new List<string>();
            foreach (var grid in added)
            {
                if (MyEntities.CreateFromObjectBuilderAndAdd(grid, true) is not MyCubeGrid made)
                    return $"FAILED {id}: {grid.DisplayName} not created, after {ids.Count} of {added.Count}";
                ids.Add($"{made.DisplayName} {made.EntityId}");
            }
            var note =
                skipped.Count > 0
                    ? $"; {skipped.Count} there already: {string.Join(", ", skipped.Select(g => g.DisplayName))}"
                    : "";
            return $"APPLIED {id}: {added.Count} grids: {string.Join(", ", ids)}{note}";
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"World patch {id} failed", e);
            return $"FAILED {id}: {e.GetType().Name}: {e.Message}";
        }
    }

    private static void Refuse(string id, string why)
    {
        if (!refused.TryGetValue(id, out var last) || last.Why != why)
            Plugin.Log.Warning(
                $"World patch {id} not applied, trying again in {RetrySeconds} s: {why}"
            );
        refused[id] = (DateTime.UtcNow.AddSeconds(RetrySeconds), why);
    }

    private static HashSet<string> Ledger() =>
        MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var text)
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()
            : [];

    private static void Record(string id)
    {
        var ledger = Ledger();
        ledger.Add(id);
        MyAPIGateway.Utilities.SetVariable(VariableName, string.Join(",", ledger.OrderBy(i => i)));
    }

    // The same for every send of a patch: a node that answered it once answers the same again
    private static Guid OperationId(string id) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("octahedron-world-patch\n" + id)));
}
