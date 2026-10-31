using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Plugins;

namespace StoreProbe;

// Test only, for tests/test_store.py: the client's side of the Store screen as chat commands, on
// the nearest Store block. The game's own requests go to the server, and the answers come back as
// chat lines:
//   /store list [item]            STORE items offers bonus; OFFER or ORDER item amount price
//   /store buy <item> <amount>    BUY result, into the character's inventory
//   /store sell <item> <amount>   SELL result, from the character's inventory
//   /store have <item>            HAVE amount
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

    private static void Say(string text) => MyAPIGateway.Utilities.ShowMessage("Store", text);

    private static void Entered(string text, ref bool sendToOthers)
    {
        if (!text.StartsWith("/store"))
            return;
        sendToOthers = false;
        try
        {
            Run(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception e)
        {
            Say("ERROR " + e.Message);
        }
    }

    private static MyStoreBlock Nearest(MyCharacter character) =>
        MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .SelectMany(g => g.GetFatBlocks<MyStoreBlock>())
            .Where(b => b.BlockDefinition.Id.SubtypeName == "StoreBlock")
            .OrderBy(b =>
                (b.PositionComp.GetPosition() - character.PositionComp.GetPosition()).LengthSquared()
            )
            .FirstOrDefault();

    private static string Name(MyStoreItem item) =>
        item.Item is { } id
            ? ((MyDefinitionId)id).TypeId.ToString().Replace("MyObjectBuilder_", "")
                + "/"
                + ((MyDefinitionId)id).SubtypeName
            : "?";

    private static void Run(string[] args)
    {
        var character = MySession.Static.LocalCharacter;
        var store = Nearest(character);
        if (store == null)
        {
            Say("NOSTORE");
            return;
        }
        string verb = args.Length > 1 ? args[1] : "list";
        string item = args.Length > 2 ? args[2] : null;
        if (verb == "have")
        {
            MyDefinitionId.TryParse("MyObjectBuilder_" + item, out var id);
            Say(
                "HAVE "
                    + ((MyFixedPoint)character.GetInventory().GetItemAmount(id)).ToString()
            );
            return;
        }
        int amount = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
        store.CreateGetStoreItemsRequest(
            (items, tick, offers, orders, npc) =>
            {
                if (verb == "list")
                    List(items, item, offers, orders, npc);
                else if (verb == "buy")
                    Buy(store, Find(items, item, StoreItemTypes.Offer), amount, character, tick);
                else if (verb == "sell")
                    Sell(store, Find(items, item, StoreItemTypes.Order), amount, character, tick);
                else
                    Say("NOVERB " + verb);
            }
        );
    }

    private static void List(
        List<MyStoreItem> items,
        string item,
        float offers,
        float orders,
        bool npc
    )
    {
        Say(
            FormattableString.Invariant(
                $"STORE {items.Count} OFFERS {items.Count(i => i.StoreItemType == StoreItemTypes.Offer)} NPC {npc} BONUS {offers} {orders}"
            )
        );
        foreach (var i in items.Where(i => item != null && Name(i) == item))
            Say(
                $"{(i.StoreItemType == StoreItemTypes.Offer ? "OFFER" : "ORDER")} {Name(i)} {i.Amount} {i.PricePerUnit}"
            );
    }

    private static MyStoreItem Find(List<MyStoreItem> items, string item, StoreItemTypes type)
    {
        var found = items.FirstOrDefault(i => i.StoreItemType == type && Name(i) == item);
        if (found == null)
            Say($"NOITEM {item}");
        return found;
    }

    private static void Buy(
        MyStoreBlock store,
        MyStoreItem item,
        int amount,
        MyCharacter character,
        double tick
    )
    {
        if (item == null)
            return;
        // Internal in the game
        typeof(MyStoreBlock)
            .GetMethod("CreateBuyRequest", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(
                store,
                [
                    item.Id,
                    amount,
                    character.EntityId,
                    tick,
                    (Action<MyStoreBuyItemResult>)(r => Say("BUY " + r.Result)),
                ]
            );
    }

    private static void Sell(
        MyStoreBlock store,
        MyStoreItem item,
        int amount,
        MyCharacter character,
        double tick
    )
    {
        if (item != null)
            store.CreateSellItemRequest(
                item.Id,
                amount,
                character.EntityId,
                tick,
                r => Say("SELL " + r.Result)
            );
    }
}
