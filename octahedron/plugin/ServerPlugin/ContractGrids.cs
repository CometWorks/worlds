using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Game;

namespace ServerPlugin;

// Runs where the grids are, a plain server or a regular node of a cluster: what the contracts need
// to know about the grids this process simulates. The authority decides from these rows.
internal static class ContractGrids
{
    private static OctahedronConfig Config => Plugin.Config;

    private static bool IsStation(MyCubeGrid grid) =>
        MySession.Static.Factions.Any(f =>
            f.Value.Stations.Any(s => s.StationEntityId == grid.EntityId)
        ) || ShipyardZoneWelding.IsShipyard(grid);

    // The Trade Lanes mod's block has no computer component, so the game gives it no owner whatever
    // the world says: it stays ownerless and, with no charge, without power. Never a wreck.
    private static bool LaneComputer(MyCubeGrid grid) =>
        grid.GetFatBlocks().Any(b => b.BlockDefinition.Id.SubtypeName == "TradeLaneComputer");

    private static HashSet<string> WreckTags =>
        Config
            .ContractSalvageFactionTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>An NPC of a wreck faction (ContractSalvageFactionTags): a shipyard's grind slot may scrap its grids.</summary>
    internal static bool IsWreckOwner(long owner) =>
        MySession.Static.Players.IdentityIsNpc(owner)
        && WreckTags.Contains(MySession.Static.Factions.GetPlayerFaction(owner)?.Tag ?? "");

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
        !grid.IsStatic && !IsStation(grid) && (grid.BigOwners.Count == 0 || WreckFaction(grid));

    private static bool WreckFaction(MyCubeGrid grid) =>
        grid.BigOwners.Count > 0
        && MySession.Static.Players.IdentityIsNpc(grid.BigOwners[0])
        && WreckTags.Contains(
            MySession.Static.Factions.GetPlayerFaction(grid.BigOwners[0])?.Tag ?? ""
        );

    private static bool OwnedBy(MyCubeGrid grid, long factionId) =>
        grid.BigOwners.Any(o =>
            MySession.Static.Factions.GetPlayerFaction(o)?.FactionId == factionId
        );

    // A battery with charge, a reactor or engine with fuel, or a solar panel or wind turbine
    private static bool Producing(MyTerminalBlock block) =>
        block.IsFunctional
        && block switch
        {
            MyBatteryBlock battery => battery.CurrentStoredPower > 0,
            MyFueledPowerProducer fueled => fueled.SourceComp.HasCapacityRemainingByType(
                MyResourceDistributorComponent.ElectricityId
            ),
            _ => block is IMyPowerProducer,
        };

    // With everything joined to it by connectors and landing gear too
    private static List<MyCubeGrid> Docked(MyCubeGrid grid) =>
        MyCubeGridGroups.Static?.Physical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList()
        ?? [grid];

    // With its rotor and piston parts: not the tug towing it
    private static List<MyCubeGrid> Attached(MyCubeGrid grid) =>
        MyCubeGridGroups.Static?.Mechanical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList()
        ?? [grid];

    // The player whose ship holds this NPC grid by a connector or landing gear, or 0. A movable NPC
    // grid only, not OCT's and no station's.
    private static long Tower(MyCubeGrid grid, long octId)
    {
        if (
            grid.IsStatic
            || grid.BigOwners.Count == 0
            || !MySession.Static.Players.IdentityIsNpc(grid.BigOwners[0])
            || IsStation(grid)
            || OwnedBy(grid, octId)
        )
            return 0;
        var attached = Attached(grid);
        return Docked(grid)
            .Except(attached)
            .SelectMany(g => g.BigOwners)
            .FirstOrDefault(o => !MySession.Static.Players.IdentityIsNpc(o));
    }

