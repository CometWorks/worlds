using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace ServerPlugin;

/// <summary>
/// The shipyards' queues and build ledgers in the game: one <see cref="BuildBook"/> kept with
/// the ledgers and the market, by the server itself or the World Authority of a cluster, in the
/// world's sandbox variables. The shipyards (<see cref="ShipyardOrders"/>) send their requests
/// here; on the authority the book moves the items through the ledgers and the market and the
/// money through the bank accounts.
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

    private static OctahedronConfig Config => Plugin.Config;

    public static BuildSettings Settings => new() { QueueMax = Config.ShipyardQueueMax };

    /// <summary>The queues, on the authority only, loaded from the world on first use.</summary>
    public static BuildBook Book
    {
        get
        {
            if (!Ledgers.IsAuthority)
                throw new InvalidOperationException("Only the authority keeps the build ledgers");
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
            var loaded = BuildBook.Deserialize(text);
            Plugin.Log.Info($"Shipyard queues loaded: {loaded.Orders.Count()} orders");
            return loaded;
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

    /// <summary>The market for the builds, none while it is closed: then the ledger alone supplies.</summary>
    private sealed class GameMarket : IBuildMarket
    {
        public List<MarketFill> PlanBuy(string station, LedgerOwner buyer, string item, long raw) =>
            Market.Book.PlanBuy(Market.World, Market.Settings, station, buyer, item, raw);

        public MarketResult Buy(
            string station,
            LedgerOwner buyer,
            string item,
            long raw,
            string reservation
        ) =>
            Market.Run(
                new MarketRequest
                {
                    Verb = KioskVerb.Buy,
                    Station = station,
                    Owner = buyer,
                    Item = item,
                    Amount = raw,
                    Reservation = reservation,
                }
            );
    }

    private static readonly GameMarket TheMarket = new();

    /// <summary>On the authority: does the request and logs the changes.</summary>
    public static string Run(BuildRequest request)
    {
        if (!request.Reads)
        {
            // OCT gets the fees and sells components; a free build needs neither
            if (request.Verb == BuildVerb.Order && request.Payer.IsSet && Market.OctId == 0)
                return $"0\tNo faction tagged {Config.MoneyAuthorityFactionTag}.";
            if (
                request.Verb is BuildVerb.Order or BuildVerb.Cancel
                && !Ledgers.StationNames().Contains(request.Station)
            )
                return $"0\tUnknown station '{request.Station}'.";
            if (request.Verb == BuildVerb.Order && request.Payer.Faction)
            {
                string refusal = FactionRefusal(request.Payer.Id, request.Player);
                if (refusal != null)
                    return "0\t" + refusal;
            }
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
        if (!request.Reads && answer.StartsWith("1"))
        {
            dirty = true;
            Plugin.Log.Info(
                $"Shipyard {request.Verb} at {request.Station} by {request.Player} {request.Id}: {answer.Split('\n')[0].Substring(2)}"
            );
        }
        return answer;
    }

    // Paying with the faction needs the trading rank, as the kiosk's Faction switch does
    private static string FactionRefusal(long factionId, long player)
    {
        var faction = MySession.Static.Factions.TryGetFactionById(factionId);
        if (faction == null || !faction.IsMember(player))
            return "You are not in that faction.";
        bool leader = faction.IsLeader(player) || faction.IsFounder(player);
        return KioskRules.MayTrade(leader, Ledgers.Book.MembersTrade(factionId))
            ? null
            : "Your faction rank may not trade with the faction's ledger.";
    }

    /// <summary>
    /// A change wherever the queues are kept; <paramref name="done"/> gets whether it worked and
    /// the message for the player, on the game thread.
    /// </summary>
    public static void Execute(Guid operationId, BuildRequest request, Action<bool, string> done)
    {
        if (Ledgers.IsAuthority)
        {
            var answer = Run(request);
            done(answer.StartsWith("1"), Message(answer));
            return;
        }
        Ledgers.Call(
            Command,
            operationId,
            request.Encode(),
            (outcome, text) =>
            {
                if (outcome != LedgerOutcome.Applied)
                {
                    done(
                        false,
                        outcome == LedgerOutcome.Unknown
                            ? $"The station did not answer. Ask an admin about shipyard request {operationId}."
                            : text
                    );
                    return;
                }
                done(text.StartsWith("1"), Message(text));
            }
        );
    }

    // The message of a change's answer, without the order that may follow it
    private static string Message(string answer) =>
        answer.Length > 2 ? answer.Substring(2).Split('\n')[0] : "";

    /// <summary>A read wherever the queues are kept: the answer, or null with why, on the game thread.</summary>
    public static void Read(BuildRequest request, Action<string, string> done)
    {
        if (Ledgers.IsAuthority)
        {
            done(Run(request), null);
            return;
        }
        Ledgers.Ask(ReadTopic, request.Encode(), done);
    }

    /// <summary>On a cluster's World Authority: serves the nodes' requests once the plugin services are up.</summary>
    public static void Update()
    {
        if (
            commandRegistration != null
            || registrationFailed
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
            || !Ledgers.IsAuthority
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

    private static byte[] Serve(byte[] payload) =>
        Encoding.UTF8.GetBytes(Run(BuildRequest.Decode(Encoding.UTF8.GetString(payload))));

    private static Task<byte[]> ServeRead(PluginMessage message) =>
        Task.FromResult(
            Encoding.UTF8.GetBytes(
                Run(BuildRequest.Decode(Encoding.UTF8.GetString(message.Payload ?? [])))
            )
        );
}
