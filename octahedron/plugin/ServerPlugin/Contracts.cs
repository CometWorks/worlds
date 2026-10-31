using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
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
// - salvage: an unowned or wrecked grid near the station, done when it is towed to the station's
//   shipyard, which then scraps it.
//
// The contracts are station contracts, without a start block: the game shows them at every contract
// block of the station and keeps them in its contract system, which is the World Authority's on a
// cluster. The game pays the reward out of nothing when a contract finishes; the Finish postfix
// takes the same amount from OCT. Bounties and salvage watch grids, which the World Authority
// doesn't have, so a cluster gets only hauling and acquisitions.
internal static class Contracts
{
    private const string VariableName = "Octahedron-Contracts";
    private const double BountyDoneShare = 0.25;
    private const double BountyWitnessMeters = 5_000;
    private const double ShipyardSearchMeters = 3_000;
    private static readonly TimeSpan PostInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TrackInterval = TimeSpan.FromSeconds(10);
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

    private static OctahedronConfig Config => Plugin.Config;
    public static bool On => Config.Enabled && Config.ContractsEnabled;

    // The contract system and the market are kept there
    private static bool Authority => Stations.IsAuthority;

    private static bool EntityContracts => !PluginCluster.IsClusterProcess;

    private static MySessionComponentContractSystem System =>
        MySession.Static.GetComponent<MySessionComponentContractSystem>();

    private static MyFaction Oct =>
        MySession.Static.Factions.TryGetFactionByTag(Config.MoneyAuthorityFactionTag);

    // Vanilla's own contracts always have a start block or aren't player made
    private static bool Ours(MyContract contract, long octId) =>
        contract.IsPlayerMade && contract.StartBlock == 0 && contract.StartFaction == octId;

    public static void Update()
    {
        if (!On || MySession.Static?.Ready != true || !Authority)
            return;
        var now = DateTime.UtcNow;
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
        Track();
        return Post();
    }

    private static string Post()
    {
        var oct = Oct;
        if (oct == null)
            return $"No faction tagged {Config.MoneyAuthorityFactionTag}.";
        var system = System;
        var stations = oct.Stations.ToList();
        var state = State.Load();
        var active = system
            .ActiveContracts.OfType<MyContract>()
            .Where(c => Ours(c, oct.FactionId))
            .ToList();
        int hauling = 0,
            acquisitions = 0,
            bounties = 0,
            salvage = 0;
        foreach (var station in stations)
        {
            var open = system
                .GetAvailableContractsForFactionStation(station.Id)
                .OfType<MyContract>()
                .Where(c => Ours(c, oct.FactionId))
                .ToList();
            hauling += Haul(system, station, stations, open);
            acquisitions += Acquire(system, station, open, active);
            if (EntityContracts)
            {
                bounties += Bounties(system, station, oct.FactionId, state);
                salvage += Salvage(system, station, state);
            }
        }
        state.Save();
        var text =
            $"Contracts posted: {hauling} hauling, {acquisitions} acquisitions, {bounties} bounties, {salvage} salvage";
        if (hauling + acquisitions + bounties + salvage > 0)
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

    private static List<MyCubeGrid> GridsNear(Vector3D position, double meters)
    {
        var sphere = new BoundingSphereD(position, meters);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var grids = entities
            .OfType<MyCubeGrid>()
            .Where(g =>
                g.Physics != null
                && !g.MarkedForClose
                && MyGridPhysicalHierarchy.Static.GetRoot(g) == g
                && Vector3D.Distance(g.PositionComp.GetPosition(), position) <= meters
            )
            .ToList();
        entities.Clear();
        return grids;
    }

    private static bool IsStation(MyCubeGrid grid) =>
        MySession.Static.Factions.Any(f =>
            f.Value.Stations.Any(s => s.StationEntityId == grid.EntityId)
        ) || ShipyardZoneWelding.IsShipyard(grid);

    private static HashSet<string> WreckTags =>
        Config
            .ContractSalvageFactionTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // An NPC grid of another faction than OCT and the wrecks
    private static bool BountyTarget(MyCubeGrid grid, long octId)
    {
        if (grid.BigOwners.Count == 0 || IsStation(grid))
            return false;
        var owner = grid.BigOwners[0];
        if (!MySession.Static.Players.IdentityIsNpc(owner))
            return false;
        var faction = MySession.Static.Factions.GetPlayerFaction(owner);
        return faction?.FactionId != octId && !WreckTags.Contains(faction?.Tag ?? "");
    }

    // Movable and nobody's, or a wreck faction's
    private static bool Derelict(MyCubeGrid grid) =>
        !grid.IsStatic
        && !IsStation(grid)
        && (
            grid.BigOwners.Count == 0
            || (
                MySession.Static.Players.IdentityIsNpc(grid.BigOwners[0])
                && WreckTags.Contains(
                    MySession.Static.Factions.GetPlayerFaction(grid.BigOwners[0])?.Tag ?? ""
                )
            )
        );

    private static string Gps(string name, Vector3D p) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "GPS:{0}:{1:0}:{2:0}:{3:0}:",
            name,
            p.X,
            p.Y,
            p.Z
        );

