using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;
#if NETCOREAPP
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
#endif

namespace ServerPlugin;

// A player's character left alive on a station with the player offline holds its space credits out
// of the wealth tax's reach. On a station is inside the bounding sphere of the station's grid, seated
// or not. After OfflineCharacterMinutes it goes: its inventory is kept by the
// bank's keeper, a plain server or the World Authority (WA), its space credits move to the bank,
// and the player's next body gets the rest of the inventory.
//
// The keeper knows who was last online when (ParkingBook), the node holding a body asks it what to
// do. Nothing is removed until a save holds the inventory: a node dying before the removal brings
// the body back, the keeper knows it by its entity id and it is removed without a second deposit.
//
// On a cluster a body usually goes offline with its partition minutes before its time is up, so
// the WA asks the gateway to load that partition then. A plain server's save holds the world and
// this book together, so there is nothing to wait for.
internal static class OfflineCharacters
{
    private const string Variable = "Octahedron.OfflineCharacters";
    private const string WorldAuthorityRole = "WorldAuthority";
    private const string StatusTopic = "offline-characters/status";
    private const string ParkCommand = "offline-character-park";
    private const string TakeCommand = "offline-character-take";
    private const string ConsumeCommand = "offline-character-consume";
    private const string Credit = "SpaceCredit";
    private const int Attempts = 4;
    private const int WakesPerPass = 5;

    // The WA holds no grids, so it wakes the partition of a player last seen this close to a
    // station's position and the node decides by the sphere. The world's spheres reach 74 m from
    // the positions; a wake too many costs one load of a partition.
    private const double WakeRange = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ObserveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SaveCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    // A WA just started has no list of who is online yet
    private static readonly TimeSpan OnlineListWait = TimeSpan.FromSeconds(60);
    private static readonly MyDefinitionId CreditId = new(
        typeof(MyObjectBuilder_PhysicalObject),
        Credit
    );

    // Past the inventory's room: the items are the player's, the new body's own gear took the room
    private static readonly MethodInfo AddPastLimit = AccessTools.Method(
        typeof(MyInventory),
        "AddItemsInternal"
    );

    private static OctahedronConfig Config => Plugin.Config;
    private static PluginClusterClient Cluster => Plugin.Cluster;
    private static long Limit => Config.OfflineCharacterMinutes * 60L;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool Authority =>
        !PluginCluster.IsClusterProcess || Cluster?.Context.Role == WorldAuthorityRole;

    private static bool On => Config.Enabled && Config.OfflineCharacterMinutes > 0;

    // The keeper's side
    private static ParkingBook book;
    private static MySession bookSession;
    private static bool dirty;
    private static IDisposable[] registrations;
    private static bool registrationFailed;
    private static DateTime keeperSince;
    private static DateTime nextObserve;
    private static DateTime nextWake;
    private static DateTime nextSaveCheck;

    // The node's side
    private static DateTime nextCheck;
    private static DateTime nextScan;
    private static bool asking;

    // A player's body that needs no more asking, until the player has another
    private static readonly Dictionary<long, long> settled = new();

    // Calls under way, by identity or by body
    private static readonly HashSet<long> busy = [];

    // A take keeps its operation id until it is answered, so a lost answer isn't lost items
    private static readonly Dictionary<long, Guid> takes = new();

    // Bodies brought back and emptied, not to empty again what the player picks up after
    private static readonly HashSet<long> emptied = [];
    private static readonly List<(long Identity, long Body)> unconsumed = [];

    // A cluster process has this once its plugins are loaded
    private static Type runtimeType;
    private static Type RuntimeType =>
        runtimeType ??= AccessTools.TypeByName("ClusterPlugin.ClusterRuntime");

    // A plugin's global command doesn't wait for a save, so this looks at the saves. Their place in
    // the order: the generation, then the sequence. Only without saves at all is everything saved.
    private static long SaveMark(long generation, long sequence) => (generation << 32) + sequence;

    // The newest global save a replacement WA could restore, as the gateway says
    private static long recoverable;

