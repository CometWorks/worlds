using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace ServerPlugin;

// When each player's last free shipyard build ended, for the cool-down between free builds. A
// plain server keeps it in a world variable, saved with the world. On a cluster the World
// Authority keeps that variable: the node ending a free build broadcasts the time to every
// process, and a node asks the World Authority for the whole list when it starts.
internal static class FreePrints
{
    private const string VariableName = "Octahedron-FreePrints";
    private const string WorldAuthorityRole = "WorldAuthority";
    private const string RecordTopic = "free-print";
    private const string ListTopic = "free-prints";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Identity id -> unix seconds the player's last free build ended
    private static Dictionary<long, long> ended = new();
    private static bool loaded;

    // Cluster only, all used on the game thread
    private static IDisposable[] handlers;
    private static Task<PluginResult> listing;
    private static bool listed;
    private static readonly Dictionary<long, long> unsent = new();
    private static Task<PluginBroadcastResult> sending;
    private static string sent;

    private static PluginClusterClient Cluster => Plugin.Cluster;

    private static bool Authority => Cluster == null || Cluster.Context.Role == WorldAuthorityRole;

    // False on a cluster node until it has the World Authority's list
    public static bool Ready
    {
        get
        {
            Load();
            return loaded && (Authority || listed);
        }
    }

    // How long the player still waits for a free build, zero when they can order one
    public static TimeSpan Wait(long player)
    {
        Load();
        if (!ended.TryGetValue(player, out var seconds))
            return TimeSpan.Zero;
        var wait =
            DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            + TimeSpan.FromMinutes(Plugin.Config.FreeBuildCooldownMinutes)
            - DateTime.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    // A free build ended with the ship handed over: the cool-down starts
    public static void Ended(long player)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Record(player, now);
        if (Cluster != null)
            unsent[player] = now;
    }

    public static void Update()
    {
        if (Cluster == null || !Cluster.Context.Available || MySession.Static?.Ready != true)
            return;
        handlers ??= Register();

        if (!Authority && !listed)
        {
            if (listing == null)
                listing = Cluster.RequestAsync(
                    new PluginTarget { Kind = PluginTargetKind.WorldAuthority },
                    ListTopic,
                    [],
                    Guid.NewGuid(),
                    Timeout
                );
            else if (listing.IsCompleted)
            {
                if (listing.Result is { Code: PluginResultCode.Success, Payload: { } payload })
                {
                    foreach (var (player, seconds) in Parse(Encoding.UTF8.GetString(payload)))
                        Record(player, seconds);
                    listed = true;
                }
                else
                    Plugin.Log.Warning(
                        $"Free builds: no cool-down list from the World Authority ({listing.Result.Code}), asking again"
                    );
                listing = null;
            }
        }

        if (sending is { IsCompleted: true })
        {
            if (sending.Result.Code != PluginResultCode.Success)
            {
                Plugin.Log.Warning(
                    $"Free builds: sending cool-downs failed ({sending.Result.Code}), sending again"
                );
                // Every process keeps the later time, so sending one twice does no harm
                foreach (var (player, seconds) in Parse(sent))
                    if (!unsent.ContainsKey(player))
                        unsent[player] = seconds;
            }
            sending = null;
        }
        if (sending == null && unsent.Count > 0)
        {
            sent = Format(unsent);
            unsent.Clear();
            sending = Cluster.BroadcastAsync(
                RecordTopic,
                Encoding.UTF8.GetBytes(sent),
                Guid.NewGuid(),
                Timeout
            );
        }
    }

    // Handlers start on the game thread
    private static IDisposable[] Register()
    {
        var record = Cluster.RegisterHandler(
            RecordTopic,
            message =>
            {
                foreach (var (player, seconds) in Parse(Encoding.UTF8.GetString(message.Payload)))
                    Record(player, seconds);
                return Task.FromResult<byte[]>([]);
            }
        );
        if (!Authority)
            return [record];
        var list = Cluster.RegisterHandler(
            ListTopic,
            _ =>
            {
                Load();
                return Task.FromResult(Encoding.UTF8.GetBytes(Format(ended)));
            }
        );
        return [record, list];
    }

    private static void Record(long player, long seconds)
    {
        Load();
        if (ended.TryGetValue(player, out var known) && known >= seconds)
            return;
        ended[player] = seconds;
        if (Authority)
            MyAPIGateway.Utilities.SetVariable(VariableName, Format(ended));
    }

    // On a cluster the role is known once the cluster context is
    private static void Load()
    {
        if (loaded || (Cluster != null && !Cluster.Context.Available))
            return;
        loaded = true;
        if (Authority && MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var text))
            foreach (var (player, seconds) in Parse(text))
                if (!ended.TryGetValue(player, out var known) || known < seconds)
                    ended[player] = seconds;
    }

    // <identity>:<unix seconds>,...
    private static string Format(Dictionary<long, long> times) =>
        string.Join(
            ",",
            times.Select(p =>
                p.Key.ToString(CultureInfo.InvariantCulture)
                + ":"
                + p.Value.ToString(CultureInfo.InvariantCulture)
            )
        );

    private static IEnumerable<(long, long)> Parse(string text)
    {
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(':');
            if (
                pair.Length == 2
                && long.TryParse(
                    pair[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var player
                )
                && long.TryParse(
                    pair[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var seconds
                )
            )
                yield return (player, seconds);
        }
    }
}
