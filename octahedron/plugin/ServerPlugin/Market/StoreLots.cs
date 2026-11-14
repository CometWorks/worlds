using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;

namespace ServerPlugin;

/// <summary>
/// The Store blocks of OCT's stations trade the items of MarketLots in lots too: on that row a unit
/// of the Store is a lot of that many units, priced in whole SC per lot, so ice can cost less than
/// 1 SC per kg, and stone can be bought back at about 1 SC per kg.
/// The game's own trade methods run, cluster patches and all; while one runs for a lot item, the
/// inventory calls in it count lots: the room checked and the items added on a purchase, the
/// amount the seller has and the items taken on a sale.
/// </summary>
internal static class StoreLots
{
    private sealed class Scope
    {
        public MyDefinitionId Item;
        public long Lot;

        // Inside TransferOrRemove, which counts real units
        public int Suspended;
    }

    [System.ThreadStatic]
    private static Scope scope;

    /// <summary>A trade of the Store item starts: its lots count from here to <see cref="End"/>.</summary>
    public static void Begin(SerializableDefinitionId? item, long lot) =>
        scope = item is { } id && lot > 1 ? new Scope { Item = id, Lot = lot } : null;

    public static void End() => scope = null;

    private static long Active(MyDefinitionId item) =>
        scope is { Suspended: 0 } s && s.Item == item ? s.Lot : 1;

    private static MyFixedPoint Scaled(MyFixedPoint amount, long lot) =>
        lot > 1 ? new MyFixedPoint { RawValue = amount.RawValue * lot } : amount;

    /// <summary>Tells the player what a trade in lots moved, as the Store's numbers count lots.</summary>
    public static void Tell(
        MyPlayer player,
        string verb,
        SerializableDefinitionId item,
        int lots,
        long lot,
        long sc
    )
    {
        if (lot <= 1 || player == null)
            return;
        MyVisualScriptLogicProvider.SendChatMessage(
            $"{verb} {lots:N0} x {lot:N0} kg of {item.SubtypeName}: {lots * lot:N0} kg for {sc:N0} SC. "
                + $"The Store trades {item.SubtypeName} in lots of {lot:N0} kg.",
            "OCT",
            player.Identity.IdentityId
        );
    }

    // When each player last heard about the lots
    private static readonly Dictionary<long, DateTime> told = new();
    private static readonly TimeSpan TellEvery = TimeSpan.FromHours(6);