    // The first save that can hold a change made now: one runs at a time, and it may have been
    // captured before this. Nothing can be saved past the end if the cluster's insides moved, so a
    // body then stays in the world, and Register says so.
    private static long NextSave
    {
        get
        {
            if (!PluginCluster.IsClusterProcess)
                return 0;
            var persistence = AccessTools
                .Field(RuntimeType, "m_worldAuthorityPersistence")
                ?.GetValue(null);
            var sequence =
                persistence == null
                    ? null
                    : AccessTools.Field(persistence.GetType(), "m_sequence")?.GetValue(persistence);
            var generation = AccessTools
                .PropertyGetter(RuntimeType, "AuthorityGeneration")
                ?.Invoke(null, null);
            return sequence == null || generation == null
                ? long.MaxValue
                : SaveMark((long)generation, (uint)sequence + 1L);
        }
    }

    private static long Saved
    {
        get
        {
            if (!PluginCluster.IsClusterProcess)
                return long.MaxValue;
            var persists = AccessTools
                .PropertyGetter(RuntimeType, "GlobalSavesPersist")
                ?.Invoke(null, null);
            return persists is false ? long.MaxValue : recoverable;
        }
    }

    private static void RequestSave()
    {
        if (RuntimeType != null)
            AccessTools.Method(RuntimeType, "RequestGlobalSave")?.Invoke(null, null);
    }

