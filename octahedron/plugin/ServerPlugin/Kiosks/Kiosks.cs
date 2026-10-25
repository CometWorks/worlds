using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
using VRage.ObjectBuilders;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace ServerPlugin;

/// <summary>
/// The station kiosks: touch screens a player uses to move items between a docked ship or
/// their character and the station's warehouse ledgers. The kiosk client mod
/// (octahedron/kiosk-mod) draws the screens with TouchScreenAPI and sends the player's
/// requests here as mod messages; this side checks them and does the work, so a modified
/// client can do no more than the screens allow.
///
/// The Market tab trades through <see cref="Market"/>, which moves the items in the ledgers.
///
/// A kiosk is a text panel named <see cref="OctahedronConfig.KioskBlockName"/> on a station's
/// grids. The plugin selects the mod's screen script on it. A ship is docked when one of
/// the station's connectors holds it; its items are in the cargo containers and connectors of
/// its mechanical group that the operator has inventory access to.
/// </summary>
internal static class Kiosks
{
    public const ushort Channel = 40683;

    private static MySession registeredSession;
    private static int scanCountdown;
    private static readonly Dictionary<ulong, DateTime> lastRequest = new();
    private static readonly HashSet<ulong> busy = new();
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static void Update()
    {
        var session = MySession.Static;
        if (session is not { Ready: true } || MyAPIGateway.Multiplayer == null)
            return;
        if (registeredSession != session)
        {
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, OnMessage);
            registeredSession = session;
            lastRequest.Clear();
            busy.Clear();
            scanCountdown = 0;
        }
        if (--scanCountdown <= 0)
        {
            scanCountdown = 600;
            SelectScripts();
        }
    }

    public static void Dispose()
    {
        if (registeredSession != null)
            MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Channel, OnMessage);
        registeredSession = null;
    }

    // --- the kiosk blocks ---

    public static bool IsKiosk(MyTerminalBlock block) =>
        block is MyTextPanel
        && !string.IsNullOrWhiteSpace(Plugin.Config.KioskBlockName)
        && string.Equals(
            block.CustomName?.ToString(),
            Plugin.Config.KioskBlockName,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>The grids of the station's mechanical group and the station's name; null when the grid is no station's.</summary>
    private static string StationGrids(MyCubeGrid grid, List<MyCubeGrid> grids)
    {
        grids.Clear();
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        if (group == null)
            grids.Add(grid);
        else
            grids.AddRange(group.Nodes.Select(n => n.NodeData));
        return grids.Select(Ledgers.StationOf).FirstOrDefault(s => s != null);
    }

    /// <summary>Every kiosk of the world's stations, with its station.</summary>
    public static IEnumerable<(MyTextPanel Kiosk, string Station)> All()
    {
        var grids = new List<MyCubeGrid>();
        var seen = new HashSet<long>();
        foreach (var faction in MySession.Static.Factions)
        foreach (var entry in faction.Value.Stations)
        {
            if (!MyEntities.TryGetEntityById(entry.StationEntityId, out MyCubeGrid grid))
                continue;
            string station = StationGrids(grid, grids);
            if (station == null)
                continue;
            foreach (var g in grids)
            foreach (var panel in g.GetFatBlocks<MyTextPanel>())
                if (IsKiosk(panel) && seen.Add(panel.EntityId))
                    yield return (panel, station);
        }
    }

    // The mod's screen script on every kiosk this server simulates; the game syncs it to the clients
    private static void SelectScripts()
    {
        string script = Plugin.Config.KioskScript;
        if (string.IsNullOrWhiteSpace(script))
            return;
        foreach (var (kiosk, station) in All())
        {
            if (!Plugin.Simulated(kiosk.CubeGrid))
                continue;
            var surface = (IMyTextSurface)kiosk;
            if (surface.ContentType == ContentType.SCRIPT && surface.Script == script)
                continue;
            surface.ContentType = ContentType.SCRIPT;
            surface.Script = script;
            Plugin.Log.Info($"Kiosk {kiosk.EntityId} at {station}: screen script {script}");
        }
    }

    // --- requests ---

    // One request on its way: what the reads found, what the reply says so far
    private sealed class Visit
    {
        public ulong Sender;
        public KioskRequest Request;
        public KioskReply Reply;
        public long Identity;
        public MyTextPanel Kiosk;
        public string Station;
        public MyFaction Faction;
        public LedgerBook Mine;
        public LedgerBook FactionBook;
        public LedgerOwner Owner => Request.Faction && Faction != null ? FactionOwner : Me;
        public LedgerOwner Me => LedgerOwner.Player(Identity);
        public LedgerOwner FactionOwner => LedgerOwner.OfFaction(Faction.FactionId);
    }

    private static void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
    {
        if (fromServer || !Plugin.Config.Enabled)
            return;
        KioskRequest request;
        try
        {
            request = KioskRequest.Parse(Utf8.GetString(data));
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            Send(sender, new KioskReply { Message = e.Message });
            return;
        }
        var visit = new Visit
        {
            Sender = sender,
            Request = request,
            Reply = new KioskReply { Sequence = request.Sequence, Kiosk = request.Kiosk },
        };
        try
        {
            Begin(visit);
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Kiosk request failed", e);
            Done(visit, false, "The kiosk failed.");
        }
    }

    private static void Begin(Visit visit)
    {
        var session = MySession.Static;
        if (
            !session.Players.TryGetPlayerBySteamId(visit.Sender, out var player)
            || player.Identity == null
        )
        {
            Send(visit.Sender, visit.Reply.With(false, "Unknown player."));
            return;
        }
        if (busy.Contains(visit.Sender))
        {
            Send(visit.Sender, visit.Reply.With(false, "Still working on your last request."));
            return;
        }
        var now = DateTime.UtcNow;
        if (
            lastRequest.TryGetValue(visit.Sender, out var last)
            && (now - last).TotalSeconds < Plugin.Config.KioskCooldownSeconds
        )
        {
            Send(visit.Sender, visit.Reply.With(false, "Not so fast."));
            return;
        }
        lastRequest[visit.Sender] = now;

        visit.Identity = player.Identity.IdentityId;
        var grids = new List<MyCubeGrid>();
        if (
            !MyEntities.TryGetEntityById(visit.Request.Kiosk, out MyTerminalBlock block)
            || !IsKiosk(block)
            || (visit.Station = StationGrids(block.CubeGrid, grids)) == null
        )
        {
            Send(visit.Sender, visit.Reply.With(false, "That is not a kiosk."));
            return;
        }
        visit.Kiosk = (MyTextPanel)block;
        visit.Faction = session.Factions.GetPlayerFaction(visit.Identity);
        visit.Reply.Station = visit.Station;
        visit.Reply.Reach = Plugin.Config.KioskReachMeters;

        busy.Add(visit.Sender);
        ReadLedgers(visit, () => Act(visit));
    }

    // The operator's part of the station's ledger and their faction's, then next
    private static void ReadLedgers(Visit visit, Action next)
    {
        Ledgers.Read(
            visit.Station,
            visit.Me,
            (mine, error) =>
            {
                if (mine == null)
                {
                    Done(visit, false, error, read: false);
                    return;
                }
                visit.Mine = mine;
                if (visit.Faction == null)
                {
                    next();
                    return;
                }
                Ledgers.Read(
                    visit.Station,
                    visit.FactionOwner,
                    (theirs, failure) =>
                    {
                        if (theirs == null)
                        {
                            Done(visit, false, failure, read: false);
                            return;
                        }
                        visit.FactionBook = theirs;
                        next();
                    }
                );
            }
        );
    }

    private static void Act(Visit visit)
    {
        var request = visit.Request;
        var faction = visit.Faction;
        bool membersTrade = faction != null && visit.FactionBook.MembersTrade(faction.FactionId);
        bool leader =
            faction != null
            && (faction.IsLeader(visit.Identity) || faction.IsFounder(visit.Identity));
        bool mayTrade = faction != null && KioskRules.MayTrade(leader, membersTrade);

        var character = MySession.Static.Players.TryGetIdentity(visit.Identity)?.Character;
        if (
            character == null
            || character.IsDead
            || character.Closed
            || visit.Kiosk.Closed
            || Vector3DDistance(character, visit.Kiosk) > Plugin.Config.KioskReachMeters
        )
        {
            Done(visit, false, "Step closer to the kiosk.");
            return;
        }

        string refusal = KioskRules.Refusal(request, faction != null, mayTrade, leader);
        if (refusal != null)
        {
            Done(visit, false, refusal);
            return;
        }
        var opId = Guid.NewGuid();
        if (KioskRules.IsMarket(request.Verb))
        {
            if (request.Verb == KioskVerb.Market)
            {
                Done(visit, true, "");
                return;
            }
            var trade = new MarketRequest
            {
                Verb = request.Verb,
                Station = visit.Station,
                Owner = visit.Owner,
                Item = request.Item,
                Amount = request.Amount,
                Price = request.Price,
                PricePercent = request.PricePercent,
                Listing = request.Listing,
            };
            Market.Execute(opId, trade, (ok, message) => Done(visit, ok, message));
            return;
        }
        switch (request.Verb)
        {
            case KioskVerb.State:
                Done(visit, true, "");
                return;
            case KioskVerb.Members:
                var members = new LedgerOp
                {
                    Action = LedgerAction.MembersTrade,
                    Owner = visit.FactionOwner,
                    Counted = request.Amount > 0,
                };
                Ledgers.Execute(
                    opId,
                    [members],
                    r =>
                        Done(
                            visit,
                            r.Applied,
                            r.Applied
                                ? (
                                    members.Counted
                                        ? "Every member may trade now."
                                        : "Only the leaders and the founder trade now."
                                )
                                : r.Message
                        )
                );
                return;
            case KioskVerb.Give:
            case KioskVerb.Take:
                bool give = request.Verb == KioskVerb.Give;
                var from = give ? visit.Me : visit.FactionOwner;
                long amount =
                    request.Amount > 0
                        ? request.Amount
                        : Free(
                            give ? visit.Mine : visit.FactionBook,
                            visit.Station,
                            from,
                            request.Item
                        );
                if (amount <= 0)
                {
                    Done(visit, false, "There is none of that.");
                    return;
                }
                var transfer = new LedgerOp
                {
                    Action = LedgerAction.Transfer,
                    Station = visit.Station,
                    Owner = from,
                    Other = give ? visit.FactionOwner : visit.Me,
                }.Add(request.Item, amount);
                Ledgers.Execute(
                    opId,
                    [transfer],
                    r =>
                        Done(
                            visit,
                            r.Applied,
                            r.Applied
                                ? $"{(give ? "Gave" : "Took")} {LedgerBook.Units(amount)} {ItemTitle(request.Item)}."
                                : r.Message
                        )
                );
                return;
        }

        // Deposit and withdraw need a source
        var inventories = Source(visit, character, out string sourceError);
        if (inventories == null)
        {
            Done(visit, false, sourceError);
            return;
        }
        if (
            !Ledgers.TryItemId(request.Item, out var id)
            || !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
        )
        {
            Done(visit, false, $"Unknown item {request.Item}.");
            return;
        }
        if (definition is MyOxygenContainerDefinition)
        {
            Done(visit, false, "The warehouse takes no gas bottles.");
            return;
        }
        if (request.Verb == KioskVerb.Deposit)
            Deposit(visit, inventories, id, opId);
        else
            Withdraw(visit, inventories, id, definition, opId);
    }

    private static double Vector3DDistance(MyCharacter character, MyTextPanel kiosk) =>
        (character.PositionComp.GetPosition() - kiosk.PositionComp.WorldAABB.Center).Length();

    private static long Free(LedgerBook book, string station, LedgerOwner owner, string item) =>
        book.FreeOf(station, owner).TryGetValue(item, out long raw) ? raw : 0;

    // Items from the inventories into the ledger. They leave the inventories first, so nothing is
    // in both; a refused deposit puts them back.
    private static void Deposit(
        Visit visit,
        List<MyInventory> inventories,
        MyDefinitionId id,
        Guid opId
    )
    {
        var available = (MyFixedPoint)0;
        foreach (var inventory in inventories)
            available += inventory.GetItemAmount(id);
        long amount = visit.Request.Amount > 0 ? visit.Request.Amount : available.RawValue;
        if (amount <= 0)
        {
            Done(visit, false, "There is none of that here.");
            return;
        }
        if (amount > available.RawValue)
        {
            Done(
                visit,
                false,
                $"There is only {LedgerBook.Units(available.RawValue)} {ItemTitle(visit.Request.Item)} here."
            );
            return;
        }

        var taken = new List<(MyInventory, MyFixedPoint)>();
        long left = amount;
        foreach (var inventory in inventories)
        {
            if (left <= 0)
                break;
            var part = MyFixedPoint.Min(
                inventory.GetItemAmount(id),
                new MyFixedPoint { RawValue = left }
            );
            if (part <= 0)
                continue;
            var removed = inventory.RemoveItemsOfType(part, id);
            if (removed <= 0)
                continue;
            taken.Add((inventory, removed));
            left -= removed.RawValue;
        }
        long moved = amount - left;
        if (moved <= 0)
        {
            PutBack(taken, id);
            Done(visit, false, "The items could not be taken.");
            return;
        }

        var op = new LedgerOp
        {
            Action = LedgerAction.Deposit,
            Station = visit.Station,
            Owner = visit.Owner,
        }.Add(visit.Request.Item, moved);
        Ledgers.Execute(
            opId,
            [op],
            r =>
            {
                if (r.Outcome == LedgerOutcome.Refused)
                    PutBack(taken, id);
                else if (r.Outcome == LedgerOutcome.Unknown)
                    Plugin.Log.Error(
                        $"Kiosk deposit {opId} unknown, the items are out of the ship: {op.Encode()}"
                    );
                Done(
                    visit,
                    r.Applied,
                    r.Applied ? $"Stored {LedgerBook.Units(moved)} {ItemTitle(visit.Request.Item)}."
                        : r.Outcome == LedgerOutcome.Unknown
                            ? $"The station did not answer. Ask an admin about deposit {opId}."
                        : r.Message
                );
            }
        );
    }

    private static void PutBack(
        List<(MyInventory Inventory, MyFixedPoint Amount)> taken,
        MyDefinitionId id
    )
    {
        foreach (var (inventory, amount) in taken)
            inventory.AddItems(amount, MyObjectBuilderSerializer.CreateNewObject(id));
    }

    // Items from the ledger into the inventories, only as many as fit. The ledger gives them
    // first; what no longer fits when they arrive goes back into the ledger.
    private static void Withdraw(
        Visit visit,
        List<MyInventory> inventories,
        MyDefinitionId id,
        MyPhysicalItemDefinition definition,
        Guid opId
    )
    {
        var book = visit.Request.Faction ? visit.FactionBook : visit.Mine;
        long have = Free(book, visit.Station, visit.Owner, visit.Request.Item);
        long fits = 0;
        foreach (var inventory in inventories)
            if (inventory.CheckConstraint(id))
                fits += inventory.ComputeAmountThatFits(id).RawValue;
        if (definition.HasIntegralAmounts)
            fits -= fits % LedgerBook.Unit;
        bool all = visit.Request.Amount == 0;
        long amount = all ? Math.Min(have, fits) : visit.Request.Amount;
        if (have <= 0)
        {
            Done(visit, false, "There is none of that in the ledger.");
            return;
        }
        if (amount <= 0 || amount > fits)
        {
            Done(
                visit,
                false,
                $"There is room for only {LedgerBook.Units(fits)} {ItemTitle(visit.Request.Item)}."
            );
            return;
        }

        var op = new LedgerOp
        {
            Action = LedgerAction.Withdraw,
            Station = visit.Station,
            Owner = visit.Owner,
        }.Add(visit.Request.Item, amount);
        Ledgers.Execute(
            opId,
            [op],
            r =>
            {
                if (!r.Applied)
                {
                    Done(visit, false, r.Message);
                    return;
                }
                long left = amount;
                foreach (var inventory in inventories)
                {
                    if (left <= 0)
                        break;
                    if (!inventory.CheckConstraint(id))
                        continue;
                    long part = Math.Min(left, inventory.ComputeAmountThatFits(id).RawValue);
                    if (definition.HasIntegralAmounts)
                        part -= part % LedgerBook.Unit;
                    if (part <= 0)
                        continue;
                    if (
                        inventory.AddItems(
                            new MyFixedPoint { RawValue = part },
                            MyObjectBuilderSerializer.CreateNewObject(id)
                        )
                    )
                        left -= part;
                }
                if (left > 0)
                    Refund(visit, op, left);
                Done(
                    visit,
                    true,
                    $"Took out {LedgerBook.Units(amount - left)} {ItemTitle(visit.Request.Item)}."
                );
            }
        );
    }

    private static void Refund(Visit visit, LedgerOp withdrawn, long left)
    {
        var back = new LedgerOp
        {
            Action = LedgerAction.Deposit,
            Station = withdrawn.Station,
            Owner = withdrawn.Owner,
        }.Add(visit.Request.Item, left);
        Plugin.Log.Warning($"Kiosk withdrawal did not fit, back into the ledger: {back.Encode()}");
        Ledgers.Execute(
            Guid.NewGuid(),
            [back],
            r =>
            {
                if (!r.Applied)
                    Plugin.Log.Error($"Kiosk refund {r.Outcome}: {r.Message}: {back.Encode()}");
            }
        );
    }

    /// <summary>The inventories of the request's source, null with the reason when there is none.</summary>
    private static List<MyInventory> Source(Visit visit, MyCharacter character, out string error)
    {
        error = null;
        if (visit.Request.FromHand)
        {
            visit.Reply.Source = KioskRequest.Hand;
            var inventory = character.GetInventory();
            if (inventory == null || !Plugin.Simulated(character))
            {
                error = "Your inventory can't be reached from here.";
                return null;
            }
            return [inventory];
        }
        var ships = DockedShips(visit.Kiosk.CubeGrid, visit.Identity);
        var ship =
            visit.Request.Source.Length == 0
                ? ships.FirstOrDefault()
                : ships.FirstOrDefault(s => s.Grid.EntityId.ToString() == visit.Request.Source);
        if (ship.Grid == null)
        {
            error =
                visit.Request.Source.Length == 0
                    ? "No ship of yours is docked here."
                    : "That ship is not docked here.";
            return null;
        }
        visit.Reply.Source = ship.Grid.EntityId.ToString();
        return ship.Inventories;
    }

    /// <summary>
    /// The ships docked at the station's connectors, by the grid on the connector, with the
    /// cargo container and connector inventories of their mechanical groups that the player has
    /// access to; only ships with at least one such inventory, and only those this server simulates.
    /// </summary>
    private static List<(MyCubeGrid Grid, List<MyInventory> Inventories)> DockedShips(
        MyCubeGrid kioskGrid,
        long identity
    )
    {
        var ships = new List<(MyCubeGrid, List<MyInventory>)>();
        var stationGrids = new List<MyCubeGrid>();
        StationGrids(kioskGrid, stationGrids);
        var station = new HashSet<MyCubeGrid>(stationGrids);
        var seen = new HashSet<MyCubeGrid>();
        foreach (var grid in stationGrids)
        foreach (var connector in grid.GetFatBlocks<MyShipConnector>())
        {
            var docked = connector.Connected ? connector.Other?.CubeGrid : null;
            if (
                docked == null
                || station.Contains(docked)
                || !seen.Add(docked)
                || !Plugin.Simulated(docked)
            )
                continue;
            var group = MyCubeGridGroups.Static.Mechanical.GetGroup(docked);
            var shipGrids = group == null ? [docked] : group.Nodes.Select(n => n.NodeData).ToList();
            if (shipGrids.Any(station.Contains))
                continue;
            var inventories = new List<MyInventory>();
            foreach (var g in shipGrids)
            foreach (var block in g.GetFatBlocks<MyTerminalBlock>())
            {
                if (
                    block is not (MyCargoContainer or MyShipConnector)
                    || !block.HasPlayerAccess(identity)
                )
                    continue;
                for (int i = 0; i < block.InventoryCount; i++)
                    inventories.Add(block.GetInventory(i));
            }
            // Cargo containers first, they take what is withdrawn before the connectors
            inventories.Sort(
                (a, b) =>
                    (a.Owner is MyCargoContainer ? 0 : 1).CompareTo(
                        b.Owner is MyCargoContainer ? 0 : 1
                    )
            );
            if (inventories.Count > 0)
                ships.Add((docked, inventories));
        }
        return ships;
    }

    // --- replies ---

    private static KioskReply With(this KioskReply reply, bool ok, string message)
    {
        reply.Ok = ok;
        reply.Message = message ?? "";
        return reply;
    }

    // Ends the request: the ledgers read again, so the reply shows what the change did, and
    // after a market request the market
    private static void Done(Visit visit, bool ok, string message, bool read = true)
    {
        visit.Reply.With(ok, message);
        if (!read || visit.Mine == null)
        {
            Finish(visit);
            return;
        }
        ReadLedgers(visit, () => ReadMarket(visit, () => Finish(visit)));
    }

    private static void ReadMarket(Visit visit, Action next)
    {
        if (!KioskRules.IsMarket(visit.Request.Verb))
        {
            next();
            return;
        }
        Market.Read(
            visit.Station,
            visit.Me,
            visit.Faction != null ? visit.FactionOwner : null,
            (text, error) =>
            {
                visit.Reply.Market = text ?? "";
                if (text == null && visit.Reply.Message.Length == 0)
                    visit.Reply.Message = error ?? "";
                next();
            }
        );
    }

    private static void Finish(Visit visit)
    {
        busy.Remove(visit.Sender);
        try
        {
            Describe(visit);
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Kiosk state failed", e);
        }
        Send(visit.Sender, visit.Reply);
    }

    // The state the screens show
    private static void Describe(Visit visit)
    {
        var reply = visit.Reply;
        var faction = visit.Faction;
        if (faction != null && visit.FactionBook != null)
        {
            bool leader = faction.IsLeader(visit.Identity) || faction.IsFounder(visit.Identity);
            reply.FactionTag = faction.Tag;
            reply.MembersTrade = visit.FactionBook.MembersTrade(faction.FactionId);
            reply.MayTrade = KioskRules.MayTrade(leader, reply.MembersTrade);
            reply.MaySetMembers = leader;
        }
        if (visit.Kiosk == null || visit.Mine == null)
            return;

        List<MyInventory> source = null;
        var ships = DockedShips(visit.Kiosk.CubeGrid, visit.Identity);
        foreach (var ship in ships)
            reply.Ships.Add(
                new KeyValuePair<long, string>(ship.Grid.EntityId, ship.Grid.DisplayName)
            );
        if (reply.Source.Length == 0)
            reply.Source = visit.Request.FromHand ? KioskRequest.Hand : visit.Request.Source;
        if (reply.Source == KioskRequest.Hand)
        {
            var character = MySession.Static.Players.TryGetIdentity(visit.Identity)?.Character;
            if (character?.GetInventory() is { } inventory)
                source = [inventory];
        }
        else
        {
            var ship =
                reply.Source.Length == 0
                    ? ships.FirstOrDefault()
                    : ships.FirstOrDefault(s => s.Grid.EntityId.ToString() == reply.Source);
            reply.Source = ship.Grid?.EntityId.ToString() ?? "";
            source = ship.Inventories;
        }

        var rows = new Dictionary<string, KioskItem>();
        KioskItem Row(string item) =>
            rows.TryGetValue(item, out var row)
                ? row
                : new KioskItem { Item = item, Name = ItemTitle(item) };
        foreach (var item in visit.Mine.FreeOf(visit.Station, visit.Me))
            rows[item.Key] = Row(item.Key) with { Mine = item.Value };
        if (faction != null && visit.FactionBook != null)
            foreach (var item in visit.FactionBook.FreeOf(visit.Station, visit.FactionOwner))
                rows[item.Key] = Row(item.Key) with { Faction = item.Value };
        foreach (var inventory in source ?? [])
        foreach (var physical in inventory.GetItems())
        {
            var id = physical.Content.GetObjectId();
            if (physical.Content is MyObjectBuilder_GasContainerObject)
                continue;
            string name = Ledgers.ItemName(id);
            var row = Row(name);
            rows[name] = row with { Source = row.Source + physical.Amount.RawValue };
        }
        reply.Items.AddRange(rows.Values);
    }

    private static string ItemTitle(string item) =>
        Ledgers.TryItemId(item, out var id)
        && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out var definition)
            ? definition.DisplayNameText
            : item;

    private static void Send(ulong steamId, KioskReply reply) =>
        MyAPIGateway.Multiplayer.SendMessageTo(Channel, Utf8.GetBytes(reply.Encode()), steamId);
}