    private static int Open(State state, char kind, IMyFactionStation station) =>
        state.Targets.Count(t =>
            t.Value.Kind == kind
            && t.Value.Station == station.Id
            && System.GetContractState(t.Key) == MyCustomContractStateEnum.Inactive
        );

    private static int Bounties(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        long octId,
        State state
    )
    {
        var wanted = Config.ContractBountiesPerStation - Open(state, 'b', station);
        if (wanted <= 0)
            return 0;
        var targeted = state.Targets.Values.Select(t => t.Grid).ToHashSet();
        var targets = GridsNear(station.Position, Config.ContractBountyRangeKm * 1000)
            .Where(g => !targeted.Contains(g.EntityId) && BountyTarget(g, octId))
            .Select(g => (Grid: g, Threat: ThreatScore.Grid(g)))
            .Where(t => t.Threat >= Config.ContractBountyMinThreat)
            .OrderByDescending(t => t.Threat)
            .Take(wanted)
            .ToList();
        var posted = 0;
        foreach (var (grid, threat) in targets)
        {
            var position = grid.PositionComp.GetPosition();
            var km = Vector3D.Distance(position, station.Position) / 1000;
            var contract = new Api.MyContractCustom(
                BountyType,
                0,
                ContractMath.BountyReward(threat, Config.ContractBountyRewardPerThreat),
                0,
                Config.ContractBountyMinutes,
                $"Bounty: {grid.DisplayName}",
                $"Destroy or disable the NPC grid \"{grid.DisplayName}\" (threat {threat:0}), "
                    + $"{km:0.#} km from {station.Name} when posted. {Gps(grid.DisplayName, position)}",
                0,
                0,
                null
            );
            if (!Add(system, contract, station, out var id))
                continue;
            state.Targets[id] = new Target('b', grid.EntityId, station.Id, threat, position);
            posted++;
        }
        return posted;
    }

    private static int Salvage(
        MySessionComponentContractSystem system,
        IMyFactionStation station,
        State state
    )
    {
        var wanted = Config.ContractSalvagePerStation - Open(state, 's', station);
        if (wanted <= 0 || Shipyard(station) == null)
            return 0;
        var targeted = state.Targets.Values.Select(t => t.Grid).ToHashSet();
        var derelicts = GridsNear(station.Position, Config.ContractBountyRangeKm * 1000)
            .Where(g =>
                !targeted.Contains(g.EntityId)
                && g.BlocksCount >= Config.ContractSalvageMinBlocks
                && Derelict(g)
            )
            .OrderBy(g => Vector3D.DistanceSquared(g.PositionComp.GetPosition(), station.Position))
            .Take(wanted)
            .ToList();
        var posted = 0;
        foreach (var grid in derelicts)
        {
            var position = grid.PositionComp.GetPosition();
            var tonnes = grid.Physics.Mass / 1000;
            var contract = new Api.MyContractCustom(
                SalvageType,
                0,
                ContractMath.SalvageReward(grid.Physics.Mass, Config.ContractSalvageRewardPerTonne),
                0,
                Config.ContractSalvageMinutes,
                $"Salvage: {grid.DisplayName}",
                $"Tow the derelict \"{grid.DisplayName}\" ({tonnes:0} t) to {station.Name}'s shipyard, "
                    + $"within {Config.ContractSalvageRadius:0} m of it. {Gps(grid.DisplayName, position)}",
                0,
                0,
                null
            );
            if (!Add(system, contract, station, out var id))
                continue;
            state.Targets[id] = new Target('s', grid.EntityId, station.Id, 0, position);
            posted++;
        }
        return posted;
    }

    private static MyCubeGrid Shipyard(IMyFactionStation station) =>
        GridsNear(station.Position, ShipyardSearchMeters)
            .Where(ShipyardZoneWelding.IsShipyard)
            .OrderBy(g => Vector3D.DistanceSquared(g.PositionComp.GetPosition(), station.Position))
            .FirstOrDefault();

