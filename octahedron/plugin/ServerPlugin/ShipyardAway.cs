using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ServerPlugin;

// A shipyard's job goes on when its player logs off or moves to another node
internal static class ShipyardAway
{
    private const string TellTopic = "shipyard/tell";

    private static bool patched;
    private static IDisposable teller;

    // The players with a running job here who aren't connected to this node
    private static readonly HashSet<ulong> away = [];

    // The cluster's plugin may load after this one, so this waits for the first update
    public static void Update()
    {
        if (!patched)
        {
            patched = true;
            StayOnline();
        }
        if (
            teller == null
            && Plugin.Cluster != null
            && PluginCluster.Current != null
            && MySession.Static?.Ready == true
        )
            teller = Plugin.Cluster.RegisterHandler(TellTopic, Told);
    }

    // --- the cluster keeps the partition online ---

    // A cluster takes a partition offline once no player is within 8 km and its grids are at rest,
    // and a shipyard and the ship on it are static. Nothing runs there until someone comes back, so
    // a build would stop when its player leaves, and run out of its time. A shipyard with something
    // in its queue is a reason to stay online, next to the cluster's own. The SDK has no way to say
    // so, hence the name.
    private static void StayOnline()
    {
        if (Plugin.Cluster == null)
            return;
        var blocker = AccessTools
            .TypeByName("ClusterPlugin.PartitionPersistence")
            ?.GetMethod("OfflineMemberBlocker", BindingFlags.Static | BindingFlags.NonPublic);
        if (blocker == null)
        {
            Plugin.Log.Warning(
                "No OfflineMemberBlocker in the cluster's plugin: shipyard jobs stop when their partition goes offline"
            );
            return;
        }
        new Harmony(Plugin.Name + ".ShipyardAway").Patch(
            blocker,
            postfix: new HarmonyMethod(typeof(ShipyardAway), nameof(Blocker))
        );
    }

    private static void Blocker(MyEntity entity, ref string __result)
    {
        if (__result == null && entity is MyCubeGrid && ShipyardOrders.Busy(entity.EntityId))
            __result = "shipyard";
    }

    // --- their welders keep placing DLC blocks ---

    /// <summary>Takes note of whose welders work for someone who is away. Called with the players of the running paid builds and repair slots.</summary>
    public static void Working(IEnumerable<long> players)
    {
        away.Clear();
        foreach (var player in players)
            if (
                !MySession.Static.Players.IsPlayerOnline(player)
                && MySession.Static.Players.TryGetSteamId(player) is var steamId and not 0
            )
                away.Add(steamId);
    }

    // Steam answers about a player's DLCs only while they are connected. A node that was restarted,
    // or got the shipyard's partition, would hear no for a player who is elsewhere, and ToolCore
    // skips the blocks that its welder's owner doesn't own. Nobody can place a block by hand from
    // here without being connected, so this reaches no further than their welders.
    [HarmonyPatch(
        typeof(MySessionComponentDLC),
        nameof(MySessionComponentDLC.HasDLC),
        typeof(string),
        typeof(ulong)
    )]
    private static class HasDlcPatch
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(ulong steamId, ref bool __result)
        {
            if (!__result && away.Contains(steamId))
                __result = true;
        }
    }

    // --- they are told where they are ---

    /// <summary>A chat message to a player, on the node they are on. One who is logged off gets none.</summary>
    public static void Tell(long player, string text)
    {
        if (Plugin.Cluster == null || MySession.Static.Players.IsPlayerOnline(player))
        {
            MyVisualScriptLogicProvider.SendChatMessage(text, "Shipyard", player);
            return;
        }
        _ = Plugin.Cluster.BroadcastAsync(
            TellTopic,
            Encoding.UTF8.GetBytes($"{player}\n{text}"),
            Guid.NewGuid(),
            TimeSpan.FromSeconds(30),
            includeWorldAuthority: false
        );
    }

    private static Task<byte[]> Told(PluginMessage message)
    {
        var parts = Encoding.UTF8.GetString(message.Payload).Split('\n', 2);
        if (long.Parse(parts[0]) is var player && MySession.Static.Players.IsPlayerOnline(player))
            MyVisualScriptLogicProvider.SendChatMessage(parts[1], "Shipyard", player);
        return Task.FromResult<byte[]>([]);
    }
}