    // Every root grid this process simulates that is a candidate for something, each physical group
    // once, the watched ones always. Not full: only the watched ones.
    public static List<ContractGrid> Scan(bool full, HashSet<long> watched, long octId)
    {
        var range = Config.ContractBountyRangeKm * 1000;
        var stations = MySession
            .Static.Factions.TryGetFactionById(octId)
            ?.Stations.Select(s => s.Position)
            .ToList();
        var seen = new HashSet<long>();
        var rows = new List<ContractGrid>();
        var towed = new List<ContractGrid>();
        var towedSeen = new HashSet<long>();
        foreach (var entity in MyEntities.GetEntities())
        {
            if (entity is not MyCubeGrid grid || grid.Physics == null || grid.MarkedForClose)
                continue;
            // A target stays one however it is attached: a tug's connector makes either the root
            var isWatched = watched.Contains(grid.EntityId);
            if (!isWatched && (!full || MyGridPhysicalHierarchy.Static.GetRoot(grid) != grid))
                continue;
            if (!Plugin.Simulated(grid))
                continue;
            var group = isWatched ? Attached(grid) : Docked(grid);
            var first = true;
            foreach (var member in group)
                first &= seen.Add(member.EntityId);
            if (!first && !isWatched)
                continue;

            var position = grid.PositionComp.GetPosition();
            var near =
                isWatched
                || stations?.Any(s => VRageMath.Vector3D.Distance(s, position) <= range) == true;
            // After the row its group would give it, which it replaces: a towed grid isn't a root
            foreach (var member in Docked(grid))
                if (
                    Tower(member, octId) is var tower and not 0
                    && Plugin.Simulated(member)
                    && towedSeen.Add(Attached(member).Min(g => g.EntityId))
                )
                {
                    var towedRow = Describe(member, Attached(member), true, true, octId);
                    towedRow.Flags |= ContractGridFlags.Towed;
                    towedRow.Tower = tower;
                    var at = member.PositionComp.GetPosition();
                    towedRow.Position = new ContractPoint(at.X, at.Y, at.Z);
                    towed.Add(towedRow);
                }

            var row = Describe(grid, group, near, isWatched, octId);
            if (row.Flags == ContractGridFlags.None && !isWatched)
                continue;
            row.Position = new ContractPoint(position.X, position.Y, position.Z);
            rows.Add(row);
        }
        rows.AddRange(towed);
        return rows;
    }

    private static ContractGrid Describe(
        MyCubeGrid grid,
        List<MyCubeGrid> group,
        bool near,
        bool isWatched,
        long octId
    )
    {
        var blocks = group.Sum(g => g.BlocksCount);
        var big = blocks >= Config.ContractSalvageMinBlocks;
        var row = new ContractGrid
        {
            Id = grid.EntityId,
            Name = grid.DisplayName,
            Blocks = blocks,
        };
        if (ShipyardZoneWelding.IsShipyard(grid))
            row.Flags |= ContractGridFlags.Shipyard;
        if (near && BountyTarget(grid, octId))
        {
            row.Flags |= ContractGridFlags.Bounty;
            row.Threat = ThreatScore.Grid(grid);
        }
        if (big && Derelict(grid))
            row.Flags |= ContractGridFlags.Derelict;

        // A dead hull docked to a live ship or a base is no wreck, but a wreck on a tug stays one
        if (!group.Any(g => IsStation(g) || OwnedBy(g, octId) || LaneComputer(g)))
        {
            var abandoned = group.All(g => g.BigOwners.Count == 0) || WreckFaction(grid);
            if (!big)
            {
                if (abandoned)
                    row.Flags |= ContractGridFlags.Debris;
            }
            else
            {
                var terminals = group.SelectMany(g => g.GetFatBlocks<MyTerminalBlock>()).ToList();
                if (!terminals.Any(Producing))
                    row.Flags |= ContractGridFlags.Unpowered;
                if (group.All(g => g.BigOwners.Count == 0) && !terminals.Any(b => b.IsFunctional))
                    row.Flags |= ContractGridFlags.Wreck;
                var owner = grid.BigOwners.FirstOrDefault();
                if (abandoned)
                    row.Flags |= ContractGridFlags.Abandoned;
                else if (MySession.Static.Players.IdentityIsNpc(owner))
                {
                    row.Flags |= ContractGridFlags.Npc;
                    if (
                        !grid.IsStatic
                        && !terminals.Any(b =>
                            b is MyShipController { EnableShipControl: true } && b.IsFunctional
                        )
                    )
                        row.Flags |= ContractGridFlags.Disabled;
                }
                else if (ShipyardOrders.IsBot(owner))
                    row.Flags |= ContractGridFlags.Bot;
            }
        }
        if (row.Flags == ContractGridFlags.None && !isWatched)
            return row;

        if (
            group.Any(g =>
                g.OccupiedBlocks.Count > 0
                || MySession.Static.Players.GetControllingPlayer(g) != null
            )
        )
            row.Flags |= ContractGridFlags.Seated;
        if (group.Any(InSafeZone))
            row.Flags |= ContractGridFlags.SafeZone;
        row.MassKg = group.Sum(Mass);
        row.Value = Value(group);
        row.Pcu = GroupPcuLimit.GroupPcu(grid);
        row.Owner =
            (
                grid.BigOwners.Count > 0 ? grid : group.FirstOrDefault(g => g.BigOwners.Count > 0)
            )?.BigOwners[0]
            ?? 0;
        row.Builder =
            group
                .SelectMany(g => g.GetBlocks())
                .GroupBy(b => b.BuiltBy)
                .OrderByDescending(b => b.Count())
                .FirstOrDefault()
                ?.Key
            ?? 0;
        if (isWatched && row.Owner == 0 && !group.Any(Ownable))
            row.Flags |= ContractGridFlags.Unownable;
        return row;
    }

