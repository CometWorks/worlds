using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

// A player's turn at a shipyard, for a paid build, a towed grid's breakdown or after a repair slot. The queue says where
// each turn is (BuildBook on the authority); the node that simulates the shipyard acts on it at its
// 10 s check, so a turn goes on after a restart or a move of the station's partition.
//
// A paid build's turn starts with the player getting the shipyard's blocks (ShipyardTurn) and
// ShipyardTurnMinutes to load components and press the button, which buys what is missing. If they
// don't, the order is dropped. After the build or breakdown they have ShipyardTurnMinutes to take
// what is in the containers. A turn ends at the deadline, once the containers are empty, or by
// !oct shipyard done: the blocks go back to OCT, a docked ship floats free, and what is left is
// sold to the station for the player.
internal static partial class ShipyardOrders
{
    // Orders whose build is starting and turns that are ending, waiting for the authority's answer
    private static readonly HashSet<string> beginning = [];
    private static readonly HashSet<string> endingTurns = [];

    // The grid in a shipyard's build space that was told to leave, and when
    private static readonly Dictionary<long, (long Grid, DateTime At)> blockers = new();

    // Grids a tow just gave a player, and until when Sweep leaves them alone
    private static readonly Dictionary<long, DateTime> towGiven = new();

    // The shipyards with something in their queue at its last answer
    private static readonly HashSet<long> queued = [];

