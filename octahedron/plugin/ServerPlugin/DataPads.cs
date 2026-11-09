using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ServerPlugin;

// The lore data pads of DataPads in the config, given out at random by a refill rule whose item
// is Datapad/Datapad (SE-1308): the Free Tools boxes hold scraps of the ore distribution and the
// coordinates of the ring's ice asteroids. A coordinate comes from the voxel map the world has
// loaded, so it's right for whatever world the server runs; every cluster node loads them all.
internal static class DataPads
{
    private static readonly Regex Gps = new(@"\{GPS:([^:}]+):([^}]+)\}");
    private static readonly HashSet<string> reportedMissing = new();

    public static bool IsDataPad(MyDefinitionId id) => id.TypeId == typeof(MyObjectBuilder_Datapad);

    // Adds pads not yet in the inventory until it holds minimum of ours; players' own pads don't count
    public static void TopUp(MyInventory inventory, int minimum)
    {
        var pads = Plugin.Config.DataPads;
        if (pads == null || pads.Count == 0)
            return;
        var titles = new HashSet<string>(pads.Select(p => p.Title));
        var held = new HashSet<string>();
        MyFixedPoint count = 0;
        foreach (var item in inventory.GetItems())
        {
            if (item.Content is MyObjectBuilder_Datapad pad && titles.Contains(pad.Name))
            {
                held.Add(pad.Name);
                count += item.Amount;
            }
        }
        var candidates = new List<(string Title, string Text)>();
        foreach (var pad in pads)
        {
            if (!held.Contains(pad.Title) && TryResolve(pad.Text ?? "", out var text))
                candidates.Add((pad.Title, text));
        }
        for (; count < minimum && candidates.Count > 0; count += 1)
        {
            var i = MyUtils.GetRandomInt(candidates.Count);
            var content = new MyObjectBuilder_Datapad
            {
                SubtypeName = "Datapad",
                Name = candidates[i].Title,
                Data = candidates[i].Text,
            };
            candidates.RemoveAt(i);
            inventory.AddItems(1, content);
        }
    }

    public static bool TryResolve(string text, out string resolved)
    {
        var ok = true;
        resolved = Gps.Replace(
            text,
            m =>
            {
                var storage = m.Groups[1].Value;
                var voxel = MySession.Static.VoxelMaps.TryGetVoxelMapByName(storage);
                if (voxel == null)
                {
                    ok = false;
                    if (reportedMissing.Add(storage))
                        Plugin.Log.Warning($"Data pads: no voxel map {storage} in the world");
                    return m.Value;
                }
                var c = Vector3D.Round(voxel.PositionComp.WorldAABB.Center, 0);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "GPS:{0}:{1}:{2}:{3}:",
                    m.Groups[2].Value,
                    c.X,
                    c.Y,
                    c.Z
                );
            }
        );
        return ok;
    }
}
