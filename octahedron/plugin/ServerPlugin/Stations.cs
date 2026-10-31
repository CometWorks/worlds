using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;

namespace ServerPlugin;

/// <summary>Whether a global command applied. Unknown: the World Authority didn't answer in time; the
/// command may or may not have applied, and sending the same operation id again tells.</summary>
public enum CallOutcome
{
    Applied,
    Refused,
    Unknown,
}

/// <summary>
/// What the stations' services share: where they are kept, the server itself or, on a cluster, the
/// World Authority, how a node reaches that, and the names of stations and items.
/// </summary>
internal static class Stations
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int Attempts = 4;

    /// <summary>This process keeps the market and the queues: a plain server, or the World Authority of a cluster.</summary>
    public static bool IsAuthority =>
        !PluginCluster.IsClusterProcess || Plugin.Cluster?.Context.Role == "WorldAuthority";

    /// <summary>
    /// Runs a global command of this plugin on the World Authority, retried with the same
    /// operation id. <paramref name="done"/> gets, on the game thread, Applied with the command's
    /// answer, Refused with why, or Unknown when the World Authority didn't answer.
    /// </summary>
    public static void Call(
        string command,
        Guid operationId,
        string payload,
        Action<CallOutcome, string> done
    ) => _ = CallRemote(command, operationId, Encoding.UTF8.GetBytes(payload), done);

    private static async Task CallRemote(
        string command,
        Guid operationId,
        byte[] payload,
        Action<CallOutcome, string> done
    )
    {
        var outcome = CallOutcome.Unknown;
        string text = "The World Authority did not answer.";
        try
        {
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
                var reply = await Plugin
                    .Cluster.ExecuteGlobalAsync(command, payload, operationId, Timeout)
                    .ConfigureAwait(false);
                if (reply.Code == PluginResultCode.Success)
                {
                    outcome = CallOutcome.Applied;
                    text = Encoding.UTF8.GetString(reply.Payload ?? []);
                    break;
                }
                if (reply.Code is PluginResultCode.Invalid or PluginResultCode.Conflict)
                {
                    outcome = CallOutcome.Refused;
                    text = reply.Error ?? reply.Code.ToString();
                    break;
                }
                text = $"The World Authority did not answer ({reply.Code}).";
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"Global command {command} failed", e);
        }
        OnGameThread(() => done(outcome, text));
    }

    /// <summary>
    /// Asks the World Authority for something one of this plugin's handlers answers: the answer, or
    /// null with the reason, to <paramref name="done"/> on the game thread.
    /// </summary>
    public static void Ask(string topic, string payload, Action<string, string> done) =>
        _ = AskRemote(topic, Encoding.UTF8.GetBytes(payload), done);

    private static async Task AskRemote(string topic, byte[] payload, Action<string, string> done)
    {
        string text = null;
        string error = null;
        try
        {
            var reply = await Plugin
                .Cluster.RequestAsync(
                    new PluginTarget { Kind = PluginTargetKind.WorldAuthority },
                    topic,
                    payload,
                    Guid.NewGuid(),
                    Timeout
                )
                .ConfigureAwait(false);
            if (reply.Code == PluginResultCode.Success)
                text = Encoding.UTF8.GetString(reply.Payload ?? []);
            else
                error = $"The World Authority did not answer ({reply.Code}).";
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"Request {topic} failed", e);
            error = "The World Authority could not be asked.";
        }
        OnGameThread(() => done(text, error));
    }

    private static void OnGameThread(Action action) =>
        MySandboxGame.Static.Invoke(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Plugin.Log.Error("World Authority callback failed", e);
                }
            },
            "Octahedron stations"
        );

    /// <summary>The market's name of an item: "Ingot/Iron" for MyObjectBuilder_Ingot/Iron.</summary>
    public static string ItemName(MyDefinitionId id) =>
        id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + id.SubtypeName;

    public static bool TryItemId(string item, out MyDefinitionId id) =>
        MyDefinitionId.TryParse("MyObjectBuilder_" + item, out id);

    /// <summary>The trade station names of the world: the Name of every faction station entry.</summary>
    public static IEnumerable<string> StationNames() =>
        MySession
            .Static.Factions.Select(f => f.Value)
            .SelectMany(f => f.Stations)
            .Select(s => s.Name)
            .Where(Amounts.ValidName)
            .Distinct();

    /// <summary>The station a grid is, by the faction station entries; null when it is none.</summary>
    public static string StationOf(MyCubeGrid grid)
    {
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (station.StationEntityId == grid.EntityId && Amounts.ValidName(station.Name))
                return station.Name;
        return null;
    }

    /// <summary>A station by its name or the start of it, for admin commands.</summary>
    public static bool Find(ref string station, out string error)
    {
        error = null;
        string given = station;
        var names = StationNames().ToList();
        var starting = names
            .Where(n => n.StartsWith(given, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var match =
            names.FirstOrDefault(n => n == given) ?? (starting.Count == 1 ? starting[0] : null);
        if (match == null)
        {
            error = $"No station '{given}'. Stations: {string.Join(", ", names)}";
            return false;
        }
        station = match;
        return true;
    }
}