    // The orders to break a grid down repeat until the grid is gone: one at a time
    private static readonly HashSet<long> towing = [];

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Clock(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToString("HH:mm", CultureInfo.InvariantCulture);

    // The player's running turn at the queue, else their order that isn't a tow
    private static BuildOrder Mine(List<BuildOrder> queue, long player) =>
        queue.FirstOrDefault(o => o.Player == player && o.Active)
        ?? queue.FirstOrDefault(o => o.Player == player && o.Grid == 0);

    private static string TakeWithin() =>
        $"Take what is in {"{0}"}'s containers within {Config.ShipyardTurnMinutes} min, then the station buys it from you. "
        + "!oct shipyard done sells it now and ends your turn.";

    // --- a turn starts ---

    private static void StartTurn(MyCubeGrid shipyard, BuildOrder next)
    {
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Turn,
                Id = next.Id,
                Station = next.Station,
            },
            (ok, message) =>
            {
                starting.Remove(shipyard.EntityId);
                if (!ok)
                {
                    Plugin.Log.Warning($"Shipyard order {next.Id} got no turn: {message}");
                    return;
                }
                ClearForTurn(shipyard);
                ShipyardTurn.Give(shipyard, next.Player);
                Plugin.Log.Info(
                    $"Shipyard order {next.Id} '{next.Ship}': {Name(next.Player)}'s turn at {shipyard.DisplayName}"
                );
                Tell(
                    next.Player,
                    $"It is your turn at {shipyard.DisplayName}: its cargo containers, connectors and welders are yours and your faction's. "
                        + $"Load the components for '{next.Ship}' within {Config.ShipyardTurnMinutes} min and press the button. "
                        + "The screen by the button shows what is missing, the press buys it for SC."
                );
            }
        );
    }

    // Whatever OCT's shipyard holds before a turn isn't the player's to take or to sell
    private static void ClearForTurn(MyCubeGrid shipyard)
    {
        foreach (var inventory in TurnInventories(shipyard))
            for (var i = inventory.GetItems().Count - 1; i >= 0; i--)
                inventory.RemoveItemsAt(i);
    }

    // --- a turn goes on, at each check ---

    private static void CheckTurn(MyCubeGrid shipyard, BuildOrder order)
    {
        if (beginning.Contains(order.Id) || endingTurns.Contains(order.Id))
            return;
        var minutes = Config.ShipyardTurnMinutes;
        if (order.TurnEnds <= Now)
        {
            EndTurn(
                shipyard,
                order,
                order.Phase == BuildPhase.Loading
                    ? $"Your turn at {shipyard.DisplayName} is over: '{order.Ship}' did not start within {minutes} min and left the queue."
                    : $"Your {minutes} min at {shipyard.DisplayName} are over."
            );
            return;
        }
        if (ShipyardTurn.Holder(shipyard) != order.Player)
            ShipyardTurn.Give(shipyard, order.Player);
        if (order.Phase == BuildPhase.After && Stock(shipyard).Count == 0)
            EndTurn(
                shipyard,
                order,
                $"Your turn at {shipyard.DisplayName} is over: its containers are empty."
            );
    }

    // Everything in the blocks a player gets, by item, raw
    private static Dictionary<string, long> Stock(MyCubeGrid shipyard)
    {
        var stock = new Dictionary<string, long>();
        foreach (var inventory in TurnInventories(shipyard))
        foreach (var item in inventory.GetItems())
        {
            var name = Stations.ItemName(item.Content.GetObjectId());
            stock[name] = (stock.TryGetValue(name, out var had) ? had : 0) + item.Amount.RawValue;
        }
        return stock;
    }

    private static IEnumerable<MyInventory> TurnInventories(MyCubeGrid shipyard) =>
        Containers(shipyard)
            .Concat(Beams(shipyard).Select(b => b.GetInventory()))
            .Concat(shipyard.GetFatBlocks<MyShipConnector>().Select(c => c.GetInventory()));

    // What the bill lacks in the shipyard, raw; the ice is always asked for, it isn't loaded
    private static Dictionary<string, long> Lacking(MyCubeGrid shipyard, BuildOrder order)
    {
        var stock = Stock(shipyard);
        var lacking = new Dictionary<string, long>();
        foreach (var (item, raw) in order.Items.Select(i => (i.Key, i.Value)))
        {
            var have = item != Hydrogen && stock.TryGetValue(item, out var s) ? s : 0;
            if (raw > have)
                lacking[item] = raw - have;
        }
        return lacking;
    }

    // --- the player presses start ---

    private static void Begin(MyProjectorBase table, BuildOrder order, Action<string> tell)
    {
        var shipyard = FindShipyard(table);
        if (shipyard == null || !beginning.Add(order.Id))
            return;
        void Refuse(string why)
        {
            beginning.Remove(order.Id);
            tell(why);
        }
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Next, Station = order.Station },
            (text, error) =>
            {
                if (string.IsNullOrEmpty(text))
                {
                    Refuse(error ?? "The station did not answer, try again.");
                    return;
                }
                var next = BuildBook.DecodeOrder(text);
                Builds.ReadBlueprint(next.Id, encoded =>
                {
                var blueprint = encoded == null ? null : DecodeBlueprint(encoded);
                if (blueprint == null || blueprint.Count == 0)
                {
                    Refuse("Its blueprint could not be read.");
                    return;
                }
                var why = Fits(shipyard, blueprint, next.Exempt, out var layout, out var pending);
                if (pending)
                {
                    // The press stands: it goes on when the arm is planned
                    beginning.Remove(order.Id);
                    ShipyardArm.WhenDone(() => Begin(table, order, tell));
                    return;
                }
                if (Obstacle(shipyard) is { } obstacle)
                    why =
                        $"Fly '{obstacle.DisplayName}' out of {shipyard.DisplayName}'s build space first.";
                var lacking = Lacking(shipyard, next);
                var volume = lacking
                    .Where(l => l.Key != Hydrogen)
                    .Sum(l =>
                        MyDefinitionManager.Static.GetPhysicalItemDefinition(ItemId(l.Key)).Volume
                        * ((double)l.Value / Amounts.Unit)
                    );
                var room = Containers(shipyard).Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
                if (why == null && volume > room)
                    why =
                        $"What is missing takes {volume:N0} m³, {shipyard.DisplayName}'s containers have room for {room:N0} m³. Take something out first.";
                if (why != null)
                {
                    Refuse(why);
                    return;
                }

                var plan = new Plan
                {
                    Table = table,
                    Player = next.Player,
                    Payer = next.Payer,
                    HydrogenLiters = next.Items.TryGetValue(Hydrogen, out var ice)
                        ? ice / (double)Amounts.Unit * Config.ShipyardHydrogenLitersPerIce
                        : 0,
                };
                var drained = Drain(plan);
                var iceTaken = TakeIce(shipyard, next, drained.Sum(d => d.Liters));
                var request = new BuildRequest
                {
                    Verb = BuildVerb.Begin,
                    Id = next.Id,
                    Station = next.Station,
                    Player = next.Player,
                    Needed = lacking,
                };
                var liters = drained.Sum(d => d.Liters);
                if (liters > 0 || iceTaken > 0)
                    request.Provided[Hydrogen] = IceFor(liters, up: false) + iceTaken;
                Builds.Execute(
                    Guid.NewGuid(),
                    request,
                    (ok, message, started) =>
                    {
                        beginning.Remove(order.Id);
                        if (!ok || started == null)
                        {
                            foreach (var (tank, l) in drained)
                                AddHydrogen(tank, l);
                            if (iceTaken > 0)
                                Put(
                                    Containers(shipyard).ToList(),
                                    (MyObjectBuilder_PhysicalObject)
                                        MyObjectBuilderSerializer.CreateNewObject(ItemId(Hydrogen)),
                                    new MyFixedPoint { RawValue = iceTaken }
                                );
                            tell(message);
                            return;
                        }
                        tell(message);
                        var failed = Create(shipyard, started, blueprint, layout);
                        if (failed != null)
                            After(
                                started,
                                0,
                                true,
                                new Dictionary<string, long>(),
                                $"'{started.Ship}' can't be built: {failed}. "
                                    + string.Format(TakeWithin(), shipyard.DisplayName)
                            );
                    }
                );
                });
            }
        );
    }

    // Takes the ice the docked ships' hydrogen didn't cover out of the shipyard; returns it, raw
    private static long TakeIce(MyCubeGrid shipyard, BuildOrder order, double liters)
    {
        if (!order.Items.TryGetValue(Hydrogen, out var needed))
            return 0;
        var left = needed - IceFor(liters, up: false);
        var id = ItemId(Hydrogen);
        foreach (var inventory in TurnInventories(shipyard))
        {
            if (left <= 0)
                break;
            var have = inventory.GetItemAmount(id).RawValue;
            if (have <= 0)
                continue;
            var take = Math.Min(have, left);
            inventory.RemoveItemsOfType(new MyFixedPoint { RawValue = take }, id);
            left -= take;
        }
        return needed - IceFor(liters, up: false) - Math.Max(left, 0);
    }

    // The bought components of a started paid build, to load
    private static Dictionary<MyDefinitionId, int> Bought(BuildOrder order) =>
        order
            .Items.Where(i => i.Key != Hydrogen)
            .ToDictionary(i => ItemId(i.Key), i => (int)(i.Value / Amounts.Unit));

    private static void After(
        BuildOrder order,
        long hydrogenUsed,
        bool refund,
        Dictionary<string, long> sold,
        string message,
        Action done = null
    )
    {
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.After,
                Id = order.Id,
                Station = order.Station,
                Needed = sold,
                HydrogenUsed = hydrogenUsed,
                Refund = refund,
            },
            (ok, answer) =>
            {
                if (!ok)
                    Plugin.Log.Error($"Shipyard order {order.Id} did not end its build: {answer}");
                Tell(order.Player, answer.Length > 0 ? $"{message} {answer}" : message);
                done?.Invoke();
            }
        );
        Plugin.Log.Info(
            $"Shipyard order {order.Id} '{order.Ship}' of {Name(order.Player)} built or broken down: {message}"
        );
    }

    // A paid build that stalled: what is still needed and not in the shipyard, in words; null if
    // nothing is. The welders may reach it through a docked ship too.
    private static string Short(MyCubeGrid shipyard, Dictionary<string, int> parts)
    {
        var stock = Stock(shipyard);
        var lines = new List<string>();
        foreach (var (subtype, count) in parts.Select(p => (p.Key, p.Value)))
        {
            var item = Stations.ItemName(
                new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype)
            );
            var have = stock.TryGetValue(item, out var s) ? s / Amounts.Unit : 0;
            if (count > have)
                lines.Add($"{count - have} {Market.ItemTitle(item)}");
        }
        return lines.Count == 0 ? null : string.Join(", ", lines);
    }

    // A paid build whose ship is removed: the components in what is built so far go back into the
    // containers, and what doesn't fit is sold, as for a breakdown
    private static void KeepParts(MyCubeGrid shipyard, Order order)
    {
        var parts = new Dictionary<MyDefinitionId, MyFixedPoint>();
        Add(parts, ShipSoFar(shipyard, order));
        var containers = Containers(shipyard).ToList();
        foreach (var (id, amount) in parts.Select(p => (p.Key, p.Value)))
        {
            var over = Put(
                containers,
                (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(id),
                amount
            );
            if (over > 0)
                order.Sold[Stations.ItemName(id)] = over.RawValue;
        }
    }

    // --- a turn ends ---

    private static void EndTurn(MyCubeGrid shipyard, BuildOrder order, string message)
    {
        if (!endingTurns.Add(order.Id))
            return;
        FloatFree(shipyard, order.Player);
        var taken = new List<(MyObjectBuilder_PhysicalObject Content, MyFixedPoint Amount)>();
        var left = new Dictionary<string, long>();
        foreach (var inventory in TurnInventories(shipyard))
        {
            var items = inventory.GetItems();
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                var name = Stations.ItemName(item.Content.GetObjectId());
                left[name] = (left.TryGetValue(name, out var had) ? had : 0) + item.Amount.RawValue;
                taken.Add((item.Content, item.Amount));
                inventory.RemoveItemsAt(i);
            }
        }
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.EndTurn,
                Id = order.Id,
                Station = order.Station,
                Needed = left,
            },
            (ok, answer) =>
            {
                endingTurns.Remove(order.Id);
                if (!ok)
                {
                    // Tried again at the next check, the deadline being past or the shipyard empty
                    Plugin.Log.Error($"Shipyard order {order.Id}: the turn did not end: {answer}");
                    var containers = Containers(shipyard).ToList();
                    foreach (var (content, amount) in taken)
                        Put(containers, content, amount);
                    return;
                }
                Plugin.Log.Info(
                    $"Shipyard order {order.Id}: the turn of {Name(order.Player)} at {shipyard.DisplayName} ended: {answer}"
                );
                Tell(order.Player, answer.Length > 0 ? $"{message} {answer}" : message);
                Refresh(order.Station);
            }
        );
    }

    // The blocks go back to OCT and off; the player and the owner of a ship still docked are told
    private static void FloatFree(MyCubeGrid shipyard, long player)
    {
        foreach (var ship in ShipyardTurn.Take(shipyard).Distinct())
        {
            var owner = ship.BigOwners.FirstOrDefault();
            var text =
                $"'{ship.DisplayName}' floats free: the turn at {shipyard.DisplayName} is over and its connectors let go.";
            if (player != 0)
                Tell(player, text);
            if (owner != player && owner != 0)
                Tell(owner, text);
        }
    }

    // Puts the items into the containers; returns what did not fit
    private static MyFixedPoint Put(
        List<MyInventory> containers,
        MyObjectBuilder_PhysicalObject content,
        MyFixedPoint amount
    )
    {
        var id = content.GetObjectId();
        foreach (var inventory in containers)
        {
            if (amount <= 0)
                break;
            var fits = MyFixedPoint.Min(amount, inventory.ComputeAmountThatFits(id));
            if (fits <= 0)
                continue;
            inventory.AddItems(fits, content);
            amount -= fits;
        }
        return amount;
    }

    /// <summary>!oct shipyard done: the player's turn at a shipyard of this node ends now.</summary>
    public static string Done(long player)
    {
        var answers = new List<string>();
        var synchronous = true;
        void Answer(string text)
        {
            if (synchronous)
                answers.Add(text);
            else
                Tell(player, text);
        }
        var shipyards = StationTables()
            .Select(s => (Station: s.Key, Shipyard: FindShipyard(s.Value[0])))
            .Where(s => s.Shipyard != null && Plugin.Simulated(s.Shipyard))
            .ToList();
        var waiting = shipyards.Count;
        var found = false;
        foreach (var (station, shipyard) in shipyards)
            Builds.Read(
                new BuildRequest { Verb = BuildVerb.Queue, Station = station },
                (text, _) =>
                {
                    waiting--;
                    var order = text == null
                        ? null
                        : DecodeQueue(text).FirstOrDefault(o => o.Player == player && o.Active);
                    if (order != null)
                    {
                        found = true;
                        if (order.Started)
                            Answer($"'{order.Ship}' is being built, your turn can't end now.");
                        else
                        {
                            Answer("Your turn ends now.");
                            EndTurn(
                                shipyard,
                                order,
                                $"You ended your turn at {shipyard.DisplayName}."
                            );
                        }
                    }
                    else if (waiting == 0 && !found)
                        Answer("You have no turn at a shipyard here.");
                }
            );
        synchronous = false;
        if (shipyards.Count == 0)
            return "You have no turn at a shipyard here.";
        return answers.Count > 0 ? string.Join("\n", answers) : "The shipyard answers in chat.";
    }

    // --- a towed grid's breakdown ---

    /// <summary>
    /// A towed grid over the player's limits arrives at the station: it is broken down at once.
    /// With the shipyard free that is into its containers, with a turn for the player. With the
    /// shipyard busy all of it is sold, and nobody's build is disturbed.
    /// </summary>
    public static void Tow(string station, long player, MyCubeGrid grid)
    {
        if (!towing.Add(grid.EntityId))
            return;
        var name = grid.DisplayName;
        void Sell(string where)
        {
            var parts = Parts(grid);
            Builds.Execute(
                Guid.NewGuid(),
                new BuildRequest
                {
                    Verb = BuildVerb.Sale,
                    Station = station,
                    Player = player,
                    Needed = parts.ToDictionary(
                        p => Stations.ItemName(p.Key),
                        p => p.Value.RawValue
                    ),
                },
                (ok, answer) =>
                {
                    if (!ok)
                    {
                        towing.Remove(grid.EntityId);
                        Plugin.Log.Error($"Towed grid {grid.EntityId} '{name}' not sold: {answer}");
                        return;
                    }
                    foreach (var part in Grids(grid))
                        part.Close();
                    Plugin.Log.Info(
                        $"Towed grid {grid.EntityId} '{name}' of {Name(player)} sold for {answer}: {where}"
                    );
                    Tell(
                        player,
                        $"'{name}' was too big for you to keep and {where}, so the station broke it down and bought all of it for {answer}."
                    );
                }
            );
        }
        var shipyard = StationTables().TryGetValue(station, out var tables)
            ? FindShipyard(tables[0])
            : null;
        if (shipyard == null || !Plugin.Simulated(shipyard))
        {
            Sell("the shipyard is out of reach");
            return;
        }
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Tow,
                Station = station,
                Player = player,
                Grid = grid.EntityId,
                Ship = name,
                Pcu = Grids(grid).Sum(g => g.BlocksPCU),
            },
            (ok, answer, order) =>
            {
                if (!ok)
                {
                    towing.Remove(grid.EntityId);
                    Plugin.Log.Error($"Towed grid {grid.EntityId} '{name}': {answer}");
                }
                else if (order == null)
                    Sell($"{shipyard.DisplayName} is busy");
                else
                    Breakdown(shipyard, order);
            }
        );
    }

    /// <summary>A tow gave the player this grid: they have a while to move it before Sweep counts it as any wreck.</summary>
    public static void GivenByTow(MyCubeGrid grid, long player)
    {
        var minutes = Config.ShipyardTowGraceMinutes;
        // The order repeats on a grid that can't have an owner
        if (towGiven.TryGetValue(grid.EntityId, out var until) && DateTime.UtcNow < until)
            return;
        towGiven[grid.EntityId] = DateTime.UtcNow.AddMinutes(minutes);
        Tell(
            player,
            $"Move '{grid.DisplayName}' out of the shipyard within {minutes} min, or it may be cleared away."
        );
    }

    private static void Breakdown(MyCubeGrid shipyard, BuildOrder order)
    {
        if (!MyEntities.TryGetEntityById(order.Grid, out MyCubeGrid grid) || grid.MarkedForClose)
        {
            // Lost after the second check: it may be broken down already
            Plugin.Log.Warning(
                $"Shipyard order {order.Id}: towed grid {order.Grid} is not here to break down"
            );
            ShipyardTurn.Give(shipyard, order.Player);
            After(
                order,
                0,
                false,
                new Dictionary<string, long>(),
                $"'{order.Ship}' is gone, there is nothing to break down at {shipyard.DisplayName}."
            );
            return;
        }
        var parts = Parts(grid);
        Builds.Read(
            new BuildRequest
            {
                Verb = BuildVerb.Prices,
                Station = order.Station,
                Needed = parts.ToDictionary(p => Stations.ItemName(p.Key), p => p.Value.RawValue),
            },
            (text, error) =>
            {
                if (text == null)
                {
                    // The queue still says it is building: Lost comes back here
                    Plugin.Log.Warning($"Shipyard order {order.Id}: no prices: {error}");
                    return;
                }
                var prices = text.Split('\n')
                    .Select(l => l.Split('\t'))
                    .Where(f => f.Length == 2)
                    .ToDictionary(f => f[0], f => BuildQuote.L(f[1]));
                if (grid.MarkedForClose)
                    return;
                BreakDown(shipyard, order, grid, parts, prices);
            }
        );
    }

    private static void BreakDown(
        MyCubeGrid shipyard,
        BuildOrder order,
        MyCubeGrid grid,
        Dictionary<MyDefinitionId, MyFixedPoint> parts,
        Dictionary<string, long> prices
    )
    {
        if (ShipyardTurn.Holder(shipyard) != order.Player)
            ClearForTurn(shipyard);
        ShipyardTurn.Give(shipyard, order.Player);
        var containers = Containers(shipyard).ToList();
        var items = parts
            .Select(p =>
            {
                var definition = MyDefinitionManager.Static.GetPhysicalItemDefinition(p.Key);
                var name = Stations.ItemName(p.Key);
                return new BreakdownItem(
                    name,
                    p.Value.RawValue,
                    definition.Volume * (double)p.Value,
                    prices.TryGetValue(name, out var price) ? price : 0,
                    definition.HasIntegralAmounts
                );
            })
            .ToList();
        var room = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        var sold = BuildBook.Overflow(items, room);
        var blocks = Grids(grid).Sum(g => g.BlocksCount);
        foreach (var part in Grids(grid))
            part.Close();
        foreach (var (id, amount) in parts.Select(p => (p.Key, p.Value)))
        {
            var name = Stations.ItemName(id);
            var keep = amount;
            if (sold.TryGetValue(name, out var raw))
                keep.RawValue -= raw;
            if (keep <= 0)
                continue;
            var over = Put(
                containers,
                (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(id),
                keep
            );
            if (over > 0)
                sold[name] = (sold.TryGetValue(name, out var had) ? had : 0) + over.RawValue;
        }
        Plugin.Log.Info(
            $"Shipyard order {order.Id}: towed grid {order.Grid} '{order.Ship}' broken down at {shipyard.DisplayName}: "
                + $"{blocks} blocks into {items.Count} items, {sold.Count} items sold for lack of room"
        );
        After(
            order,
            0,
            false,
            sold,
            $"'{order.Ship}' was too big for you to keep, so {shipyard.DisplayName} broke it down at once: its {blocks:N0} blocks' components and its cargo are in the containers. "
                + string.Format(TakeWithin(), shipyard.DisplayName)
        );
    }

    // What the grid and its rotor and piston parts hold
    private static Dictionary<MyDefinitionId, MyFixedPoint> Parts(MyCubeGrid grid)
    {
        var parts = new Dictionary<MyDefinitionId, MyFixedPoint>();
        Add(parts, Grids(grid).SelectMany(g => g.GetBlocks()));
        return parts;
    }

    // Each block's mounted components, what waits in its stockpile, and its inventories
    private static void Add(
        Dictionary<MyDefinitionId, MyFixedPoint> parts,
        IEnumerable<MySlimBlock> blocks
    )
    {
        void Count(MyDefinitionId id, MyFixedPoint amount) =>
            parts[id] = (parts.TryGetValue(id, out var had) ? had : 0) + amount;
        foreach (var block in blocks)
        {
            var stack = block.ComponentStack;
            for (var i = 0; i < stack.GroupCount; i++)
            {
                var mounted = stack.GetGroupInfo(i).MountedCount;
                if (mounted > 0)
                    Count(block.BlockDefinition.Components[i].DeconstructItem.Id, mounted);
            }
            if (!block.StockpileEmpty)
                foreach (var item in block.GetObjectBuilder().ConstructionStockpile.Items)
                    Count(item.PhysicalContent.GetObjectId(), item.Amount);
            if (block.FatBlock == null)
                continue;
            for (var i = 0; i < block.FatBlock.InventoryCount; i++)
                foreach (var item in block.FatBlock.GetInventory(i).GetItems())
                    Count(item.Content.GetObjectId(), item.Amount);
        }
    }

    // --- a ship in the way ---

    // The next order waits for the grid in the shipyard's build space. Its owner is told; after
    // ShipyardClearMinutes it is moved out of the way. A restart starts the wait over.
    private static void ClearWay(MyCubeGrid shipyard, MyCubeGrid obstacle)
    {
        var id = shipyard.EntityId;
        if (!blockers.TryGetValue(id, out var blocker) || blocker.Grid != obstacle.EntityId)
        {
            blockers[id] = (obstacle.EntityId, DateTime.UtcNow);
            if (obstacle.BigOwners.FirstOrDefault() is var owner and not 0)
                Tell(
                    owner,
                    $"'{obstacle.DisplayName}' blocks the next order at {shipyard.DisplayName}. "
                        + $"Fly it out within {Config.ShipyardClearMinutes} min, or it is moved out."
                );
            return;
        }
        if (DateTime.UtcNow - blocker.At < TimeSpan.FromMinutes(Config.ShipyardClearMinutes))
            return;
        blockers.Remove(id);
        MoveOut(shipyard, obstacle);
    }

    private static void MoveOut(MyCubeGrid shipyard, MyCubeGrid obstacle)
    {
        var box = BoundingBoxD.CreateInvalid();
        foreach (var node in MyCubeGridGroups.Static.Physical.GetGroup(obstacle).Nodes)
            box.Include(node.NodeData.PositionComp.WorldAABB);
        var radius = (float)box.HalfExtents.Length();
        var side = shipyard.WorldMatrix.Up;
        var reach = shipyard.PositionComp.LocalAABB.HalfExtents.Y + radius + 50;
        var target = MyEntities.FindFreePlace(
            shipyard.PositionComp.WorldAABB.Center + side * reach,
            radius,
            allowSafezones: true
        );
        if (target == null)
        {
            Plugin.Log.Warning(
                $"Shipyard {shipyard.DisplayName}: no free place to move '{obstacle.DisplayName}' to"
            );
            return;
        }
        var matrix = obstacle.WorldMatrix;
        matrix.Translation += target.Value - box.Center;
        obstacle.Teleport(matrix);
        Plugin.Log.Info(
            $"Shipyard {shipyard.DisplayName}: '{obstacle.DisplayName}' moved out of the build space, to {target.Value}"
        );
        if (obstacle.BigOwners.FirstOrDefault() is var owner and not 0)
            Tell(
                owner,
                $"'{obstacle.DisplayName}' was moved out of {shipyard.DisplayName}: the next order was waiting."
            );
    }
}
