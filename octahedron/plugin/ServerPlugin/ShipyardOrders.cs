using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;

namespace ServerPlugin;

// A player loads a blueprint into a station's hologram table, reads the quote on the table's
// large screen and presses the button next to it (ShipyardConfirmButtonName) to queue the
// ship at the station's shipyard; pressing it again cancels their entry before its build starts.
// The queue and the build ledgers are kept by Builds, on the authority; the station's queue
// panels show it.
//
// A ship is a free build for a player under FreeBuildPcuLimit, once FreeBuildCooldownMinutes
// passed since their last free build ended (FreePrints), without the blocks in
// FreeBuildBlockedBlocks. Any other ship is a paid build: its components, and the ice for the
// hydrogen to fill its H2 tanks to ShipyardHydrogenFillPercent, come from the payer's ledger at
// the station (the player's, or their faction's with the kiosk's Faction switch on), then the
// cheapest listings and OCT, plus the labor fee; hydrogen in the tanks of the player's ships
// docked at the station counts as ice first.
//
// When the shipyard is free, the next order starts. The ship starts as a static grid of one of
// its blocks, centered in the shipyard's free space, with an NPC-owned projector and battery
// (the scaffold) next to that block projecting the whole blueprint. The shipyard's cargo
// containers get the components of the remaining blocks, from the build ledger for a paid
// build, and its OmniArc welders (ToolCore) are switched on. Projected blocks welded onto the
// ship are the player's, owner and BuiltBy. The order ends when every block is complete or
// after ShipyardOrderMinutes: the welders go off, the scaffold is removed, the leftover
// components go back to the payer's ledger (a free build's are removed), a paid build's H2
// tanks are filled, and the ship, finished or not, becomes the player's dynamic ship.
//
// The running order lives in the world, in the scaffold projector's CustomData, so it survives
// a restart and moves with the station's partition on a cluster: every node checks the
// shipyards it simulates every 10 s.
internal static class ShipyardOrders
{
    private const string ScaffoldName = "Shipyard Order";
    private const string ShootProperty = "ToolCore_Shoot";
    private const string TrackProperty = "ToolCore_TrackTargets";
    private const string Hydrogen = "Ore/Ice";
    private const int UpdateTicks = 600;
    private const int QuoteTicks = 60;

    private sealed class Order
    {
        public MyProjectorBase Projector;
        public long Player;
        public DateTime Ends;
        public int Blocks;
        public Dictionary<MyDefinitionId, int> Components;

        // The queue entry; empty for an order from before the queues
        public string Id = "";
        public bool Free = true;

        // Liters for the ship's H2 tanks at the end
        public double Hydrogen;

        // The support arm's blocks on the rotor head's grid, the projector's included; empty for
        // a ship built from a projector on itself
        public List<Vector3I> Arm = [];

        // The arm's connector, and the ship's block the projector took the place of
        public Vector3I? Connector;
        public MyDefinitionId? GivenUp;

        // The finished ship's entity id while it docks to the arm
        public long Docking;

        // The blueprint's name
        public string Name = "";
    }

    // A finished ship docking to its arm's connector, by shipyard entity id
    private sealed class Dock
    {
        public MyCubeGrid Shipyard;
        public Order Order;
        public MyCubeGrid Ship;
        public MyShipConnector Connector;
        public int Ticks;
        public string Why;
    }

    private static readonly Dictionary<long, Dock> docks = new();

    // By shipyard entity id
    private static readonly Dictionary<long, Order> orders = new();

    // Shipyards whose next order is on its way from the authority
    private static readonly HashSet<long> starting = new();

    // Shipyards whose queue shows a running build without its ship, counted per check
    private static readonly Dictionary<long, int> lost = new();

    // The shipyard's free space around its welders, in its grid's cells
    private static readonly Dictionary<long, BoundingBoxI> freeBoxes = new();

    // The quote each hologram table shows, by table entity id
    private static readonly Dictionary<long, TableQuote> quotes = new();

    // Tables whose blueprint a player just loaded: the quote is for them
    private static readonly Dictionary<long, long> loaded = new();

    private static int ticks;

    private static string blockedPatterns;
    private static Regex blockedRegex;

    private static OctahedronConfig Config => Plugin.Config;

    // --- a blueprint at a table, for a player ---

    private sealed class Plan
    {
        public string Error;
        public MyProjectorBase Table;
        public MyCubeGrid Shipyard;
        public string Station;
        public long Player;
        public List<MyObjectBuilder_CubeGrid> Blueprint;
        public string Ship = "";
        public int Pcu;
        public bool Free;

        // Why it is a paid build, and which rule: pcu, blocked or cooldown
        public string Paid;
        public string PaidRule = "";
        public LedgerOwner Payer;
        public string PayerName = "";
        public long Fee;

        // Every block's components and the ice for the hydrogen, raw
        public Dictionary<string, long> Needed = new();
        public double HydrogenLiters;

        // Liters the player's docked ships have, at most what the build needs
        public double ShipHydrogen;

        public string Key =>
            $"{Ship}|{Pcu}|{Blueprint?.Sum(g => g.CubeBlocks.Count)}|{Free}|{PaidRule}|{Payer}|{Error}";
    }

    private sealed class TableQuote
    {
        public long Player;
        public string Key;
        public BuildQuote Quote;
        public DateTime At;
    }

    private static Plan MakePlan(long identityId, MyProjectorBase table)
    {
        var plan = new Plan { Table = table, Player = identityId };
        plan.Station = StationOf(table.CubeGrid);
        plan.Shipyard = FindShipyard(table);
        var identity = MySession.Static.Players.TryGetIdentity(identityId);
        if (identity == null)
            return Fail(plan, "Unknown player.");
        if (plan.Station == null || plan.Shipyard == null)
            return Fail(plan, "This station has no shipyard.");
        var blueprint = ((MyObjectBuilder_ProjectorBase)table.GetObjectBuilderCubeBlock(true))
            .ProjectedGrids?.Select(g => (MyObjectBuilder_CubeGrid)g.Clone())
            .ToList();
        if (blueprint == null || blueprint.Count == 0)
            return Fail(plan, "Switch the hologram table on and load a blueprint into it first.");
        plan.Blueprint = blueprint;
        plan.Ship = blueprint[0].DisplayName ?? "Ship";
        plan.Pcu = blueprint.Sum(g => g.CalculatePCUs());
        var error = Fits(plan.Shipyard, blueprint);
        if (error != null)
            return Fail(plan, error);

        // Free, or why not
        var playerPcu = StarterShip.PlayerPcu(identity);
        var blocked = Blocked(blueprint, Config.FreeBuildBlockedBlocks);
        var wait = FreePrints.Wait(identityId);
        if (playerPcu >= Config.FreeBuildPcuLimit)
            (plan.PaidRule, plan.Paid) = (
                "pcu",
                $"you have {playerPcu:N0} PCU, free builds are for players under {Config.FreeBuildPcuLimit:N0}"
            );
        else if (!FreePrints.Ready)
            return Fail(plan, "The shipyard is busy, try again in a few seconds.");
        else if (wait > TimeSpan.Zero)
            (plan.PaidRule, plan.Paid) = (
                "cooldown",
                $"your next free ship is in {Math.Ceiling(wait.TotalMinutes)} min"
            );
        else if (blocked.Count > 0)
            (plan.PaidRule, plan.Paid) = (
                "blocked",
                $"a free ship can't have {string.Join(", ", blocked)}"
            );
        plan.Free = plan.Paid == null;
        if (plan.Free)
            return plan;

        var faction = MySession.Static.Factions.GetPlayerFaction(identityId);
        plan.Payer =
            Kiosks.FactionSwitch(identityId) && faction != null
                ? LedgerOwner.OfFaction(faction.FactionId)
                : LedgerOwner.Player(identityId);
        plan.PayerName = plan.Payer.Faction ? $"your faction {faction.Tag}" : "you";
        plan.Fee = BuildBook.Fee(plan.Pcu, Config.ShipyardLaborFeePer10Pcu);
        foreach (var c in Components(blueprint, skipFirst: false))
            plan.Needed[Ledgers.ItemName(c.Key)] = c.Value * LedgerBook.Unit;
        plan.HydrogenLiters =
            HydrogenCapacity(blueprint) * Config.ShipyardHydrogenFillPercent / 100;
        if (plan.HydrogenLiters > 0)
        {
            plan.Needed[Hydrogen] = IceFor(plan.HydrogenLiters, up: true);
            plan.ShipHydrogen = Math.Min(
                plan.HydrogenLiters,
                ShipTanks(plan).Sum(t => t.FilledRatio * t.Capacity)
            );
        }
        return plan;
    }

    private static Plan Fail(Plan plan, string error)
    {
        plan.Error = error;
        return plan;
    }

    // Why the shipyard can't build the blueprint at all, or null
    private static string Fits(MyCubeGrid shipyard, List<MyObjectBuilder_CubeGrid> blueprint)
    {
        var pcu = blueprint.Sum(g => g.CalculatePCUs());
        // The ordered ship is the player's, so GroupPcuLimit would stop welding it there
        if (Config.GroupPcuLimit > 0 && pcu > Config.GroupPcuLimit)
            return $"The blueprint has {pcu:N0} PCU, a ship at most {Config.GroupPcuLimit:N0}.";
        var main = blueprint[0];
        var arms = ShipyardArm.Rotors(shipyard).Count > 0;
        MyObjectBuilder_CubeBlock start = null;
        var outward = Vector3I.Zero;
        if (!arms && !FindStart(main, out start, out outward))
            return "The blueprint's main grid needs a 1x1x1 block on its outside to start from.";
        var size = Size(blueprint, start, outward, out _);
        var space = FreeSpace(shipyard);
        var room = space.HalfExtent * 2;
        if (size.X > room.X || size.Y > room.Y || size.Z > room.Z)
            return $"The ship is {Meters(size)} (width, height, length), {shipyard.DisplayName} builds up to {Meters(room)}.";
        var needed = Components(blueprint, skipFirst: !arms)
            .Sum(c => MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value);
        var capacity = Containers(shipyard).Sum(i => (double)i.MaxVolume);
        if (needed > capacity)
            return $"The ship's components take {needed:N0} m³, {shipyard.DisplayName}'s containers hold {capacity:N0} m³.";
        if (arms)
        {
            ShipyardArm.Plan(
                shipyard,
                space,
                ShipRotation(shipyard, main),
                blueprint,
                out var error
            );
            return error;
        }
        return null;
    }

