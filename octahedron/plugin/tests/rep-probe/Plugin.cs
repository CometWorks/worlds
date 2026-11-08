using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Plugins;

namespace RepProbe;

// Test only, for tests/test_reputation.py and tests/reputation_cluster.py: what a player does to
// their reputation, as chat commands. The game's own requests go to the server:
//   /rep claim <grid id>                makes the local player the owner of the grid's unowned blocks
//   /rep share <grid id> <None|Faction>  the share box of every block of the grid the player owns
//   /rep deposit <amount>               the faction screen's Deposit
// Each answers with a REP chat line.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private bool registered;

    public void Init(object gameInstance) { }

    public void Update()
    {
        if (registered || MySession.Static is not { Ready: true } || MyAPIGateway.Utilities == null)
            return;
        registered = true;
        MyAPIGateway.Utilities.MessageEntered += Entered;
    }

    public void Dispose()
    {
        if (registered && MyAPIGateway.Utilities != null)
            MyAPIGateway.Utilities.MessageEntered -= Entered;
    }

    private static void Say(string text) => MyAPIGateway.Utilities.ShowMessage("Rep", text);

    private static void Entered(string text, ref bool sendToOthers)
    {
        if (!text.StartsWith("/rep "))
            return;
        sendToOthers = false;
        try
        {
            Say("REP " + Run(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        catch (Exception e)
        {
            Say("REP ERROR " + e.Message);
        }
    }

    private static string Run(string[] args)
    {
        var me = MySession.Static.LocalPlayerId;
        if (args[1] == "deposit")
        {
            MyBankingSystem.RequestDeposit(long.Parse(args[2]));
            return "deposit sent";
        }
        if (!MyEntities.TryGetEntityById(long.Parse(args[2]), out MyCubeGrid grid))
            return "no grid " + args[2];
        var blocks = grid.GetFatBlocks().Where(b => b.IDModule != null).ToList();
        if (args[1] == "claim")
        {
            var requests = blocks
                .Where(b => b.OwnerId == 0)
                .Select(b => new MyCubeGrid.MySingleOwnershipRequest
                {
                    BlockId = b.EntityId,
                    Owner = me,
                    Sender = MySession.Static.LocalHumanPlayer.Id.SteamId,
                })
                .ToList();
            MyCubeGrid.ChangeOwnersRequest(
                MyOwnershipShareModeEnum.None,
                new Dictionary<MyCubeGrid, List<MyCubeGrid.MySingleOwnershipRequest>>
                {
                    [grid] = requests,
                }
            );
            return $"claim sent for {requests.Count} blocks";
        }
        if (args[1] == "share")
        {
            var mode = (MyOwnershipShareModeEnum)
                Enum.Parse(typeof(MyOwnershipShareModeEnum), args[3]);
            var requests = blocks
                .Where(b => b.OwnerId == me)
                .Select(b => new MyCubeGrid.MySingleOwnershipRequest
                {
                    BlockId = b.EntityId,
                    Owner = me,
                })
                .ToList();
            MyCubeGrid.ChangeShareRequest(
                mode,
                new Dictionary<MyCubeGrid, List<MyCubeGrid.MySingleOwnershipRequest>>
                {
                    [grid] = requests,
                }
            );
            return $"share {mode} sent for {requests.Count} blocks";
        }
        return "unknown " + args[1];
    }
}