    /// <summary>The lots, in a chat line: the first time a player opens an OCT Store, then every few hours.</summary>
    public static string Advert()
    {
        var settings = Market.Settings;
        string Name(string item) =>
            Stations.TryItemId(item, out var id)
            && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var d)
                ? d.DisplayNameText
                : item;
        var both = settings
            .Lots.Keys.Where(i => !settings.LotsOnly.Contains(i))
            .Select(Name)
            .ToList();
        var only = settings.LotsOnly.Select(Name).ToList();
        if (both.Count + only.Count == 0)
            return null;
        var lot = settings.Lots.Values.Max();
        // With OctahedronMod the rows of lots read "Iron Ore (1 t)"
        bool named =
            lot == ShownLot
            && Stations.TryItemId(settings.Lots.Keys.First(), out var first)
            && Shown(first, lot) != null;
        return "OCT trades by the tonne: "
            + (
                both.Count > 0
                    ? named
                        ? $"{string.Join(", ", both)} have a second row, \"(1 t)\", priced per lot of {lot:N0} kg (or pieces). "
                        : $"{string.Join(", ", both)} have a second row whose unit is a lot of {lot:N0} kg (or pieces), priced per lot. "
                    : ""
            )
            + (
                only.Count > 0 ? $"{string.Join(", ", only)} come in lots of {lot:N0} kg only." : ""
            );
    }

    [HarmonyPatch(typeof(MyStoreBlock), "GetStoreItems", typeof(bool))]
    private static class Opened
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(MyStoreBlock __instance, bool isPlayerRequest)
        {
            if (!isPlayerRequest || !Market.On || MyEventContext.Current.IsLocallyInvoked)
                return;
            var station = MySession.Static.Factions.GetStationByGridId(
                __instance.CubeGrid.EntityId
            );
            if (station == null || Market.OctStations().All(s => s.Id != station.Id))
                return;
            long identity = MySession.Static.Players.TryGetIdentityId(
                MyEventContext.Current.Sender.Value
            );
            var now = DateTime.UtcNow;
            if (
                identity == 0
                || told.TryGetValue(identity, out var last) && now - last < TellEvery
                || Advert() is not { } text
            )
                return;
            told[identity] = now;
            MyVisualScriptLogicProvider.SendChatMessage(text, "OCT", identity);
        }
    }

    // OctahedronMod's items named after a lot, "Silicon Ore (1 t)": the Store screen takes a row's
    // name, icon and unit from its item, so a client is shown a row of lots as one of these
    private const long ShownLot = 1000;
    private const string ShownPrefix = "OctahedronLot_";

    /// <summary>The item a client sees on a row of lots of the item: OctahedronMod's "(1 t)" item, if the world has it and the lot is a tonne.</summary>
    public static MyDefinitionId? Shown(MyDefinitionId item, long lot)
    {
        if (lot != ShownLot)
            return null;
        var id = new MyDefinitionId(
            typeof(MyObjectBuilder_Component),
            ShownPrefix + Stations.ItemName(item).Replace('/', '_')
        );
        return MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out _) ? id : null;
    }

    // The rows a client gets: the station's own, with the rows of lots showing OctahedronMod's
    // "(1 t)" items. The trades still find the station's rows by id, with the real items in them.
    [HarmonyPatch(typeof(MyStoreBlock), "GetStoreItemsForStation")]
    private static class Listed
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(
            IMyFactionStation station,
            ref StoreItemsWithEconomyStats __result
        )
        {
            if (
                !Market.On
                || __result?.StoreItems is not { } items
                || Market.OctStations().All(s => s.Id != station.Id)
            )
                return;
            List<MyStoreItem> shown = null;
            for (int i = 0; i < items.Count; i++)
            {
                var row = items[i];
                if (
                    row.Item is not { } item
                    || Shown(item, Market.LotOf(station.Name, row)) is not { } lotItem
                )
                    continue;
                shown ??= new List<MyStoreItem>(items);
                shown[i] = new MyStoreItem(
                    row.Id,
                    lotItem,
                    row.Amount,
                    row.PricePerUnit,
                    row.ItemType,
                    row.StoreItemType
                )
                {
                    PreviousPricePerUnit = row.PreviousPricePerUnit,
                    PricePerUnitDiscount = row.PricePerUnitDiscount,
                };
            }
            if (shown != null)
                __result = new StoreItemsWithEconomyStats(
                    shown,
                    __result.OffersBonus,
                    __result.OrdersBonus,
                    __result.FactionStation
                );
        }
    }

    [HarmonyPatch(
        typeof(MyInventory),
        nameof(MyInventory.CanItemsBeAdded),
        typeof(MyFixedPoint),
        typeof(MyDefinitionId)
    )]
    private static class Room
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(ref MyFixedPoint amount, MyDefinitionId contentId) =>
            amount = Scaled(amount, Active(contentId));
    }

    [HarmonyPatch(
        typeof(MyInventory),
        "AddItems",
        typeof(MyFixedPoint),
        typeof(MyObjectBuilder_Base),
        typeof(uint?),
        typeof(int)
    )]
    private static class Add
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(ref MyFixedPoint amount, MyObjectBuilder_Base objectBuilder)
        {
            if (objectBuilder is MyObjectBuilder_PhysicalObject item)
                amount = Scaled(amount, Active(item.GetObjectId()));
        }
    }

    [HarmonyPatch(
        typeof(MyInventory),
        nameof(MyInventory.GetItemAmount),
        typeof(MyDefinitionId),
        typeof(MyItemFlags),
        typeof(bool)
    )]
    private static class Have
    {
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(MyDefinitionId contentId, ref MyFixedPoint __result)
        {
            long lot = Active(contentId);
            if (lot > 1)
                __result = (int)(__result.RawValue / lot / 1_000_000);
        }
    }

    [HarmonyPatch(typeof(MyInventory), "TransferOrRemove")]
    private static class Take
    {
        // ReSharper disable once UnusedMember.Local
        private static void Prefix(
            ref MyFixedPoint? amount,
            MyDefinitionId contentId,
            out bool __state
        )
        {
            long lot = Active(contentId);
            __state = scope != null;
            if (lot > 1 && amount is { } units)
                amount = Scaled(units, lot);
            if (__state)
                scope.Suspended++;
        }

        // ReSharper disable once UnusedMember.Local
        private static void Finalizer(bool __state)
        {
            if (__state && scope != null)
                scope.Suspended--;
        }
    }
}
