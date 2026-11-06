using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace ServerPlugin;

// Every new player joins OCT's faction, so players are allies of each other and of the stations
// (SE-975). The game shares whatever a player builds with their faction, which in OCT is every
// player: a block an OCT member places, welds to working order or gets from a shipyard starts as
// "No share" instead. Players in other factions or none keep the game's rule, and anyone can
// share a block later.
//
// Placing a block runs on every machine, and each one gives it faction share there, so the server
// follows up with the game's own ownership change, which reaches every client. It waits a frame:
// the server tells the clients about the block only after building it, and a client drops a change
// to a block it doesn't have yet. Welding a block to working order sets its owner on the server
// only, so the share is changed before that.
//
// On a cluster the World Authority makes a new player's identity when the Gateway admits them,
// and the node they land on gets it ready-made, so the game's RealPlayerIdentityCreated, which
// fires only for an identity a server makes for a joining player itself, fires nowhere. The World
// Authority joins them instead (SE-1012).
//
// OCT's leaders are the players OctLeaders names, Viktor on the Octahedron (SE-1015). The game makes
// an admin who joins a faction of NPCs its leader, so on a fresh world the first admin to join would
// lead OCT. Every other player is demoted right after joining. The NPC stays OCT's founder.
//
// Only a leader can take money out of OCT's account, so a member's deposit would be money gone
// (SE-1160). The game's faction screen offers every member the Deposit button and only a client
// could grey it out, so the server refuses the deposit and tells the player why.
internal static class OctFaction
{
    private static MyPlayerCollection bound;

    // Identities the World Authority made for new players this frame
    private static readonly List<long> created = [];

    // Blocks placed this frame, with who placed them
    private static readonly List<(MySlimBlock Block, long Owner)> placed = [];

    public static MyFaction Faction =>
        MySession.Static?.Factions.TryGetFactionByTag(Plugin.Config.MoneyAuthorityFactionTag);

    /// <summary>A player, not an NPC, in OCT's faction</summary>
    public static bool Member(long identity) =>
        identity != 0
        && !MySession.Static.Players.IdentityIsNpc(identity)
        && Faction?.IsMember(identity) == true;

    /// <summary>The player's faction for telling teammates apart: none for OCT, which every player is in</summary>
    public static MyFaction Team(long identity)
    {
        var faction = MySession.Static.Factions.GetPlayerFaction(identity);
        return faction != null && faction == Faction ? null : faction;
    }

    /// <summary>The share of what the player builds or gets built: no share in OCT, the faction's otherwise</summary>
    public static MyOwnershipShareModeEnum BuildShare(long identity) =>
        Plugin.Config.Enabled && Plugin.Config.OctBuildsPrivate && Member(identity)
            ? MyOwnershipShareModeEnum.None
            : MyOwnershipShareModeEnum.Faction;

    public static void Bind()
    {
        var players = MySession.Static?.Players;
        if (players == null || players == bound || MySession.Static?.Ready != true)
            return;
        bound = players;
        players.RealPlayerIdentityCreated += Join;
    }

    // The game's join request and its acceptance, sent as the server, which may make both: what a
    // scenario's medical room does for a spawning player. OCT accepts no request of its own.
    private static void Join(long identity)
    {
        var oct = Faction;
        if (
            !Plugin.Config.Enabled
            || !Plugin.Config.NewPlayersJoinOct
            || oct == null
            || MySession.Static.Factions.GetPlayerFaction(identity) != null
        )
            return;
        MyFactionCollection.SendJoinRequest(oct.FactionId, identity);
        MyFactionCollection.AcceptJoin(oct.FactionId, identity);
        var leader = Leaders.Contains(MySession.Static.Players.TryGetSteamId(identity));
        if (leader && !oct.IsLeader(identity))
            MyFactionCollection.PromoteMember(oct.FactionId, identity);
        else if (!leader && oct.IsLeader(identity))
            MyFactionCollection.DemoteMember(oct.FactionId, identity);
        Plugin.Log.Info(
            $"New player {identity} joins {oct.Tag} as {(oct.IsLeader(identity) ? "leader" : "member")}"
        );
    }