    private static ParkingBook Book
    {
        get
        {
            var session = MySession.Static;
            if (book == null || bookSession != session)
            {
                book = MyAPIGateway.Utilities.GetVariable<string>(Variable, out var text)
                    ? ParkingBook.Deserialize(text)
                    : new ParkingBook();
                bookSession = session;
                dirty = false;
                keeperSince = DateTime.UtcNow;
            }
            return book;
        }
    }

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true || MyBankingSystem.Static == null)
            return;
        if (PluginCluster.IsClusterProcess && Cluster?.Context.Available != true)
            return;
        if (Authority)
            UpdateKeeper();
        // A World Authority holds no bodies
        if (!PluginCluster.IsClusterProcess || !Authority)
            UpdateNode();
    }

    // --- The keeper ---

    private static void UpdateKeeper()
    {
        if (PluginCluster.IsClusterProcess && !Register())
            return;
        var utc = DateTime.UtcNow;
        if (utc >= nextObserve)
        {
            nextObserve = utc + ObserveInterval;
            Observe();
        }
#if NETCOREAPP
        if (PluginCluster.IsClusterProcess && utc >= nextWake && OnlineListReady)
        {
            nextWake = utc + WakeInterval;
            Wake();
        }
        var unsaved = Book.Parked.Any(p => p.Order > recoverable);
        if (PluginCluster.IsClusterProcess && unsaved && utc >= nextSaveCheck)
        {
            nextSaveCheck = utc + SaveCheckInterval;
            _ = Task.Run(CheckSaves);
        }
#endif
    }

    private static bool OnlineListReady => DateTime.UtcNow - keeperSince >= OnlineListWait;

    private static bool Register()
    {
        if (registrations != null)
            return true;
        if (registrationFailed || PluginCluster.Current == null)
            return false;
        if (NextSave == long.MaxValue)
            Plugin.Log.Error(
                "The cluster's saves can't be followed: no kept inventory counts as saved, so offline characters stay in the world"
            );
        var commands = new[]
        {
            Cluster.RegisterGlobalCommand(ParkCommand, Text(Park)),
            Cluster.RegisterGlobalCommand(TakeCommand, Text(Take)),
            Cluster.RegisterGlobalCommand(ConsumeCommand, Text(Consume)),
        };
        if (commands.Any(c => c == null))
        {
            registrationFailed = true;
            Plugin.Log.Error(
                "This cluster has no global commands: offline characters stay in the world"
            );
            return false;
        }
        registrations =
        [
            .. commands,
            Cluster.RegisterHandler(
                StatusTopic,
                message =>
                    Task.FromResult(
                        Encoding.UTF8.GetBytes(Status(Encoding.UTF8.GetString(message.Payload)))
                    )
            ),
        ];
        return true;
    }

    private static Func<byte[], byte[]> Text(Func<string, string> handler) =>
        payload => Encoding.UTF8.GetBytes(handler(Encoding.UTF8.GetString(payload ?? [])));

    private static void Observe()
    {
        var at = StationPositions().ToList();
        if (Cluster != null)
        {
            var positions = Cluster.PlayerPositions();
            var online = Cluster.OnlinePlayers();
            if (online == null)
                return;
            Book.Observe(
                online.Select(p =>
                {
                    var position = positions?.FirstOrDefault(x => x.IdentityId == p.IdentityId);
                    return (p.IdentityId, position != null && NearStation(position.Position, at));
                }),
                Now
            );
        }
        else
            Book.Observe(
                MySession
                    .Static.Players.GetOnlinePlayers()
                    .Where(p => p.IsRealPlayer && p.Identity != null)
                    .Select(p =>
                        (
                            p.Identity.IdentityId,
                            p.Character != null
                                && NearStation(p.Character.PositionComp.GetPosition(), at)
                        )
                    ),
                Now
            );
        dirty = true;
    }

    // Lines "identity body 0|1", 1 for a body its player is in; answers one verdict a line
    private static string Status(string request)
    {
        var durable = Saved;
        var hold = false;
        var verdicts = new List<string>();
        foreach (var line in request.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            var verdict = Book.Status(Number(f[0]), Number(f[1]), f[2] == "1", Now, Limit, durable);
            // Whoever looks at the time before the online list is in may see a player gone
            if (verdict == ParkingVerdict.Park && !OnlineListReady)
                verdict = ParkingVerdict.Wait;
            hold |= verdict == ParkingVerdict.Hold;
            verdicts.Add(verdict.ToString());
        }
        dirty = true;
        if (hold)
            RequestSave();
        return string.Join("\n", verdicts);
    }

    // "identity body credits items": answers ok, or why not
    private static string Park(string payload)
    {
        var f = payload.Split(' ');
        var (identity, body, credits) = (Number(f[0]), Number(f[1]), Number(f[2]));
        // A body the world can't pay out stays until it can
        if (credits > 0 && !HasAccount(identity))
            return $"{identity} has no bank account";
        var outcome = Book.Park(identity, body, credits, f[3], Now, Limit, NextSave);
        if (outcome != ParkOutcome.Added)
            return outcome.ToString();
        dirty = true;
        if (credits > 0 && !MyBankingSystem.ChangeBalance(identity, credits))
            Plugin.Log.Error($"Offline character of {identity}: {credits} SC not paid in");
        Plugin.Log.Info(
            $"Offline character of {identity}: body {body} kept, {f[3].Length} characters of items, {credits} SC to the bank"
        );
        RequestSave();
        return "ok";
    }

    // "identity body": answers wait, none, or "items credits clear(0|1) items..."
    private static string Take(string payload)
    {
        var f = payload.Split(' ');
        var (identity, body) = (Number(f[0]), Number(f[1]));
        var answer = Book.Take(identity, body, NextSave, Saved);
        dirty = true;
        if (answer.Wait)
        {
            RequestSave();
            return "wait";
        }
        if (answer.Parked.Count == 0)
            return "none";
        return string.Join(
            " ",
            new[]
            {
                "items",
                answer.Parked.Sum(p => p.Credits).ToString(CultureInfo.InvariantCulture),
            }.Concat(answer.Parked.Select(p => p.Items))
        );
    }

    private static string Consume(string payload)
    {
        var f = payload.Split(' ');
        var (identity, body) = (Number(f[0]), Number(f[1]));
        var count = Book.Consume(identity, body);
        dirty = true;
        Plugin.Log.Info(
            $"Offline character of {identity}: {count} kept inventories now on body {body}"
        );
        return "ok";
    }

    private static bool HasAccount(long id) => MyBankingSystem.Static.TryGetAccountInfo(id, out _);

    private static long Number(string text) => long.Parse(text, CultureInfo.InvariantCulture);

    // Called before every save, the World Authority's global saves included
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
                    MyAPIGateway.Utilities.SetVariable(Variable, book.Serialize());
                    dirty = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error("Saving the offline characters failed", e);
            }
        }
    }

