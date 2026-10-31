using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Commands;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRageMath;

namespace ServerPlugin;

// Builds the blueprint loaded into a station's hologram table as a new ship for the player
// who asks, outside the station's safe zone, facing away from it.
internal static class StarterShip
{
    private static readonly Dictionary<long, DateTime> lastShip = new();

    public static CommandReply Request(long identityId, string tableName)
    {
        var config = Plugin.Config;
        if (!config.StarterShipEnabled)
            return CommandReply.Error("Starter ships are off on this server.");

        var identity = MySession.Static.Players.TryGetIdentity(identityId);
        var character = identity?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return CommandReply.Error("Spawn first, then ask at a hologram table.");

        var now = DateTime.UtcNow;
        var exempt = FreePrints.Exempt(identityId);
        if (!exempt && lastShip.TryGetValue(identityId, out var last))
        {
            var wait = last.AddMinutes(config.StarterShipCooldownMinutes) - now;
            if (wait > TimeSpan.Zero)
                return CommandReply.Error(
                    $"Your next starter ship is ready in {Math.Ceiling(wait.TotalMinutes)} min."
                );
        }

        var owned = PlayerPcu(identity);
        if (!exempt && owned >= config.FreeBuildPcuLimit)
            return CommandReply.Error(
                $"You have {owned:N0} PCU, ships in Hangar included. Starter ships are for players under "
                    + $"{config.FreeBuildPcuLimit:N0} PCU; build your ship at the station's shipyard."
            );

        var table = FindTable(character, tableName, config);
        if (table == null)
            return CommandReply.Error(
                tableName == null
                    ? $"Stand within {config.StarterShipTableReach:0} m of a station's hologram table."
                    : $"No station hologram table named '{tableName}' within {config.StarterShipTableReach:0} m."
            );
        if (!Plugin.Simulated(table.CubeGrid))
            return CommandReply.Error("The station is busy, try again in a few seconds.");

        var builder = (MyObjectBuilder_ProjectorBase)table.GetObjectBuilderCubeBlock(true);
        var grids = builder.ProjectedGrids;
        if (grids == null || grids.Count == 0)
            return CommandReply.Error(
                "Switch the hologram table on and load a blueprint into it first."
            );
        if (grids.Count != 1)
            return CommandReply.Error(
                $"The blueprint has {grids.Count} grids, a starter ship is a single grid."
            );

        // A saved projection is the projector's own list, so it is never changed in place
        var grid = (MyObjectBuilder_CubeGrid)grids[0].Clone();
        var pcu = grid.CalculatePCUs();
        var maxPcu =
            config.GroupPcuLimit > 0
                ? Math.Min(config.StarterShipMaxShipPcu, config.GroupPcuLimit)
                : config.StarterShipMaxShipPcu;
        if (pcu > maxPcu)
            return CommandReply.Error(
                $"The blueprint has {pcu:N0} PCU, a starter ship at most {maxPcu:N0}."
            );
        if (!Place(grid, table, config))
            return CommandReply.Error(
                "There is no free space for the ship outside the station, try again later."
            );
        Prepare(grid, identityId);
        if (MyEntities.CreateFromObjectBuilderAndAdd(grid, true) is not MyCubeGrid ship)
            return CommandReply.Error("The ship could not be built.");
        if (config.StarterShipChargeBatteries)
        {
            foreach (var battery in ship.GetFatBlocks<MyBatteryBlock>())
                battery.CurrentStoredPower = battery.MaxStoredPower;
        }

        lastShip[identityId] = now;
        // A lasting, always shown cyan GPS for this player only, sent to their client
        var gps = (MyGps)
            MyAPIGateway.Session.GPS.Create(
                ship.DisplayName,
                "Your starter ship",
                ship.PositionComp.WorldAABB.Center,
                showOnHud: true,
                temporary: false
            );
        gps.GPSColor = Color.Cyan;
        gps.AlwaysVisible = true;
        MyAPIGateway.Session.GPS.AddGps(identityId, gps);
        Plugin.Log.Info(
            $"Starter ship '{ship.DisplayName}' ({pcu} PCU, {ship.EntityId}) for {identity.DisplayName} "
                + $"from {table.CustomName} of {table.CubeGrid.DisplayName}"
                + (exempt ? ", ignoring PCU limits: no cool-down or FreeBuildPcuLimit" : "")
        );
        return CommandReply.Ok(
            $"Your ship '{ship.DisplayName}' ({pcu:N0} PCU) waits outside the station, see your GPS."
        );
    }

