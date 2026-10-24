using System;
using HarmonyLib;
using Sandbox.Game.World;

namespace ServerPlugin;

// The MES spawn gate, folded in from MesCluster (CometWorks/mod-mes, DF-582): patches Modular
// Encounters Systems once the session has loaded, so its spawns follow the cell pattern and the
// NPC density limits, and publishes the gap bands for the cluster's NPC sweep.
internal static class MesCluster
{
    // The cluster's sweep reads it to remove unvisited NPC grids in the gap bands sooner (DF-611)
    internal const string InGapSlot = "CometWorks.ModMes.InGap.v1";

    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(10);

    private static bool installed;
    private static DateTime nextStats;

    public static void Init() =>
        AppDomain.CurrentDomain.SetData(InGapSlot, (Func<double, double, double, bool>)InGap);

    public static void Update()
    {
        if (!installed && MySession.Static is { Ready: true })
        {
            installed = true;
            try
            {
                MesSpawnGate.Install(new Harmony(Plugin.Name + ".MesCluster"), Plugin.Config);
            }
            catch (Exception e)
            {
                Plugin.Log.Critical("Patching MES failed; MES runs UNGATED", e);
            }
            nextStats = DateTime.UtcNow + StatsInterval;
        }

        if (MesSpawnGate.Active && DateTime.UtcNow >= nextStats)
        {
            nextStats = DateTime.UtcNow + StatsInterval;
            var (online, all) = NpcCensus.Totals();
            Plugin.Log.Info(
                $"spawns since start: placed {MesSpawnGate.Accepted}, moved into a core {MesSpawnGate.Moved}, "
                    + $"refused: pattern {MesSpawnGate.RefusedPattern}, density {MesSpawnGate.RefusedDensity}, "
                    + $"cluster cap {MesSpawnGate.RefusedCluster}, census unknown {MesSpawnGate.RefusedUnknown}; "
                    + $"census {(NpcCensus.ClusterWide ? "cluster-wide" : "this process only")}: "
                    + $"NPC grids {all}, online {(online < 0 ? "n/a" : online.ToString())}"
            );
        }
    }

    public static void Dispose() => AppDomain.CurrentDomain.SetData(InGapSlot, null);

    private static bool InGap(double x, double y, double z) =>
        MesSpawnGate.Active && MesSpawnGate.Pattern is { } pattern && pattern.InGap(x, y, z);
}
