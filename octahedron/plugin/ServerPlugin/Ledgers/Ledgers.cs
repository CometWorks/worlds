using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;

namespace ServerPlugin;

/// <summary>Whether a ledger change applied. Unknown: the World Authority didn't answer in time; the
/// change may or may not have applied, and sending the same operation id again tells.</summary>
public enum LedgerOutcome
{
    Applied,
    Refused,
    Unknown,
}

public readonly struct LedgerResult
{
    public readonly LedgerOutcome Outcome;

    // Why it was refused or is unknown
    public readonly string Message;

    public LedgerResult(LedgerOutcome outcome, string message)
    {
        Outcome = outcome;
        Message = message;
    }

    public bool Applied => Outcome == LedgerOutcome.Applied;
}

/// <summary>
/// The station warehouse ledgers in the game: one <see cref="LedgerBook"/> kept by the authority,
/// the server itself or, on a cluster, the World Authority. It lives in the world's sandbox
/// variables (Sandbox.sbc), so it is saved with the world, and on a cluster with the World
/// Authority's global saves.
///
/// Every node changes and reads it through <see cref="Execute"/> and <see cref="Read"/>, which
/// call the World Authority on a cluster: changes as global commands, once per operation id,
/// reads as requests. Code that runs on the authority, for example a World Authority command
/// that also moves money, uses <see cref="Apply"/> directly.
/// </summary>
internal static class Ledgers
{
    public const string Variable = "Octahedron.Ledgers";
    private const string Command = "ledger";
    private const string ReadTopic = "ledger/read";
    private const string Applied = "ok";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const int Attempts = 4;

    private static LedgerBook book;
    private static MySession bookSession;
    private static bool dirty;
    private static IDisposable commandRegistration;
    private static IDisposable readRegistration;
    private static bool registrationFailed;

    /// <summary>This process keeps the ledgers: a plain server, or the World Authority of a cluster.</summary>
    public static bool IsAuthority =>
        !PluginCluster.IsClusterProcess || Plugin.Cluster?.Context.Role == "WorldAuthority";

    private static readonly Rules GameRules = new();