    private static HashSet<ulong> Leaders =>
        Plugin
            .Config.OctLeaders.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => ulong.TryParse(id.Trim(), out var steamId) ? steamId : 0)
            .ToHashSet();

    public static void Update()
    {
        // A frame later, when the identity has reached the nodes
        foreach (var identity in created)
            Join(identity);
        created.Clear();
        foreach (var (block, owner) in placed)
            Private(block, owner);
        placed.Clear();
    }

    private static void Placed(MySlimBlock block, long owner)
    {
        if (block != null && Sync.IsServer && BuildShare(owner) == MyOwnershipShareModeEnum.None)
            placed.Add((block, owner));
    }

    private static void Private(MySlimBlock block, long owner)
    {
        if (
            block.FatBlock is not { } fat
            || fat.Closed
            || fat.OwnerId != owner
            || fat.IDModule?.ShareMode != MyOwnershipShareModeEnum.Faction
            || BuildShare(owner) != MyOwnershipShareModeEnum.None
        )
            return;
        fat.CubeGrid.ChangeOwnerRequest(fat.CubeGrid, fat, owner, MyOwnershipShareModeEnum.None);
    }

    // The Gateway's admission asks for an initial player's identity, which nothing else on the World
    // Authority does. A node asks it for the identities it makes, so only the World Authority counts.
    [HarmonyPatch(
        typeof(MyPlayerCollection),
        nameof(MyPlayerCollection.CreateNewIdentity),
        [typeof(string), typeof(string), typeof(Vector3?), typeof(bool), typeof(bool)]
    )]
    private static class Created
    {
        private static void Postfix(MyIdentity __result, bool initialPlayer, bool addToNpcs)
        {
            if (
                initialPlayer
                && !addToNpcs
                && __result != null
                && Plugin.Cluster != null
                && Stations.IsAuthority
            )
                created.Add(__result.IdentityId);
        }
    }

    // A block placed by hand or welded from a projection, and many placed at once
    [HarmonyPatch(typeof(MyCubeGrid), "BuildBlockSuccess")]
    private static class PlacedOne
    {
        private static void Postfix(MySlimBlock __result, long ownerId) =>
            Placed(__result, ownerId);
    }

    [HarmonyPatch(typeof(MyCubeGrid), "BuildBlocksSuccess")]
    private static class PlacedMany
    {
        private static void Postfix(
            MyCubeGrid __instance,
            HashSet<MyCubeGrid.MyBlockLocation> resultBlocks,
            long ownerId
        )
        {
            foreach (var location in resultBlocks)
                Placed(__instance.GetCubeBlock(location.Min), ownerId);
        }
    }

    [HarmonyPatch(typeof(MySlimBlock), nameof(MySlimBlock.IncreaseMountLevel))]
    private static class Welded
    {
        private static void Prefix(long welderOwnerIdentId, ref MyOwnershipShareModeEnum sharing)
        {
            if (Sync.IsServer && sharing == MyOwnershipShareModeEnum.Faction)
                sharing = BuildShare(welderOwnerIdentId);
        }
    }

    // The client's deposit, before the game takes the money; on a cluster on the player's node
    [HarmonyPatch(typeof(MyBankingSystem), "RequestDeposit_Server")]
    private static class NoDeposits
    {
        private static bool Prefix()
        {
            if (!Plugin.Config.Enabled || !Plugin.Config.OctNoDeposits)
                return true;
            var identity = MySession.Static.Players.TryGetIdentityId(
                MyEventContext.Current.Sender.Value
            );
            if (!Member(identity) || Faction.IsLeader(identity))
                return true;
            GroupPcuLimit.Notify(
                identity,
                "OCT's account takes no deposits: only its leader can take money out. You keep your money."
            );
            return false;
        }
    }
}