#if NETCOREAPP
    private static readonly HttpClient Gateway = NewGatewayClient();

    private static HttpClient NewGatewayClient()
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

    // The route the cluster's own durable commands wait on: catalogued and held by another node too
    private static async Task CheckSaves()
    {
        var root = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        var node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(node))
            return;
        try
        {
            using var answer = await Gateway
                .GetAsync(
                    $"{root}/registry/global-saves/recovery?without={Uri.EscapeDataString(node)}"
                )
                .ConfigureAwait(false);
            if (!answer.IsSuccessStatusCode)
                return;
            using var document = JsonDocument.Parse(
                await answer.Content.ReadAsStringAsync().ConfigureAwait(false)
            );
            var id = document.RootElement.GetProperty("SaveId");
            var mark = SaveMark(
                id.GetProperty("Generation").GetInt64(),
                id.GetProperty("Sequence").GetInt64()
            );
            OnGameThread(() => recoverable = Math.Max(recoverable, mark));
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"Offline characters: no answer on the saves: {e.Message}");
        }
    }

    private static void Wake()
    {
        var root = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
        if (string.IsNullOrEmpty(root))
            return;
        foreach (var identity in Book.Wakes(Now, Limit, WakesPerPass))
        {
            var client = MySession.Static.Players.TryGetSteamId(identity);
            _ = Task.Run(async () =>
            {
                var done = await LoadHome(root, client, identity).ConfigureAwait(false);
                if (done)
                    OnGameThread(() =>
                    {
                        if (Book.Players.TryGetValue(identity, out var seen))
                            seen.Woken = true;
                        dirty = true;
                    });
            });
        }
    }

    // True when the partition is on its way or online, or the player has no body to look for
    private static async Task<bool> LoadHome(string root, ulong client, long identity)
    {
        try
        {
            using var home = await Gateway
                .GetAsync($"{root}/registry/character-homes?client={client}")
                .ConfigureAwait(false);
            if (!home.IsSuccessStatusCode)
                return true;
            using var document = JsonDocument.Parse(
                await home.Content.ReadAsStringAsync().ConfigureAwait(false)
            );
            var row = document.RootElement;
            var body = row.GetProperty("Entity").GetInt64();
            if (body == 0)
                return true;
            var partition =
                await OfflinePartitionOf(root, body).ConfigureAwait(false)
                ?? row.GetProperty("Partition").GetUInt64();
            using var load = await Gateway
                .PostAsync(
                    $"{root}/registry/partitions/offline/load?partition={partition}&reason=offline-character",
                    null
                )
                .ConfigureAwait(false);
            load.EnsureSuccessStatusCode();
            var outcome = JsonDocument
                .Parse(await load.Content.ReadAsStringAsync().ConfigureAwait(false))
                .RootElement.GetProperty("outcome")
                .GetString();
            Plugin.Log.Info(
                $"Offline character of {identity}: load of partition {partition} for body {body}: {outcome}"
            );
            return outcome != "no-target";
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"Offline character of {identity}: no load: {e.Message}");
            return false;
        }
    }

    // A body that merged into another partition after its last home report; a seated one isn't a
    // member itself, its home is good for that
    private static async Task<ulong?> OfflinePartitionOf(string root, long body)
    {
        var text = await Gateway
            .GetStringAsync($"{root}/registry/partitions/offline")
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        foreach (var row in document.RootElement.GetProperty("Partitions").EnumerateArray())
        foreach (var member in row.GetProperty("Members").EnumerateArray())
            if (member.GetInt64() == body)
                return row.GetProperty("Partition").GetUInt64();
        return null;
    }
