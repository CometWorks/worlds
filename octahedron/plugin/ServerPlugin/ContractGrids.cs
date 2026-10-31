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
            var row = Describe(grid, group, near, isWatched, octId);
            if (row.Flags == ContractGridFlags.None && !isWatched)
                continue;
            row.Position = new ContractPoint(position.X, position.Y, position.Z);
            rows.Add(row);
        }
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
        if (near && big && Derelict(grid))
            row.Flags |= ContractGridFlags.Derelict;

        // A dead hull docked to a live ship or a base is no wreck, but a wreck on a tug stays one
        if (big && !group.Any(g => IsStation(g) || OwnedBy(g, octId)))
        {
            var terminals = group.SelectMany(g => g.GetFatBlocks<MyTerminalBlock>()).ToList();
            if (!terminals.Any(Producing))
                row.Flags |= ContractGridFlags.Unpowered;
            if (group.All(g => g.BigOwners.Count == 0) && !terminals.Any(b => b.IsFunctional))
                row.Flags |= ContractGridFlags.Wreck;
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
        row.MassKg = group.Sum(Mass);
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

    // A static grid's physics has no mass
    private static double Mass(MyCubeGrid grid) =>
        grid.IsStatic ? grid.GetBlocks().Sum(b => (double)b.GetMass()) : grid.Physics?.Mass ?? 0;

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
        foreach (var member in Attached(grid))
        {
            if (member.BigOwners.Count > 0 && !member.BigOwners.Contains(owner))
                continue;
            member.ChangeGridOwnership(player, MyOwnershipShareModeEnum.Faction);
            foreach (var builder in member.GetBlocks().Select(b => b.BuiltBy).Distinct().ToList())
                member.TransferBlocksBuiltByID(builder, player);
        }
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
