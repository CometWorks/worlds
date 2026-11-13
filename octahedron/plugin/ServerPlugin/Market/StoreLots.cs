using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ServerPlugin;

/// <summary>
/// The Store blocks of OCT's stations trade an item of MarketLots in lots: a unit of the Store is
/// a lot of that many units, priced in whole SC per lot, so ice can cost less than 1 SC per kg.
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
    public static void Begin(SerializableDefinitionId? item)
    {
        scope = null;
        if (item is not { } id)
            return;
        long lot = Market.Settings.Lot(Stations.ItemName(id));
        if (lot > 1)
            scope = new Scope { Item = id, Lot = lot };
    }

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