    private static bool InSafeZone(MyCubeGrid grid) =>
        MySessionComponentSafeZones.SafeZones.Any(z =>
            z.Enabled && !z.MarkedForClose && z.Contains(grid.PositionComp.WorldAABB.Center)
        );

    // The blocks only: a ship's physics mass has its cargo and tank fill too (SE-1025)
    private static double Mass(MyCubeGrid grid) => grid.GetBlocks().Sum(b => (double)b.GetMass());

    // The components mounted on the blocks and waiting in their stockpiles, at the market's
    // reference prices; never what the blocks' inventories hold
    public static double Value(IEnumerable<MyCubeGrid> group)
    {
        var milli = 0L;
        var prices = new Dictionary<VRage.Game.MyDefinitionId, long>();
        long Price(VRage.Game.MyDefinitionId id)
        {
            if (!prices.TryGetValue(id, out var price))
                prices[id] = price = Market.ReferencePrice(Stations.ItemName(id));
            return price;
        }
        foreach (var block in group.SelectMany(g => g.GetBlocks()))
        {
            var stack = block.ComponentStack;
            for (var i = 0; i < stack.GroupCount; i++)
            {
                var mounted = stack.GetGroupInfo(i).MountedCount;
                if (mounted > 0)
                    milli +=
                        mounted * Price(block.BlockDefinition.Components[i].DeconstructItem.Id);
            }
            if (!block.StockpileEmpty)
                foreach (var item in block.GetObjectBuilder().ConstructionStockpile.Items)
                    milli += item.Amount * Price(item.PhysicalContent.GetObjectId());
        }
        return milli / 1000.0;
    }

    private static bool Ownable(MyCubeGrid grid) =>
        grid.GetFatBlocks()
            .Any(b =>
                b.IDModule != null
                && b.SlimBlock.BlockDefinition.RatioEnoughForOwnership(b.SlimBlock.BuildLevelRatio)
            );

    // The towed grid and its rotor and piston parts become the player's, and count against their
    // limits instead of the old owner's. A tug's connector is not part of it, and a ship of another
    // player on a rotor stays theirs. Does nothing where the grid isn't, so all nodes can be told.
    public static void Give(long gridId, long player)
    {
        if (
            !MyEntities.TryGetEntityById(gridId, out var entity)
            || entity is not MyCubeGrid { MarkedForClose: false } grid
            || !Plugin.Simulated(grid)
        )
            return;
        var owner = grid.BigOwners.FirstOrDefault();
        if (!grid.BigOwners.Contains(player))
            ShipyardOrders.GivenByTow(grid, player);
        foreach (var member in Attached(grid))
        {
            if (member.BigOwners.Count > 0 && !member.BigOwners.Contains(owner))
                continue;
            // The order repeats until a report shows the owner: not over what the player set since
            if (!member.BigOwners.Contains(player))
                TargetNeutrals.Clear(member);
            member.ChangeGridOwnership(player, OctFaction.BuildShare(player));
            foreach (var builder in member.GetBlocks().Select(b => b.BuiltBy).Distinct().ToList())
                member.TransferBlocksBuiltByID(builder, player);
        }
    }

    public static void BreakDown(long gridId, long player, string station)
    {
        if (
            MyEntities.TryGetEntityById(gridId, out var entity)
            && entity is MyCubeGrid { MarkedForClose: false } grid
            && Plugin.Simulated(grid)
        )
            ShipyardOrders.Tow(station, player, grid);
    }

