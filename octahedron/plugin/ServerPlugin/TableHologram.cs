using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRageMath;

namespace ServerPlugin;

// Fits the blueprint loaded into a station hologram table into the free space above the
// table's top, within the table's own block: scaled to fit, centered, and turned so that its main grid's cockpit points the table's way.
// The game only scales a blueprint to fit the 2.5 m block and keeps the grid origin at the
// block's center. Set through the projector's synced offset, rotation and scale, which the
// server broadcasts to the clients.
internal static class TableHologram
{
    private static readonly Dictionary<MyProjectorBase, List<MyObjectBuilder_CubeGrid>> pending =
        new();

    internal static readonly AccessTools.FieldRef<
        MyProjectorBase,
        List<MyObjectBuilder_CubeGrid>
    > Blueprint = AccessTools.FieldRefAccess<MyProjectorBase, List<MyObjectBuilder_CubeGrid>>(
        "m_originalGridBuilders"
    );

    // A client loading a blueprint ends up here on the server too. Fitted on the next update,
    // after the game has passed the new blueprint on to the other clients.
    [HarmonyPatch(typeof(MyProjectorBase), "SetNewBlueprint")]
    private static class SetNewBlueprint
    {
        private static void Postfix(
            MyProjectorBase __instance,
            List<MyObjectBuilder_CubeGrid> gridBuilders
        )
        {
            if (Sync.IsServer)
                pending[__instance] = gridBuilders;
        }
    }

    public static void Update()
    {
        if (pending.Count == 0)
            return;
        var tables = new List<KeyValuePair<MyProjectorBase, List<MyObjectBuilder_CubeGrid>>>(
            pending
        );
        pending.Clear();
        foreach (var entry in tables)
            Fit(entry.Key, entry.Value);
    }

    // Fits every station table that holds a blueprint, for example after a config change
    public static int FitAll()
    {
        var count = 0;
        foreach (var entity in MyEntities.GetEntities())
        {
            if (entity is not MyCubeGrid grid)
                continue;
            foreach (var table in grid.GetFatBlocks<MyProjectorBase>())
            {
                if (Fit(table, Blueprint(table)))
                    count++;
            }
        }
        return count;
    }

    private static bool Fit(MyProjectorBase table, List<MyObjectBuilder_CubeGrid> grids)
    {
        // Not gated on the starter ship: the shipyard quotes ships on the same tables
        var config = Plugin.Config;
        if (
            !config.Enabled
            || grids == null
            || grids.Count == 0
            || table.MarkedForClose
            || !table.AllowScaling
            || !ShipyardOrders.IsTable(table)
        )
            return false;

        var main = grids[0];
        var bounds = Bounds(grids);

        // The cockpit's forward and up become the table's
        var (forward, up) = StarterShip.CockpitFrame(main);
        var (rotation, turn) = TableRotation(MatrixD.CreateWorld(Vector3D.Zero, forward, up));

        // The free space is the table block's own cell above the table's top: the cell above
        // may be taken (the stations have a column there). Scaled to fit it, margin included.
        var cell = table.CubeGrid.GridSize;
        var margin = config.StarterShipHologramMargin;
        var top = config.StarterShipHologramTableTop;
        var free = new Vector3D(cell - 2 * margin, cell / 2 - top - 2 * margin, cell - 2 * margin);
        var size = Vector3D.Abs(Vector3D.TransformNormal(bounds.Size, turn));
        var scale = Math.Min(
            1,
            Math.Min(free.X / size.X, Math.Min(free.Y / size.Y, free.Z / size.Z))
        );
        var center = new Vector3D(0, (top + cell / 2) / 2, 0);

        // The game moves the main grid's origin by -offset * cube size * scale in the table's
        // frame, so the offset that puts the blueprint's center at the free space's center is
        // (center turned - free space center / scale) / cube size, in whole cubes
        var cubeSize = MyDefinitionManager.Static.GetCubeSize(main.GridSizeEnum);
        var cells = (Vector3D.TransformNormal(bounds.Center, turn) - center / scale) / cubeSize;
        var offset = Vector3I.Clamp(Vector3I.Round(cells), new Vector3I(-50), new Vector3I(50));

        table.SendNewOffset(
            offset,
            rotation,
            (float)scale,
            ((IMyProjector)table).ShowOnlyBuildable
        );
        return true;
    }

    // The blueprint's bounds in its main grid's frame, all grids included
    internal static BoundingBoxD Bounds(List<MyObjectBuilder_CubeGrid> grids)
    {
        var toMain = MatrixD.Invert(GridMatrix(grids[0]));
        var bounds = BoundingBoxD.CreateInvalid();
        foreach (var grid in grids)
        {
            var toFrame = GridMatrix(grid) * toMain;
            var box = (BoundingBoxD)grid.CalculateBoundingBox();
            for (var i = 0; i < 8; i++)
                bounds.Include(Vector3D.Transform(box.GetCorner(i), toFrame));
        }
        return bounds;
    }

    private static MatrixD GridMatrix(MyObjectBuilder_CubeGrid grid) =>
        grid.PositionAndOrientation?.GetMatrix() ?? MatrixD.Identity;

    // The projector's rotation, whole degrees of yaw, pitch and roll, that turns the given
    // frame onto the table's forward and up, and the turn as a matrix. Cockpit frames are
    // turned by right angles, so a search over those finds it.
    private static (Vector3I Degrees, MatrixD Turn) TableRotation(MatrixD frame)
    {
        var wanted = MatrixD.Transpose(frame);
        int[] angles = [0, 90, 180, -90];
        foreach (var yaw in angles)
        foreach (var pitch in angles)
        foreach (var roll in angles)
        {
            var turn = MatrixD.CreateFromQuaternion(
                Quaternion.CreateFromYawPitchRoll(
                    MathHelper.ToRadians(yaw),
                    MathHelper.ToRadians(pitch),
                    MathHelper.ToRadians(roll)
                )
            );
            if (
                Vector3D.Dot(turn.Forward, wanted.Forward) > 0.99
                && Vector3D.Dot(turn.Up, wanted.Up) > 0.99
            )
                return (new Vector3I(yaw, pitch, roll), turn);
        }
        return (Vector3I.Zero, MatrixD.Identity);
    }
}
