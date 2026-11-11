using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.FileSystem;

namespace ServerPlugin;

/// <summary>
/// The shipyards' queues in the game: one <see cref="BuildBook"/> kept with the market, by the
/// server itself or the World Authority of a cluster, in the world's sandbox variables. The
/// shipyards (<see cref="ShipyardOrders"/>) send their requests here; on the authority the book
/// buys at the market and moves the money through the bank accounts.
/// </summary>
internal static class Builds
{
    public const string Variable = "Octahedron.Builds";
    private const string Command = "build";
    private const string ReadTopic = "build/read";

    private static BuildBook book;
    private static MySession bookSession;
    private static bool dirty;
    private static IDisposable commandRegistration;
    private static IDisposable readRegistration;
    private static bool registrationFailed;
    private static readonly Stopwatch queueSampleClock = Stopwatch.StartNew();

    private static OctahedronConfig Config => Plugin.Config;

    public static BuildSettings Settings =>
        new()
        {
            QueueMax = Config.ShipyardQueueMax,
            TurnMinutes = Config.ShipyardTurnMinutes,
            LoadTest = Config.ShipyardLoadTest,
        };

    /// <summary>The queues, on the authority only, loaded from the world on first use.</summary>
    public static BuildBook Book
    {
        get
        {
            if (!Stations.IsAuthority)
                throw new InvalidOperationException("Only the authority keeps the shipyard queues");
            var session = MySession.Static;
            if (book == null || bookSession != session)
            {
                book = Load();
                bookSession = session;
                dirty = false;
            }
            return book;
        }
    }

    private static BuildBook Load()
    {
        if (
            MyAPIUtilities.Static.Variables.TryGetValue(Variable, out object value)
            && value is string text
            && text.Length > 0
        )
        {
            try
            {
                var loaded = BuildBook.Deserialize(text);
                Plugin.Log.Info($"Shipyard queues loaded: {loaded.Orders.Count()} orders");
                return loaded;
            }
            catch (FormatException e)
            {
                // A world from before the Store blocks; those need a wipe anyway
                Plugin.Log.Warning("Shipyard queues: the saved ones are left out: " + e.Message);
            }
        }
        return new BuildBook();
    }

    [HarmonyPatch(typeof(MySession), nameof(MySession.GetCheckpoint))]
    private static class SavePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix()
        {
            try
            {
                if (dirty && book != null && bookSession == MySession.Static)
                {
                    MyAPIUtilities.Static.Variables[Variable] = book.Serialize();
                    dirty = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error("Saving the shipyard queues failed", e);
            }
        }
    }

    /// <summary>The market for the builds, none while it is closed: then nothing is for sale.</summary>
    private sealed class GameMarket : IBuildMarket
    {
        public MarketFill PlanBuy(string station, string item, long raw) =>
            Market.Book.PlanBuy(Market.World, Market.Settings, station, item, raw);

        public MarketResult Buy(string station, long buyer, string item, long raw) =>
            Market.Buy(station, buyer, item, raw);

        public void Return(string station, string item, long raw) =>
            Market.Return(station, item, raw);

        public long SalePrice(string station, string item) =>
            Market.Book.SalePrice(Market.World, Market.Settings, station, item);

        public long SellOff(string station, long seller, IReadOnlyDictionary<string, long> items) =>
            Market.SellOff(station, seller, items);
    }

    private static readonly GameMarket TheMarket = new();

    /// <summary>On the authority: does the request and logs the changes.</summary>
    public static string Run(BuildRequest request)
    {
        if (!request.Reads)
        {
            // OCT gets the fees and sells components; a free build needs neither
            if (request.Verb == BuildVerb.Order && request.Payer != 0 && Market.OctId == 0)
                return $"0\tNo faction tagged {Config.MoneyAuthorityFactionTag}.";
            if (
                request.Verb
                    is BuildVerb.Order
                        or BuildVerb.Cancel
                        or BuildVerb.Tow
                        or BuildVerb.Sale
                && !Stations.StationNames().Contains(request.Station)
            )
                return $"0\tUnknown station '{request.Station}'.";
        }
        string answer;
        try
        {
            answer = request.Run(Book, Market.World, Market.On ? TheMarket : null, Settings);
        }
        catch (OverflowException)
        {
            answer = "0\tThe amount is too large.";
        }
        if (!request.Reads)
        {
            // A refusal too: the player only sees it in chat
            var ok = answer.StartsWith("1");
            dirty |= ok;
            Plugin.Log.Info(
                $"Shipyard {request.Verb} at {request.Station} by {request.Player} {request.Id}{(ok ? "" : " refused")}: {Message(answer)}"
            );
        }
        return answer;
    }

    /// <summary>
    /// A change wherever the queues are kept; <paramref name="done"/> gets whether it worked and
    /// the message for the player, on the game thread.
    /// </summary>
    public static void Execute(Guid operationId, BuildRequest request, Action<bool, string> done) =>
        Execute(operationId, request, (ok, message, _) => done(ok, message));

