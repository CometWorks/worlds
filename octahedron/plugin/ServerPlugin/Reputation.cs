using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.Utils;

namespace ServerPlugin;

// OCT members have a reputation, in SC (SE-1283). It starts at ReputationStart. Grinding, shooting,
// blowing up or ramming a grid that another member owns most of lowers it by the value lost: the
// block's components at Market.ReferencePrice, the prices the store and the market start from,
// times the share of the block's integrity lost. Paying into OCT's account raises it by the SC
// paid, so a member can pay for what they took apart. Sharing a block with OCT raises it by the
// block's value, unsharing lowers it by as much, and so does taking apart a block of your own
// that you share. Grids the member owns most of are theirs to do with as they like.
//
// "Owns most of" is the game's BigOwners: the owners of the most ownable blocks of the grid, or
// of its mechanical group's largest grid when the grid itself has none (a rotor top of armor).
// OCT's NPC and the trader bots are members too, so taking their ships apart costs reputation.
// The trader bots themselves have no reputation: they are OctProtectedSteamIds.
//
// A ram is the collision damage one member's ship, the faster of the two, does to other
// members' grids, until ReputationRamGapSeconds pass without one. Its pilot pays. The first ram in
// ReputationRamGraceSeconds is forgiven as an accident.
//
// Below zero the member is told. Below ReputationKickBelow a countdown of
// ReputationKickAfterSeconds starts, with warnings at its start and ReputationWarnSeconds before
// its end; at its end they are removed from OCT, and told why.
//
// The reputations live where the faction and the bank accounts do: on a plain server, or on the
// World Authority of a cluster, as a world variable saved with them. The countdowns run there.
// The nodes, where the grids are, send it their changes every second as a global command, which
// the World Authority runs once even when a node sends it again.
internal static class Reputation
{
    private const string Variable = "Octahedron.Reputation";
    private const string Command = "reputation";
    private const string ReadTopic = "reputation/read";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static OctahedronConfig Config => Plugin.Config;
    public static bool On => Config.Enabled && Config.ReputationEnabled;

    private static readonly SteamIdRanges Bots = new(
        nameof(OctahedronConfig.OctProtectedSteamIds),
        () => Config.OctProtectedSteamIds
    );

    // --- on the authority ---

    private static ReputationBook book;
    private static IDisposable commandRegistration;
    private static IDisposable readRegistration;
    private static bool registrationFailed;
    private static DateTime lastTick;

