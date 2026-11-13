using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

// The shipyard's modes besides building (SE-1386): a repair, grind or hack slot, booked with the
// buttons of the station's mode panel. A slot works only in the shipyard's build volume, the
// largest cylinder in its free space around the center line (BuildVolume), so it never welds,
// grinds or hacks the ship docked at its connectors or the one that towed another in.
//
// - Repair: the welders are the player's, and what they lack for the player's blocks in the
//   volume, damaged or projected, is bought from the station's Store, as far as its stock, the
//   container room and the player's account go.
// - Grind: the deconstructors are the player's and take apart what in the volume is the player's,
//   their faction's, nobody's or a wreck faction's. At the end what they took goes into the
//   player's ship docked at the shipyard's connectors, and the rest is sold for them.
// - Hack: a ship in the volume for ShipyardHackSeconds becomes the player's: one they are not the
//   big owner of, of no player in their faction, and whose big owner isn't online anywhere on
//   the cluster. NPC ships and nobody's wrecks always qualify.
internal static partial class ShipyardOrders
{
    // The shipyard's ToolCore deconstructors: OctahedronMod's filament one, and OmniArc's
    internal static readonly HashSet<string> DeconstructorSubtypes =
    [
        "OctahedronFilamentDeconstructor",
        "OmniArcDestro",
    ];

    // The volume of each shipyard with a slot running, kept by Check for ToolCore's threads
    private static readonly Dictionary<
        long,
        (BuildVolume Volume, SlotKind Kind, long Player)
    > slotVolumes = [];

    // A repair slot's purchase on its way
    private static readonly HashSet<string> buying = [];

