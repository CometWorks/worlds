using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.GUI;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Plugins;

namespace StoreProbe;

// Test only, for tests/test_store.py: the client's side of the Store screen as chat commands, on
// the nearest Store block. The game's own requests go to the server, and the answers come back as
// chat lines:
//   /store list [item|all]        STORE items offers bonus; OFFER or ORDER item amount price
//   /store buy <item> <amount>    BUY result, into the character's inventory; <item>@lot buys
//                                 from the row of lots of an item that has two rows
//   /store sell <item> <amount>   SELL result, from the character's inventory
//   /store have <item>            HAVE amount
//   /store show <item> [orders]   opens the Store screen with only the item's rows (offers, or
//                                 OCT's orders); SHOW the rows as the screen words them
//   /store ships                  SHIPS the inventories of ships docked at the station's trading
//                                 connectors, as the Store screen lists them: entity ids
//   /store buyship <item> <amount>   BUY result, into the first of those
//   /store sellship <item> <amount>  SELL result, from the first of those
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
                (
                    b.PositionComp.GetPosition() - character.PositionComp.GetPosition()
                ).LengthSquared()
            )
            .FirstOrDefault();

    // A row of lots may come with OctahedronMod's "(1 t)" item (SE-1497),
    // Component/OctahedronLot_Ore_Silicon: it counts as the item it is a lot of
    private static string Name(MyStoreItem item) => item.Item is { } id ? Name(id) : "?";

    private static string Name(MyDefinitionId id)
    {
        const string lot = "OctahedronLot_";
        return id.SubtypeName.StartsWith(lot)
            ? id.SubtypeName.Substring(lot.Length).Replace('_', '/')
            : id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + id.SubtypeName;
    }

    // What the Store screen calls the row: its item's display name
    private static string Shown(MyStoreItem item) =>
        item.Item is { } id
        && Sandbox.Definitions.MyDefinitionManager.Static.TryGetPhysicalItemDefinition(
            id,
            out var d
        )
            ? d.DisplayNameText
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
        if (verb == "show")
        {
            Show(store, item, args.Length > 3 && args[3] == "orders");
            return;
        }
        if (verb == "have")
        {
            MyDefinitionId.TryParse("MyObjectBuilder_" + item, out var id);
            Say("HAVE " + ((MyFixedPoint)character.GetInventory().GetItemAmount(id)).ToString());
            return;
        }
        int amount = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
        if (verb is "ships" or "buyship" or "sellship")
        {
            store.CreateGetConnectedGridInventoriesRequest(ships =>
            {
                MyDefinitionId.TryParse("MyObjectBuilder_" + item, out var id);
                // Buys go into a cargo container, sales come from the inventory that has the items
                var ship = ships
                    .Select(e => MyEntities.GetEntityById(e))
                    .FirstOrDefault(e =>
                        verb == "buyship"
                            ? e is MyCargoContainer
                            : e?.GetInventory()?.GetItemAmount(id) >= amount
                    );
                if (verb == "ships" || ship == null)
                {
                    Say("SHIPS " + string.Join(",", ships));
                    return;
                }
                store.CreateGetStoreItemsRequest(
                    (items, tick, _, _, _) =>
                    {
                        if (verb == "buyship")
                            Buy(
                                store,
                                Find(items, item, StoreItemTypes.Offer),
                                amount,
                                ship.EntityId,
                                tick
                            );
                        else
                            Sell(
                                store,
                                Find(items, item, StoreItemTypes.Order),
                                amount,
                                ship.EntityId,
                                tick
                            );
                    }
                );
            });
            return;
        }
        store.CreateGetStoreItemsRequest(
            (items, tick, offers, orders, npc) =>
            {
                if (verb == "list")
                    List(items, item, offers, orders, npc);
                else if (verb == "buy")
                    Buy(
                        store,
                        Find(items, item, StoreItemTypes.Offer),
                        amount,
                        character.EntityId,
                        tick
                    );
                else if (verb == "sell")
                    Sell(
                        store,
                        Find(items, item, StoreItemTypes.Order),
                        amount,
                        character.EntityId,
                        tick
                    );
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
        foreach (var i in items.Where(i => item == "all" || Name(i) == item))
            Say(
                $"{(i.StoreItemType == StoreItemTypes.Offer ? "OFFER" : "ORDER")} {Name(i)} {i.Amount} {i.PricePerUnit} {Shown(i)}"
            );
    }

    // The Store screen, as Use on the block opens it, listing only the item's rows so that a
    // screenshot shows them; on the tab of OCT's orders with "orders"
    private static void Show(MyStoreBlock store, string item, bool orders)
    {
        var model = new MyStoreBlockViewModel(store);
        var screen = Sandbox.Graphics.GUI.MyGuiSandbox.CreateScreen<MyGuiScreenStoreBlock>(model);
        Sandbox.Graphics.GUI.MyGuiSandbox.AddScreen(screen);
        if (orders)
            model.TabSelectedIndex = 1;
        // The rows come with the answer to the screen's own request: narrow them once they are in
        int tries = 0;
        void Narrow()
        {
            if (model.OfferedItems.Count + model.OrderedItems.Count == 0 && ++tries < 300)
            {
                MyAPIGateway.Utilities.InvokeOnGameThread(Narrow);
                return;
            }
            foreach (var list in new[] { model.OfferedItems, model.OrderedItems })
            foreach (var row in list.Where(r => Name(r.ItemDefinitionId) != item).ToList())
                list.Remove(row);
            Say(
                "SHOW "
                    + string.Join(
                        "; ",
                        model
                            .OfferedItems.Select(r =>
                                $"OFFER {r.Name} {r.AmountFormatted} {r.PricePerUnitFormatted}"
                            )
                            .Concat(
                                model.OrderedItems.Select(r =>
                                    $"ORDER {r.Name} {r.AmountFormatted} {r.PricePerUnitFormatted}"
                                )
                            )
                    )
            );
        }
        MyAPIGateway.Utilities.InvokeOnGameThread(Narrow);
    }

    private static MyStoreItem Find(List<MyStoreItem> items, string item, StoreItemTypes type)
    {
        // An item of OCT's lots has two rows (SE-1471): "<item>@lot" is the row of lots, the dearer
        // one, a plain item the row of single units
        bool lot = item.EndsWith("@lot");
        if (lot)
            item = item.Substring(0, item.Length - 4);
        var rows = items.Where(i => i.StoreItemType == type && Name(i) == item);
        var found = lot
            ? rows.OrderByDescending(i => i.PricePerUnit).FirstOrDefault()
            : rows.OrderBy(i => i.PricePerUnit).FirstOrDefault();
        if (found == null)
            Say($"NOITEM {item}");
        return found;
    }

    private static void Buy(
        MyStoreBlock store,
        MyStoreItem item,
        int amount,
        long target,
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
                    target,
                    tick,
                    (Action<MyStoreBuyItemResult>)(r => Say("BUY " + r.Result)),
                ]
            );
    }

    private static void Sell(
        MyStoreBlock store,
        MyStoreItem item,
        int amount,
        long source,
        double tick
    )
    {
        if (item != null)
            store.CreateSellItemRequest(
                item.Id,
                amount,
                source,
                tick,
                r => Say("SELL " + r.Result)
            );
    }
}
