using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;

namespace ServerPlugin;

// A player's turn at a shipyard: its containers, welders and connectors are theirs, shared with their
// faction unless that is OCT, which every player is in (OctFaction), so they can dock a ship and the
// welders take components from it. Everything
// else stays OCT's, the reactors most of all, whose uranium conveyors can't take out either. The
// grid stays a shipyard while OCT owns any of its blocks (ShipyardZoneWelding.Owners).
//
// Without a turn the connectors and welders are off, and a free build turns on only the welders.
// Cargo containers and terminals have no switch: OCT's ownership is what locks them, and with the
// connectors off no conveyor reaches them either. Reactors, batteries and refill stations are
// never touched here.
internal static class ShipyardTurn
{
    // As the world has them between turns: OCT's alone, not shared with its faction, which every
    // player is in (SHIPYARD_SHARED in build_world.py)
    private const MyOwnershipShareModeEnum IdleShare = MyOwnershipShareModeEnum.None;

    private static IEnumerable<MyCubeBlock> Blocks(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyCubeBlock>()
            .Where(b => b is MyCargoContainer or MyShipConnector)
            .Concat(ShipyardOrders.Beams(shipyard));

    /// <summary>Hands the blocks to the player, shared as OctFaction.BuildShare says, and on. Connectors don't trade. A grind slot gets the deconstructors too.</summary>
    public static void Give(MyCubeGrid shipyard, long player, bool deconstructors = false)
    {
        ShipyardOrders.WakeBeams(shipyard);
        foreach (
            var block in Blocks(shipyard)
                .Concat(deconstructors ? ShipyardOrders.Deconstructors(shipyard) : [])
        )
        {
            SetOwner(block, player, OctFaction.BuildShare(player));
            if (block is MyFunctionalBlock functional)
                functional.Enabled = true;
            if (block is MyShipConnector connector)
                connector.TradingEnabled.Value = false;
        }
    }

    // Every connector unlocks, a ship still docked floats free, and the blocks are OCT's and off.
    // Returns the grids that were docked.
    public static List<MyCubeGrid> Take(MyCubeGrid shipyard)
    {
        var npc = Npc(shipyard);
        var docked = new List<MyCubeGrid>();
        foreach (var block in Blocks(shipyard))
        {
            if (block is MyShipConnector connector)
            {
                if (connector.Connected && connector.Other != null)
                    docked.Add(connector.Other.CubeGrid);
                ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Disconnect();
                connector.TradingEnabled.Value = false;
            }
            SetOwner(block, npc, IdleShare);
            if (block is MyFunctionalBlock functional)
                functional.Enabled = false;
        }
        // A grind slot's: OCT's again, and on, as links of the welders' conveyor lines
        foreach (var block in ShipyardOrders.Deconstructors(shipyard))
            SetOwner(block, npc, IdleShare);
        return docked;
    }

    /// <summary>The player who has the shipyard's blocks, 0 when OCT has them. The blocks say so, so it survives a restart and a move to another node.</summary>
    public static long Holder(MyCubeGrid shipyard) =>
        Blocks(shipyard)
            .Select(b => b.OwnerId)
            .FirstOrDefault(o => o != 0 && !MySession.Static.Players.IdentityIsNpc(o));

    /// <summary>The NPC who owns the shipyard, in or out of a turn.</summary>
    public static long Npc(MyCubeGrid shipyard) =>
        ShipyardZoneWelding.Owners(shipyard).First(MySession.Static.Players.IdentityIsNpc);

    // The game's request lets the server give only a block without an owner to someone
    internal static void SetOwner(MyCubeBlock block, long owner, MyOwnershipShareModeEnum share)
    {
        if (block.OwnerId == owner && block.IDModule?.ShareMode == share)
            return;
        block.ChangeBlockOwnerRequest(0, share);
        block.ChangeBlockOwnerRequest(owner, share);
    }
}