    /// <summary>As the other Execute, and gets the order as the change left it, null if it did not work or the order is gone from the answer.</summary>
    public static void Execute(
        Guid operationId,
        BuildRequest request,
        Action<bool, string, BuildOrder> done
    )
    {
        if (Stations.IsAuthority)
        {
            var answer = Run(request);
            done(answer.StartsWith("1"), Message(answer), Order(answer));
            return;
        }
        Stations.Call(
            Command,
            operationId,
            request.Encode(),
            (outcome, text) =>
            {
                if (outcome != CallOutcome.Applied)
                {
                    done(
                        false,
                        outcome == CallOutcome.Unknown
                            ? $"The station did not answer. Ask an admin about shipyard request {operationId}."
                            : text,
                        null
                    );
                    return;
                }
                done(text.StartsWith("1"), Message(text), Order(text));
            }
        );
    }

    private static BuildOrder Order(string answer)
    {
        var lines = answer.Split('\n');
        return lines.Length > 1 ? BuildBook.DecodeOrder(lines[1]) : null;
    }

    /// <summary>
    /// Orders a ship, wherever the queues are kept: a blueprint too big for one message goes
    /// ahead in parts. <paramref name="done"/> as for <see cref="Execute"/>.
    /// </summary>
    public static void Order(
        Guid operationId,
        BuildRequest request,
        Action<bool, string, BuildOrder> done
    )
    {
        var parts = BuildBook.BlueprintParts(request.Blueprint);
        if (parts.Count == 1)
        {
            Execute(operationId, request, done);
            return;
        }
        request.Id = operationId.ToString("N");
        request.Blueprint = "";
        request.Part = parts.Count;
        Upload(0);

        void Upload(int part)
        {
            if (part == parts.Count)
            {
                Execute(operationId, request, done);
                return;
            }
            Read(
                new BuildRequest
                {
                    Verb = BuildVerb.Upload,
                    Id = request.Id,
                    Part = part,
                    Blueprint = parts[part],
                },
                (answer, error) =>
                {
                    if (answer == "1")
                        Upload(part + 1);
                    else
                        done(false, error ?? "The blueprint did not reach the shipyard.", null);
                }
            );
        }
    }

    /// <summary>The blueprint of an order to <paramref name="done"/>, null when it can't be read or the order is gone.</summary>
    public static void ReadBlueprint(string orderId, Action<string> done)
    {
        var blueprint = new StringBuilder();
        Download(0);

        void Download(int part) =>
            Read(
                new BuildRequest
                {
                    Verb = BuildVerb.Download,
                    Id = orderId,
                    Part = part,
                },
                (answer, _) =>
                {
                    if (answer == null || !answer.StartsWith("1\t"))
                    {
                        done(null);
                        return;
                    }
                    blueprint.Append(answer, 2, answer.Length - 2);
                    if (answer.Length - 2 < BuildBook.BlueprintPartSize)
                        done(blueprint.ToString());
                    else
                        Download(part + 1);
                }
            );
    }

    // The message of a change's answer, without the order that may follow it
    private static string Message(string answer) =>
        answer.Length > 2 ? answer.Substring(2).Split('\n')[0] : "";

    /// <summary>A read wherever the queues are kept: the answer, or null with why, on the game thread.</summary>
    public static void Read(BuildRequest request, Action<string, string> done)
    {
        if (Stations.IsAuthority)
        {
            done(Run(request), null);
            return;
        }
        Stations.Ask(ReadTopic, request.Encode(), done);
    }

    /// <summary>On a cluster's World Authority: serves the nodes' requests once the plugin services are up.</summary>
    public static void Update()
    {
        if (Config.ShipyardLoadTest && Stations.IsAuthority && MySession.Static?.Ready == true)
            PublishQueueCounts();

        if (
            commandRegistration != null
            || registrationFailed
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
            || !Stations.IsAuthority
            || MySession.Static?.Ready != true
        )
            return;
        commandRegistration = Plugin.Cluster.RegisterGlobalCommand(Command, Serve);
        if (commandRegistration == null)
        {
            registrationFailed = true;
            Plugin.Log.Error("This cluster has no global command build: no paid shipyard builds");
            return;
        }
        readRegistration = Plugin.Cluster.RegisterHandler(ReadTopic, ServeRead);
        Plugin.Log.Info("Serving the shipyard queues on the World Authority");
    }

    private static void PublishQueueCounts()
    {
        if (queueSampleClock.ElapsedMilliseconds < 1000)
            return;
        queueSampleClock.Restart();

        try
        {
            var counts = Book.Orders.ToLookup(o => o.Station);
            var stations = Stations.StationNames().Union(counts.Select(g => g.Key));
            var path = Path.Combine(MyFileSystem.UserDataPath, "octahedron-shipyard-queues.tsv");
            var temporary = path + ".tmp";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cap = Math.Min(Config.ShipyardQueueMax, BuildSettings.LoadTestQueueMax);
            using var process = Process.GetCurrentProcess();
            File.WriteAllLines(
                temporary,
                new[] { FormattableString.Invariant($"{process.Id}\t{timestamp}\t{cap}") }.Concat(
                    stations.Select(name =>
                        name + "\t" + counts[name].Count().ToString(CultureInfo.InvariantCulture)
                    )
                )
            );
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Warning($"Could not publish shipyard queue counts: {e.Message}");
        }
    }

    private static byte[] Serve(byte[] payload) =>
        Encoding.UTF8.GetBytes(Run(BuildRequest.Decode(Encoding.UTF8.GetString(payload))));

    private static Task<byte[]> ServeRead(PluginMessage message) =>
        Task.FromResult(
            Encoding.UTF8.GetBytes(
                Run(BuildRequest.Decode(Encoding.UTF8.GetString(message.Payload ?? [])))
            )
        );
}