    private static ReputationBook Book
    {
        get
        {
            if (book != null)
                return book;
            MyAPIGateway.Utilities.GetVariable<string>(Variable, out var text);
            try
            {
                book = ReputationBook.Deserialize(text);
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
            {
                Plugin.Log.Error($"Reputation: unreadable state, everyone starts over: {text}", e);
                book = new ReputationBook();
            }
            return book;
        }
    }

    // --- on the nodes ---

    private static readonly Dictionary<
        (long Identity, ReputationKind Kind, string Incident),
        decimal
    > pending = new();
    private static (Guid Id, byte[] Payload)? sending;
    private static bool inFlight;
    private static DateTime nextSend;

    // Each member's latest ram: its incident and when it last hit
    private static readonly Dictionary<long, (string Incident, DateTime Last)> rams = new();

    // Share changes asked for, checked a frame later when the game has made them
    private static readonly List<(long Identity, MyCubeBlock Block, bool Was)> shares = [];

    private static readonly Dictionary<MyCubeBlockDefinition, decimal> values = new();

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true)
            return;
        WatchGrinding();
        CheckShares();
        var now = DateTime.UtcNow;
        if (now < nextSend)
            return;
        nextSend = now + Interval;
        if (Stations.IsAuthority)
        {
            Register();
            Flush();
            Tick(now);
        }
        else
            Send();
    }

    // --- what the members do ---

    /// <summary>A player of OCT with a reputation: not an NPC, not a trader bot</summary>
    private static bool Tracked(long identity) =>
        OctFaction.Member(identity)
        && !Bots.Contains(MySession.Static.Players.TryGetSteamId(identity));

    private static void Add(long identity, ReputationKind kind, decimal amount, string incident)
    {
        if (amount == 0)
            return;
        pending.TryGetValue((identity, kind, incident), out var sum);
        pending[(identity, kind, incident)] = sum + amount;
    }

    /// <summary>The block's components at their reference prices, whole and built, in SC</summary>
    private static decimal Value(MyCubeBlockDefinition definition)
    {
        if (values.TryGetValue(definition, out var value))
            return value;
        foreach (var component in definition.Components ?? [])
            value +=
                (decimal)component.Count
                * Market.ReferencePrice(Stations.ItemName(component.Definition.Id))
                / MarketBook.MilliPerSc;
        return values[definition] = value;
    }

    private static readonly List<MyCubeGrid> group = [];

    private static List<long> Owners(MyCubeGrid grid)
    {
        if (grid.BigOwners.Count > 0)
            return grid.BigOwners;
        group.Clear();
        MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, group);
        return group.OrderByDescending(g => g.BlocksCount).First().BigOwners;
    }

    /// <summary>Integrity the block lost to the member; incident: their ram, null for anything else.
    /// owners: the grid's before the damage, when it may have lost its owned blocks since</summary>
    private static void Lost(
        MySlimBlock block,
        float integrity,
        long identity,
        string incident,
        List<long> owners = null
    )
    {
        if (
            integrity <= 0
            || block.MaxIntegrity <= 0
            || !Tracked(identity)
            || !Plugin.Simulated(block.CubeGrid)
        )
            return;
        var value = Value(block.BlockDefinition) * (decimal)(integrity / block.MaxIntegrity);
        var grid = block.CubeGrid;
        // A block of your own you share: it is unshared as it goes
        if (block.FatBlock is { } fat && fat.OwnerId == identity && Shared(fat))
        {
            Add(identity, ReputationKind.Share, -value, null);
            return;
        }
        owners ??= Owners(grid);
        if (owners.Count == 0 || owners.Contains(identity))
            return;
        var owner = owners.FirstOrDefault(o => OctFaction.Faction.IsMember(o));
        if (owner == 0)
            return;
        Add(
            identity,
            incident == null ? ReputationKind.Damage : ReputationKind.Ram,
            -value,
            incident
        );
        GroupPcuLimit.Notify(
            identity,
            $"{grid.DisplayName} belongs to {MySession.Static.Players.TryGetIdentity(owner)?.DisplayName ?? "OCT"}: "
                + "damaging it costs you reputation in OCT."
        );
    }

    private static bool Shared(MyCubeBlock block) =>
        block.IDModule?.ShareMode
            is MyOwnershipShareModeEnum.Faction
                or MyOwnershipShareModeEnum.All;

    // Who did the damage: a character, a hand tool or gun, a weapon block, a missile
    private static long Attacker(MyEntity entity) =>
        entity switch
        {
            MyCharacter character => character.GetPlayerIdentityId(),
            IMyGunBaseUser gun => gun.OwnerId,
            MyMissile missile => missile.Owner,
            MyCubeBlock block => block.OwnerId,
            _ => 0,
        };

    // The pilot of a ship that hit another, when it was the faster of the two, and their ram
    private static (long Identity, string Incident) Rammer(MyCubeGrid ship, MyCubeGrid hit)
    {
        var pilot =
            ship.GridSystems?.ControlSystem?.GetController()?.Player?.Identity?.IdentityId ?? 0;
        if (pilot == 0 || ship.Physics == null)
            return (0, null);
        var speed = ship.Physics.LinearVelocity.LengthSquared();
        if (hit.Physics != null && hit.Physics.LinearVelocity.LengthSquared() > speed)
            return (0, null);
        var now = DateTime.UtcNow;
        if (
            !rams.TryGetValue(pilot, out var ram)
            || now - ram.Last > TimeSpan.FromSeconds(Config.ReputationRamGapSeconds)
        )
            ram.Incident = Guid.NewGuid().ToString("N");
        rams[pilot] = (ram.Incident, now);
        return (pilot, ram.Incident);
    }

    private static bool Counts(MySlimBlock block) =>
        On && Sync.IsServer && block.CubeGrid != null && OctFaction.Faction != null;

    // Every damage but grinding: weapons, explosions, collisions. The integrity left before and after;
    // the game may only add it up and apply it later.
    [HarmonyPatch(typeof(MySlimBlock), "DoDamageInternal")]
    private static class Damaged
    {
        private static void Prefix(MySlimBlock __instance, out float __state) =>
            __state = __instance.Integrity - __instance.AccumulatedDamage;

        private static void Postfix(
            MySlimBlock __instance,
            float __state,
            MyStringHash damageType,
            long attackerId
        )
        {
            if (attackerId == 0 || exploding || !Counts(__instance))
                return;
            var lost = __state - Math.Max(0, __instance.Integrity - __instance.AccumulatedDamage);
            if (
                lost <= 0
                || !MyEntities.TryGetEntityById(
                    attackerId,
                    out MyEntity attacker,
                    allowClosed: true
                )
            )
                return;
            if (attacker is MyCubeGrid ship)
            {
                if (damageType != MyDamageType.Deformation)
                    return;
                var (pilot, incident) = Rammer(ship, __instance.CubeGrid);
                if (pilot != 0)
                    Lost(__instance, lost, pilot, incident);
                return;
            }
            Lost(__instance, lost, Attacker(attacker), null);
        }
    }

    // Explosions: a warhead, a rocket, an ammo box or a reactor going off. The explosion removes the
    // blocks it destroys without DoDamageInternal and damages the rest through it: all of it is
    // charged here, with the owners each grid had before, and nothing in Damaged.
    [ThreadStatic]
    private static bool exploding;

    [HarmonyPatch]
    private static class Exploded
    {
        // MyExplosion is internal
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                "Sandbox.Game.MyExplosion:ApplyVolumetricDamageToGrid",
                [typeof(MyGridExplosion), typeof(long), typeof(long)]
            );

        private static void Prefix(
            MyGridExplosion damageInfo,
            long attackerEntity,
            out List<(MySlimBlock Block, float Integrity, List<long> Owners)> __state
        )
        {
            __state = null;
            if (
                attackerEntity == 0
                || !On
                || !Sync.IsServer
                || OctFaction.Faction == null
                || damageInfo?.DamagedBlocks is not { Count: > 0 } blocks
            )
                return;
            var owners = new Dictionary<MyCubeGrid, List<long>>();
            __state = [];
            foreach (var block in blocks.Keys)
            {
                if (block.CubeGrid == null)
                    continue;
                if (!owners.TryGetValue(block.CubeGrid, out var gridOwners))
                    owners[block.CubeGrid] = gridOwners = [.. Owners(block.CubeGrid)];
                __state.Add((block, block.Integrity - block.AccumulatedDamage, gridOwners));
            }
            exploding = true;
        }

        private static void Postfix(
            long attackerEntity,
            List<(MySlimBlock Block, float Integrity, List<long> Owners)> __state
        )
        {
            if (
                __state == null
                || !MyEntities.TryGetEntityById(
                    attackerEntity,
                    out MyEntity attacker,
                    allowClosed: true
                )
            )
                return;
            var identity = Attacker(attacker);
            foreach (var (block, before, owners) in __state)
            {
                var gone =
                    block.IsDestroyed || block.CubeGrid.GetCubeBlock(block.Position) != block;
                var after = gone ? 0 : Math.Max(0, block.Integrity - block.AccumulatedDamage);
                Lost(block, before - after, identity, null, owners);
            }
        }

        private static void Finalizer() => exploding = false;
    }

    // A missile's explosion belongs to its owner's character, none when they are offline or dead:
    // then it is the launcher's, which the game would also name for a weapon block's damage
    [ThreadStatic]
    private static MyEntity launcher;

    [HarmonyPatch(typeof(MyMissile), "ExecuteExplosion")]
    private static class MissileExploding
    {
        private static void Prefix(long ___m_launcherId) =>
            launcher = On ? MyEntities.GetEntityById(___m_launcherId, allowClosed: true) : null;

        private static void Finalizer() => launcher = null;
    }

    [HarmonyPatch(typeof(MyExplosions), nameof(MyExplosions.AddExplosion))]
    private static class Explosion
    {
        private static void Prefix(ref MyExplosionInfo explosionInfo)
        {
            if (launcher != null && explosionInfo.OwnerEntity == null)
                explosionInfo.OwnerEntity = launcher;
        }
    }

    // ToolCore grinds with no player (identityId 0), but tells the damage system first which tool
    // block grinds, so its owner pays. The last grind announced on this thread, until the block's grinding.
    [ThreadStatic]
    private static (MySlimBlock Block, long AttackerId) announced;
    private static MyDamageSystem watched;

    private static void WatchGrinding()
    {
        var system = MyDamageSystem.Static;
        if (system == null || system == watched)
            return;
        watched = system;
        system.RegisterBeforeDamageHandler(
            int.MaxValue,
            (object target, ref MyDamageInformation info) =>
            {
                if (target is MySlimBlock block && info.Type == MyDamageType.Grind)
                    announced = (block, info.AttackerId);
            }
        );
    }

    // Hand and ship grinders say who grinds; ToolCore's say which tool block
    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.DecreaseMountLevel))]
    private static class Ground
    {
        private static void Prefix(MySlimBlock __instance, out float __state) =>
            __state = __instance.Integrity;

        private static void Postfix(MySlimBlock __instance, float __state, long identityId)
        {
            var (block, attackerId) = announced;
            announced = default;
            if (
                identityId == 0
                && block == __instance
                && MyEntities.TryGetEntityById(attackerId, out MyEntity tool, allowClosed: true)
            )
                identityId = Attacker(tool);
            if (identityId != 0 && Counts(__instance))
                Lost(__instance, __state - __instance.Integrity, identityId, null);
        }
    }

    // A deposit from the faction screen, and its transfer once the bank (the World Authority's on a
    // cluster) took it
    [ThreadStatic]
    private static bool depositing;

    [HarmonyPatch(typeof(MyBankingSystem), "RequestDeposit_Server")]
    private static class Deposit
    {
        private static void Prefix() => depositing = true;

        private static void Finalizer() => depositing = false;
    }

    [HarmonyPatch(typeof(MyBankingSystem), "Transfer_Internal")]
    private static class Transfer
    {
        private static void Postfix(
            bool __result,
            long fromIdentifier,
            long toIdentifier,
            long amount
        )
        {
            if (
                __result
                && depositing
                && On
                && amount > 0
                && toIdentifier == OctFaction.Faction?.FactionId
                && Tracked(fromIdentifier)
            )
                Add(fromIdentifier, ReputationKind.Deposit, amount, null);
        }
    }

    // The share box of a block's terminal; the change is made a moment later
    [HarmonyPatch(typeof(MyCubeGrid), "OnChangeShareRequest")]
    private static class Share
    {
        private static void Prefix(List<MyCubeGrid.MySingleOwnershipRequest> requests)
        {
            if (!On || !Sync.IsServer)
                return;
            var identity = MySession.Static.Players.TryGetIdentityId(
                MyEventContext.Current.Sender.Value
            );
            if (!Tracked(identity))
                return;
            foreach (var request in requests)
                if (
                    MyEntities.TryGetEntityById(request.BlockId, out MyCubeBlock block)
                    && block.OwnerId == identity
                )
                    shares.Add((identity, block, Shared(block)));
        }
    }

    private static void CheckShares()
    {
        foreach (var (identity, block, was) in shares)
        {
            if (block.Closed || block.OwnerId != identity || Shared(block) == was)
                continue;
            var slim = block.SlimBlock;
            var value =
                Value(block.BlockDefinition) * (decimal)(slim.Integrity / slim.MaxIntegrity);
            Add(identity, ReputationKind.Share, was ? -value : value, null);
        }
        shares.Clear();
    }

    // --- to the authority ---

    private static string Changes()
    {
        var text = ReputationBook.EncodeChanges(
            pending.Select(p => (p.Key.Identity, p.Key.Kind, p.Key.Incident, p.Value))
        );
        pending.Clear();
        return text;
    }

    private static void Send()
    {
        if (inFlight || Plugin.Cluster == null)
            return;
        if (sending == null)
        {
            if (pending.Count == 0)
                return;
            sending = (Guid.NewGuid(), Encoding.UTF8.GetBytes(Changes()));
        }
        inFlight = true;
        _ = SendAsync(sending.Value.Id, sending.Value.Payload);
    }

    // The same id again until it lands: the World Authority runs it once
    private static async Task SendAsync(Guid id, byte[] payload)
    {
        var sent = false;
        try
        {
            var result = await Plugin
                .Cluster.ExecuteGlobalAsync(Command, payload, id, SendTimeout)
                .ConfigureAwait(false);
            sent = result.Code == PluginResultCode.Success;
            if (!sent)
                Plugin.Log.Warning(
                    $"Reputation: the World Authority did not take the changes ({result.Code} {result.Error}), sending again"
                );
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Reputation: sending the changes failed, sending again", e);
        }
        MySandboxGameInvoke(() =>
        {
            if (sent)
                sending = null;
            inFlight = false;
        });
    }

    private static void MySandboxGameInvoke(Action action) =>
        Sandbox.MySandboxGame.Static.Invoke(action, "Octahedron reputation");

    // --- on the authority ---

    private static void Register()
    {
        if (
            commandRegistration != null
            || registrationFailed
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
        )
            return;
        commandRegistration = Plugin.Cluster.RegisterGlobalCommand(Command, Serve);
        if (commandRegistration == null)
        {
            registrationFailed = true;
            Plugin.Log.Error(
                "Reputation: this cluster has no global commands, nodes' changes are lost"
            );
            return;
        }
        readRegistration = Plugin.Cluster.RegisterHandler(
            ReadTopic,
            message =>
                Task.FromResult(
                    Encoding.UTF8.GetBytes(
                        Describe(
                            long.Parse(
                                Encoding.UTF8.GetString(message.Payload ?? []),
                                CultureInfo.InvariantCulture
                            )
                        )
                    )
                )
        );
        Plugin.Log.Info("Reputation: keeping the members' reputation on the World Authority");
    }

    private static byte[] Serve(byte[] payload)
    {
        if (On)
            Apply(ReputationBook.DecodeChanges(Encoding.UTF8.GetString(payload)));
        return [];
    }

    private static void Flush()
    {
        if (pending.Count > 0)
            Apply(ReputationBook.DecodeChanges(Changes()));
    }

    private static void Apply(
        List<(long Identity, ReputationKind Kind, string Incident, decimal Amount)> changes
    )
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Settings();
        foreach (var (identity, kind, incident, amount) in changes)
        {
            var before = Book.Get(identity);
            var told = Book.Apply(identity, kind, amount, incident, now);
            Plugin.Log.Info(
                $"Reputation of {Name(identity)} ({identity}): {kind} {amount:0.##}, {before:0.##} -> {Book.Get(identity):0.##}"
                    + (amount != 0 && Book.Get(identity) == before ? " forgiven" : "")
            );
            if (told != null)
                Tell(identity, told);
        }
        Save();
    }

    private static void Settings()
    {
        var s = Book.Settings;
        s.Start = Config.ReputationStart;
        s.KickBelow = Config.ReputationKickBelow;
        s.KickAfterSeconds = Config.ReputationKickAfterSeconds;
        s.WarnSeconds = ReputationSettings.ParseWarnings(Config.ReputationWarnSeconds);
        s.RamGraceSeconds = Config.ReputationRamGraceSeconds;
    }

    private static readonly List<(long Identity, string Text)> tell = [];
    private static readonly List<long> kick = [];

    private static void Tick(DateTime now)
    {
        // Only the time the server runs counts, a pause or a stop does not
        var seconds =
            lastTick == default ? 0 : Math.Min(5, Math.Max(0, (now - lastTick).TotalSeconds));
        lastTick = now;
        var oct = OctFaction.Faction;
        if (oct == null)
            return;
        Settings();
        Book.Tick(seconds, oct.IsMember, tell, kick);
        foreach (var (identity, text) in tell)
            Tell(identity, text);
        foreach (var identity in kick)
        {
            Plugin.Log.Info(
                $"Reputation: {Name(identity)} ({identity}) is removed from {oct.Tag} at {Book.Get(identity):0.##}"
            );
            MyFactionCollection.KickMember(oct.FactionId, identity);
        }
        tell.Clear();
        kick.Clear();
        Save();
    }

    private static void Save()
    {
        if (!Book.Dirty)
            return;
        MyAPIGateway.Utilities.SetVariable(Variable, Book.Serialize());
        Book.Dirty = false;
    }

    private static void Tell(long identity, string text)
    {
        Plugin.Log.Info($"Reputation, to {Name(identity)} ({identity}): {text}");
        ShipyardAway.Tell(identity, text, "OCT");
    }

    private static string Name(long identity) =>
        MySession.Static.Players.TryGetIdentity(identity)?.DisplayName ?? "?";

    private static string Describe(long identity)
    {
        var left = Book.Left(identity);
        return $"Your reputation in OCT: {ReputationBook.Sc(Book.Get(identity))}."
            + (
                left is { } seconds
                    ? $" Below {ReputationBook.Sc(Book.Settings.KickBelow)}: removed from OCT in {ReputationBook.Duration(seconds)} unless it rises."
                    : ""
            );
    }

    // --- commands ---

    /// <summary>!oct rep: the caller's reputation, to done on the game thread; null and the reason on failure</summary>
    public static void Read(long identity, Action<string, string> done)
    {
        if (!On)
            done(null, "Reputation is off on this server.");
        else if (!Tracked(identity))
            done(null, "Only OCT's members have a reputation.");
        else if (Stations.IsAuthority)
        {
            Settings();
            done(Describe(identity), null);
        }
        else
            Stations.Ask(ReadTopic, identity.ToString(CultureInfo.InvariantCulture), done);
    }

    /// <summary>!oct rep add: an admin's change, sent like a member's</summary>
    public static string Change(long identity, decimal amount)
    {
        if (!On)
            return "Reputation is off on this server.";
        Add(identity, ReputationKind.Admin, amount, null);
        return $"{Name(identity)}'s reputation changes by {ReputationBook.Sc(amount)}.";
    }

    public static void Dispose()
    {
        commandRegistration?.Dispose();
        readRegistration?.Dispose();
    }
}