    // The ship's size along the shipyard, turned so its cockpit points along it, the scaffold on
    // the start block included
    private static Vector3D Size(
        List<MyObjectBuilder_CubeGrid> blueprint,
        MyObjectBuilder_CubeBlock start,
        Vector3I outward,
        out BoundingBoxD bounds
    )
    {
        var main = blueprint[0];
        var cube = MyDefinitionManager.Static.GetCubeSize(main.GridSizeEnum);
        bounds = TableHologram.Bounds(blueprint);
        if (start != null)
        {
            Vector3I startCell = start.Min;
            foreach (var cell in new[] { startCell + outward, startCell + 2 * outward })
                bounds.Include(
                    new BoundingBoxD(((Vector3D)cell - 0.5) * cube, ((Vector3D)cell + 0.5) * cube)
                );
        }
        return Vector3D.Abs(Vector3D.TransformNormal(bounds.Size, CockpitTurn(main)));
    }

    // Turns the main grid so its cockpit points along the shipyard, up its up
    private static MatrixD CockpitTurn(MyObjectBuilder_CubeGrid main)
    {
        var (forward, up) = StarterShip.CockpitFrame(main);
        return MatrixD.Transpose(MatrixD.CreateWorld(Vector3D.Zero, forward, up));
    }

    // The ship's main grid's orientation in the world when built
    private static MatrixD ShipRotation(MyCubeGrid shipyard, MyObjectBuilder_CubeGrid main) =>
        CockpitTurn(main) * shipyard.WorldMatrix.GetOrientation();

    // --- the button ---

