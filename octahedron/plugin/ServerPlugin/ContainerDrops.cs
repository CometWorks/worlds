using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRageMath;

namespace ServerPlugin;

// The game's drop pods (EnableContainerDrops) come in two kinds. Each due drop is a personal
// "Unknown Signal" (Container_MK-1..26), only for its player, or with a 5 % chance in
// multiplayer a "Strong Unknown Signal" (LargeContainer_Mk-1..15) everyone sees. Personal
// ones spawn wherever the players fly and chain the cluster's partitions together (#1389).
// With ContainerDropsPersonal off the game still rolls the kind, but a personal drop finds
// no spawn position, so the game counts it as not spawned; strong drops are left alone.
internal static class ContainerDrops
{
    private static readonly MethodInfo SpawnInternal = AccessTools.Method(
        typeof(MySessionComponentContainerDropSystem),
        "SpawnContainerDropInternal"
    );
    private static readonly FieldInfo PlayerData = AccessTools.Field(
        typeof(MySessionComponentContainerDropSystem),
        "m_playerData"
    );
    private static readonly Type PlayerContainerData = AccessTools.Inner(
        typeof(MySessionComponentContainerDropSystem),
        "MyPlayerContainerData"
    );
    private static readonly FieldInfo PlayerId = AccessTools.Field(PlayerContainerData, "PlayerId");

    [HarmonyPatch(typeof(MySessionComponentContainerDropSystem), "FindNewSpawnPosition")]
    private static class SpawnPosition
    {
        private static bool Prefix(
            bool personal,
            ref bool validSpawn,
            ref MyPlanet planet,
            ref Vector3D __result
        )
        {
            if (!personal || !Plugin.Config.Enabled || Plugin.Config.ContainerDropsPersonal)
                return true;
            validSpawn = false;
            planet = null;
            __result = Vector3D.Zero;
            Plugin.Log.Info("Personal container drop (Unknown Signal) refused");
            return false;
        }
    }

    // !oct drop: one drop for the caller, of the kind asked, the way the game's timer spawns it
    internal static string Spawn(long identityId, bool strong)
    {
        var system = MySession.Static.GetComponent<MySessionComponentContainerDropSystem>();
        if (system == null)
            return "The container drop system is not loaded.";
        var players = (IList)PlayerData.GetValue(system);
        object data = null;
        foreach (var entry in players)
            if ((long)PlayerId.GetValue(entry) == identityId)
                data = entry;
        if (data == null)
        {
            data = Activator.CreateInstance(PlayerContainerData, identityId, 0, true, strong, 0L);
            players.Add(data);
        }
        // (playerData, forceCompetitiveSpawn, out spawnedCompetitive, allowCompetitive, chosenContainer)
        var args = new object[] { data, strong, false, strong, null };
        var spawned = (bool)SpawnInternal.Invoke(system, args);
        var kind = strong ? "Strong Unknown Signal" : "Unknown Signal";
        Plugin.Log.Info($"!oct drop {kind} for identity {identityId}: spawned={spawned}");
        return spawned ? $"{kind} dropped." : $"No {kind} dropped.";
    }
}
