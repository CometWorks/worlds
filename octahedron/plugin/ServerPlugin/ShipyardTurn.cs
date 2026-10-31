using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;

namespace ServerPlugin;

// A player's turn at a shipyard: its containers, welders and connectors are theirs and shared
// with their faction, so they can dock a ship and the welders take components from it. Everything
// else stays OCT's, the reactors most of all, whose uranium conveyors can't take out either. OCT
// keeps most of the shipyard's blocks, so the grid stays its big owner and a shipyard.
internal static class ShipyardTurn
{
    // As the world has them between turns (SHIPYARD_SHARED in build_world.py)
    private const MyOwnershipShareModeEnum IdleShare = MyOwnershipShareModeEnum.All;

    private static IEnumerable<MyCubeBlock> Blocks(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyCubeBlock>()
            .Where(b => b is MyCargoContainer or MyShipConnector)
            .Concat(ShipyardOrders.Beams(shipyard));

    public static void Give(MyCubeGrid shipyard, long player)
    {
        foreach (var block in Blocks(shipyard))
        {
            SetOwner(block, player, MyOwnershipShareModeEnum.Faction);
            if (block is MyShipConnector connector)
                connector.Enabled = true;
        }
    }

    // Every connector unlocks: a ship still docked floats free
    public static void Take(MyCubeGrid shipyard)
    {
        var npc = shipyard.BigOwners.First(MySession.Static.Players.IdentityIsNpc);
        foreach (var block in Blocks(shipyard))
        {
            if (block is MyShipConnector connector)
            {
                ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Disconnect();
                connector.TradingEnabled.Value = false;
            }
            SetOwner(block, npc, IdleShare);
        }
    }

    /// <summary>Whether a player has the shipyard's blocks.</summary>
    public static bool Given(MyCubeGrid shipyard) =>
        Blocks(shipyard).Any(b => !MySession.Static.Players.IdentityIsNpc(b.OwnerId));

    // The game's request lets the server give only a block without an owner to someone
    internal static void SetOwner(MyCubeBlock block, long owner, MyOwnershipShareModeEnum share)
    {
        if (block.OwnerId == owner && block.IDModule?.ShareMode == share)
            return;
        block.ChangeBlockOwnerRequest(0, share);
        block.ChangeBlockOwnerRequest(owner, share);
    }
}
