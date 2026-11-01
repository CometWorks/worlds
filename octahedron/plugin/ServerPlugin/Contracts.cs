using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRageMath;
using Api = Sandbox.ModAPI.Contracts;

namespace ServerPlugin;

// OCT posts the stations' contracts in the game's own contract system, in place of the game's
// generator, and pays every reward from its account:
// - hauling: a package to another station, the game's delivery contract;
// - acquisition: items OCT's stock at the station lacks (the station's profile), delivered at
//   the station's contract block into OCT's stock there;
// - bounty: an NPC grid near the station, done when its threat score drops to a quarter or it is
//   gone while the hunter is near;
// - salvage: an unowned or wrecked grid near the station, done when the player who took it has
//   towed it to the station's shipyard; it is then theirs, and the reward is paid too;
//   (any NPC grid towed to a shipyard becomes the player's too, without a contract and unpaid)
// - wreck: on a cluster, a wreck left in an offline partition anywhere, static or not, picked at
//   random from the biggest ones, posted at the nearest station with a shipyard; done when towed
//   there too and then theirs as well, or when it is ground down or gone with them near.
//
// The contracts are station contracts, without a start block: the game shows them at every contract
// block of the station and keeps them in its contract system, which is the World Authority's on a
// cluster. The game pays the reward out of nothing when a contract finishes; the Finish postfix
// takes the same amount from OCT. The World Authority has no grids, so the nodes report the ones
// they simulate (ContractGrids) and it decides from those rows (ContractBook); a plain server does
// both itself.
internal static class Contracts
{
    private const string VariableName = "Octahedron-Contracts";
    private const string GridsVariableName = "Octahedron-Contract-Grids";
    private const string GivenVariableName = "Octahedron-Contract-Given";
    private const string ReportTopic = "contracts/report";
    private const double WitnessMeters = 5_000;
    private const double ShipyardSearchMeters = 3_000;
    private const int RowsPerReport = 500;
    private static readonly TimeSpan PostInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TrackInterval = TimeSpan.FromSeconds(30);

    // How long a target's node must report without it before it counts as gone: a handover or a
    // partition's recovery isn't its end. A plain server knows at once.
    private static readonly TimeSpan GoneAfter = PluginCluster.IsClusterProcess
        ? TimeSpan.FromMinutes(3)
        : TimeSpan.Zero;
    private static readonly MyDefinitionId BountyType = MyDefinitionId.Parse(
        "MyObjectBuilder_ContractTypeDefinition/PvEBounty_HuntPirate"
    );
    private static readonly MyDefinitionId SalvageType = MyDefinitionId.Parse(
        "MyObjectBuilder_ContractTypeDefinition/SalvageCloseRange"
    );

    private static readonly PropertyInfo TransferItems = AccessTools.Property(
        typeof(MyContractConditionDeliverItems),
        nameof(MyContractConditionDeliverItems.TransferItems)
    );
    private static readonly MethodInfo FinishCondition = AccessTools.Method(
        typeof(MySessionComponentContractSystem),
        "FinishContractCondition"
    );
    private static readonly MethodInfo FinishContract = AccessTools.Method(
        typeof(MySessionComponentContractSystem),
        "FinishContract"
    );

    private static readonly Random Random = new();
    private static DateTime nextPost;
    private static DateTime nextTrack;
    private static DateTime nextScout;
    private static DateTime nextScan;
    private static int reportsInFlight;
    private static readonly TimeSpan RefusalEvery = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<long, DateTime> refused = new();
    private static HashSet<long> watched = [];
    private static IDisposable reportRegistration;
    private static ContractBook book;
    private static MySession bookSession;

    // Tests only, set by reflection (tests/console-probe): moves the clock that ages offline
    // partitions, so a test doesn't wait a week
    internal static TimeSpan OfflineClockShift = TimeSpan.Zero;

    // Tests only, set by reflection: the player's PCU limit, so a test needn't own a million PCU
    internal static int? PcuLimitForTests;

