using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ServerPlugin;

// Players don't grind OCT's grids or the trader bots' ships for components to sell (SE-1160). The
// stations' safe zones forbid grinding, but nothing stopped it anywhere else. A grid is OCT's when
// an NPC of OCT's faction owns most of it, as a station's NPC does, and a bot's when its owner's
// Steam ID is in OctProtectedSteamIds; a subgrid counts with the grids it is mechanically joined
// to. Every grinder takes the block apart through DecreaseMountLevel on the server: a hand
// grinder, a ship grinder and ToolCore's tools. Only the owner gets through.
internal static class OctGrids
{
    private static readonly SteamIdRanges Bots = new(
        nameof(OctahedronConfig.OctProtectedSteamIds),
        () => Plugin.Config.OctProtectedSteamIds
    );

    private static bool Protected(long owner)
    {
        var players = MySession.Static.Players;
        if (players.IdentityIsNpc(owner))
            return OctFaction.Faction?.IsMember(owner) == true;
        return Bots.Contains(players.TryGetSteamId(owner));
    }

    private static readonly List<MyCubeGrid> joined = [];

    /// <summary>Whose protected grid this is, 0 when anyone may grind it</summary>
    public static long Guard(MyCubeGrid grid)
    {
        joined.Clear();
        MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, joined);
        return joined.SelectMany(g => g.BigOwners).FirstOrDefault(Protected);
    }

    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.DecreaseMountLevel))]
    private static class Grinding
    {
        private static bool Prefix(MySlimBlock __instance, long identityId)
        {
            if (
                !Sync.IsServer
                || !Plugin.Config.Enabled
                || !Plugin.Config.OctGridsNoGrinding
                || __instance.CubeGrid is not { } grid
            )
                return true;
            var owner = Guard(grid);
            if (owner == 0 || owner == identityId)
                return true;
            // ToolCore's tools grind without a player
            if (identityId != 0)
                GroupPcuLimit.Notify(
                    identityId,
                    $"{grid.DisplayName} belongs to {MySession.Static.Players.TryGetIdentity(owner)?.DisplayName ?? "OCT"}: nobody else can grind it."
                );
            return false;
        }
    }
}
