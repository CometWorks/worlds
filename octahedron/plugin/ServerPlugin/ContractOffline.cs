using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
#if NETCOREAPP
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
#endif

namespace ServerPlugin;

// On the World Authority: which grids are in offline partitions and since when, from the gateway's
// table, and what they were at the freeze, from its grid rows (Docs/Gateway/GatewayAPI.md of
// CometWorks/cluster). The plugin SDK has no view of them. Grids is null until the first answer;
// without one the wrecks wait. A cluster older than the rows' route leaves only what nodes reported.
// A cluster only runs on .NET 10.
internal static class ContractOffline
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static DateTime nextPoll;
    private static volatile Task polling;
    private static volatile Dictionary<long, DateTime> grids;
    private static volatile List<ContractOfflineGrid> rows = [];

#if NETCOREAPP
    private static readonly HttpClient Gateway = NewClient();
    private static long revision;

    // Partitions whose grids the gateway doesn't know yet: its nodes are reading them from the saves
    private static volatile int unread;

    // Null while nothing was asked; false once a cluster without the route refused
    private static bool? hasRows;

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var token =
            Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT")
            ?? Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token
            );
        return client;
    }

    private static async Task Poll(string root)
    {
        try
        {
            var text = await Gateway
                .GetStringAsync(
                    $"{root}/registry/partitions/offline?since={revision.ToString(CultureInfo.InvariantCulture)}"
                )
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var table = document.RootElement;
            var answered = table.GetProperty("Revision").GetInt64();
            var changed = !table.GetProperty("Unchanged").GetBoolean() || grids == null;
            if (changed)
            {
                var found = new Dictionary<long, DateTime>();
                foreach (var row in table.GetProperty("Partitions").EnumerateArray())
                {
                    var since = row.GetProperty("OfflineAt").GetDateTimeOffset().UtcDateTime;
                    foreach (var member in row.GetProperty("Members").EnumerateArray())
                        found[member.GetInt64()] = since;
                }
                grids = found;
                revision = answered;
            }
            if (changed || unread > 0 || hasRows == null)
                await PollRows(root).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Contracts: the offline partitions could not be read", e);
        }
    }

    private static async Task PollRows(string root)
    {
        using var answer = await Gateway
            .GetAsync($"{root}/registry/partitions/offline/grids")
            .ConfigureAwait(false);
        if (answer.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            if (hasRows != false)
                Plugin.Log.Info(
                    "Contracts: this cluster has no offline grid rows, wrecks only from what nodes reported"
                );
            hasRows = false;
            return;
        }
        answer.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await answer.Content.ReadAsStringAsync().ConfigureAwait(false)
        );
        var found = new List<ContractOfflineGrid>();
        foreach (var row in document.RootElement.GetProperty("grids").EnumerateArray())
        {
            var grid = row.GetProperty("grid");
            double? Number(string name) =>
                grid.TryGetProperty(name, out var value) ? value.GetDouble() : null;
            long? Id(string name) =>
                grid.TryGetProperty(name, out var value) ? value.GetInt64() : null;
            bool? Fact(string name) =>
                grid.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
            found.Add(
                new ContractOfflineGrid(
                    grid.GetProperty("id").GetInt64(),
                    grid.GetProperty("n").GetString(),
                    grid.GetProperty("s").GetBoolean(),
                    grid.GetProperty("b").GetInt32(),
                    new ContractPoint(
                        grid.GetProperty("x").GetDouble(),
                        grid.GetProperty("y").GetDouble(),
                        grid.GetProperty("z").GetDouble()
                    ),
                    grid.GetProperty("o").EnumerateArray().Select(o => o.GetInt64()).ToArray(),
                    Number("m"),
                    Id("g"),
                    Fact("f"),
                    Fact("p"),
                    Fact("t")
                )
            );
        }
        rows = found;
        unread = document.RootElement.GetProperty("unknown").GetInt32();
        hasRows = true;
    }
#endif

    public static Dictionary<long, DateTime> Grids(DateTime now)
    {
#if NETCOREAPP
        var root = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        if (now >= nextPoll && polling?.IsCompleted != false && !string.IsNullOrEmpty(root))
        {
            nextPoll = now + Interval;
            polling = Task.Run(() => Poll(root));
        }
#endif
        return grids;
    }

    // The gateway's grid rows as last read; Grids polls
    public static List<ContractOfflineGrid> Rows => rows;

#if NETCOREAPP
    public static int Unread => unread;
#else
    public static int Unread => 0;
#endif
}