    private static OctahedronConfig Config => Plugin.Config;
    public static bool On => Config.Enabled && Config.ContractsEnabled;

    // The contract system and the market are kept there
    private static bool Authority => Stations.IsAuthority;

    // A regular node of a cluster, or a plain server: has grids
    private static bool Simulates => !PluginCluster.IsClusterProcess || !Authority;

    private static MySessionComponentContractSystem System =>
        MySession.Static.GetComponent<MySessionComponentContractSystem>();

    private static MyFaction Oct =>
        MySession.Static.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag);

    // Vanilla's own contracts always have a start block or aren't player made
    private static bool Ours(MyContract contract, long octId) =>
        contract.IsPlayerMade && contract.StartBlock == 0 && contract.StartFaction == octId;

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true)
            return;
        var now = DateTime.UtcNow;
        if (Simulates && now >= nextScout)
        {
            nextScout = now + TrackInterval;
            var full = now >= nextScan;
            if (full)
                nextScan = now + PostInterval;
            Scout(full);
        }
        if (!Authority)
            return;
        Serve();
        if (now >= nextTrack)
        {
            nextTrack = now + TrackInterval;
            Track();
        }
        if (now >= nextPost)
        {
            nextPost = now + PostInterval;
            Post();
        }
    }

    // !oct contracts post: tracks and posts now
    public static string Run()
    {
        if (!On)
            return "The contracts are off.";
        if (!Authority)
            return "The contracts run on the World Authority.";
        if (Simulates)
            Scout(true);
        Track();
        return Post();
    }

    // The book, on the authority, loaded from the world on first use
    private static ContractBook Book
    {
        get
        {
            var session = MySession.Static;
            if (book == null || bookSession != session)
            {
                book = LoadBook();
                bookSession = session;
            }
            book.Offline = PluginCluster.IsClusterProcess
                ? ContractOffline.Grids(DateTime.UtcNow)
                : [];
            return book;
        }
    }

    private static ContractBook LoadBook()
    {
        var loaded = new ContractBook(GoneAfter);
        try
        {
            if (MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var targets))
                loaded.LoadTargets(targets);
            if (MyAPIGateway.Utilities.GetVariable<string>(GridsVariableName, out var grids))
                loaded.LoadGrids(grids, DateTime.UtcNow);
            if (MyAPIGateway.Utilities.GetVariable<string>(GivenVariableName, out var given))
                loaded.LoadGiven(given);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
        {
            // The contracts stay; only their tracking is lost
            Plugin.Log.Error("Contracts: unreadable state, starting over", e);
            return new ContractBook(GoneAfter);
        }
        return loaded;
    }

    private static void SaveBook()
    {
        MyAPIGateway.Utilities.SetVariable(VariableName, book.SerializeTargets());
        MyAPIGateway.Utilities.SetVariable(GridsVariableName, book.SerializeGrids());
        MyAPIGateway.Utilities.SetVariable(GivenVariableName, book.SerializeGiven());
    }

    // --- the grids: nodes report, the authority answers ---

    private static string Node => Plugin.Cluster?.Context.Node ?? "local";

    private static void Scout(bool full)
    {
        var oct = Oct;
        if (oct == null)
            return;
        if (Authority)
            watched = Book.Watched.ToHashSet();
        // Nothing to say between full scans; a full report still names what to watch and scrap
        if (!full && watched.Count == 0)
            return;
        if (Authority)
        {
            var state = Book;
            Apply(
                state.Report(
                    Node,
                    ContractGrids.Scan(full, watched, oct.FactionId),
                    DateTime.UtcNow
                )
            );
            return;
        }
        if (reportsInFlight > 0 || PluginCluster.Current == null)
            return;
        var rows = ContractGrids.Scan(full, watched, oct.FactionId);
        // A message holds 64 KiB; every part gets its own answer
        var part = 0;
        do
        {
            reportsInFlight++;
            Stations.Ask(
                ReportTopic,
                string.Join(
                    "\n",
                    rows.Skip(part * RowsPerReport)
                        .Take(RowsPerReport)
                        .Select(r => r.Encode())
                        .Prepend(Node)
                ),
                (answer, _) =>
                {
                    reportsInFlight--;
                    if (answer != null)
                        Apply(answer);
                }
            );
        } while (++part * RowsPerReport < rows.Count);
    }

    private static void Apply(string answer)
    {
        (watched, var close, var give, var tell) = ContractBook.ParseAnswer(answer);
        foreach (var id in close)
            ContractGrids.Close(id);
        foreach (var (grid, player) in give)
            ContractGrids.Give(grid, player);
        foreach (var (player, text) in tell)
            MyVisualScriptLogicProvider.SendChatMessage(text, "Shipyard", player);
    }

    private static void Serve()
    {
        if (
            reportRegistration != null
            || !PluginCluster.IsClusterProcess
            || PluginCluster.Current == null
        )
            return;
        reportRegistration = Plugin.Cluster.RegisterHandler(ReportTopic, ServeReport);
    }

    private static Task<byte[]> ServeReport(PluginMessage message)
    {
        var lines = Encoding.UTF8.GetString(message.Payload ?? []).Split('\n');
        var answer = Book.Report(
            lines[0],
            lines.Skip(1).Select(ContractGrid.Decode),
            DateTime.UtcNow
        );
        return Task.FromResult(Encoding.UTF8.GetBytes(answer));
    }

    private static string Post()
    {
        var oct = Oct;
        if (oct == null)
            return $"No faction tagged {Config.MoneyAuthorityFactionTag}.";
        var system = System;
        var stations = oct.Stations.ToList();
        var state = Book;
        var active = system
            .ActiveContracts.OfType<MyContract>()
            .Where(c => Ours(c, oct.FactionId))
            .ToList();
        int hauling = 0,
            acquisitions = 0,
            bounties = 0,
            salvage = 0,
            wrecks = 0;
        foreach (var station in stations)
        {
            var open = system
                .GetAvailableContractsForFactionStation(station.Id)
                .OfType<MyContract>()
                .Where(c => Ours(c, oct.FactionId))
                .ToList();
            hauling += Haul(system, station, stations, open);
            acquisitions += Acquire(system, station, open, active);
            bounties += Bounties(system, station, state);
            salvage += Salvage(system, station, state);
        }
        wrecks = Wrecks(system, stations, state);
        SaveBook();
        var text =
            $"Contracts posted: {hauling} hauling, {acquisitions} acquisitions, {bounties} bounties, {salvage} salvage, {wrecks} wrecks";
        if (hauling + acquisitions + bounties + salvage + wrecks > 0)
            Plugin.Log.Info(text);
        return text + ".";
    }

    private static bool Add(
        MySessionComponentContractSystem system,
        IMyContract contract,
        IMyFactionStation station,
        out long id
    )
    {
        var result = system.AddContract(contract, station.Id);
        id = result.ContractId;
        return result.Success;
    }

    private static int Haul(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        List<IMyFactionStation> stations,
        List<MyContract> open
    )
    {
        var others = stations.Where(s => s.Id != station.Id).ToList();
        var posted = 0;
        for (
            var n = open.Count(c => c is MyContractDeliver);
            n < Config.ContractHaulingPerStation && others.Count > 0;
            n++
        )
        {
            var to = others[Random.Next(others.Count)];
            var km = Vector3D.Distance(station.Position, to.Position) / 1000;
            var contract = new Api.MyContractHauling
            {
                EndFactionStationId = to.Id,
                MoneyReward = ContractMath.HaulingReward(
                    km,
                    Config.ContractHaulingBaseReward,
                    Config.ContractHaulingRewardPerKm
                ),
                Duration = ContractMath.HaulingMinutes(km, Config.ContractHaulingMinutes),
            };
            if (!Add(system, contract, station, out _))
                break;
            posted++;
        }
        return posted;
    }

    private static MyContractConditionDeliverItems Items(MyContract contract) =>
        contract is MyContractObtainAndDeliver
            ? contract.ContractCondition as MyContractConditionDeliverItems
            : null;

    private static int Acquire(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        List<MyContract> open,
        List<MyContract> active
    )
    {
        if (!Amounts.ValidName(station.Name))
            return 0;
        var pending = open.Concat(active.Where(c => c.StartStation == station.Id))
            .Select(Items)
            .Where(i => i != null)
            .ToList();
        var posted = 0;
        // The market's daily stock is OCT's target at every station
        foreach (var (item, missing) in Market.Missing(station.Name))
        {
            if (!Stations.TryItemId(item, out var id) || open.Any(c => Items(c)?.ItemType == id))
                continue;
            var amount = ContractMath.AcquisitionAmount(
                missing / Amounts.Unit,
                pending.Where(i => i.ItemType == id).Sum(i => (long)i.ItemAmount),
                Config.ContractAcquisitionMaxAmount
            );
            var price = Market.ReferencePrice(item);
            if (amount <= 0 || price <= 0)
                continue;
            var contract = new Api.MyContractAcquisition(
                0,
                ContractMath.AcquisitionReward(
                    amount,
                    price,
                    Config.ContractAcquisitionPricePercent
                ),
                0,
                Config.ContractAcquisitionMinutes,
                0,
                id,
                amount
            );
            if (!Add(system, contract, station, out var contractId))
                continue;
            // The items go into OCT's stock, not into the station's containers
            if (
                system.GetContractById(contractId) is MyContract added
                && Items(added) is { } condition
            )
                TransferItems.SetValue(condition, false);
            posted++;
        }
        return posted;
    }

    private static ContractPoint At(Vector3D p) => new(p.X, p.Y, p.Z);

    private static string Gps(string name, ContractPoint p) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "GPS:{0}:{1:0}:{2:0}:{3:0}:",
            name.Replace(':', ' '),
            p.X,
            p.Y,
            p.Z
        );

    private static int Open(ContractBook state, char kind, long? station = null) =>
        state.Targets.Count(t =>
            t.Value.Kind == kind
            && (station == null || t.Value.Station == station)
            && System.GetContractState(t.Key) == MyCustomContractStateEnum.Inactive
        );

    private static int Bounties(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        ContractBook state
    )
    {
        var at = At(station.Position);
        var targets = state.Bounties(
            at,
            Config.ContractBountyRangeKm * 1000,
            Config.ContractBountyMinThreat,
            Config.ContractBountiesPerStation - Open(state, 'b', station.Id)
        );
        var posted = 0;
        foreach (var grid in targets)
        {
            var km = grid.Position.Distance(at) / 1000;
            var contract = new Api.MyContractCustom(
                BountyType,
                0,
                ContractMath.BountyReward(grid.Threat, Config.ContractBountyRewardPerThreat),
                0,
                Config.ContractBountyMinutes,
                $"Bounty: {grid.Name}",
                $"Destroy or disable the NPC grid \"{grid.Name}\" (threat {grid.Threat:0}), "
                    + $"{km:0.#} km from {station.Name} when posted. {Gps(grid.Name, grid.Position)}",
                0,
                0,
                null
            );
            if (!Add(system, contract, station, out var id))
                continue;
            state.Targets[id] = new ContractTarget(
                'b',
                grid.Id,
                station.Id,
                grid.Threat,
                grid.Position
            );
            posted++;
        }
        return posted;
    }

    private static int Salvage(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        ContractBook state
    )
    {
        var at = At(station.Position);
        if (state.Shipyard(at, ShipyardSearchMeters) == null)
            return 0;
        var derelicts = state.Derelicts(
            at,
            Config.ContractBountyRangeKm * 1000,
            Config.ContractSalvagePerStation - Open(state, 's', station.Id),
            ShipyardSearchMeters,
            Config.ContractSalvageRadius
        );
        var posted = 0;
        foreach (var grid in derelicts)
        {
            var contract = new Api.MyContractCustom(
                SalvageType,
                0,
                ContractMath.SalvageReward(grid.MassKg, Config.ContractSalvageRewardPerTonne),
                0,
                Config.ContractSalvageMinutes,
                $"Salvage: {grid.Name}",
                $"Tow the derelict \"{grid.Name}\" ({grid.MassKg / 1000:0} t) to {station.Name}'s shipyard, "
                    + $"within {Config.ContractSalvageRadius:0} m of it, and it is yours. Stay near it, with "
                    + $"nobody in its seats. {Gps(grid.Name, grid.Position)}",
                0,
                0,
                null
            );
            if (!Add(system, contract, station, out var id))
                continue;
            state.Targets[id] = new ContractTarget('s', grid.Id, station.Id, 0, grid.Position)
            {
                Owner = grid.Owner,
                Builder = grid.Builder,
            };
            posted++;
        }
        return posted;
    }

    // Wrecks in offline partitions, wherever they are: the stations have no range for these
    private static int Wrecks(
        MySessionComponentContractSystem system,
        List<IMyFactionStation> stations,
        ContractBook state
    )
    {
        var yards = stations
            .Where(s => state.Shipyard(At(s.Position), ShipyardSearchMeters) != null)
            .ToList();
        if (yards.Count == 0)
            return 0;
        state.OfflineRows = OfflineRows();
        var wrecks = state.Wrecks(
            DateTime.UtcNow + OfflineClockShift,
            TimeSpan.FromDays(Config.ContractWreckOfflineDays),
            Config.ContractWreckPool,
            Config.ContractWreckContracts - Open(state, 'w'),
            Random
        );
        var posted = 0;
        foreach (var grid in wrecks)
        {
            var station = yards.OrderBy(s => grid.Position.Distance(At(s.Position))).First();
            // Already at the shipyard: nobody would have to tow it to own it
            if (
                state.InReach(
                    grid.Position,
                    At(station.Position),
                    ShipyardSearchMeters,
                    Config.ContractSalvageRadius
                )
            )
                continue;
            var km = grid.Position.Distance(At(station.Position)) / 1000;
            var contract = new Api.MyContractCustom(
                SalvageType,
                0,
                ContractMath.SalvageReward(grid.MassKg, Config.ContractSalvageRewardPerTonne),
                0,
                ContractMath.HaulingMinutes(km, Config.ContractSalvageMinutes),
                $"Wreck: {grid.Name}",
                $"Clear away the wreck \"{grid.Name}\" ({grid.MassKg / 1000:0} t, {grid.Blocks} blocks), "
                    + $"{km:0.#} km from {station.Name}: tow it to the station's shipyard and it is "
                    + $"yours, or grind it down on the spot. Stay near it, with nobody in its seats. A "
                    + $"static one can be cut free and converted to a ship. {Gps(grid.Name, grid.Position)}",
                0,
                0,
                null
            );
            if (!Add(system, contract, station, out var id))
                continue;
            // From a gateway's row: the book needs it now, a node reports it once its partition wakes
            state.Grids.TryAdd(grid.Id, grid);
            state.Targets[id] = new ContractTarget(
                'w',
                grid.Id,
                station.Id,
                grid.Blocks,
                grid.Position
            )
            {
                Owner = grid.Owner,
                Builder = grid.Builder,
            };
            posted++;
        }
        return posted;
    }

    private static List<ContractOfflineRow> OfflineRows()
    {
        var oct = Oct;
        var factions = MySession.Static.Factions;
        return ContractBook.OfflineGroups(
            ContractOffline.Rows,
            Config.ContractSalvageMinBlocks,
            identity => factions.GetPlayerFaction(identity)?.FactionId == oct?.FactionId,
            DateTime.UtcNow
        );
    }

    // Bounties and salvage: done, failed, or withdrawn when the target is gone before anyone took it
    private static void Track()
    {
        var oct = Oct;
        if (oct == null)
            return;
        var system = System;
        var state = Book;
        var now = DateTime.UtcNow;
        state.Forget(now);
        var taken = new Dictionary<ContractTarget, long>();
        foreach (var (id, target) in state.Targets.ToList())
        {
            var contractState = system.GetContractState(id);
            if (
                contractState
                is not (MyCustomContractStateEnum.Inactive or MyCustomContractStateEnum.Active)
            )
            {
                state.Targets.Remove(id);
                continue;
            }
            var active = contractState == MyCustomContractStateEnum.Active;
            var hunter = active
                ? (system.GetContractById(id) as MyContract)?.Owners.FirstOrDefault() ?? 0
                : 0;
            taken[target] = hunter;
            var station = oct.Stations.FirstOrDefault(s => s.Id == target.Station);
            var verdict = state.Judge(
                target,
                active,
                station == null ? null : At(station.Position),
                active && Near(hunter, target.Position, WitnessMeters),
                ShipyardSearchMeters,
                Config.ContractSalvageRadius,
                now,
                hunter,
                active && Theirs(hunter, target)
            );
            switch (verdict)
            {
                case ContractVerdict.Withdraw:
                    system.RemoveContract(id);
                    state.Targets.Remove(id);
                    Plugin.Log.Info($"Contract {id} withdrawn: its target is gone");
                    break;
                case ContractVerdict.Finish:
                    system.TryFinishCustomContract(id);
                    break;
                case ContractVerdict.Scrap:
                    if (system.TryFinishCustomContract(id))
                        state.Scrap(target.Grid, now);
                    break;
                case ContractVerdict.Give:
                    // Refused, the contract stays open and the grid where it is
                    if (Allows(state, state.Grids[target.Grid], hunter, now))
                        if (system.TryFinishCustomContract(id))
                        {
                            state.Give(target.Grid, hunter);
                            Plugin.Log.Info($"Contract {id}: grid {target.Grid} goes to {hunter}");
                        }
                    break;
                case ContractVerdict.Fail:
                    system.TryFailCustomContract(id);
                    break;
            }
        }
        Takeovers(oct, state, taken, now);
        SaveBook();
    }

    // NPC grids towed to a shipyard: the player's, with no contract and no pay
    private static void Takeovers(
        MyFaction oct,
        ContractBook state,
        Dictionary<ContractTarget, long> taken,
        DateTime now
    )
    {
        var towed = state.TowedIn(
            oct.Stations.Select(s => At(s.Position)),
            ShipyardSearchMeters,
            Config.ContractSalvageRadius,
            t => taken.GetValueOrDefault(t)
        );
        foreach (var grid in towed)
        {
            if (
                !Near(grid.Tower, grid.Position, WitnessMeters)
                || !Allows(state, grid, grid.Tower, now)
            )
                continue;
            state.Give(grid.Id, grid.Tower);
            Plugin.Log.Info($"Towed in: grid {grid.Id} goes to {grid.Tower}");
        }
    }

    // Whether the player may have the grid, against their PCU limit, counted over the whole cluster,
    // and a ship's. Tells them either way.
    private static bool Allows(ContractBook state, ContractGrid grid, long player, DateTime now)
    {
        var identity = MySession.Static.Players.TryGetIdentity(player);
        if (identity == null)
            return false;
        var used = StarterShip.BuiltPcu(identity);
        var limit = PcuLimitForTests ?? MyBlockLimits.GetMaxPCU(identity);
        var after = used + grid.Pcu;
        switch (ContractMath.Tow(grid.Pcu, used, limit, Config.GroupPcuLimit))
        {
            case ContractMath.TowResult.TooBig:
                RefuseTow(
                    state,
                    grid,
                    player,
                    $"\"{grid.Name}\" stays as it is: it has {grid.Pcu:N0} PCU, and a ship may have at most {Config.GroupPcuLimit:N0}.",
                    now
                );
                return false;
            case ContractMath.TowResult.OverLimit:
                RefuseTow(
                    state,
                    grid,
                    player,
                    $"\"{grid.Name}\" stays as it is: with its {grid.Pcu:N0} PCU you would have {after:N0}, more than 10% over your limit of {limit:N0}.",
                    now
                );
                return false;
            case ContractMath.TowResult.TakeOverLimit:
                state.Tell(
                    player,
                    $"\"{grid.Name}\" is yours. You now have {after:N0} PCU, over your limit of {limit:N0}: you can't build blocks until you are back under it.",
                    now
                );
                return true;
            default:
                state.Tell(player, $"\"{grid.Name}\" is yours.", now);
                return true;
        }
    }

    // For now a tow over the limits ends here: SE-840 breaks the grid down at the shipyard instead
    private static void RefuseTow(
        ContractBook state,
        ContractGrid grid,
        long player,
        string text,
        DateTime now
    )
    {
        if (refused.TryGetValue(grid.Id, out var until) && now < until)
            return;
        refused[grid.Id] = now + RefusalEvery;
        state.Tell(player, text, now);
        Plugin.Log.Info($"Tow refused: grid {grid.Id} for {player}: {text}");
    }

    // The player or their faction owned or built the target when it was posted
    private static bool Theirs(long identity, ContractTarget target)
    {
        var factions = MySession.Static.Factions;
        var faction = factions.GetPlayerFaction(identity);
        bool Same(long other) =>
            other != 0
            && (
                other == identity
                || (faction != null && factions.GetPlayerFaction(other) == faction)
            );
        return Same(target.Owner) || Same(target.Builder);
    }

    // Anywhere in the cluster: the World Authority has the merged positions, not the characters
    private static bool Near(long identityId, ContractPoint position, double meters)
    {
        if (identityId == 0)
            return false;
        var positions = Plugin.Cluster?.PlayerPositions();
        if (positions != null)
            return positions.Any(p =>
                p.IdentityId == identityId && At(p.Position).Distance(position) <= meters
            );
        return MySession.Static.Players.TryGetIdentity(identityId)?.Character is { } character
            && At(character.PositionComp.GetPosition()).Distance(position) <= meters;
    }

    // --- admin commands ---

    public static IEnumerable<string> Describe()
    {
        if (!On)
            yield return "The contracts are off.";
        if (!Authority)
        {
            yield return "The contracts run on the World Authority.";
            yield break;
        }
        var oct = Oct;
        if (oct == null)
            yield break;
        var system = System;
        var state = Book;
        var all = system
            .InactiveContracts.Concat(system.ActiveContracts)
            .OfType<MyContract>()
            .ToList();
        foreach (var station in oct.Stations.OrderBy(s => s.Name))
        {
            var here = all.Where(c => c.StartStation == station.Id).ToList();
            var ours = here.Where(c => Ours(c, oct.FactionId)).OrderBy(c => c.Id).ToList();
            yield return $"{station.Name}: {ours.Count} contracts, {here.Count - ours.Count} others";
            foreach (var c in ours)
                yield return $"  {c.Id} {Kind(c, state)} {c.State} "
                    + $"{c.RewardMoney.ToString("N0", CultureInfo.InvariantCulture)} SC{Detail(c)}";
        }
        var now = DateTime.UtcNow + OfflineClockShift;
        var days = TimeSpan.FromDays(Config.ContractWreckOfflineDays);
        state.OfflineRows = OfflineRows();
        var wrecks =
            state.Offline == null
                ? "unknown"
                : state
                    .Grids.Values.Concat(state.Unreported())
                    .Count(r =>
                        state.Offline.TryGetValue(r.Id, out var since)
                        && ContractBook.IsWreck(r.Flags, now - since, days)
                        && !state.InActiveSafeZone(r)
                    )
                    .ToString(CultureInfo.InvariantCulture);
        yield return $"Known grids: {state.Grids.Count}, in offline partitions {state.Offline?.Count.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, wrecks {wrecks}, reports {state.Reports}, gateway rows {ContractOffline.Rows.Count}, partitions unread {ContractOffline.Unread}";
    }

    private static string Kind(MyContract contract, ContractBook state) =>
        contract switch
        {
            MyContractDeliver => "hauling",
            MyContractObtainAndDeliver => "acquisition",
            _ when state.Targets.TryGetValue(contract.Id, out var t) => t.Kind switch
            {
                'b' => "bounty",
                's' => "salvage",
                _ => "wreck",
            },
            _ => contract.GetType().Name,
        };

    private static string Detail(MyContract contract) =>
        contract switch
        {
            MyContractObtainAndDeliver when Items(contract) is { } items =>
                $" {Stations.ItemName(items.ItemType)} {items.ItemAmount}",
            MyContractDeliver when contract.ContractCondition is { } c => " to "
                + (Oct?.Stations.FirstOrDefault(s => s.Id == c.StationEndId)?.Name ?? "?"),
            MyContractCustom custom => $" {((IMyContractCustom)custom).Name}",
            _ => "",
        };

    public static string Accept(long identityId, long contractId) =>
        Authority
            ? $"Accept {contractId}: {System.AcceptContract(identityId, contractId)}"
            : "The contracts run on the World Authority.";

    // Delivers a hauling or acquisition contract as the player would at the station's contract block,
    // from their character's inventory; fill first adds the items an acquisition asks for
    public static string Deliver(long identityId, long contractId, bool fill)
    {
        if (!Authority)
            return "The contracts run on the World Authority.";
        var system = System;
        if (
            system.GetContractById(contractId)
            is not MyContract { State: MyContractStateEnum.Active } contract
        )
            return $"No active contract {contractId}.";
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null)
            return "You have no character.";
        if (fill && Items(contract) is { } items)
            character
                .GetInventory()
                .AddItems(
                    items.ItemAmount,
                    (VRage.Game.MyObjectBuilder_PhysicalObject)
                        VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(
                            items.ItemType
                        )
                );
        var result = FinishCondition.Invoke(
            system,
            [identityId, contract, contract.ContractCondition, character.EntityId]
        );
        if (result is not MyContractResults.Success)
            return $"Deliver {contractId}: {result}";
        return $"Deliver {contractId}: {FinishContract.Invoke(system, [identityId, contract])}";
    }

    public static string Complete(long contractId) =>
        Authority
            ? $"Complete {contractId}: {(System.TryFinishCustomContract(contractId) ? "finished" : "not an active custom contract")}"
            : "The contracts run on the World Authority.";

    // OCT pays what the game gave the player, and an acquisition's items go into its stock
    [HarmonyPatch(typeof(MyContract), nameof(MyContract.Finish))]
    private static class Paid
    {
        private static void Postfix(MyContract __instance, MyContractResults __result)
        {
            if (__result != MyContractResults.Success || !On)
                return;
            try
            {
                Settle(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Contract {__instance.Id}: settling it failed", e);
            }
        }
    }

    private static void Settle(MyContract contract)
    {
        var oct = Oct;
        if (oct == null || !Ours(contract, oct.FactionId))
            return;
        var station = oct.Stations.FirstOrDefault(s => s.Id == contract.StartStation);
        var paid =
            contract.RewardMoney == 0
            || MyBankingSystem.ChangeBalance(oct.FactionId, -contract.RewardMoney);
        Plugin.Log.Info(
            $"Contract {contract.Id} ({contract.GetType().Name}) finished at {station?.Name}: OCT "
                + (paid ? "paid " : "could NOT pay ")
                + contract.RewardMoney.ToString("N0", CultureInfo.InvariantCulture)
                + " SC"
        );
        if (Items(contract) is not { } items || station == null)
            return;
        // The same id for the same contract, so a retried deposit applies once
        var bytes = new byte[16];
        BitConverter.GetBytes(contract.Id).CopyTo(bytes, 0);
        "contract"u8.CopyTo(bytes.AsSpan(8));
        Market.AddStock(
            new Guid(bytes),
            station.Name,
            Stations.ItemName(items.ItemType),
            items.ItemAmount * Amounts.Unit
        );
    }

    // The game's own generator stays off while the plugin posts the contracts
    [HarmonyPatch(typeof(MySessionComponentContractSystem), "CreateContractsForStation")]
    private static class NoGenerator
    {
        private static bool Prefix() => !On;
    }
}