    // The confirm button's press on the server, before the game runs its toolbar item, which
    // is only there so the clients offer the button at all
    [HarmonyPatch(typeof(MyButtonPanel), nameof(MyButtonPanel.ActivateButton))]
    private static class ButtonPress
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(MyButtonPanel __instance)
        {
            if (!Sync.IsServer || !Config.Enabled || !IsConfirmButton(__instance))
                return true;
            try
            {
                var sender = MyEventContext.Current.Sender.Value;
                var identity = MySession.Static.Players.TryGetIdentityId(sender);
                var table = TableBy(__instance);
                if (identity == 0)
                    return false;
                if (table == null)
                    Tell(identity, "This button has no hologram table next to it.");
                else
                    Press(identity, table, text => Tell(identity, text));
            }
            catch (Exception e)
            {
                Plugin.Log.Error("Shipyard button failed", e);
            }
            return false;
        }
    }

    private static bool IsConfirmButton(MyTerminalBlock block) =>
        !string.IsNullOrWhiteSpace(Config.ShipyardConfirmButtonName)
        && string.Equals(
            block.CustomName?.ToString(),
            Config.ShipyardConfirmButtonName,
            StringComparison.OrdinalIgnoreCase
        );

    // The station hologram table nearest the button, within the table reach
    private static MyProjectorBase TableBy(MyCubeBlock button)
    {
        var position = button.PositionComp.GetPosition();
        return Grids(button.CubeGrid)
            .SelectMany(g => g.GetFatBlocks<MyProjectorBase>())
            .Where(IsTable)
            .Select(t =>
                (Table: t, Distance: Vector3D.Distance(t.PositionComp.GetPosition(), position))
            )
            .Where(t => t.Distance <= Config.StarterShipTableReach)
            .OrderBy(t => t.Distance)
            .Select(t => t.Table)
            .FirstOrDefault();
    }

    private static bool IsTable(MyProjectorBase table) =>
        table.BlockDefinition.Id.SubtypeName == Config.StarterShipTableSubtype
        && MySession.Static.Players.IdentityIsNpc(table.OwnerId);

    // The button gets a toolbar item, its own On switch, so that clients let players press it
    private static void ArmButton(MyButtonPanel button)
    {
        if (button.Toolbar.GetItemAtIndex(0) != null)
            return;
        var item = MyToolbarItemFactory.TerminalBlockObjectBuilderFromBlock(button);
        item._Action = "OnOff_On";
        button.Toolbar.SetItemAtIndex(0, MyToolbarItemFactory.CreateToolbarItem(item));
        Plugin.Log.Info($"Shipyard button {button.EntityId} armed");
    }

    // !oct order presses the button of the nearest table for an admin
    public static string Request(long identityId, string tableName)
    {
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return "Spawn first, then order at a hologram table.";
        var table = StarterShip.FindTable(character, tableName, Config);
        if (table == null)
            return $"Stand within {Config.StarterShipTableReach:0} m of a station's hologram table.";
        var answers = new List<string>();
        bool synchronous = true;
        Press(
            identityId,
            table,
            text =>
            {
                if (synchronous)
                    answers.Add(text);
                else
                    Tell(identityId, text);
            }
        );
        synchronous = false;
        return answers.Count > 0 ? string.Join("\n", answers) : "The shipyard answers in chat.";
    }

    /// <summary>A press of the table's button: cancels the player's queue entry, shows their quote or orders.</summary>
    public static void Press(long identityId, MyProjectorBase table, Action<string> tell)
    {
        if (Config.ShipyardOrderMinutes <= 0)
        {
            tell("Shipyard orders are off on this server.");
            return;
        }
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
        {
            tell("Spawn first, then order at a hologram table.");
            return;
        }
        var plan = MakePlan(identityId, table);
        if (plan.Station == null || plan.Shipyard == null)
        {
            tell(plan.Error);
            return;
        }
        if (!Plugin.Simulated(table.CubeGrid) || !Plugin.Simulated(plan.Shipyard))
        {
            tell("The station is busy, try again in a few seconds.");
            return;
        }
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Queue, Station = plan.Station },
            (text, error) =>
            {
                if (text == null)
                {
                    tell(error);
                    return;
                }
                var mine = DecodeQueue(text).FirstOrDefault(o => o.Player == identityId);
                if (mine == null)
                    Place(plan, tell);
                else if (mine.Started)
                    tell($"'{mine.Ship}' is being built for you, it can't be canceled now.");
                else
                    Builds.Execute(
                        Guid.NewGuid(),
                        new BuildRequest
                        {
                            Verb = BuildVerb.Cancel,
                            Station = plan.Station,
                            Player = identityId,
                        },
                        (_, message) =>
                        {
                            tell(message);
                            Refresh(plan.Station);
                        }
                    );
            }
        );
    }

    private static void Place(Plan plan, Action<string> tell)
    {
        if (plan.Error != null)
        {
            ShowQuote(plan, null);
            tell(plan.Error);
            return;
        }
        quotes.TryGetValue(plan.Table.EntityId, out var shown);
        if (
            shown == null
            || shown.Player != plan.Player
            || shown.Key != plan.Key
            || (!plan.Free && shown.Quote == null)
            || DateTime.UtcNow - shown.At > TimeSpan.FromMinutes(5)
        )
        {
            Quote(
                plan,
                quote =>
                    tell(
                        quote == null && !plan.Free
                            ? "The station's market did not answer, try again."
                            : $"Your quote for '{plan.Ship}' is on the hologram table's screen"
                                + (
                                    plan.Free
                                        ? ", a free build."
                                        : $": a paid build ({plan.Paid}), {quote.Total:N0} SC paid by {plan.PayerName}."
                                )
                                + " Press the button again to order."
                    )
            );
            return;
        }

        var request = new BuildRequest
        {
            Verb = BuildVerb.Order,
            Station = plan.Station,
            Player = plan.Player,
            Ship = plan.Ship,
            Pcu = plan.Pcu,
            Blueprint = EncodeBlueprint(plan.Blueprint),
        };
        var drained = new List<(MyGasTank Tank, double Liters)>();
        if (!plan.Free)
        {
            var missing = shown.Quote.Short(Hydrogen);
            if (missing != null)
            {
                tell(
                    $"'{plan.Ship}' is a paid build ({plan.Paid}), and this station has "
                        + $"{Units(missing.Item, missing.Missing)} {Market.ItemTitle(missing.Item)} "
                        + "too few for sale. Deposit them into the ledger first."
                );
                return;
            }
            request.Payer = plan.Payer;
            request.Fee = plan.Fee;
            request.Needed = plan.Needed;
            request.MaxTotal = shown.Quote.Total;
            drained = Drain(plan);
            var liters = drained.Sum(d => d.Liters);
            if (liters > 0)
                request.Provided[Hydrogen] = IceFor(liters, up: false);
        }
        Builds.Execute(
            Guid.NewGuid(),
            request,
            (ok, message) =>
            {
                if (!ok)
                    foreach (var (tank, liters) in drained)
                        AddHydrogen(tank, liters);
                tell(message);
                if (ok)
                {
                    quotes.Remove(plan.Table.EntityId);
                    Write(
                        plan.Table,
                        Config.ShipyardQuoteSurface,
                        $"'{plan.Ship}' is in the queue for {Name(plan.Player)}.\n\nLoad a blueprint for a new quote."
                    );
                    Refresh(plan.Station);
                }
            }
        );
    }

    // --- quotes on the hologram table ---

    // A client loading a blueprint ends up here on the server too: the quote is for them
    [HarmonyPatch(typeof(MyProjectorBase), "SetNewBlueprint")]
    private static class SetNewBlueprint
    {
        private static void Postfix(MyProjectorBase __instance)
        {
            if (!Sync.IsServer || MyEventContext.Current.IsLocallyInvoked)
                return;
            var identity = MySession.Static.Players.TryGetIdentityId(
                MyEventContext.Current.Sender.Value
            );
            if (identity != 0)
                loaded[__instance.EntityId] = identity;
        }
    }

    private static void Quote(Plan plan, Action<BuildQuote> done)
    {
        if (plan.Free || plan.Error != null)
        {
            ShowQuote(plan, null);
            done(null);
            return;
        }
        var needed = new Dictionary<string, long>(plan.Needed);
        if (plan.ShipHydrogen > 0 && needed.ContainsKey(Hydrogen))
            needed[Hydrogen] = Math.Max(0, needed[Hydrogen] - IceFor(plan.ShipHydrogen, up: false));
        Builds.Read(
            new BuildRequest
            {
                Verb = BuildVerb.Quote,
                Station = plan.Station,
                Payer = plan.Payer,
                Fee = plan.Fee,
                Needed = needed,
            },
            (text, error) =>
            {
                BuildQuote quote = null;
                if (text != null)
                    try
                    {
                        quote = BuildQuote.Decode(text);
                    }
                    catch (FormatException e)
                    {
                        Plugin.Log.Error("Shipyard quote", e);
                    }
                if (quote != null)
                    ShowQuote(plan, quote);
                done(quote);
            }
        );
    }

    private static void ShowQuote(Plan plan, BuildQuote quote)
    {
        quotes[plan.Table.EntityId] = new TableQuote
        {
            Player = plan.Player,
            Key = plan.Key,
            Quote = quote,
            At = DateTime.UtcNow,
        };
        var text = new StringBuilder();
        text.AppendLine($"Shipyard quote for {Name(plan.Player)}");
        if (plan.Blueprint != null)
            text.AppendLine($"'{plan.Ship}', {plan.Pcu:N0} PCU");
        if (plan.Error != null)
            text.AppendLine().AppendLine(plan.Error);
        else if (plan.Free)
            text.AppendLine()
                .AppendLine("A free build.")
                .AppendLine()
                .AppendLine("Press the button to order, again to cancel.");
        else if (quote != null)
        {
            text.AppendLine($"A paid build: {plan.Paid}. Paid by {plan.PayerName}.");
            text.AppendLine();
            text.Append(
                BuildBook.QuoteText(
                    quote,
                    Hydrogen,
                    plan.ShipHydrogen > 0 ? IceFor(plan.ShipHydrogen, up: false) : 0,
                    Market.ItemTitle,
                    Units
                )
            );
            if (plan.HydrogenLiters > 0)
                text.AppendLine(
                    $"Hydrogen: {plan.HydrogenLiters:N0} L for the H2 tanks, as {Config.ShipyardHydrogenLitersPerIce:0.##} L per kg of ice"
                );
            if (quote.Lines.Any(l => l.Item == Hydrogen && l.Missing > 0))
                text.AppendLine("Too little ice for sale here: the H2 tanks get less hydrogen.");
            text.AppendLine();
            text.AppendLine(
                quote.Short(Hydrogen) == null
                    ? "Press the button to order, again to cancel."
                    : "Not everything is for sale here: deposit it into the ledger first."
            );
        }
        Write(plan.Table, Config.ShipyardQuoteSurface, text.ToString());
    }

    private static void Write(IMyTextSurfaceProvider provider, int index, string text)
    {
        if (index < 0 || index >= provider.SurfaceCount)
            return;
        var surface = provider.GetSurface(index);
        var lines = text.Split('\n').Length;
        surface.ContentType = ContentType.TEXT_AND_IMAGE;
        surface.Font = "Monospace";
        surface.FontSize = (float)Math.Max(0.3, Math.Min(0.7, 0.7 * 16 / Math.Max(16, lines)));
        surface.TextPadding = 2;
        if (surface.GetText() != text)
            surface.WriteText(text);
    }

    // --- the update ---

    public static void Update()
    {
        ticks++;
        if (docks.Count > 0)
            UpdateDocks();
        if (ticks % QuoteTicks == 0 && loaded.Count > 0)
        {
            foreach (var (tableId, player) in loaded.ToList())
                if (
                    MyEntities.TryGetEntityById(tableId, out MyProjectorBase table)
                    && IsTable(table)
                )
                    Quote(MakePlan(player, table), _ => { });
            loaded.Clear();
        }
        if (ticks % UpdateTicks != 0)
            return;

        var shipyards = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(ShipyardZoneWelding.IsShipyard)
            .ToList();
        foreach (var shipyard in shipyards)
            Check(shipyard);
        foreach (var station in StationTables())
            Refresh(station.Key, station.Value);
    }

    private static void Check(MyCubeGrid shipyard)
    {
        if (!Plugin.Simulated(shipyard))
        {
            orders.Remove(shipyard.EntityId);
            return;
        }
        if (docks.ContainsKey(shipyard.EntityId))
            return;
        orders.TryGetValue(shipyard.EntityId, out var order);
        var projector = ShipyardArm
            .Rotors(shipyard)
            .Select(r => r.TopGrid)
            .Concat(GridsIn(FreeSpace(shipyard)))
            .SelectMany(g => g.GetFatBlocks<MyProjectorBase>())
            .FirstOrDefault(IsScaffold);
        if (projector == null)
        {
            if (order != null)
                End(shipyard, order, "the ship is gone");
            return;
        }
        if (order?.Projector != projector)
        {
            order = Restore(projector);
            if (order == null)
                return;
            orders[shipyard.EntityId] = order;
            Plugin.Log.Info(
                $"Shipyard order '{projector.CubeGrid.DisplayName}' ({projector.CubeGrid.EntityId}) "
                    + $"of {Name(order.Player)} picked up at {shipyard.DisplayName}, until {order.Ends:u}"
            );
        }

        if (order.Arm.Count > 0)
        {
            if (order.Docking != 0)
            {
                // Restarted while the ship docked: it is split off already
                MyEntities.TryGetEntityById(order.Docking, out MyCubeGrid ship);
                orders.Remove(shipyard.EntityId);
                Welders(shipyard, false);
                Docked(shipyard, order, ship, false, null);
                return;
            }
            // The arm first: the projection starts when its projector is complete
            if (!projector.SlimBlock.IsFullIntegrity)
            {
                if (DateTime.UtcNow >= order.Ends)
                    End(shipyard, order, "the support arm unfinished");
                else
                    Welders(shipyard, true);
                return;
            }
            if (!projector.Enabled)
            {
                projector.Enabled = true;
                Plugin.Log.Info(
                    $"Shipyard order {order.Id} at {shipyard.DisplayName}: the arm's projector is complete, projecting"
                );
            }
        }
        var built =
            order.Arm.Count > 0
                ? ShipBlocks(shipyard, order).Count(b => b.IsFullIntegrity)
                : Built(projector.CubeGrid);
        if (built >= order.Blocks)
            End(shipyard, order, null);
        else if (DateTime.UtcNow >= order.Ends)
            End(shipyard, order, $"{built} of {order.Blocks} blocks complete");
        else
            Welders(shipyard, true);
    }

    // Every station's hologram tables, by station name
    private static Dictionary<string, List<MyProjectorBase>> StationTables()
    {
        var tables = new Dictionary<string, List<MyProjectorBase>>();
        var seen = new HashSet<long>();
        foreach (var faction in MySession.Static.Factions)
        foreach (var entry in faction.Value.Stations)
        {
            if (
                !MyEntities.TryGetEntityById(entry.StationEntityId, out MyCubeGrid grid)
                || !Plugin.Simulated(grid)
            )
                continue;
            var station = StationOf(grid);
            if (station == null)
                continue;
            foreach (var g in Grids(grid))
            foreach (var table in g.GetFatBlocks<MyProjectorBase>())
                if (IsTable(table) && seen.Add(table.EntityId))
                {
                    if (!tables.TryGetValue(station, out var list))
                        tables[station] = list = [];
                    list.Add(table);
                }
        }
        return tables;
    }

    private static void Refresh(string station)
    {
        if (StationTables().TryGetValue(station, out var tables))
            Refresh(station, tables);
    }

    // The station's buttons, quotes and queue panels, and its shipyard's next order
    private static void Refresh(string station, List<MyProjectorBase> tables)
    {
        var grids = Grids(tables[0].CubeGrid);
        foreach (var button in grids.SelectMany(g => g.GetFatBlocks<MyButtonPanel>()))
            if (IsConfirmButton(button))
                ArmButton(button);
        foreach (var table in tables)
            if (quotes.TryGetValue(table.EntityId, out var shown))
                Quote(MakePlan(shown.Player, table), _ => { });

        var shipyard = FindShipyard(tables[0]);
        if (shipyard == null || !Plugin.Simulated(shipyard))
            return;
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Queue, Station = station },
            (text, _) =>
            {
                if (text == null || shipyard.MarkedForClose)
                    return;
                var queue = DecodeQueue(text);
                ShowQueue(grids, shipyard, queue);
                if (queue.Count == 0)
                    return;
                if (queue[0].Started)
                    Lost(shipyard, queue[0]);
                else
                    Start(station, shipyard);
            }
        );
    }

    private static List<BuildOrder> DecodeQueue(string text) =>
        text.Split('\n').Where(l => l.Length > 0).Select(BuildBook.DecodeOrder).ToList();

    private static void ShowQueue(
        List<MyCubeGrid> grids,
        MyCubeGrid shipyard,
        List<BuildOrder> queue
    )
    {
        if (string.IsNullOrWhiteSpace(Config.ShipyardQueuePanelName))
            return;
        var text = new StringBuilder($"{shipyard.DisplayName} queue\n\n");
        if (queue.Count == 0)
            text.AppendLine("No ships in the queue.");
        orders.TryGetValue(shipyard.EntityId, out var running);
        int place = 1;
        foreach (var o in queue)
        {
            var state = o.Started
                ? running != null
                    ? $"building, done by {running.Ends:HH:mm} UTC at the latest"
                    : "building"
                : o.Free
                    ? "waiting, free"
                    : "waiting, paid";
            text.AppendLine(
                $"{place++}. '{o.Ship}' ({o.Pcu:N0} PCU) for {Name(o.Player)}: {state}"
            );
        }
        var obstacle = running == null ? Obstacle(shipyard) : null;
        if (obstacle != null && queue.Count > 0)
            text.AppendLine()
                .AppendLine($"Waiting for '{obstacle.DisplayName}' to leave the shipyard.");
        foreach (var panel in grids.SelectMany(g => g.GetFatBlocks<MyTextPanel>()))
            if (
                string.Equals(
                    panel.CustomName?.ToString(),
                    Config.ShipyardQueuePanelName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                Write(panel, 0, text.ToString());
    }

    // --- starting the next order ---

    private static void Start(string station, MyCubeGrid shipyard)
    {
        if (
            orders.ContainsKey(shipyard.EntityId)
            || docks.ContainsKey(shipyard.EntityId)
            || !starting.Add(shipyard.EntityId)
        )
            return;
        if (Obstacle(shipyard) != null)
        {
            starting.Remove(shipyard.EntityId);
            return;
        }
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Next, Station = station },
            (text, _) =>
            {
                if (string.IsNullOrEmpty(text) || shipyard.MarkedForClose)
                {
                    starting.Remove(shipyard.EntityId);
                    return;
                }
                var next = BuildBook.DecodeOrder(text);
                var blueprint = DecodeBlueprint(next.Blueprint);
                var error =
                    blueprint == null || blueprint.Count == 0
                        ? "its blueprint could not be read"
                        : Fits(shipyard, blueprint);
                if (error != null)
                {
                    // Not started: the build ledger is still whole
                    Finish(
                        next,
                        new Dictionary<string, long>(),
                        0,
                        refund: true,
                        $"'{next.Ship}' can't be built: {error}"
                    );
                    starting.Remove(shipyard.EntityId);
                    return;
                }
                Builds.Execute(
                    Guid.NewGuid(),
                    new BuildRequest
                    {
                        Verb = BuildVerb.Start,
                        Id = next.Id,
                        Station = station,
                    },
                    (ok, message) =>
                    {
                        starting.Remove(shipyard.EntityId);
                        if (!ok)
                        {
                            Plugin.Log.Warning(
                                $"Shipyard order {next.Id} did not start: {message}"
                            );
                            return;
                        }
                        error = Create(shipyard, next, blueprint);
                        if (error != null)
                            Finish(
                                next,
                                Taken(next),
                                0,
                                refund: true,
                                $"'{next.Ship}' can't be built: {error}"
                            );
                    }
                );
            }
        );
    }

    // What a started paid build took out of its build ledger, raw
    private static Dictionary<string, long> Taken(BuildOrder order) =>
        order.Free
            ? new Dictionary<string, long>()
            : order.Items.Where(i => i.Key != Hydrogen).ToDictionary(i => i.Key, i => i.Value);

    // The ship's first block and the scaffold, the components loaded; null when it started
    private static string Create(
        MyCubeGrid shipyard,
        BuildOrder next,
        List<MyObjectBuilder_CubeGrid> blueprint
    )
    {
        var space = FreeSpace(shipyard);
        var obstacle = Obstacle(shipyard);
        if (obstacle != null)
            return $"'{obstacle.DisplayName}' is in {shipyard.DisplayName}";
        if (ShipyardArm.Rotors(shipyard).Count > 0)
            return CreateWithArm(shipyard, next, blueprint, space);
        var main = blueprint[0];
        FindStart(main, out var start, out var outward);
        // The projector anchors the projection's first block, so the start block goes first
        main.CubeBlocks.Remove(start);
        main.CubeBlocks.Insert(0, start);
        Vector3I startCell = start.Min;
        var scaffold = new[] { startCell + outward, startCell + 2 * outward };
        Size(blueprint, start, outward, out var bounds);
        var (forward, up) = StarterShip.CockpitFrame(main);
        var turn = MatrixD.Transpose(MatrixD.CreateWorld(Vector3D.Zero, forward, up));

        var components = Components(blueprint, skipFirst: true);
        var containers = Containers(shipyard).ToList();
        var needed = components.Sum(c =>
            MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value
        );
        var free = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        if (needed > free)
            return $"the components take {needed:N0} m³, the containers have room for {free:N0} m³";

        var npc = shipyard.BigOwners.First(MySession.Static.Players.IdentityIsNpc);
        var ends = DateTime.UtcNow.AddMinutes(Config.ShipyardOrderMinutes);
        var world = turn * shipyard.WorldMatrix.GetOrientation();
        var ship = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_CubeGrid>();
        ship.GridSizeEnum = main.GridSizeEnum;
        ship.IsStatic = true;
        ship.DisplayName = main.DisplayName ?? "Ship";
        ship.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        ship.PositionAndOrientation = new MyPositionAndOrientation(
            space.Center - Vector3D.TransformNormal(bounds.Center, world),
            (Vector3)world.Forward,
            (Vector3)world.Up
        );
        var first = (MyObjectBuilder_CubeBlock)start.Clone();
        first.SetupForProjector();
        first.IntegrityPercent = first.BuildPercent = 1;
        first.ConstructionStockpile = null;
        first.Owner = first.BuiltBy = next.Player;
        first.ShareMode = MyOwnershipShareModeEnum.Faction;
        ship.CubeBlocks =
        [
            first,
            Projector(main.GridSizeEnum, scaffold[0], outward, blueprint, npc),
            Battery(main.GridSizeEnum, scaffold[1], npc),
        ];
        MyEntities.RemapObjectBuilder(ship);
        if (MyEntities.CreateFromObjectBuilderAndAdd(ship, true) is not MyCubeGrid grid)
            return "the ship could not be started";

        var hydrogen =
            next.Free || !next.Items.TryGetValue(Hydrogen, out var ice)
                ? 0
                : ice / (double)LedgerBook.Unit * Config.ShipyardHydrogenLitersPerIce;
        var projector = grid.GetFatBlocks<MyProjectorBase>().First();
        projector.CustomData = string.Join(
            "\n",
            $"Player={next.Player}",
            $"Ends={ends.ToString("o", CultureInfo.InvariantCulture)}",
            $"Build={next.Id}",
            $"Free={(next.Free ? 1 : 0)}",
            $"Hydrogen={hydrogen.ToString("R", CultureInfo.InvariantCulture)}"
        );
        Load(containers, components);
        orders[shipyard.EntityId] = new Order
        {
            Projector = projector,
            Player = next.Player,
            Ends = ends,
            Blocks = blueprint.Sum(g => g.CubeBlocks.Count),
            Components = components,
            Id = next.Id,
            Free = next.Free,
            Hydrogen = hydrogen,
        };
        lost.Remove(shipyard.EntityId);
        Welders(shipyard, true);
        Plugin.Log.Info(
            $"Shipyard order {next.Id} '{grid.DisplayName}' ({grid.EntityId}, {next.Pcu} PCU, "
                + $"{(next.Free ? "free" : "paid by " + next.Payer)}) for {Name(next.Player)} at {shipyard.DisplayName}, until {ends:u}"
        );
        Tell(
            next.Player,
            $"{shipyard.DisplayName} builds '{grid.DisplayName}' for you now, in {Config.ShipyardOrderMinutes} min at most."
        );
        return null;
    }

    // The queue says a build runs, but the shipyard has no order: after a second check in a
    // row (the order may still be picked up from the world) it ends without its ship
    private static void Lost(MyCubeGrid shipyard, BuildOrder running)
    {
        if (orders.ContainsKey(shipyard.EntityId) || starting.Contains(shipyard.EntityId))
        {
            lost.Remove(shipyard.EntityId);
            return;
        }
        lost.TryGetValue(shipyard.EntityId, out var count);
        lost[shipyard.EntityId] = ++count;
        if (count < 2)
            return;
        lost.Remove(shipyard.EntityId);
        var left = RemoveLeftovers(
            shipyard,
            Taken(running).ToDictionary(t => ItemId(t.Key), t => (int)(t.Value / LedgerBook.Unit))
        );
        Finish(
            running,
            left,
            0,
            refund: false,
            $"Your order '{running.Ship}' at {shipyard.DisplayName} ended: the ship is gone."
        );
    }

    private static void Finish(
        BuildOrder order,
        Dictionary<string, long> leftovers,
        long hydrogenUsed,
        bool refund,
        string message
    )
    {
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Finish,
                Id = order.Id,
                Station = order.Station,
                Needed = leftovers,
                HydrogenUsed = hydrogenUsed,
                Refund = refund,
            },
            (ok, answer) =>
            {
                if (!ok)
                    Plugin.Log.Error($"Shipyard order {order.Id} could not finish: {answer}");
                if (message != null)
                    Tell(order.Player, message);
            }
        );
        Plugin.Log.Info(
            $"Shipyard order {order.Id} '{order.Ship}' of {Name(order.Player)} ended: {message}"
        );
    }

    // --- the end of a build ---

    // A projected block welded onto an order's ship is the player's. Runs before the
    // projection limits, so they count it toward the player.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlockRequestInternal))]
    private static class BuildRequest_
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            MyCubeGrid __instance,
            ref MyCubeGrid.MyBlockLocation location,
            MyObjectBuilder_CubeBlock blockObjectBuilder,
            ref long ownerId,
            bool isProjection
        )
        {
            if (!isProjection || orders.Count == 0 || !Plugin.Config.Enabled)
                return;
            foreach (var order in orders.Values)
            {
                var ship = order.Projector.CubeGrid;
                if (
                    ship != __instance
                    && !MyCubeGridGroups.Static.Mechanical.HasSameGroup(ship, __instance)
                )
                    continue;
                ownerId = location.Owner = order.Player;
                if (blockObjectBuilder != null)
                    blockObjectBuilder.Owner = blockObjectBuilder.BuiltBy = order.Player;
                return;
            }
        }
    }

    // why: null when the ship is complete, else why it ended unfinished
    private static void End(MyCubeGrid shipyard, Order order, string why)
    {
        orders.Remove(shipyard.EntityId);
        Welders(shipyard, false);
        if (order.Arm.Count > 0)
        {
            EndArm(shipyard, order, why);
            return;
        }
        var ship = order.Projector.CubeGrid;
        var name = ship.DisplayName;
        double filled = 0;
        var gone = ship.MarkedForClose;
        if (!gone)
        {
            var scaffold = ship.GetFatBlocks<MyTerminalBlock>()
                .Where(IsScaffold)
                .Select(b => b.Position)
                .ToList();
            // RazeBlocks would ask the safe zone, which forbids grinding
            ship.RazeGeneratedBlocks(scaffold);
            MyMultiplayer.RaiseEvent(ship, x => x.RazeBlocksClient, scaffold);
            HandOver(ship, order.Player);
            if (order.Free)
                FreePrints.Ended(order.Player);
            if (order.Hydrogen > 0)
                filled = FillHydrogen(ship, order.Hydrogen);
            if (ship.IsStatic)
                MyMultiplayer.RaiseEvent(ship, x => x.OnConvertToDynamic);
        }
        var left = RemoveLeftovers(shipyard, order.Components);
        Report(shipyard, order, name, gone ? "gone" : null, why, filled, left, "");
    }

    // Tells the player how the build ended and gives the build ledger back
    private static void Report(
        MyCubeGrid shipyard,
        Order order,
        string name,
        string missing,
        string why,
        double filled,
        Dictionary<string, long> left,
        string extra
    )
    {
        var gone = missing != null;
        var message =
            missing == "unbuilt"
                ? $"{shipyard.DisplayName} stopped after {Config.ShipyardOrderMinutes} min with {why} of '{name}', no ship was built."
            : gone ? $"Your order '{name}' at {shipyard.DisplayName} ended: the ship is gone."
            : why == null
                ? $"Your ship '{name}' is ready in {shipyard.DisplayName}. Fly it out to make room for the next order."
            : $"{shipyard.DisplayName} stopped after {Config.ShipyardOrderMinutes} min with {why} of '{name}'. "
                + "The projection is gone, the unfinished ship is yours.";
        if (!order.Free)
        {
            if (left.Count > 0)
                message += " The leftover components are back in the ledger.";
            if (order.Hydrogen > 0 && !gone)
                message +=
                    filled <= 0
                        ? " Warning: the ship's H2 tanks took no hydrogen, the ice for it is back in the ledger."
                    : filled < order.Hydrogen - 1
                        ? $" Its H2 tanks took {filled:N0} of {order.Hydrogen:N0} L, the ice for the rest is back in the ledger."
                    : $" Its H2 tanks have {filled:N0} L of hydrogen.";
        }
        message += extra;
        if (order.Id.Length > 0)
            Finish(
                new BuildOrder
                {
                    Id = order.Id,
                    Player = order.Player,
                    Ship = name,
                },
                order.Free ? new Dictionary<string, long>() : left,
                filled > 0
                    ? Math.Min(IceFor(filled, up: true), IceFor(order.Hydrogen, up: true))
                    : 0,
                refund: false,
                message
            );
        else
        {
            Tell(order.Player, message);
            Plugin.Log.Info($"Shipyard order '{name}' of {Name(order.Player)} ended: {message}");
        }
    }

    /// <summary>The shipyards this node simulates, their arm rotors and running builds.</summary>
    public static IEnumerable<string> Describe()
    {
        foreach (
            var shipyard in MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(ShipyardZoneWelding.IsShipyard)
                .Where(Plugin.Simulated)
        )
        {
            var rotors = ShipyardArm.Rotors(shipyard);
            yield return $"{shipyard.DisplayName}: "
                + (
                    rotors.Count == 0
                        ? "no arm rotors"
                        : string.Join(
                            ", ",
                            rotors.Select(r =>
                                $"'{r.CustomName}' head {r.TopGrid.BlocksCount} blocks{(r.TopGrid.IsStatic ? " static" : "")}"
                            )
                        )
                );
            if (docks.TryGetValue(shipyard.EntityId, out var dock))
                yield return $"  '{dock.Order.Name}' docking, {dock.Ticks} ticks left";
            if (!orders.TryGetValue(shipyard.EntityId, out var order))
                continue;
            var text = $"  '{order.Name}' for {Name(order.Player)} until {order.Ends:HH:mm:ss}";
            if (order.Arm.Count > 0)
            {
                var grid = order.Projector.CubeGrid;
                var built = order.Arm.Count(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true);
                text +=
                    $": arm {built}/{order.Arm.Count} complete on {grid.EntityId}, projector "
                    + (order.Projector.Enabled ? "on" : "off")
                    + $", ship {ShipBlocks(shipyard, order).Count(b => b.IsFullIntegrity)}/{order.Blocks}"
                    + (order.Connector != null ? ", connector" : ", cap")
                    + (
                        order.GivenUp != null ? $", gives up {order.GivenUp.Value.SubtypeName}" : ""
                    );
            }
            else
                text += $": {Built(order.Projector.CubeGrid)}/{order.Blocks} without an arm";
            yield return text;
        }
    }

    // --- the support arm ---

    private static string CreateWithArm(
        MyCubeGrid shipyard,
        BuildOrder next,
        List<MyObjectBuilder_CubeGrid> blueprint,
        MyOrientedBoundingBoxD space
    )
    {
        var main = blueprint[0];
        var layout = ShipyardArm.Plan(
            shipyard,
            space,
            ShipRotation(shipyard, main),
            blueprint,
            out var error
        );
        if (layout == null)
            return error;

        // The ship's components, the given up block's included, and the arm's, which OCT pays
        var components = Components(blueprint, skipFirst: false);
        var load = new Dictionary<MyDefinitionId, int>(components);
        foreach (var c in layout.Components)
            load[c.Key] = (load.TryGetValue(c.Key, out var had) ? had : 0) + c.Value;
        var containers = Containers(shipyard).ToList();
        var needed = load.Sum(c =>
            MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value
        );
        var free = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        if (needed > free)
            return $"the components take {needed:N0} m³, the containers have room for {free:N0} m³";

        var npc = shipyard.BigOwners.First(MySession.Static.Players.IdentityIsNpc);
        var ends = DateTime.UtcNow.AddMinutes(Config.ShipyardOrderMinutes);
        var hydrogen =
            next.Free || !next.Items.TryGetValue(Hydrogen, out var ice)
                ? 0
                : ice / (double)LedgerBook.Unit * Config.ShipyardHydrogenLitersPerIce;
        var arm = layout
            .Plan.Blocks.Select(b => new Vector3I(b.Cell.X, b.Cell.Y, b.Cell.Z))
            .ToList();
        var connector = layout
            .Plan.Blocks.Where(b => b.Piece == ArmPiece.Connector)
            .Select(b => (Vector3I?)new Vector3I(b.Cell.X, b.Cell.Y, b.Cell.Z))
            .FirstOrDefault();
        var givenUp = layout.GivenUp?.GetId();
        var data = new List<string>
        {
            $"Player={next.Player}",
            $"Ends={ends.ToString("o", CultureInfo.InvariantCulture)}",
            $"Build={next.Id}",
            $"Free={(next.Free ? 1 : 0)}",
            $"Hydrogen={hydrogen.ToString("R", CultureInfo.InvariantCulture)}",
            $"Arm={string.Join(";", arm.Select(Format))}",
        };
        if (connector != null)
            data.Add($"Connector={Format(connector.Value)}");
        if (givenUp != null)
            data.Add($"GivenUp={givenUp}");
        var projector = ShipyardArm.Place(layout, npc, ScaffoldName, string.Join("\n", data));
        if (projector == null)
        {
            Raze(layout.Grid, arm);
            return "the support arm could not be placed";
        }
        Load(containers, load);
        var order = new Order
        {
            Projector = projector,
            Player = next.Player,
            Ends = ends,
            Blocks = layout.Projection.Sum(g => g.CubeBlocks.Count),
            Components = components,
            Id = next.Id,
            Free = next.Free,
            Hydrogen = hydrogen,
            Arm = arm,
            Connector = connector,
            GivenUp = givenUp,
            Name = main.DisplayName ?? "Ship",
        };
        orders[shipyard.EntityId] = order;
        lost.Remove(shipyard.EntityId);
        Welders(shipyard, true);
        var plan = layout.Plan;
        Plugin.Log.Info(
            $"Shipyard order {next.Id} '{order.Name}' ({next.Pcu} PCU, "
                + $"{(next.Free ? "free" : "paid by " + next.Payer)}) for {Name(next.Player)} at {shipyard.DisplayName}, until {ends:u}: "
                + $"arm on '{layout.Rotor.CustomName}' of {plan.Blocks.Count} blocks, cost {plan.Cost}, "
                + (plan.Connector != null ? "docking at a connector" : "with a cap")
                + (
                    plan.GivenUp != null
                        ? $", the projector in place of a {layout.GivenUp.SubtypeName}"
                        : ""
                )
        );
        Tell(
            next.Player,
            $"{shipyard.DisplayName} builds '{order.Name}' for you now, in {Config.ShipyardOrderMinutes} min at most."
                + (
                    layout.GivenUp != null
                        ? $" The arm's projector takes the place of one {Definition(layout.GivenUp)}: build it yourself after."
                        : ""
                )
        );
        return null;
    }

    private static string Definition(MyObjectBuilder_CubeBlock block) =>
        MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out var d)
            ? d.DisplayNameText
            : block.SubtypeName;

    private static string Format(Vector3I v) => $"{v.X},{v.Y},{v.Z}";

    private static bool TryCell(string text, out Vector3I cell)
    {
        cell = default;
        var parts = text.Split(',');
        if (
            parts.Length != 3
            || !int.TryParse(
                parts[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var x
            )
            || !int.TryParse(
                parts[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var y
            )
            || !int.TryParse(
                parts[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var z
            )
        )
            return false;
        cell = new Vector3I(x, y, z);
        return true;
    }

    // The grids of the shipyard itself: the shipyard and the heads of its rotors and pistons
    private static HashSet<MyCubeGrid> Own(MyCubeGrid shipyard)
    {
        var own = new HashSet<MyCubeGrid> { shipyard };
        foreach (var joint in shipyard.GetFatBlocks<MyMechanicalConnectionBlockBase>())
            if (joint.TopGrid != null)
                own.Add(joint.TopGrid);
        return own;
    }

    // The ship's blocks on the arm's grid: not the arm, nor the rotor head
    private static List<MySlimBlock> ShipBlocksOnArm(Order order)
    {
        var grid = order.Projector.CubeGrid;
        var arm = new HashSet<Vector3I>(order.Arm);
        return grid.GetBlocks()
            .Where(b =>
                !arm.Contains(b.Position)
                && !(
                    b.FatBlock is MyAttachableTopBlockBase top
                    && top.Stator?.CubeGrid != null
                    && top.Stator.CubeGrid != grid
                    && ShipyardZoneWelding.IsShipyard(top.Stator.CubeGrid)
                )
            )
            .ToList();
    }

    // The ship's blocks so far: on the arm's grid and on its own subgrids
    private static IEnumerable<MySlimBlock> ShipBlocks(MyCubeGrid shipyard, Order order)
    {
        var grid = order.Projector.CubeGrid;
        var own = Own(shipyard);
        return ShipBlocksOnArm(order)
            .Concat(
                Grids(grid).Where(g => g != grid && !own.Contains(g)).SelectMany(g => g.GetBlocks())
            );
    }

    /// <summary>The PCU of the support arms on the grid, which the cap of a ship leaves out.</summary>
    internal static int ArmPcu(MyCubeGrid grid)
    {
        foreach (var order in orders.Values)
            if (order.Arm.Count > 0 && order.Projector.CubeGrid == grid)
                return order.Arm.Sum(c => grid.GetCubeBlock(c)?.BlockDefinition.PCU ?? 0);
        return 0;
    }

    // The ship splits off the arm's grid, then docks to the arm's connector if it has one
    private static void EndArm(MyCubeGrid shipyard, Order order, string why)
    {
        var grid = order.Projector.CubeGrid;
        MyCubeGrid ship = null;
        if (!grid.MarkedForClose)
        {
            var blocks = ShipBlocksOnArm(order);
            if (blocks.Count > 0)
                ship = MyCubeGrid.CreateSplit(grid, blocks);
            ship?.ChangeDisplayNameRequest(order.Name);
            // A split of the static arm grid is static: a dynamic ship docks to the arm
            if (ship?.IsStatic == true)
                MyMultiplayer.RaiseEvent(ship, x => x.OnConvertToDynamic);
        }
        var connector =
            order.Connector is { } cell && !grid.MarkedForClose
                ? grid.GetCubeBlock(cell)?.FatBlock as MyShipConnector
                : null;
        if (ship == null || connector == null || !connector.IsFunctional)
        {
            Docked(shipyard, order, ship, false, why);
            return;
        }
        order.Docking = ship.EntityId;
        if (!order.Projector.MarkedForClose)
            order.Projector.CustomData += $"\nDocking={ship.EntityId}";
        connector.Enabled = true;
        var reach = 1.5 * grid.GridSize;
        foreach (var other in ship.GetFatBlocks<MyShipConnector>())
            if (
                Vector3D.Distance(
                    other.PositionComp.GetPosition(),
                    connector.PositionComp.GetPosition()
                ) < reach
            )
                other.Enabled = true;
        docks[shipyard.EntityId] = new Dock
        {
            Shipyard = shipyard,
            Order = order,
            Ship = ship,
            Connector = connector,
            Ticks = Math.Max(1, (int)(Config.ShipyardArmDockSeconds * 60)),
            Why = why,
        };
        Plugin.Log.Info(
            $"Shipyard order {order.Id} '{order.Name}' at {shipyard.DisplayName}: split off as {ship.EntityId}, docking"
        );
    }

    // Each tick: the connectors of finished ships lock, or their time is up
    private static void UpdateDocks()
    {
        foreach (var (id, dock) in docks.ToList())
        {
            var connector = (Sandbox.ModAPI.Ingame.IMyShipConnector)dock.Connector;
            var gone = dock.Connector.MarkedForClose || dock.Ship.MarkedForClose;
            var connected =
                !gone && connector.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected;
            if (
                !gone
                && !connected
                && connector.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable
            )
                connector.Connect();
            if (!connected && !gone && --dock.Ticks > 0)
                continue;
            docks.Remove(id);
            Docked(dock.Shipyard, dock.Order, dock.Ship, connected, dock.Why);
        }
    }

    // The end of a build with an arm: the hydrogen through the connector, or by the plugin
    // itself, the arm destroyed, the ship handed over
    private static void Docked(
        MyCubeGrid shipyard,
        Order order,
        MyCubeGrid ship,
        bool connected,
        string why
    )
    {
        var alive = ship != null && !ship.MarkedForClose;
        double filled = 0;
        var charged = 0;
        if (alive)
        {
            if (order.Hydrogen > 0)
                filled = FillHydrogen(ship, order.Hydrogen);
            if (connected)
                foreach (
                    var battery in Group(ship)
                        .SelectMany(g => ((MyCubeGrid)g).GetFatBlocks<MyBatteryBlock>())
                )
                {
                    battery.CurrentStoredPower = battery.MaxStoredPower;
                    charged++;
                }
        }
        Plugin.Log.Info(
            $"Shipyard order {order.Id} '{order.Name}' at {shipyard.DisplayName}: "
                + (connected ? $"docked, {charged} batteries charged" : "no dock, admin fill")
                + $", {filled:N0} of {order.Hydrogen:N0} L hydrogen"
        );
        var armLeft = RemoveArm(order);
        if (alive)
        {
            HandOver(ship, order.Player);
            if (order.Free)
                FreePrints.Ended(order.Player);
            if (ship.IsStatic)
                MyMultiplayer.RaiseEvent(ship, x => x.OnConvertToDynamic);
        }
        // What the arm didn't use is OCT's
        RemoveLeftovers(shipyard, armLeft);
        var left = RemoveLeftovers(shipyard, order.Components);
        var extra = "";
        if (alive && order.GivenUp is { } givenUp)
            extra +=
                $" One {(MyDefinitionManager.Static.TryGetCubeBlockDefinition(givenUp, out var d) ? d.DisplayNameText : givenUp.SubtypeName)} "
                + "is missing where the arm's projector was: build it yourself.";
        Report(
            shipyard,
            order,
            order.Name,
            alive ? null
                : ship == null && why != null && why != "the ship is gone" ? "unbuilt"
                : "gone",
            why,
            filled,
            left,
            extra
        );
    }

    // Destroys the arm's blocks, never the rotor head; returns the components they still miss
    private static Dictionary<MyDefinitionId, int> RemoveArm(Order order)
    {
        var missing = new Dictionary<MyDefinitionId, int>();
        var grid = order.Projector.CubeGrid;
        if (grid.MarkedForClose)
            return missing;
        foreach (var cell in order.Arm)
        {
            var parts = new Dictionary<string, int>();
            grid.GetCubeBlock(cell)?.GetMissingComponents(parts);
            foreach (var part in parts)
            {
                var id = new MyDefinitionId(typeof(MyObjectBuilder_Component), part.Key);
                missing[id] = (missing.TryGetValue(id, out var had) ? had : 0) + part.Value;
            }
        }
        Raze(grid, order.Arm);
        if (grid.IsStatic)
            MyMultiplayer.RaiseEvent(grid, x => x.OnConvertToDynamic);
        return missing;
    }

    // RazeBlocks would ask the safe zone, which forbids grinding
    private static void Raze(MyCubeGrid grid, List<Vector3I> cells)
    {
        var present = cells.Where(c => grid.CubeExists(c)).ToList();
        grid.RazeGeneratedBlocks(present);
        MyMultiplayer.RaiseEvent(grid, x => x.RazeBlocksClient, present);
    }

    // Blocks the projector built itself, like the tops of rotors and pistons, are BuiltBy its NPC
    private static void HandOver(MyCubeGrid ship, long player)
    {
        foreach (MyCubeGrid grid in Group(ship))
        {
            var authors = new HashSet<long>();
            foreach (var block in grid.GetBlocks())
            {
                if (block.BuiltBy == player)
                    continue;
                authors.Add(block.BuiltBy);
                block.TransferAuthorship(player);
            }
            foreach (var author in authors)
                MyMultiplayer.RaiseStaticEvent(
                    (IMyEventOwner _) => MyBlockLimits.TransferBlocksBuiltByIDClient,
                    grid.EntityId,
                    author,
                    player
                );
            grid.ChangeGridOwnership(player, MyOwnershipShareModeEnum.Faction);
        }
    }

    private static Order Restore(MyProjectorBase projector)
    {
        var grids = (
            (MyObjectBuilder_ProjectorBase)projector.GetObjectBuilderCubeBlock(true)
        ).ProjectedGrids;
        if (grids == null || grids.Count == 0)
            return null;
        // Written by Create: "Key=value" lines
        var values = projector
            .CustomData.Split('\n')
            .Select(l => l.Split(['='], 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        if (
            !values.TryGetValue("Player", out var p)
            || !long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var player)
            || !values.TryGetValue("Ends", out var e)
            || !DateTime.TryParse(
                e,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var ends
            )
        )
            return null;
        values.TryGetValue("Hydrogen", out var h);
        double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hydrogen);
        var order = new Order
        {
            Projector = projector,
            Player = player,
            Ends = ends,
            Blocks = grids.Sum(g => g.CubeBlocks.Count),
            Id = values.TryGetValue("Build", out var id) ? id : "",
            Free = !values.TryGetValue("Free", out var f) || f != "0",
            Hydrogen = hydrogen,
            Name = grids[0].DisplayName ?? "Ship",
        };
        if (values.TryGetValue("Arm", out var arm))
            foreach (var text in arm.Split(';'))
                if (TryCell(text, out var cell))
                    order.Arm.Add(cell);
        if (values.TryGetValue("Connector", out var c) && TryCell(c, out var connector))
            order.Connector = connector;
        if (values.TryGetValue("GivenUp", out var g) && MyDefinitionId.TryParse(g, out var givenUp))
            order.GivenUp = givenUp;
        if (values.TryGetValue("Docking", out var d))
            long.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out order.Docking);
        // Without an arm the ship's first block was built at the start
        order.Components = Components(grids, skipFirst: order.Arm.Count == 0);
        if (
            order.GivenUp is { } block
            && MyDefinitionManager.Static.TryGetCubeBlockDefinition(block, out var definition)
        )
            foreach (var component in definition.Components)
            {
                order.Components.TryGetValue(component.Definition.Id, out var count);
                order.Components[component.Definition.Id] = count + component.Count;
            }
        return order;
    }

    // --- hydrogen ---

    private static long IceFor(double liters, bool up)
    {
        var raw = liters / Config.ShipyardHydrogenLitersPerIce * LedgerBook.Unit;
        return (long)(up ? Math.Ceiling(raw - 1e-6) : Math.Floor(raw + 1e-6));
    }

    private static bool IsHydrogen(MyGasTank tank) =>
        tank.BlockDefinition is MyGasTankDefinition d && d.StoredGasId.SubtypeName == "Hydrogen";

    // Liters of all the blueprint's H2 tanks
    private static double HydrogenCapacity(List<MyObjectBuilder_CubeGrid> blueprint) =>
        blueprint
            .SelectMany(g => g.CubeBlocks)
            .Sum(b =>
                MyDefinitionManager.Static.TryGetCubeBlockDefinition(b.GetId(), out var d)
                && d is MyGasTankDefinition tank
                && tank.StoredGasId.SubtypeName == "Hydrogen"
                    ? tank.Capacity
                    : 0
            );

    // The H2 tanks with hydrogen of the payer's ships docked at the station: the player's own,
    // or with the faction paying any member's
    private static List<MyGasTank> ShipTanks(Plan plan)
    {
        var station = Grids(plan.Table.CubeGrid);
        var faction = plan.Payer.Faction
            ? MySession.Static.Factions.TryGetFactionById(plan.Payer.Id)
            : null;
        var ships = station
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .Where(c => c.Connected && c.Other != null)
            .SelectMany(c => Grids(c.Other.CubeGrid))
            .Where(g => !station.Contains(g))
            .Distinct();
        return ships
            .Where(g =>
                g.BigOwners.Contains(plan.Player)
                || (faction != null && g.BigOwners.Any(o => faction.IsMember(o)))
            )
            .SelectMany(g => g.GetFatBlocks<MyGasTank>())
            .Where(t => IsHydrogen(t) && t.IsFunctional && t.FilledRatio > 0)
            .ToList();
    }

    // Takes the build's hydrogen, as far as there is any, from the payer's docked ships
    private static List<(MyGasTank Tank, double Liters)> Drain(Plan plan)
    {
        var taken = new List<(MyGasTank, double)>();
        if (plan.HydrogenLiters <= 0)
            return taken;
        var left = plan.HydrogenLiters;
        foreach (var tank in ShipTanks(plan))
        {
            if (left <= 0)
                break;
            var have = tank.FilledRatio * tank.Capacity;
            var take = Math.Min(have, left);
            ((IMyGasTank)tank).ChangeFilledRatio((have - take) / tank.Capacity, true);
            taken.Add((tank, take));
            left -= take;
        }
        return taken;
    }

    private static void AddHydrogen(MyGasTank tank, double liters)
    {
        if (tank.MarkedForClose)
            return;
        var ratio = Math.Min(1, tank.FilledRatio + liters / tank.Capacity);
        ((IMyGasTank)tank).ChangeFilledRatio(ratio, true);
    }

    // Fills the ship's H2 tanks up to ShipyardHydrogenFillPercent with the build's hydrogen;
    // returns the liters that went in
    private static double FillHydrogen(MyCubeGrid ship, double liters)
    {
        var target = Config.ShipyardHydrogenFillPercent / 100.0;
        var left = liters;
        foreach (var tank in Group(ship).SelectMany(g => ((MyCubeGrid)g).GetFatBlocks<MyGasTank>()))
        {
            if (left <= 0 || !IsHydrogen(tank) || !tank.IsFunctional)
                continue;
            var room = Math.Max(0, target * tank.Capacity - tank.FilledRatio * tank.Capacity);
            var give = Math.Min(room, left);
            if (give <= 0)
                continue;
            AddHydrogen(tank, give);
            left -= give;
        }
        return liters - left;
    }

    // --- the blueprint in the queue ---

    private static string EncodeBlueprint(List<MyObjectBuilder_CubeGrid> grids)
    {
        var definition =
            MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
        definition.CubeGrids = grids.ToArray();
        using var stream = new MemoryStream();
        MyObjectBuilderSerializerKeen.SerializeXML(
            stream,
            definition,
            MyObjectBuilderSerializerKeen.XmlCompression.Gzip
        );
        return Convert.ToBase64String(stream.ToArray());
    }

    private static List<MyObjectBuilder_CubeGrid> DecodeBlueprint(string text)
    {
        try
        {
            if (
                MyObjectBuilderSerializerKeen.DeserializeXML(
                    Convert.FromBase64String(text),
                    out MyObjectBuilder_ShipBlueprintDefinition definition,
                    out _
                )
            )
                return definition.CubeGrids?.ToList();
        }
        catch (FormatException e)
        {
            Plugin.Log.Error("Shipyard blueprint", e);
        }
        return null;
    }

    // --- the station and the shipyard ---

    private static List<MyCubeGrid> Grids(MyCubeGrid grid)
    {
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        return group == null ? [grid] : group.Nodes.Select(n => n.NodeData).ToList();
    }

    private static string StationOf(MyCubeGrid grid) =>
        Grids(grid).Select(Ledgers.StationOf).FirstOrDefault(s => s != null);

    // The nearest shipyard within 1 km of the table
    private static MyCubeGrid FindShipyard(MyProjectorBase table)
    {
        var position = table.PositionComp.GetPosition();
        var sphere = new BoundingSphereD(position, 1000);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var shipyard = entities
            .OfType<MyCubeGrid>()
            .Where(ShipyardZoneWelding.IsShipyard)
            .OrderBy(g => Vector3D.DistanceSquared(g.PositionComp.GetPosition(), position))
            .FirstOrDefault();
        entities.Clear();
        return shipyard;
    }

    // The largest box free of the shipyard's blocks, grown from the middle of its welders
    // one layer at a time and kept within its grid's bounds, in world coordinates
    private static MyOrientedBoundingBoxD FreeSpace(MyCubeGrid shipyard)
    {
        if (!freeBoxes.TryGetValue(shipyard.EntityId, out var box))
        {
            var beams = Beams(shipyard).ToList();
            var start = Vector3I.Zero;
            foreach (var beam in beams)
                start += beam.Position;
            start /= Math.Max(1, beams.Count);
            box = new BoundingBoxI(start, start);
            for (var grew = true; grew; )
            {
                grew = false;
                foreach (var d in Base6Directions.IntDirections)
                {
                    var grown = new BoundingBoxI(
                        box.Min + Vector3I.Min(d, Vector3I.Zero),
                        box.Max + Vector3I.Max(d, Vector3I.Zero)
                    );
                    // The new layer only
                    var layer = grown;
                    if (d.X + d.Y + d.Z > 0)
                        layer.Min = box.Min + (box.Max - box.Min + Vector3I.One) * d;
                    else
                        layer.Max = box.Max + (box.Max - box.Min + Vector3I.One) * d;
                    if (
                        Vector3I.Min(grown.Min, shipyard.Min) != shipyard.Min
                        || Vector3I.Max(grown.Max, shipyard.Max) != shipyard.Max
                        || !Free(shipyard, layer)
                    )
                        continue;
                    box = grown;
                    grew = true;
                }
            }
            if (beams.Count > 0)
                freeBoxes[shipyard.EntityId] = box;
        }
        var cube = shipyard.GridSize;
        return new MyOrientedBoundingBoxD(
            new BoundingBoxD(((Vector3D)box.Min - 0.5) * cube, ((Vector3D)box.Max + 0.5) * cube),
            shipyard.WorldMatrix
        );
    }

    private static bool Free(MyCubeGrid grid, BoundingBoxI box)
    {
        for (var x = box.Min.X; x <= box.Max.X; x++)
        for (var y = box.Min.Y; y <= box.Max.Y; y++)
        for (var z = box.Min.Z; z <= box.Max.Z; z++)
            if (grid.CubeExists(new Vector3I(x, y, z)))
                return false;
        return true;
    }

    // A grid in the shipyard's free space that isn't the shipyard or on its rotors
    private static MyCubeGrid Obstacle(MyCubeGrid shipyard)
    {
        var own = Grids(shipyard);
        return GridsIn(FreeSpace(shipyard)).FirstOrDefault(g => !own.Contains(g));
    }

    private static List<MyCubeGrid> GridsIn(MyOrientedBoundingBoxD box)
    {
        var entities = MyEntities.GetEntitiesInOBB(ref box);
        var grids = entities.OfType<MyCubeGrid>().ToList();
        entities.Clear();
        return grids;
    }

    // A 1x1x1 block of the main grid on its outer face, preferably plain armor, and the way out
    // of the grid from it: the scaffold goes there, outside the main grid's bounds
    private static bool FindStart(
        MyObjectBuilder_CubeGrid grid,
        out MyObjectBuilder_CubeBlock start,
        out Vector3I outward
    )
    {
        var cube = MyDefinitionManager.Static.GetCubeSize(grid.GridSizeEnum);
        var bounds = grid.CalculateBoundingBox();
        var min = Vector3I.Round(bounds.Min / cube + 0.5f);
        var max = Vector3I.Round(bounds.Max / cube - 0.5f);
        start = null;
        outward = Vector3I.Zero;
        foreach (var block in grid.CubeBlocks)
        {
            if (
                !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                    block.GetId(),
                    out var definition
                )
                || definition.Size != Vector3I.One
            )
                continue;
            foreach (var d in Base6Directions.IntDirections)
            {
                Vector3I cell = block.Min;
                if (Vector3I.Dot(cell, d) != Math.Max(Vector3I.Dot(min, d), Vector3I.Dot(max, d)))
                    continue;
                if (start == null || block.GetType() == typeof(MyObjectBuilder_CubeBlock))
                {
                    start = block;
                    outward = d;
                }
                if (block.GetType() == typeof(MyObjectBuilder_CubeBlock))
                    return true;
                break;
            }
        }
        return start != null;
    }

    // The blueprint's blocks matching FreeBuildBlockedBlocks, counted by name: "2 Jump Drive"
    private static List<string> Blocked(List<MyObjectBuilder_CubeGrid> grids, string patterns)
    {
        if (blockedPatterns != patterns)
        {
            blockedPatterns = patterns;
            var globs = patterns
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Select(p => Regex.Escape(p).Replace(@"\*", ".*"))
                .ToList();
            blockedRegex =
                globs.Count == 0
                    ? null
                    : new Regex(
                        $"^(?:{string.Join("|", globs)})$",
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
                    );
        }
        if (blockedRegex == null)
            return [];
        return grids
            .SelectMany(g => g.CubeBlocks)
            .Select(b => b.GetId())
            .Where(id =>
                blockedRegex.IsMatch(
                    $"{id.TypeId.ToString().Replace("MyObjectBuilder_", "")}/{id.SubtypeName}"
                )
            )
            .GroupBy(id =>
                MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out var definition)
                    ? definition.DisplayNameText
                    : id.SubtypeName
            )
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
    }

    private static MyObjectBuilder_Projector Projector(
        MyCubeSize size,
        Vector3I cell,
        Vector3I outward,
        List<MyObjectBuilder_CubeGrid> blueprint,
        long npc
    )
    {
        var projector = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Projector>(
            size == MyCubeSize.Large ? "LargeProjector" : "SmallProjector"
        );
        projector.Min = cell;
        projector.BlockOrientation = new SerializableBlockOrientation(
            Base6Directions.Direction.Forward,
            Base6Directions.Direction.Up
        );
        projector.Owner = npc;
        projector.ShareMode = MyOwnershipShareModeEnum.None;
        projector.Enabled = true;
        projector.CustomName = ScaffoldName;
        projector.ProjectedGrids = blueprint;
        // The projection's first block lands on the projector's cell minus the offset
        projector.ProjectionOffset = outward;
        projector.KeepProjection = true;
        return projector;
    }

    private static MyObjectBuilder_BatteryBlock Battery(MyCubeSize size, Vector3I cell, long npc)
    {
        var subtype =
            size == MyCubeSize.Large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock";
        var battery = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_BatteryBlock>(
            subtype
        );
        battery.Min = cell;
        battery.Owner = npc;
        battery.ShareMode = MyOwnershipShareModeEnum.None;
        battery.Enabled = true;
        battery.CustomName = ScaffoldName;
        battery.CurrentStoredPower = (
            (MyBatteryBlockDefinition)MyDefinitionManager.Static.GetCubeBlockDefinition(battery)
        ).MaxStoredPower;
        return battery;
    }

    // The components of the blocks; skipFirst: but the first one, which is built already
    private static Dictionary<MyDefinitionId, int> Components(
        List<MyObjectBuilder_CubeGrid> grids,
        bool skipFirst
    )
    {
        var components = new Dictionary<MyDefinitionId, int>();
        foreach (var block in grids.SelectMany(g => g.CubeBlocks).Skip(skipFirst ? 1 : 0))
        {
            if (
                !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                    block.GetId(),
                    out var definition
                )
            )
                continue;
            foreach (var component in definition.Components)
            {
                components.TryGetValue(component.Definition.Id, out var count);
                components[component.Definition.Id] = count + component.Count;
            }
        }
        return components;
    }

    private static void Load(
        List<MyInventory> containers,
        Dictionary<MyDefinitionId, int> components
    )
    {
        foreach (var (id, count) in components.Select(c => (c.Key, c.Value)))
        {
            MyFixedPoint left = count;
            foreach (var inventory in containers)
            {
                var amount = MyFixedPoint.Min(left, inventory.ComputeAmountThatFits(id));
                if (amount <= 0)
                    continue;
                inventory.AddItems(amount, MyObjectBuilderSerializer.CreateNewObject(id));
                left -= amount;
            }
        }
    }

    // Takes back what is left of the loaded components from the shipyard's containers and
    // welders; returns what it took, raw by ledger item name
    private static Dictionary<string, long> RemoveLeftovers(
        MyCubeGrid shipyard,
        Dictionary<MyDefinitionId, int> components
    )
    {
        var left = components.ToDictionary(c => c.Key, c => (MyFixedPoint)c.Value);
        var taken = new Dictionary<string, long>();
        foreach (
            var inventory in Containers(shipyard)
                .Concat(Beams(shipyard).Select(b => b.GetInventory()))
        )
        {
            foreach (var id in left.Keys.ToList())
            {
                var amount = MyFixedPoint.Min(left[id], inventory.GetItemAmount(id));
                if (amount <= 0)
                    continue;
                inventory.RemoveItemsOfType(amount, id);
                left[id] -= amount;
                var name = Ledgers.ItemName(id);
                taken[name] = (taken.TryGetValue(name, out var had) ? had : 0) + amount.RawValue;
            }
        }
        return taken;
    }

    private static MyDefinitionId ItemId(string item) =>
        Ledgers.TryItemId(item, out var id) ? id : default;

    private static IEnumerable<MyInventory> Containers(MyCubeGrid shipyard) =>
        shipyard.GetFatBlocks<MyCargoContainer>().Select(c => c.GetInventory());

    // The shipyard's ToolCore tools, its OmniArc welders
    private static IEnumerable<MyFunctionalBlock> Beams(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyFunctionalBlock>()
            .Where(b => ((IMyTerminalBlock)b).GetProperty(ShootProperty) != null);

    // Through the tools' terminal switches, which ToolCore also sends to the clients. In a
    // no-welding zone the clients turn the tools off again, so they show no beams (SE1-0259).
    private static void Welders(MyCubeGrid shipyard, bool on)
    {
        foreach (var beam in Beams(shipyard))
        {
            if (on)
                beam.Enabled = true;
            var block = (IMyTerminalBlock)beam;
            if (block.GetValueBool(TrackProperty) != on)
                block.SetValueBool(TrackProperty, on);
            if (block.GetValueBool(ShootProperty) != on)
                block.SetValueBool(ShootProperty, on);
        }
    }

    // Complete blocks of the ship and its subgrids, the scaffold left out
    private static int Built(MyCubeGrid ship) =>
        Group(ship)
            .SelectMany(g => ((MyCubeGrid)g).GetBlocks())
            .Count(b => b.IsFullIntegrity && !(b.FatBlock is MyTerminalBlock t && IsScaffold(t)));

    private static List<IMyCubeGrid> Group(MyCubeGrid ship)
    {
        var grids = new List<IMyCubeGrid>();
        MyAPIGateway.GridGroups.GetGroup(ship, GridLinkTypeEnum.Mechanical, grids);
        return grids;
    }

    private static bool IsScaffold(MyTerminalBlock block) =>
        block.CustomName.ToString() == ScaffoldName
        && MySession.Static.Players.IdentityIsNpc(block.OwnerId);

    private static string Name(long identityId) =>
        MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName ?? "someone";

    private static void Tell(long identityId, string text) =>
        MyVisualScriptLogicProvider.SendChatMessage(text, "Shipyard", identityId);

    // An amount in its item's unit: whole components, kg of ore and ingots
    private static string Units(string item, long raw)
    {
        var units = (decimal)raw / LedgerBook.Unit;
        return item.StartsWith("Component/")
            ? units.ToString("N0", CultureInfo.InvariantCulture)
            : units.ToString("N0", CultureInfo.InvariantCulture) + " kg";
    }

    private static string Meters(Vector3D size) => $"{size.X:0} x {size.Y:0} x {size.Z:0} m";
}