    /// <summary>The ledgers, on the authority only, loaded from the world on first use.</summary>
    public static LedgerBook Book
    {
        get
        {
            if (!IsAuthority)
                throw new InvalidOperationException("Only the authority keeps the ledgers");
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

    private static LedgerBook Load()
    {
        if (
            MyAPIUtilities.Static.Variables.TryGetValue(Variable, out object value)
            && value is string text
            && text.Length > 0
        )
        {
            var loaded = LedgerBook.Deserialize(text, GameRules);
            Plugin.Log.Info($"Ledgers loaded: {loaded.Stations.Count} stations");
            return loaded;
        }
        return new LedgerBook(GameRules);
    }

    /// <summary>On the authority: applies the ops, all or none. Null when they applied, else why not.</summary>
    public static string Apply(IReadOnlyList<LedgerOp> ops)
    {
        string error = Book.Apply(ops);
        if (error != null)
            return error;
        dirty = true;
        foreach (var op in ops)
            Plugin.Log.Info("Ledger " + Describe(op));
        return null;
    }

    private static string Describe(LedgerOp op) =>
        $"{op.Action} at {op.Station}: {op.Owner}"
        + (op.Other.IsSet ? $" to {op.Other}" : "")
        + (op.Reservation != null ? $" reservation {op.Reservation}" : "")
        + (op.Counted ? " counted" : "")
        + (
            op.Items.Count == 0
                ? " (all)"
                : " "
                    + string.Join(
                        ", ",
                        op.Items.Select(i => $"{i.Key} {LedgerBook.Units(i.Value)}")
                    )
        );

    // Called before every save, the World Authority's global saves included
    [HarmonyPatch(typeof(MySession), nameof(MySession.GetCheckpoint))]
    private static class SavePatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix()
        {
            try
            {
                Flush();
            }
            catch (Exception e)
            {
                Plugin.Log.Error("Saving the ledgers failed", e);
            }
        }
    }

    private static void Flush()
    {
        if (!dirty || book == null || bookSession != MySession.Static)
            return;
        MyAPIUtilities.Static.Variables[Variable] = book.Serialize();
        dirty = false;
    }

    /// <summary>
    /// Applies the ops, all or none, wherever the ledgers are kept. <paramref name="done"/> runs on the
    /// game thread, at once on the authority. Keep the operation id with the thing it is for: after
    /// an Unknown outcome, the same id and ops tell whether it applied, without applying it twice.
    /// </summary>
    public static void Execute(
        Guid operationId,
        IReadOnlyList<LedgerOp> ops,
        Action<LedgerResult> done
    )
    {
        if (IsAuthority)
        {
            string error = Apply(ops);
            done(
                error == null
                    ? new LedgerResult(LedgerOutcome.Applied, null)
                    : new LedgerResult(LedgerOutcome.Refused, error)
            );
            return;
        }
        var payload = Encoding.UTF8.GetBytes(LedgerOp.EncodeBatch(ops));
        _ = ExecuteRemote(operationId, payload, done);
    }

    private static async Task ExecuteRemote(
        Guid operationId,
        byte[] payload,
        Action<LedgerResult> done
    )
    {
        var result = new LedgerResult(LedgerOutcome.Unknown, "The World Authority did not answer.");
        try
        {
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
                var reply = await Plugin
                    .Cluster.ExecuteGlobalAsync(Command, payload, operationId, Timeout)
                    .ConfigureAwait(false);
                if (reply.Code == PluginResultCode.Success)
                {
                    string text = Encoding.UTF8.GetString(reply.Payload ?? []);
                    result =
                        text == Applied
                            ? new LedgerResult(LedgerOutcome.Applied, null)
                            : new LedgerResult(LedgerOutcome.Refused, text);
                    break;
                }
                if (reply.Code is PluginResultCode.Invalid or PluginResultCode.Conflict)
                {
                    result = new LedgerResult(
                        LedgerOutcome.Refused,
                        reply.Error ?? reply.Code.ToString()
                    );
                    break;
                }
                result = new LedgerResult(
                    LedgerOutcome.Unknown,
                    $"The World Authority did not answer ({reply.Code})."
                );
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Ledger command failed", e);
        }
        OnGameThread(() => done(result));
    }

    /// <summary>
    /// A copy of the station's ledger, all of it or one owner's part, to <paramref name="done"/> on
    /// the game thread; null with the reason when the World Authority can't be reached.
    /// </summary>
    public static void Read(string station, LedgerOwner? owner, Action<LedgerBook, string> done)
    {
        if (IsAuthority)
        {
            done(Book.Extract(station, owner), null);
            return;
        }
        _ = ReadRemote(station, owner, done);
    }

    private static async Task ReadRemote(
        string station,
        LedgerOwner? owner,
        Action<LedgerBook, string> done
    )
    {
        LedgerBook copy = null;
        string error = null;
        try
        {
            var reply = await Plugin
                .Cluster.RequestAsync(
                    new PluginTarget { Kind = PluginTargetKind.WorldAuthority },
                    ReadTopic,
                    Encoding.UTF8.GetBytes(station + "\t" + owner),
                    Guid.NewGuid(),
                    Timeout
                )
                .ConfigureAwait(false);
            if (reply.Code == PluginResultCode.Success)
                copy = LedgerBook.Deserialize(Encoding.UTF8.GetString(reply.Payload), GameRules);
            else
                error = $"The World Authority did not answer ({reply.Code}).";
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Ledger read failed", e);
            error = "The ledger could not be read.";
        }
        OnGameThread(() => done(copy, error));
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
                    Plugin.Log.Error("Ledger callback failed", e);
                }
            },
            "Octahedron ledgers"
        );

    /// <summary>On the World Authority: serves the nodes' changes and reads once the cluster's plugin services are up.</summary>
    public static void Update()
    {
        if (
            commandRegistration != null
            || registrationFailed
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
            || !IsAuthority
        )
            return;

        commandRegistration = Plugin.Cluster.RegisterGlobalCommand(Command, Serve);
        if (commandRegistration == null)
        {
            registrationFailed = true;
            Plugin.Log.Error(
                "This cluster has no global command ledger: the station ledgers are unavailable"
            );
            return;
        }
        readRegistration = Plugin.Cluster.RegisterHandler(ReadTopic, ServeRead);
        Plugin.Log.Info("Serving the station ledgers on the World Authority");
    }

    private static byte[] Serve(byte[] payload)
    {
        var ops = LedgerOp.DecodeBatch(Encoding.UTF8.GetString(payload));
        return Encoding.UTF8.GetBytes(Apply(ops) ?? Applied);
    }

    private static Task<byte[]> ServeRead(PluginMessage message)
    {
        var f = Encoding.UTF8.GetString(message.Payload ?? []).Split('\t');
        LedgerOwner? owner = f.Length > 1 && LedgerOwner.TryParse(f[1], out var o) ? o : null;
        return Task.FromResult(Encoding.UTF8.GetBytes(Book.Extract(f[0], owner).Serialize()));
    }

    // --- items and stations ---

    /// <summary>The ledger's name of an item: "Ingot/Iron" for MyObjectBuilder_Ingot/Iron.</summary>
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
            .Where(LedgerBook.ValidName)
            .Distinct();

    /// <summary>The station a grid is, by the faction station entries; null when it is none.</summary>
    public static string StationOf(MyCubeGrid grid)
    {
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (station.StationEntityId == grid.EntityId && LedgerBook.ValidName(station.Name))
                return station.Name;
        return null;
    }

    private sealed class Rules : ILedgerRules
    {
        public bool Item(string item, out double massKg, out bool integral)
        {
            massKg = 0;
            integral = true;
            if (
                !TryItemId(item, out var id)
                || !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
            )
                return false;
            massKg = definition.Mass;
            integral = definition.HasIntegralAmounts;
            return true;
        }

        public double LimitKg(LedgerOwner owner) =>
            1000
            * (
                owner.Faction
                    ? Plugin.Config.LedgerFactionLimitTonnes
                    : Plugin.Config.LedgerPlayerLimitTonnes
            );

        public bool KnownStation(string station) => StationNames().Contains(station);

        public bool KnownOwner(LedgerOwner owner) =>
            owner.Faction
                ? MySession.Static.Factions.TryGetFactionById(owner.Id) != null
                : MySession.Static.Players.TryGetIdentity(owner.Id) != null;
    }
}