    internal static IEnumerable<MyFunctionalBlock> Deconstructors(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyFunctionalBlock>()
            .Where(b => DeconstructorSubtypes.Contains(b.BlockDefinition.Id.SubtypeName))
            .Where(b => ((IMyTerminalBlock)b).GetProperty(ShootProperty) != null);

    /// <summary>The cylinder in the shipyard's free space around its center line.</summary>
    internal static BuildVolume Volume(MyCubeGrid shipyard)
    {
        var box = FreeSpace(shipyard);
        var axes = MatrixD.CreateFromQuaternion(box.Orientation);
        double[] V(Vector3D v) => [v.X, v.Y, v.Z];
        return BuildVolume.OfBox(
            V(box.Center),
            [V(axes.Right), V(axes.Up), V(axes.Backward)],
            [box.HalfExtent.X, box.HalfExtent.Y, box.HalfExtent.Z]
        );
    }

    internal static bool Contains(BuildVolume volume, Vector3D point) =>
        volume.Contains(point.X, point.Y, point.Z);

    private static bool Contains(BuildVolume volume, IMySlimBlock block)
    {
        block.ComputeWorldCenter(out var center);
        return Contains(volume, center);
    }

    // Every corner of the grid's box: a convex volume holds all of it
    private static bool Inside(BuildVolume volume, MyCubeGrid grid)
    {
        var box = new MyOrientedBoundingBoxD(grid.PositionComp.LocalAABB, grid.WorldMatrix);
        var corners = new Vector3D[8];
        box.GetCorners(corners, 0);
        return corners.All(c => Contains(volume, c));
    }

    private static BoundingSphereD Sphere(BuildVolume volume) =>
        new(
            new Vector3D(volume.Center[0], volume.Center[1], volume.Center[2]),
            Math.Sqrt(volume.Radius * volume.Radius + volume.HalfLength * volume.HalfLength)
        );

    // The grids with their center or a block in the volume: not the shipyard's, a station's or a projection
    private static List<MyCubeGrid> GridsInVolume(MyCubeGrid shipyard, BuildVolume volume)
    {
        var sphere = Sphere(volume);
        var own = Grids(shipyard);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var grids = entities
            .OfType<MyCubeGrid>()
            .Where(g =>
                !g.MarkedForClose
                && g.Physics != null
                && g.Projector == null
                && !own.Contains(g)
                && !ShipyardZoneWelding.IsShipyard(g)
                && Stations.StationOf(g) == null
            )
            .Where(g =>
                Contains(volume, g.PositionComp.WorldAABB.Center)
                || g.GetBlocks().Any(b => Contains(volume, b))
            )
            .ToList();
        entities.Clear();
        return grids;
    }

    /// <summary>A shipyard tool's filter while a slot runs: true for a block out of the slot's volume. Null without a slot.</summary>
    internal static Predicate<IMySlimBlock> OutOfVolume(MyCubeBlock tool) =>
        slotVolumes.TryGetValue(tool.CubeGrid.EntityId, out var slot)
            ? block => !Contains(slot.Volume, block)
            : null;

    /// <summary>A deconstructor's filter: null in a grind slot's volume for what the player may grind, everything else.</summary>
    internal static Predicate<IMySlimBlock> NotToGrind(MyCubeBlock tool)
    {
        if (
            !DeconstructorSubtypes.Contains(tool.BlockDefinition.Id.SubtypeName)
            || !ShipyardZoneWelding.IsShipyard(tool)
        )
            return null;
        if (
            !slotVolumes.TryGetValue(tool.CubeGrid.EntityId, out var slot)
            || slot.Kind != SlotKind.Grind
        )
            return _ => true;
        return block =>
            !Contains(slot.Volume, block)
            || block.CubeGrid is not MyCubeGrid grid
            || grid.Projector != null
            || !Grindable(grid, slot.Player);
    }

    /// <summary>The deconstructors of a shipyard in a grind slot may grind at this point, the safe zone notwithstanding.</summary>
    internal static bool GrindsHere(MyEntity tool, Vector3D point) =>
        tool is MyCubeBlock block
        && DeconstructorSubtypes.Contains(block.BlockDefinition.Id.SubtypeName)
        && slotVolumes.TryGetValue(block.CubeGrid.EntityId, out var slot)
        && slot.Kind == SlotKind.Grind
        && Contains(slot.Volume, point);

    /// <summary>The shipyard grid has a grind slot: its tools may grind, which ToolCore asks the zone about.</summary>
    internal static bool Grinding(MyCubeGrid shipyard) =>
        slotVolumes.TryGetValue(shipyard.EntityId, out var slot) && slot.Kind == SlotKind.Grind;

    // The player's, their faction's, nobody's or a wreck faction's: by the big owners of its group
    private static bool Grindable(MyCubeGrid grid, long player)
    {
        var owners = Grids(grid).SelectMany(g => g.BigOwners).Distinct().ToList();
        if (owners.Count == 0)
            return true;
        var faction = OctFaction.Team(player);
        return owners.All(o =>
            o == player || faction != null && faction.IsMember(o) || ContractGrids.IsWreckOwner(o)
        );
    }

    private static void Deconstruct(MyCubeGrid shipyard, bool on)
    {
        foreach (var tool in Deconstructors(shipyard))
        {
            var block = (IMyTerminalBlock)tool;
            if (on && !tool.Enabled)
                tool.Enabled = true;
            if (block.GetValueBool(TrackProperty) != on)
                block.SetValueBool(TrackProperty, on);
            if (block.GetValueBool(ShootProperty) != on)
                block.SetValueBool(ShootProperty, on);
        }
        if (on)
        {
            ShipyardZoneWelding.ShowBeams(shipyard, true);
            Fuel(shipyard);
        }
        ShipyardZoneWelding.ShowShooting(Deconstructors(shipyard), on);
    }

    // --- a slot at each check ---

    private static void CheckSlot(MyCubeGrid shipyard, RepairSlot slot)
    {
        var volume = Volume(shipyard);
        slotVolumes[shipyard.EntityId] = (volume, slot.Kind, slot.Player);
        var idle =
            DateTime.UtcNow - slot.LastWork
            >= TimeSpan.FromMinutes(Config.ShipyardRepairIdleMinutes);
        switch (slot.Kind)
        {
            case SlotKind.Repair:
                if (idle)
                    EndRepair(
                        shipyard,
                        slot,
                        $"no building or welding for {Config.ShipyardRepairIdleMinutes} min"
                    );
                else
                {
                    Welders(shipyard, true);
                    BuyForRepair(shipyard, slot, volume);
                }
                break;
            case SlotKind.Grind:
                // ToolCore's deconstructors don't always name their inventory to the grinding
                // patch, so what they ground counts as work too
                if (CollectGround(shipyard))
                {
                    slot.LastWork = DateTime.UtcNow;
                    slot.Worked = true;
                    idle = false;
                }
                if (idle)
                    EndGrind(
                        shipyard,
                        slot,
                        $"nothing was ground for {Config.ShipyardRepairIdleMinutes} min"
                    );
                else if (slot.Worked && !LeftToGrind(shipyard, volume, slot.Player))
                    EndGrind(shipyard, slot, "nothing is left to grind in its build volume");
                else
                    Deconstruct(shipyard, true);
                break;
            case SlotKind.Hack:
                if (idle)
                    EndHack(
                        shipyard,
                        slot,
                        $"no ship to hack came for {Config.ShipyardRepairIdleMinutes} min"
                    );
                else
                    Hack(shipyard, slot, volume);
                break;
        }
    }

    // --- repair ---

    // The components the welders lack for the player's blocks in the volume, built or projected,
    // are bought, less what the shipyard and the player's docked ships hold
    private static void BuyForRepair(MyCubeGrid shipyard, RepairSlot slot, BuildVolume volume)
    {
        if (
            !Config.ShipyardRepairBuy
            || buying.Contains(slot.Id)
            || !StationTables().TryGetValue(slot.Station, out var tables)
        )
            return;
        var player = slot.Player;
        var faction = OctFaction.Team(player);
        bool Theirs(long identity) =>
            identity == player || faction != null && faction.IsMember(identity);

        var needed = new Dictionary<string, long>();
        void Need(MyDefinitionId id, long count)
        {
            var name = Stations.ItemName(id);
            needed[name] = needed.GetValueOrDefault(name) + count * Amounts.Unit;
        }
        var missing = new Dictionary<string, int>();
        foreach (var grid in GridsInVolume(shipyard, volume))
        {
            foreach (var block in grid.GetBlocks())
            {
                if (
                    !Theirs(block.OwnerId != 0 ? block.OwnerId : block.BuiltBy)
                    || !Contains(volume, block)
                )
                    continue;
                if (block.IsFullIntegrity && !block.HasDeformation)
                    continue;
                missing.Clear();
                block.GetMissingComponents(missing);
                foreach (var (subtype, count) in missing)
                    Need(new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype), count);
            }
            foreach (var projector in grid.GetFatBlocks<MyProjectorBase>())
            {
                if (projector.ProjectedGrid == null || !Theirs(projector.OwnerId))
                    continue;
                foreach (var block in projector.ProjectedGrid.GetBlocks())
                    if (Contains(volume, block))
                        foreach (var component in block.BlockDefinition.Components)
                            Need(component.Definition.Id, component.Count);
            }
        }
        if (needed.Count == 0)
            return;

        var have = Stock(shipyard);
        foreach (var inventory in Cargo(DockedShips(tables[0], player)))
        foreach (var item in inventory.GetItems())
        {
            var name = Stations.ItemName(item.Content.GetObjectId());
            have[name] = have.GetValueOrDefault(name) + item.Amount.RawValue;
        }
        var room = Containers(shipyard).Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        var lack = new Dictionary<string, long>();
        foreach (var (item, raw) in needed.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            var short_ = raw - have.GetValueOrDefault(item);
            if (short_ <= 0)
                continue;
            var unit =
                MyDefinitionManager.Static.GetPhysicalItemDefinition(ItemId(item))?.Volume ?? 0;
            var units = short_ / Amounts.Unit;
            if (unit > 0)
                units = Math.Min(units, (long)(room / unit));
            if (units <= 0)
                continue;
            lack[item] = units * Amounts.Unit;
            room -= units * unit;
        }
        if (lack.Count == 0)
            return;
        buying.Add(slot.Id);
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Buy,
                Station = slot.Station,
                Player = player,
                Needed = lack,
            },
            (ok, message, bought) =>
            {
                buying.Remove(slot.Id);
                if (!ok || bought == null || bought.Items.Count == 0)
                {
                    if (!ok)
                        Plugin.Log.Warning($"Repair slot {slot.Id}: no purchase: {message}");
                    return;
                }
                var containers = Containers(shipyard).ToList();
                foreach (var (item, raw) in bought.Items)
                    Put(
                        containers,
                        (MyObjectBuilder_PhysicalObject)
                            MyObjectBuilderSerializer.CreateNewObject(ItemId(item)),
                        new MyFixedPoint { RawValue = raw }
                    );
                var what = string.Join(
                    ", ",
                    bought.Items.Select(i => $"{i.Value / Amounts.Unit} {Market.ItemTitle(i.Key)}")
                );
                slot.LastWork = DateTime.UtcNow;
                Plugin.Log.Info(
                    $"Repair slot {slot.Id} of {Name(player)} bought {what}: {message}"
                );
                Tell(player, $"The station sold your repair what it lacked: {what}. {message}");
            }
        );
    }

    // --- grind ---

    // ToolCore puts what it grinds into the deconstructor; the containers keep it for the end
    // True if the deconstructors held anything
    private static bool CollectGround(MyCubeGrid shipyard)
    {
        var any = false;
        var containers = Containers(shipyard).ToList();
        foreach (var tool in Deconstructors(shipyard))
        {
            var inventory = tool.GetInventory();
            if (inventory == null)
                continue;
            var items = inventory.GetItems();
            any |= items.Count > 0;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                var over = Put(containers, item.Content, item.Amount);
                if (over < item.Amount)
                    inventory.RemoveItemsAt(i, item.Amount - over);
            }
        }
        return any;
    }

    private static bool LeftToGrind(MyCubeGrid shipyard, BuildVolume volume, long player) =>
        GridsInVolume(shipyard, volume)
            .Any(g => Grindable(g, player) && g.GetBlocks().Any(b => Contains(volume, b)));

    // What was ground goes into the player's docked ships, the rest is sold for them as a turn's end does
    private static void EndGrind(
        MyCubeGrid shipyard,
        RepairSlot slot,
        string why,
        Action then = null
    )
    {
        repairs.Remove(shipyard.EntityId);
        slotVolumes.Remove(shipyard.EntityId);
        Deconstruct(shipyard, false);
        CollectGround(shipyard);
        ending.Add(slot.Id);
        var moved = 0L;
        var into = new List<string>();
        if (StationTables().TryGetValue(slot.Station, out var tables))
        {
            var ships = DockedShips(tables[0], slot.Player);
            var cargo = Cargo(ships).ToList();
            foreach (var inventory in TurnInventories(shipyard))
            {
                var items = inventory.GetItems();
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    var over = Put(cargo, item.Content, item.Amount);
                    if (over >= item.Amount)
                        continue;
                    moved += (item.Amount - over).RawValue;
                    inventory.RemoveItemsAt(i, item.Amount - over);
                }
            }
            if (moved > 0)
                into.AddRange(ships.Select(s => $"'{s.DisplayName}'"));
        }
        Plugin.Log.Info(
            $"Grind slot {slot.Id} of {Name(slot.Player)} at {shipyard.DisplayName} ended: {why}"
        );
        EndTurn(
            shipyard,
            new BuildOrder
            {
                Id = slot.Id,
                Station = slot.Station,
                Player = slot.Player,
                Ship = "Grind",
                Repair = true,
                Slot = SlotKind.Grind,
            },
            $"Your grind slot at {shipyard.DisplayName} ended: {why}."
                + (
                    moved > 0
                        ? $" What was ground went into {string.Join(", ", into.Distinct())} as far as it fit."
                        : ""
                ),
            () =>
            {
                ending.Remove(slot.Id);
                then?.Invoke();
            }
        );
    }

    // --- hack ---

    private static void Hack(MyCubeGrid shipyard, RepairSlot slot, BuildVolume volume)
    {
        var now = DateTime.UtcNow;
        var player = slot.Player;
        var faction = OctFaction.Team(player);
        var online = OnlineIdentities();
        var seen = new HashSet<long>();
        foreach (
            var group in GridsInVolume(shipyard, volume).GroupBy(g => Grids(g).Min(m => m.EntityId))
        )
        {
            var grids = Grids(group.First());
            var main = grids.OrderByDescending(g => g.BlocksCount).First();
            var name = main.DisplayName;
            var owner = main.BigOwners.FirstOrDefault();
            string why =
                main.BigOwners.Contains(player) ? null
                : !grids.All(g => Inside(volume, g))
                    ? $"'{name}' sticks out of {shipyard.DisplayName}'s build volume: move it all the way in to hack it."
                : owner != 0 && faction != null && faction.IsMember(owner)
                    ? $"'{name}' belongs to your faction, it isn't hacked."
                : owner != 0
                && !MySession.Static.Players.IdentityIsNpc(owner)
                && online.Contains(owner)
                    ? $"'{name}' isn't hacked while its owner {Name(owner)} is online."
                : "";
            if (why == null)
                continue;
            slot.LastWork = now;
            if (why.Length > 0)
            {
                if (slot.Told.Add(group.Key))
                    Tell(player, why);
                continue;
            }
            seen.Add(group.Key);
            if (!slot.Seen.TryGetValue(group.Key, out var since))
            {
                slot.Seen[group.Key] = now;
                Tell(
                    player,
                    $"Hacking '{name}': keep it in {shipyard.DisplayName}'s build volume for {Config.ShipyardHackSeconds} s."
                );
                continue;
            }
            if ((now - since).TotalSeconds < Config.ShipyardHackSeconds)
                continue;

            var identity = MySession.Static.Players.TryGetIdentity(player);
            var pcu = grids.Sum(g => g.BlocksPCU);
            var used = identity == null ? 0 : StarterShip.BuiltPcu(identity);
            var limit =
                Contracts.PcuLimitForTests
                ?? (identity == null ? 0 : MyBlockLimits.GetMaxPCU(identity));
            switch (ContractMath.Tow(pcu, used, limit, Config.GroupPcuLimit))
            {
                case ContractMath.TowResult.TooBig:
                case ContractMath.TowResult.OverLimit:
                    if (slot.Told.Add(group.Key))
                        Tell(
                            player,
                            $"'{name}' has {pcu:N0} PCU: you can't take it over with {used:N0} of your {limit:N0} PCU in use, and a ship may have at most {Config.GroupPcuLimit:N0}."
                        );
                    continue;
            }
            Plugin.Log.Info(
                $"Hack slot {slot.Id}: '{name}' ({main.EntityId}, {pcu} PCU) of {Name(owner)} goes to {Name(player)}"
            );
            ContractGrids.Give(main.EntityId, player);
            EndHack(shipyard, slot, $"'{name}' is yours now");
            return;
        }
        foreach (var gone in slot.Seen.Keys.Where(k => !seen.Contains(k)).ToList())
            slot.Seen.Remove(gone);
    }

    private static void EndHack(
        MyCubeGrid shipyard,
        RepairSlot slot,
        string why,
        Action then = null
    )
    {
        repairs.Remove(shipyard.EntityId);
        slotVolumes.Remove(shipyard.EntityId);
        ending.Add(slot.Id);
        Plugin.Log.Info(
            $"Hack slot {slot.Id} of {Name(slot.Player)} at {shipyard.DisplayName} ended: {why}"
        );
        EndTurn(
            shipyard,
            new BuildOrder
            {
                Id = slot.Id,
                Station = slot.Station,
                Player = slot.Player,
                Ship = "Hack",
                Repair = true,
                Slot = SlotKind.Hack,
            },
            $"Your hack slot at {shipyard.DisplayName} ended: {why}.",
            () =>
            {
                ending.Remove(slot.Id);
                then?.Invoke();
            }
        );
    }

    // Online anywhere on the cluster, or on this server
    private static HashSet<long> OnlineIdentities()
    {
        var cluster = Plugin.Cluster?.OnlinePlayers();
        if (cluster != null)
            return cluster.Select(p => p.IdentityId).ToHashSet();
        return MySession
            .Static.Players.GetOnlinePlayers()
            .Where(p => p.Identity != null)
            .Select(p => p.Identity.IdentityId)
            .ToHashSet();
    }
}