    // The nearest NPC-owned hologram table within reach, of the given name if there is one
    internal static MyProjectorBase FindTable(
        MyCharacter character,
        string name,
        OctahedronConfig config
    )
    {
        var position = character.PositionComp.GetPosition();
        var reach = config.StarterShipTableReach;
        var sphere = new BoundingSphereD(position, reach);
        MyProjectorBase best = null;
        var bestDistance = double.MaxValue;
        // The game's shared result list, cleared after use as the game asks
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        foreach (var entity in entities)
        {
            if (entity is not MyCubeGrid grid)
                continue;
            foreach (var table in grid.GetFatBlocks<MyProjectorBase>())
            {
                if (
                    table.BlockDefinition.Id.SubtypeName != config.StarterShipTableSubtype
                    || !MySession.Static.Players.IdentityIsNpc(table.OwnerId)
                    || (
                        name != null
                        && !string.Equals(
                            table.CustomName?.ToString().Trim(),
                            name.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                    continue;
                var distance = Vector3D.Distance(table.PositionComp.GetPosition(), position);
                if (distance <= reach && distance < bestDistance)
                {
                    best = table;
                    bestDistance = distance;
                }
            }
        }
        entities.Clear();
        return best;
    }

    // Puts the ship's center past the station's safe zone, on the table's side of the station
    // if there is room, its cockpit facing away from the station and its up the station's up
    private static bool Place(
        MyObjectBuilder_CubeGrid grid,
        MyProjectorBase table,
        OctahedronConfig config
    )
    {
        var station = table.CubeGrid;
        var tablePosition = table.PositionComp.GetPosition();
        var (center, radius) = StationBounds(station, tablePosition);
        var box = grid.CalculateBoundingBox();
        var shipRadius = box.HalfExtents.Length();
        var stationUp = station.WorldMatrix.Up;
        var away = Vector3D.Normalize(tablePosition - center);
        if (!away.IsValid())
            away = station.WorldMatrix.Forward;
        var (cockpitForward, cockpitUp) = CockpitFrame(grid);
        var cockpit = MatrixD.CreateWorld(Vector3D.Zero, cockpitForward, cockpitUp);

        // Straight out from the table first, then turning about the station's up either way
        foreach (var degrees in new[] { 0, 45, -45, 90, -90, 135, -135, 180 })
        {
            var direction = Vector3D.Transform(
                away,
                MatrixD.CreateFromAxisAngle(stationUp, MathHelper.ToRadians(degrees))
            );
            var target = center + direction * (radius + config.StarterShipClearance + shipRadius);
            var free = MyEntities.FindFreePlaceCustom(target, (float)shipRadius);
            if (free == null || Vector3D.Distance(free.Value, center) < radius + shipRadius)
                continue;

            var up = Vector3D.Reject(stationUp, direction);
            if (up.LengthSquared() < 1e-6)
                up = Vector3D.CalculatePerpendicularVector(direction);
            var world =
                MatrixD.Transpose(cockpit)
                * MatrixD.CreateWorld(Vector3D.Zero, direction, Vector3D.Normalize(up));
            var origin = free.Value - Vector3D.TransformNormal(box.Center, world);
            grid.PositionAndOrientation = new MyPositionAndOrientation(
                origin,
                (Vector3)world.Forward,
                (Vector3)world.Up
            );
            return true;
        }
        return false;
    }

    // The safe zone the table is in, or the station grid's bounding sphere without one
    private static (Vector3D Center, double Radius) StationBounds(
        MyCubeGrid station,
        Vector3D tablePosition
    )
    {
        foreach (var zone in MySessionComponentSafeZones.SafeZones)
        {
            if (!zone.Enabled || zone.MarkedForClose || !zone.Contains(tablePosition))
                continue;
            var radius =
                zone.Shape == MySafeZoneShape.Sphere ? zone.Radius : zone.Size.Length() / 2;
            return (zone.PositionComp.GetPosition(), radius);
        }
        var sphere = station.PositionComp.WorldVolume;
        return (sphere.Center, sphere.Radius);
    }

    // The forward and up of the main cockpit, else of any cockpit, control seat or remote
    // control, in grid coordinates; the grid's own without any
    internal static (Vector3D Forward, Vector3D Up) CockpitFrame(MyObjectBuilder_CubeGrid grid)
    {
        MyObjectBuilder_CubeBlock found = null;
        foreach (var block in grid.CubeBlocks)
        {
            if (block is MyObjectBuilder_ShipController { IsMainCockpit: true })
            {
                found = block;
                break;
            }
            if (found == null && block is MyObjectBuilder_ShipController)
                found = block;
        }
        if (found == null)
            return (Vector3D.Forward, Vector3D.Up);
        var orientation = found.BlockOrientation;
        return (
            Base6Directions.GetVector(orientation.Forward),
            Base6Directions.GetVector(orientation.Up)
        );
    }

    // Built empty, as a projector would: no cargo, fuel, gas or charge, nobody seated. The
    // player owns it and it counts toward their PCU.
    private static void Prepare(MyObjectBuilder_CubeGrid grid, long identityId)
    {
        grid.SetupForProjector();
        grid.SetupForGridPaste();
        grid.IsStatic = false;
        grid.LinearVelocity = Vector3.Zero;
        grid.AngularVelocity = Vector3.Zero;
        foreach (var block in grid.CubeBlocks)
        {
            block.Owner = identityId;
            block.BuiltBy = identityId;
            TargetNeutrals.Clear(block);
        }
        MyEntities.RemapObjectBuilder(grid);
    }

    // The player's PCU: built blocks on this node and, on a cluster, on the other nodes (the
    // cluster plugin's ledger), plus the ships the player keeps in Hangar. The ledger knows only
    // the grids some node has loaded, not those in offline partitions.
    internal static int PlayerPcu(MyIdentity identity) =>
        BuiltPcu(identity) + HangarPcu(MySession.Static.Players.TryGetSteamId(identity.IdentityId));

    // Without Hangar: what the game's limit counts
    internal static int BuiltPcu(MyIdentity identity)
    {
        var pcu = identity.BlockLimits.PCUBuilt;
        if (RemoteUsage.Value != null)
            pcu += (
                (ValueTuple<int, int>)RemoteUsage.Value.Invoke(null, [identity.IdentityId])
            ).Item1;
        return pcu;
    }

    private static readonly Lazy<MethodInfo> RemoteUsage = new(() =>
        AccessTools.TypeByName("ClusterPlugin.PcuLedgerService")?.GetMethod("RemoteUsage")
    );

    // Hangar's public Plugin.Instance.Storage.ListPlayerEntries, found by reflection so the
    // plugin works without Hangar. Hangar's types share our namespace and names, hence the lookup
    // through its storage type's assembly.
    private static int HangarPcu(ulong steamId)
    {
        var storageType = AccessTools.TypeByName("ServerPlugin.HangarStorage");
        var hangar = storageType
            ?.Assembly.GetType("ServerPlugin.Plugin")
            ?.GetProperty("Instance")
            ?.GetValue(null);
        var storage = hangar?.GetType().GetProperty("Storage")?.GetValue(hangar);
        if (
            storage == null
            || storageType.GetMethod("ListPlayerEntries")?.Invoke(storage, [steamId])
                is not IEnumerable entries
        )
            return 0;
        var pcu = 0;
        foreach (var entry in entries)
            pcu += (int)entry.GetType().GetProperty("Pcu").GetValue(entry);
        return pcu;
    }
}