    // Wreck hunts handed in here that their player may still have scrapped: grid -> player, station
    private static Dictionary<long, (long Player, string Station)> scrappable = [];

    public static void Scrappable(Dictionary<long, (long Player, string Station)> grids) =>
        scrappable = grids;

    /// <summary>
    /// !oct scrap from a player: the wreck they just handed in, if this node has it, goes to pieces.
    /// Its components and what it holds go into OCT's stock at the station; the reward is paid
    /// already.
    /// </summary>
    public static string ScrapHandedIn(long player)
    {
        var mine = scrappable.Where(s => s.Value.Player == player).ToList();
        if (mine.Count == 0)
            return "You have no wreck to scrap: towing one in for a contract lets you, for a while.";
        var at = MySession
            .Static.Players.TryGetIdentity(player)
            ?.Character?.PositionComp.GetPosition();
        // The one they stand nearest to, of those this node has
        var here = mine.Select(s =>
                (
                    s.Key,
                    s.Value.Station,
                    Grid: MyEntities.TryGetEntityById(s.Key, out var e)
                    && e is MyCubeGrid { MarkedForClose: false } g
                    && Plugin.Simulated(g)
                        ? g
                        : null
                )
            )
            .Where(s => s.Grid != null)
            .OrderBy(s =>
                at == null
                    ? 0
                    : VRageMath.Vector3D.Distance(at.Value, s.Grid.PositionComp.GetPosition())
            )
            .ToList();
        foreach (var (id, station, grid) in here)
        {
            var group = Attached(grid);
            if (
                !group.Any(g => g.BigOwners.Contains(player))
                && group.Any(g => g.BigOwners.Count > 0)
            )
                return $"'{grid.DisplayName}' is not yours any more.";
            if (group.Any(g => g.OccupiedBlocks.Count > 0))
                return $"Someone sits in '{grid.DisplayName}'.";
            var parts = Parts(group);
            var name = grid.DisplayName;
            foreach (var member in group)
                member.Close();
            scrappable.Remove(id);
            foreach (var (item, amount) in parts)
            {
                Market.AddStock(Guid.NewGuid(), station, Stations.ItemName(item), amount.RawValue);
            }
            Plugin.Log.Info(
                $"Contracts: {player} scrapped the handed-in wreck {id} '{name}' at {station}, {parts.Count} kinds of items to OCT"
            );
            return $"{station}'s shipyard took '{name}' apart. Your reward stays yours.";
        }
        return "Your wreck isn't here: stand near it at the shipyard.";
    }

    /// <summary>What taking the grids apart yields: their mounted and stockpiled components, and what they hold.</summary>
    internal static Dictionary<VRage.Game.MyDefinitionId, VRage.MyFixedPoint> Parts(
        IEnumerable<MyCubeGrid> grids
    )
    {
        var parts = new Dictionary<VRage.Game.MyDefinitionId, VRage.MyFixedPoint>();
        void Count(VRage.Game.MyDefinitionId item, VRage.MyFixedPoint amount) =>
            parts[item] = (parts.TryGetValue(item, out var had) ? had : 0) + amount;
        foreach (var block in grids.SelectMany(g => g.GetBlocks()))
        {
            var stack = block.ComponentStack;
            for (var i = 0; i < stack.GroupCount; i++)
                if (stack.GetGroupInfo(i).MountedCount is var mounted and > 0)
                    Count(block.BlockDefinition.Components[i].DeconstructItem.Id, mounted);
            if (!block.StockpileEmpty)
                foreach (var item in block.GetObjectBuilder().ConstructionStockpile.Items)
                    Count(item.PhysicalContent.GetObjectId(), item.Amount);
            if (block.FatBlock == null)
                continue;
            for (var i = 0; i < block.FatBlock.InventoryCount; i++)
                foreach (var item in block.FatBlock.GetInventory(i).GetItems())
                    Count(item.Content.GetObjectId(), item.Amount);
        }
        return parts;
    }

    // Scrapped, with its rotor and piston parts; a tug's connector lets go of it
    public static void Close(long gridId)
    {
        if (
            !MyEntities.TryGetEntityById(gridId, out var entity)
            || entity is not MyCubeGrid { MarkedForClose: false } grid
            || !Plugin.Simulated(grid)
        )
            return;
        foreach (var member in Attached(grid))
            member.Close();
    }
}