    // Bounties and salvage: done, failed, or withdrawn when the target is gone before anyone took it
    private static void Track()
    {
        var oct = Oct;
        if (oct == null)
            return;
        var system = System;
        var state = State.Load();
        if (state.Targets.Count == 0)
            return;
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
            if (!EntityContracts)
                continue;
            var grid =
                MyEntities.TryGetEntityById(target.Grid, out var entity)
                && entity is MyCubeGrid { MarkedForClose: false } g
                    ? g
                    : null;
            var station = oct.Stations.FirstOrDefault(s => s.Id == target.Station);
            if (contractState == MyCustomContractStateEnum.Inactive)
            {
                var stillOn =
                    grid != null
                    && station != null
                    && (target.Kind == 'b' ? BountyTarget(grid, oct.FactionId) : Derelict(grid));
                if (!stillOn)
                {
                    system.RemoveContract(id);
                    state.Targets.Remove(id);
                    Plugin.Log.Info($"Contract {id} withdrawn: its target is gone");
                }
                continue;
            }

            var contract = system.GetContractById(id) as MyContract;
            var hunter = contract?.Owners.FirstOrDefault() ?? 0;
            bool? done = null;
            if (target.Kind == 'b')
            {
                if (grid != null)
                {
                    target.Position = grid.PositionComp.GetPosition();
                    if (
                        !BountyTarget(grid, oct.FactionId)
                        || ThreatScore.Grid(grid) <= target.Threat * BountyDoneShare
                    )
                        done = true;
                }
                else
                    done = Near(hunter, target.Position, BountyWitnessMeters);
            }
            else if (grid == null || station == null)
                done = false;
            else if (
                Shipyard(station) is { } shipyard
                && Vector3D.Distance(
                    grid.PositionComp.GetPosition(),
                    shipyard.PositionComp.GetPosition()
                ) <= Config.ContractSalvageRadius
            )
            {
                done = system.TryFinishCustomContract(id);
                if (done == true)
                    grid.Close();
                continue;
            }

            if (done == true)
                system.TryFinishCustomContract(id);
            else if (done == false)
                system.TryFailCustomContract(id);
        }
        state.Save();
    }

    private static bool Near(long identityId, Vector3D position, double meters) =>
        MySession.Static.Players.TryGetIdentity(identityId)?.Character is { } character
        && Vector3D.Distance(character.PositionComp.GetPosition(), position) <= meters;

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
        var state = State.Load();
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
    }

    private static string Kind(MyContract contract, State state) =>
        contract switch
        {
            MyContractDeliver => "hauling",
            MyContractObtainAndDeliver => "acquisition",
            _ when state.Targets.TryGetValue(contract.Id, out var t) => t.Kind == 'b'
                ? "bounty"
                : "salvage",
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

    private sealed class Target(
        char kind,
        long grid,
        long station,
        double threat,
        Vector3D position
    )
    {
        public readonly char Kind = kind;
        public readonly long Grid = grid;
        public readonly long Station = station;
        public readonly double Threat = threat;
        public Vector3D Position = position;
    }

    // contract:kind:grid:station:threat:x:y:z;... for the bounties and salvage
    private sealed class State
    {
        public readonly Dictionary<long, Target> Targets = new();

        private static double D(string text) => double.Parse(text, CultureInfo.InvariantCulture);

        private static long L(string text) => long.Parse(text, CultureInfo.InvariantCulture);

        public static State Load()
        {
            var state = new State();
            if (!MyAPIGateway.Utilities.GetVariable<string>(VariableName, out var text))
                return state;
            try
            {
                foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = entry.Split(':');
                    state.Targets[L(p[0])] = new Target(
                        p[1][0],
                        L(p[2]),
                        L(p[3]),
                        D(p[4]),
                        new Vector3D(D(p[5]), D(p[6]), D(p[7]))
                    );
                }
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
            {
                // The contracts stay; only their tracking is lost
                Plugin.Log.Error($"Contracts: unreadable state, starting over: {text}", e);
                return new State();
            }
            return state;
        }

        public void Save()
        {
            string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            MyAPIGateway.Utilities.SetVariable(
                VariableName,
                string.Join(
                    ";",
                    Targets.Select(t =>
                        $"{t.Key}:{t.Value.Kind}:{t.Value.Grid}:{t.Value.Station}:{N(t.Value.Threat)}:"
                        + $"{N(t.Value.Position.X)}:{N(t.Value.Position.Y)}:{N(t.Value.Position.Z)}"
                    )
                )
            );
        }
    }
}
