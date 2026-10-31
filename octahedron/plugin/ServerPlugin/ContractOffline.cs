using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
#if NETCOREAPP
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
#endif


namespace ServerPlugin;

// On the World Authority: which grids are in offline partitions and since when, from the gateway's
// table (Docs/Gateway/GatewayAPI.md of CometWorks/cluster). The plugin SDK has no view of it. Null
// until the first answer; without one the wrecks wait. A cluster only runs on .NET 10.
internal static class ContractOffline
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static DateTime nextPoll;
    private static volatile Task polling;
    private static volatile Dictionary<long, DateTime> grids;

#if NETCOREAPP
    private static readonly HttpClient Gateway = NewClient();
    private static long revision;

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
            if (table.GetProperty("Unchanged").GetBoolean() && grids != null)
                return;
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
        catch (Exception e)
        {
            Plugin.Log.Error("Contracts: the offline partitions could not be read", e);
        }
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
}