#endif

    // --- The node ---

    private static void UpdateNode()
    {
        var utc = DateTime.UtcNow;
        if (utc < nextCheck)
            return;
        nextCheck = utc + CheckInterval;

        foreach (var (identity, body) in unconsumed.ToList())
            SendConsume(identity, body);

        var questions = new List<(long Identity, long Body, bool Online)>();
        var here = new HashSet<long>();
        foreach (
            var player in MySession
                .Static.Players.GetOnlinePlayers()
                .Where(p => p.IsRealPlayer && p.Identity != null)
        )
        {
            var character = player.Character;
            if (character == null || character.IsDead || character.MarkedForClose)
                continue;
            if (!Plugin.Simulated(character.GetTopMostParent() ?? character))
                continue;
            var identity = player.Identity.IdentityId;
            here.Add(character.EntityId);
            if (settled.GetValueOrDefault(identity) != character.EntityId)
                questions.Add((identity, character.EntityId, true));
        }
        // Gone from here: if a body comes back it may have been taken out meanwhile, so it asks again
        foreach (var gone in settled.Where(s => !here.Contains(s.Value)).ToList())
            settled.Remove(gone.Key);
        emptied.RemoveWhere(body => !here.Contains(body));
        if (utc >= nextScan)
        {
            nextScan = utc + ScanInterval;
            questions.AddRange(OfflineBodies().Select(b => (b.Identity, b.Body.EntityId, false)));
        }
        if (questions.Count == 0 || asking)
            return;

        asking = true;
        var request = string.Join(
            "\n",
            questions.Select(q => $"{N(q.Identity)} {N(q.Body)} {(q.Online ? 1 : 0)}")
        );
        Ask(
            request,
            answer =>
            {
                asking = false;
                var verdicts = answer?.Split('\n');
                if (verdicts == null || verdicts.Length != questions.Count)
                    return;
                for (var i = 0; i < verdicts.Length; i++)
                    Act(questions[i].Identity, questions[i].Body, verdicts[i]);
            }
        );
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Act(long identity, long body, string verdict)
    {
        switch (Enum.Parse<ParkingVerdict>(verdict))
        {
            case ParkingVerdict.None:
                settled[identity] = body;
                break;
            case ParkingVerdict.Take:
                SendTake(identity, body);
                break;
            case ParkingVerdict.TakeBack:
                EmptyOnce(identity, body);
                SendTake(identity, body);
                break;
            case ParkingVerdict.Park:
                SendPark(identity, body);
                break;
            case ParkingVerdict.Remove:
                Remove(identity, body);
                break;
        }
    }

    private static IEnumerable<IMyFactionStation> Stations() =>
        MySession.Static.Factions.Select(f => f.Value).SelectMany(f => f.Stations);

    private static IEnumerable<Vector3D> StationPositions() => Stations().Select(s => s.Position);

    private static bool NearStation(Vector3D position, List<Vector3D> stations) =>
        stations.Any(s => Vector3D.DistanceSquared(s, position) <= WakeRange * WakeRange);

    // The stations loaded here. Partitions merge within kilometers, so a body inside a sphere has
    // the station's grid on its node.
    private static List<BoundingSphereD> StationSpheres()
    {
        var spheres = new List<BoundingSphereD>();
        foreach (var station in Stations())
            if (MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid))
                spheres.Add(grid.PositionComp.WorldVolume);
        return spheres;
    }

    // The bodies on the stations loaded here whose players are online nowhere, seated or not
    private static List<(MyCharacter Body, long Identity)> OfflineBodies()
    {
        var found = new Dictionary<long, (MyCharacter, long)>();
        var entities = new List<MyEntity>();
        var spheres = StationSpheres();
        var online = Cluster?.OnlinePlayers()?.Select(p => p.IdentityId).ToHashSet();
        foreach (var station in spheres)
        {
            entities.Clear();
            var sphere = station;
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, entities);
            foreach (var entity in entities)
            {
                if (entity is MyCharacter standing)
                    Consider(standing);
                else if (entity is MyCubeGrid grid)
                    foreach (var cockpit in grid.GetFatBlocks<MyCockpit>())
                        if (cockpit.Pilot != null)
                            Consider(cockpit.Pilot);
            }
        }
        return found.Values.ToList();

        void Consider(MyCharacter body)
        {
            // A ship reaching into a sphere doesn't bring the pilot in: the body itself must be inside
            var position = body.PositionComp.GetPosition();
            if (
                spheres.Any(s => s.Contains(position) != ContainmentType.Disjoint)
                && IsOffline(body, online, out var identity)
            )
                found[body.EntityId] = (body, identity);
        }
    }

    private static bool IsOffline(MyCharacter body, HashSet<long> online, out long identity)
    {
        identity = 0;
        if (body.MarkedForClose || body.IsDead)
            return false;
        if ((body.ControllerInfo?.ControllingIdentityId ?? 0) != 0)
            return false;
        var top = body.GetTopMostParent() ?? body;
        if (top.MarkedForClose || !top.InScene || !Plugin.Simulated(top))
            return false;
        identity = body.GetPlayerIdentityId();
        var players = MySession.Static.Players;
        if (identity == 0 || players.IdentityIsNpc(identity))
            return false;
        if (players.TryGetSteamId(identity) == 0 || online?.Contains(identity) == true)
            return false;
        return !(players.TryGetPlayerId(identity, out var id) && players.IsPlayerOnline(ref id));
    }

    private static MyCharacter Offline(long identity, long body) =>
        OfflineBodies().Find(b => b.Identity == identity && b.Body.EntityId == body).Body;

    private static void SendPark(long identity, long bodyId)
    {
        if (busy.Contains(bodyId) || Offline(identity, bodyId) is not { } body)
            return;
        var (items, credits) = Pack(body);
        busy.Add(bodyId);
        Call(
            ParkCommand,
            Guid.NewGuid(),
            $"{N(identity)} {N(bodyId)} {N(credits)} {items}",
            (reply, error) =>
            {
                busy.Remove(bodyId);
                if (reply is not ("ok" or "Known" or "Refused"))
                    Plugin.Log.Warning(
                        $"Offline character of {identity}: not kept, tries again: {reply ?? error}"
                    );
            }
        );
    }

    private static void Remove(long identity, long bodyId)
    {
        if (Offline(identity, bodyId) is not { } body)
            return;
        if (body.Parent is MyCockpit seat && seat.Pilot == body)
            seat.RemovePilot();
        var owner = MySession.Static.Players.TryGetIdentity(identity);
        if (owner?.Character == body)
            owner.ChangeCharacter(null);
        owner?.SavedCharacters.Remove(bodyId);
        body.Close();
        Plugin.Log.Info($"Offline character of {identity}: body {bodyId} removed");
    }

    private static void EmptyOnce(long identity, long bodyId)
    {
        if (!emptied.Add(bodyId))
            return;
        var character = PlayerCharacter(identity);
        if (character?.EntityId == bodyId)
            (character.GetInventory() as MyInventory)?.Clear();
        Plugin.Log.Info($"Offline character of {identity}: body {bodyId} came back, emptied");
    }

    private static MyCharacter PlayerCharacter(long identity) =>
        MySession
            .Static.Players.GetOnlinePlayers()
            .FirstOrDefault(p => p.Identity?.IdentityId == identity)
            ?.Character;

    private static void SendTake(long identity, long bodyId)
    {
        if (!busy.Add(identity))
            return;
        if (!takes.TryGetValue(bodyId, out var operation))
            takes[bodyId] = operation = Guid.NewGuid();
        Call(
            TakeCommand,
            operation,
            $"{N(identity)} {N(bodyId)}",
            (reply, _) =>
            {
                busy.Remove(identity);
                // No answer: the same operation again, its answer may be the items
                if (reply == null)
                    return;
                takes.Remove(bodyId);
                if (reply == "none")
                    settled[identity] = bodyId;
                else if (reply != "wait")
                    Deliver(identity, bodyId, reply);
            }
        );
    }

    private static void Deliver(long identity, long bodyId, string reply)
    {
        var f = reply.Split(' ');
        var character = PlayerCharacter(identity);
        // Died meanwhile: the next body asks, the keeper hands them over again
        if (character == null || character.EntityId != bodyId || character.IsDead)
            return;
        var inventory = character.GetInventory() as MyInventory;
        foreach (var items in f.Skip(2))
            Unpack(items, inventory);
        settled[identity] = bodyId;
        unconsumed.Add((identity, bodyId));
        SendConsume(identity, bodyId);

        var credits = Number(f[1]);
        var words =
            "Your last character was taken off the station while you were away, and this one has its inventory.";
        if (credits > 0)
            words +=
                $" The {credits.ToString("N0", CultureInfo.InvariantCulture)} SC it carried are in your bank account.";
        MyVisualScriptLogicProvider.SendChatMessage(words, "Station", identity);
    }

    private static void SendConsume(long identity, long body)
    {
        if (!busy.Add(body))
            return;
        Call(
            ConsumeCommand,
            Guid.NewGuid(),
            $"{N(identity)} {N(body)}",
            (reply, _) =>
            {
                busy.Remove(body);
                if (reply != null)
                    unconsumed.Remove((identity, body));
            }
        );
    }

    // --- Inventories as text ---

    // A body's inventory without its space credits, gzipped base64 without a space in it
    private static (string Items, long Credits) Pack(MyCharacter body)
    {
        var inventory = ((MyInventory)body.GetInventory()).GetObjectBuilder();
        long credits = 0;
        foreach (var item in inventory.Items)
            if (item.PhysicalContent.GetObjectId() == CreditId)
                credits += (long)item.Amount;
        inventory.Items.RemoveAll(i => i.PhysicalContent.GetObjectId() == CreditId);
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal))
            MyObjectBuilderSerializerKeen.SerializeXML(gzip, inventory);
        return (Convert.ToBase64String(packed.ToArray()), credits);
    }

    private static void Unpack(string items, MyInventory inventory)
    {
        using var gzip = new GZipStream(
            new MemoryStream(Convert.FromBase64String(items)),
            CompressionMode.Decompress
        );
        // The serializer wants to seek
        using var xml = new MemoryStream();
        gzip.CopyTo(xml);
        xml.Position = 0;
        if (
            !MyObjectBuilderSerializerKeen.DeserializeXML<MyObjectBuilder_Inventory>(
                xml,
                out var builder
            )
        )
            throw new InvalidOperationException("A kept inventory could not be read");
        foreach (var item in builder.Items)
            if (!inventory.AddItems(item.Amount, item.PhysicalContent))
                AddPastLimit.Invoke(
                    inventory,
                    [item.Amount, item.PhysicalContent, null, -1, false, -1, 0]
                );
    }

    // --- Calls to the keeper, which may be this process ---

    private static void Ask(string request, Action<string> done)
    {
        if (Cluster == null)
        {
            done(Status(request));
            return;
        }
        _ = AskRemote(request, done);
    }

    private static async Task AskRemote(string request, Action<string> done)
    {
        string answer = null;
        try
        {
            var reply = await Cluster
                .RequestAsync(
                    new PluginTarget { Kind = PluginTargetKind.WorldAuthority },
                    StatusTopic,
                    Encoding.UTF8.GetBytes(request),
                    Guid.NewGuid(),
                    Timeout
                )
                .ConfigureAwait(false);
            if (reply.Code == PluginResultCode.Success)
                answer = Encoding.UTF8.GetString(reply.Payload ?? []);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"Offline characters: no status: {e.Message}");
        }
        OnGameThread(() => done(answer));
    }

    private static void Call(
        string command,
        Guid operation,
        string payload,
        Action<string, string> done
    )
    {
        if (Cluster == null)
        {
            done(
                command switch
                {
                    ParkCommand => Park(payload),
                    TakeCommand => Take(payload),
                    _ => Consume(payload),
                },
                null
            );
            return;
        }
        _ = CallRemote(command, operation, payload, done);
    }

    private static async Task CallRemote(
        string command,
        Guid operation,
        string payload,
        Action<string, string> done
    )
    {
        string answer = null;
        var error = "The World Authority did not answer.";
        try
        {
            for (var attempt = 0; attempt < Attempts && answer == null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
                var reply = await Cluster
                    .ExecuteGlobalAsync(
                        command,
                        Encoding.UTF8.GetBytes(payload),
                        operation,
                        Timeout
                    )
                    .ConfigureAwait(false);
                if (reply.Code == PluginResultCode.Success)
                    answer = Encoding.UTF8.GetString(reply.Payload ?? []);
                else if (reply.Code is PluginResultCode.Invalid or PluginResultCode.Conflict)
                {
                    error = reply.Error ?? reply.Code.ToString();
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"Global command {command} failed", e);
        }
        OnGameThread(() => done(answer, error));
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
                    Plugin.Log.Error("Offline characters callback failed", e);
                }
            },
            "Octahedron offline characters"
        );

    // !oct offline: on the keeper
    public static IEnumerable<string> Describe()
    {
        if (!On)
            yield return "Offline characters are off.";
        if (!Authority)
        {
            yield return "Run on the World Authority.";
            yield break;
        }
        var now = Now;
        var durable = Saved;
        yield return $"Players seen: {Book.Players.Count}, kept inventories: {Book.Parked.Count}";
        foreach (var parked in Book.Parked)
            yield return $"{parked.Identity}: body {parked.Body}, {parked.Credits} SC banked, "
                + $"{parked.Items.Length} characters of items, "
                + (parked.Order <= durable ? "saved" : "not saved yet")
                + (parked.Taker != 0 ? $", handed to {parked.Taker}" : "");
        foreach (var player in Book.Players.OrderBy(p => p.Key))
            yield return $"{player.Key}: seen {now - player.Value.Seconds} s ago"
                + (player.Value.NearStation ? " near a station" : "")
                + (player.Value.Woken ? ", its partition asked to load" : "");
    }
}
