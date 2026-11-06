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
using Sandbox.Game.EntityComponents;
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

// A player loads a blueprint into a station's hologram table, which makes the table theirs until
// the blueprint is gone: at most ShipyardTableMinutes, and nobody else can clear it. They read the
// quote on the table's large screen, the parts on the billboard next to it
// (ShipyardDetailsPanelName), and press the button next to it (ShipyardConfirmButtonName) to queue
// the ship at the station's shipyard. Who presses decides what the press means, whoever holds the
// table: it queues the ship of the presser's blueprint, or else cancels their queue entry or
// running build, or else books them a repair slot. The queue is kept by
// Builds, on the authority; the station's queue panels show it.
//
// A repair slot is a queue entry too. When its turn comes the shipyard's welders go on and
// ShipyardTurn gives its blocks to the player, who docks a ship with a repair projector and the
// components. It ends when no building or welding happened for ShipyardRepairIdleMinutes, or at
// the player's next press, and goes on to the last part of the turn. The running slot's state is the queue's: a restart or a move of the
// station to another node picks it up from there.
//
// A ship is a free build for a player under FreeBuildPcuLimit, once FreeBuildCooldownMinutes
// passed since their last free build ended (FreePrints), without a block that needs a component in
// FreeBuildExcludedComponents or is in FreeBuildBlockedBlocks. An admin with "Ignore PCU limits" on
// (FreePrints.Exempt) gets every ship as a free build: no PCU limit, cool-down or blocked blocks,
// and no per-ship cap of GroupPcuLimit. The order keeps that, for the node that builds it. Any other ship is a paid build
// on the player's turn at the shipyard (ShipyardOrderTurns.cs): they load the components into its
// containers, and pressing the button buys what is missing and the ice for the hydrogen to fill its
// H2 tanks to ShipyardHydrogenFillPercent at the station's Store from the player's account, plus
// the labor fee; what the player's ships docked at the shipyard or the station have in their tanks
// over DockedTankKeep counts as ice first, then the ice in those ships' containers and in the
// shipyard's. A free ship's H2 tanks are filled too, from the same ships and then by OCT.
//
// When the shipyard is free, the next order starts. The ship starts as a static grid of one of
// its blocks, centered in the shipyard's free space, with an NPC-owned projector and battery
// (the scaffold) next to that block projecting the whole blueprint. The shipyard's cargo
// containers get the components of the remaining blocks for a free build, and its OmniArc welders
// (ToolCore) are switched on. Projected blocks welded onto the ship are the player's, owner and
// BuiltBy. The order ends when every block is complete or after ShipyardOrderMinutes: the welders
// go off, the scaffold is removed, a free build's leftover components are removed (a paid build's
// stay the player's for the rest of their turn), a paid build's H2 tanks are filled, and the ship,
// finished or not, becomes the player's dynamic ship.
//
// The running order lives in the world, in the scaffold projector's CustomData, so it survives
// a restart and moves with the station's partition on a cluster: every node checks the
// shipyards it simulates every 10 s.
internal static partial class ShipyardOrders
{
    private const string ScaffoldName = "Shipyard Order";
    private const string ShootProperty = "ToolCore_Shoot";
    private const string TrackProperty = "ToolCore_TrackTargets";
    private const string Hydrogen = "Ore/Ice";
    private const string RepairName = "Repair";
    private const int UpdateTicks = 600;
    private const int QuoteTicks = 60;

    // LCD text is neither wrapped nor shrunk to fit. The console's screen is 256x176 at half the
    // text scale, the billboard's 307x512.
    private const int TableColumns = 26;
    private const int PaidColumns = 38;
    private const int FreeColumns = 25;

    // The queue panel's 512x307 screen fits 6 entries of two lines each at font size 0.57
    private const int QueueColumns = 46;
    private const int QueueEntryLines = 2;
    private const string TableIdle = "Project,\nthen press\nButton!";
    private const string DetailsIdle = "Ordered ship details...";

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

        // Ordered by an admin who ignores PCU limits (BuildOrder.Exempt)
        public bool Exempt;

        // Liters for the ship's H2 tanks at the end
        public double Hydrogen;

        // The support arm's blocks on the rotor head's grid, the projector's included; empty for
        // a ship built from a projector on itself
        public List<Vector3I> Arm = [];

        // The arm's complete blocks at the last check, and the checks since one was added
        public int ArmBuilt;
        public int ArmIdle;

        // The ship's integrity at the last check, and the checks since it grew
        public float Progress = -1;
        public int Idle;

        // A build that stalled: what the ship misses, and whether it is scrapped
        public string Missing;
        public bool Refund;

        // The blocks placed by force that made the ship clang, which removed it
        public string Clang;

        // The arm's grid, which the projector leaves when it stays on an unfinished ship
        public MyCubeGrid ArmGrid;

        // The arm's connector, and the ship's block the projector took the place of
        public Vector3I? Connector;
        public MyDefinitionId? GivenUp;

        // The finished ship's entity id while it docks to the arm
        public long Docking;

        // The blueprint's name
        public string Name = "";

        // Since when a paid build waits for a top-up
        public DateTime? ShortSince;

        // The player canceled the running build
        public bool Canceled;

        // What a removed paid build's containers had no room for, to be sold, raw
        public Dictionary<string, long> Sold = [];
    }

    // A running repair slot, by shipyard entity id
    private sealed class RepairSlot
    {
        public string Id;
        public string Station;
        public long Player;
        public DateTime LastWork;
    }

    private static readonly Dictionary<long, RepairSlot> repairs = new();

    // Repair slots that ended and aren't out of the queue yet
    private static readonly HashSet<string> ending = new();

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

    // When each table's blueprint was loaded, or found loaded after a restart or a move of the
    // station to this node. Who holds the table is the table's owner (Holder).
    private static readonly Dictionary<long, DateTime> loadedAt = new();

    // The player who last queued a ship at each table: its empty screen offers them the cancel
    private static readonly Dictionary<long, long> placed = new();

    private static int ticks;

    private static readonly Dictionary<string, Regex> patternRegexes = new();

    private static OctahedronConfig Config => Plugin.Config;

    // --- a blueprint at a table, for a player ---

    private sealed class Plan
    {
        public string Error;
        public MyProjectorBase Table;
        public bool Pending;
        public MyCubeGrid Shipyard;
        public string Station;
        public long Player;
        public List<MyObjectBuilder_CubeGrid> Blueprint;
        public string Ship = "";
        public int Pcu;
        public bool Free;

        // The blocks left out, of DLCs the player doesn't own (ShipyardDlc)
        public string LeftOut;

        // The blocks left out, of mods this server doesn't have (LeaveOutUnknown)
        public string Unknown;

        // The player ignores PCU limits: a free build whatever the ship (FreePrints.Exempt)
        public bool Exempt;

        // Why it is a paid build, and which rule: pcu, blocked or cooldown
        public string Paid;
        public string PaidRule = "";
        public long Payer;
        public long Fee;

        // Every block's components and the ice for the hydrogen, raw
        public Dictionary<string, long> Needed = new();
        public double HydrogenLiters;

        // Liters the player's docked ships can spare, their ice too, at most what the build needs
        public double ShipHydrogen;

        // Why a press won't queue this ship: the queue is full, or the player's order is in it
        public string Blocker;

        public string Key =>
            $"{Ship}|{Pcu}|{Blueprint?.Sum(g => g.CubeBlocks.Count)}|{Free}|{PaidRule}|{Payer}|{Error}";
    }

    private sealed class TableQuote
    {
        public long Player;
        public string Key;
        public BuildQuote Quote;
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
        plan.Ship = blueprint[0].DisplayName ?? "Ship";
        // The table's own copy: the copy above has each grid's ids remapped anew
        if (Unproven(table))
            return Fail(
                plan,
                $"'{plan.Ship}' has subgrids, which your game may have sent damaged. {WorkshopHint}"
            );
        var main = blueprint[0];
        plan.Unknown = LeaveOutUnknown(blueprint);
        if (!blueprint.Contains(main))
            return Fail(
                plan,
                $"'{plan.Ship}' can't be built: its main grid has only blocks of mods this server doesn't have ({plan.Unknown})."
            );
        plan.LeftOut = ShipyardDlc.LeaveOut(
            blueprint,
            MySession.Static.Players.TryGetSteamId(identityId)
        );
        if (!blueprint.Contains(main))
            return Fail(
                plan,
                $"'{plan.Ship}' can't be built: its main grid needs DLCs you don't own ({plan.LeftOut})."
            );
        plan.Blueprint = blueprint;
        plan.Pcu = blueprint.Sum(g => g.CalculatePCUs());
        // The player is on this node, so its copy of their admin settings is current
        plan.Exempt = FreePrints.Exempt(identityId);
        var error = Fits(plan.Shipyard, blueprint, plan.Exempt, out _, out plan.Pending);
        if (plan.Pending)
            // Quoted again when the plan is done
            ShipyardArm.WhenDone(() => loaded[table.EntityId] = identityId);
        if (error != null)
            return Fail(plan, error);

        // Free, or why not
        var playerPcu = StarterShip.PlayerPcu(identity);
        var blocked = Blocked(blueprint);
        var wait = FreePrints.Wait(identityId);
        if (!plan.Exempt && playerPcu >= Config.FreeBuildPcuLimit)
            (plan.PaidRule, plan.Paid) = (
                "pcu",
                $"you have {playerPcu:N0} PCU, free builds are for players under {Config.FreeBuildPcuLimit:N0}"
            );
        else if (!FreePrints.Ready)
            return Fail(plan, "The shipyard is busy, try again in a few seconds.");
        else if (!plan.Exempt && wait > TimeSpan.Zero)
            (plan.PaidRule, plan.Paid) = (
                "cooldown",
                $"your next free ship is in {Math.Ceiling(wait.TotalMinutes)} min"
            );
        else if (!plan.Exempt && blocked.Count > 0)
            (plan.PaidRule, plan.Paid) = (
                "blocked",
                $"a free ship can't have {string.Join(", ", blocked)}"
            );
        plan.Free = plan.Paid == null;
        if (plan.Free)
            return plan;

        plan.Payer = identityId;
        plan.Fee = BuildBook.Fee(plan.Pcu, Config.ShipyardLaborFeePer100Pcu);
        foreach (var c in Components(blueprint, skipFirst: false))
            plan.Needed[Stations.ItemName(c.Key)] = c.Value * Amounts.Unit;
        plan.HydrogenLiters =
            HydrogenCapacity(blueprint) * Config.ShipyardHydrogenFillPercent / 100;
        if (plan.HydrogenLiters > 0)
        {
            plan.Needed[Hydrogen] = IceFor(plan.HydrogenLiters, up: true);
            var ships = DockedShips(table, identityId);
            plan.ShipHydrogen = Math.Min(
                plan.HydrogenLiters,
                SpareTanks(ships).Sum(Spare)
                    + Cargo(ships).Sum(i => (double)i.GetItemAmount(ItemId(Hydrogen)))
                        * Config.ShipyardHydrogenLitersPerIce
            );
        }
        return plan;
    }

    private static Plan Fail(Plan plan, string error)
    {
        plan.Error = error;
        return plan;
    }

    // Why the shipyard can't build the blueprint at all, or null, with the arm if it needs one.
    // Pending: the arm is still being planned, that is the reason given for now.
    private static string Fits(
        MyCubeGrid shipyard,
        List<MyObjectBuilder_CubeGrid> blueprint,
        bool exempt,
        out ShipyardArm.Layout layout,
        out bool pending
    )
    {
        layout = null;
        pending = false;
        var pcu = blueprint.Sum(g => g.CalculatePCUs());
        // The ordered ship is the player's, so GroupPcuLimit would stop welding it there, unless
        // the order is exempt (Exempt below)
        if (!exempt && Config.GroupPcuLimit > 0 && pcu > Config.GroupPcuLimit)
            return $"The blueprint has {pcu:N0} PCU, a ship at most {Config.GroupPcuLimit:N0}.";
        var main = blueprint[0];
        var arms = ShipyardArm.Rotors(shipyard).Count > 0;
        MyObjectBuilder_CubeBlock start = null;
        var outward = Vector3I.Zero;
        if (!arms && !FindStart(main, out start, out outward))
            return "The blueprint's main grid needs a 1x1x1 block on its outside to start from.";
        var space = FreeSpace(shipyard);
        var room = space.HalfExtent * 2;
        var size = Size(blueprint, start, outward, room, out _, out var turn);
        if (size.X > room.X || size.Y > room.Y || size.Z > room.Z)
            // A blueprint with subgrids got here intact (Unproven), but an old one can have them out
            // of place; a new one made offline has them right
            return $"The ship is {Meters(size)} (width, height, length), {shipyard.DisplayName} builds up to {Meters(room)}."
                + (
                    blueprint.Count > 1
                        ? " An old blueprint can have its subgrids out of place: blueprint the ship again in an offline game."
                        : ""
                );
        var needed = Components(blueprint, skipFirst: !arms)
            .Sum(c => MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value);
        var capacity = Containers(shipyard).Sum(i => (double)i.MaxVolume);
        if (needed > capacity)
            return $"The ship's components take {needed:N0} m³, {shipyard.DisplayName}'s containers hold {capacity:N0} m³.";
        if (arms)
        {
            layout = ShipyardArm.Plan(
                shipyard,
                ShipCenter(shipyard, space, size.Z),
                turn * shipyard.WorldMatrix.GetOrientation(),
                blueprint,
                out var error,
                out pending
            );
            return pending ? "The shipyard is planning a support arm for the ship..." : error;
        }
        return null;
    }

    // The ship's size along the shipyard, the scaffold on the start block included, and the turn
    // of its main grid that gives it: its cockpit along the shipyard if it fits that way, else by
    // right angles from there until it fits the room
    private static Vector3D Size(
        List<MyObjectBuilder_CubeGrid> blueprint,
        MyObjectBuilder_CubeBlock start,
        Vector3I outward,
        Vector3D room,
        out BoundingBoxD bounds,
        out MatrixD turn
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
        var (forward, up) = StarterShip.CockpitFrame(main);
        var cockpit = MatrixD.Transpose(MatrixD.CreateWorld(Vector3D.Zero, forward, up));
        var size = Vector3D.Abs(Vector3D.TransformNormal(bounds.Size, cockpit));
        var axes = ShipyardPlacement.Align([size.X, size.Y, size.Z], [room.X, room.Y, room.Z]);
        var align = MatrixD.Identity;
        for (var i = 0; i < 3; i++)
        {
            align[i, i] = 0;
            align[axes[i], i] = 1;
        }
        // Swapping two axes alone would mirror the ship
        if (align.Determinant() < 0)
            align[axes[0], 0] = -1;
        turn = cockpit * align;
        return Vector3D.Abs(Vector3D.TransformNormal(bounds.Size, turn));
    }

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

    // A held table is the player's block on the station's grid
    internal static bool IsTable(MyProjectorBase table) =>
        table.BlockDefinition.Id.SubtypeName == Config.StarterShipTableSubtype
        && (
            MySession.Static.Players.IdentityIsNpc(table.OwnerId)
            || table.CubeGrid.BigOwners.Any(MySession.Static.Players.IdentityIsNpc)
        );

    /// <summary>The player who holds the table, 0 when it is open to everyone.</summary>
    internal static long Holder(MyProjectorBase table) =>
        MySession.Static.Players.IdentityIsNpc(table.OwnerId) ? 0 : table.OwnerId;

    // Only the player has the table's terminal now
    private static void Hold(MyProjectorBase table, long player) =>
        ShipyardTurn.SetOwner(table, player, MyOwnershipShareModeEnum.None);

    // Back to OCT and open to everyone
    private static void Open(MyProjectorBase table)
    {
        loadedAt.Remove(table.EntityId);
        var players = MySession.Static.Players;
        ShipyardTurn.SetOwner(
            table,
            players.IdentityIsNpc(table.OwnerId)
                ? table.OwnerId
                : table.CubeGrid.BigOwners.FirstOrDefault(players.IdentityIsNpc),
            MyOwnershipShareModeEnum.All
        );
    }

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

    // !oct table hold: the admin counts as the player who loaded the nearest table's blueprint
    public static string Hold(long identityId, string tableName)
    {
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return "Spawn first.";
        var table = StarterShip.FindTable(character, tableName, Config);
        if (table == null || TableHologram.Blueprint(table) == null)
            return "Stand by a station's hologram table with a blueprint loaded.";
        Hold(table, identityId);
        loadedAt[table.EntityId] = DateTime.UtcNow;
        return $"You hold the blueprint of hologram table {table.EntityId}.";
    }

    // !oct table remove: what the Remove button of the table's terminal asks the server to do
    public static string Remove(long identityId, string tableName)
    {
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null || character.IsDead || character.MarkedForClose)
            return "Spawn first.";
        var table = StarterShip.FindTable(character, tableName, Config);
        if (table == null || TableHologram.Blueprint(table) == null)
            return "Stand by a station's hologram table with a blueprint loaded.";
        if (Holder(table) is var holder && holder != 0 && holder != identityId)
            return $"{Name(holder)} holds this hologram table.";
        ClearTable(table);
        return $"The blueprint of hologram table {table.EntityId} is removed.";
    }

    // !oct table show: what the nearest table's screen and billboard show, and the queue's size
    public static string Show(long identityId, string tableName)
    {
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        var table = character == null ? null : StarterShip.FindTable(character, tableName, Config);
        if (table == null)
            return "Stand by a station's hologram table.";
        string Screen(IMyTextSurfaceProvider provider, int index) =>
            ((MyTextPanelComponent)provider.GetSurface(index)).Text;
        var panel = DetailsPanel(table);
        var how = panel == null ? null : (IMyTextSurfaceProvider)panel;
        var holder = Holder(table) is var player && player != 0 ? Name(player) : "none";
        var queue = Grids(table.CubeGrid)
            .SelectMany(g => g.GetFatBlocks<MyTextPanel>())
            .FirstOrDefault(p =>
                string.Equals(
                    p.CustomName?.ToString(),
                    Config.ShipyardQueuePanelName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        return $"BLUEPRINT {TableHologram.Blueprint(table) != null} HOLDER {holder}\n"
            + $"TABLE\n{Screen(table, Config.ShipyardQuoteSurface)}\nDETAILS\n"
            + (how == null ? "none" : Screen(how, 0))
            + "\nHOW\n"
            + (how == null ? "none" : Screen(how, 1))
            + "\nQUEUE\n"
            + (queue == null ? "none" : Screen(queue, 0));
    }

    /// <summary>A press of the table's button. Who presses decides what it means: the presser's own queue entry or running build is canceled, else the blueprint they hold is ordered, else they get a repair slot. Nobody else's blueprint or queue entry is touched.</summary>
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
        var station = StationOf(table.CubeGrid);
        var shipyard = FindShipyard(table);
        if (station == null || shipyard == null)
        {
            tell("This station has no shipyard.");
            return;
        }
        if (!Plugin.Simulated(table.CubeGrid) || !Plugin.Simulated(shipyard))
        {
            tell("The station is busy, try again in a few seconds.");
            return;
        }
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Queue, Station = station },
            (text, error) =>
            {
                if (text == null)
                {
                    tell(error);
                    return;
                }
                var mine = Mine(DecodeQueue(text), identityId);
                var holder = Holder(table);
                var hasBlueprint =
                    TableHologram.Blueprint(table) != null && (holder == 0 || holder == identityId);
                if (mine != null && mine.Phase is BuildPhase.Waiting or BuildPhase.Building)
                    Cancel(mine, station, shipyard, tell);
                else if (hasBlueprint)
                {
                    // Loaded before a restart or a move of the station to this node: the presser's now
                    Hold(table, identityId);
                    var plan = MakePlan(identityId, table);
                    if (plan.Pending)
                        // The press stands: it goes on when the arm is planned
                        ShipyardArm.WhenDone(() => Press(identityId, table, tell));
                    else
                        Place(plan, tell);
                }
                else if (mine?.Phase == BuildPhase.Loading)
                    Begin(table, mine, tell);
                else if (mine?.Phase == BuildPhase.After)
                    tell(
                        $"Your turn at this shipyard ends in {Left(mine.TurnEnds)}. "
                            + "Load a blueprint and press to build with what is in the containers, or end the turn with !oct shipyard done."
                    );
                else
                    BookRepair(identityId, station, table, tell);
            }
        );
    }

    private static void Cancel(
        BuildOrder entry,
        string station,
        MyCubeGrid shipyard,
        Action<string> tell
    )
    {
        if (!entry.Started)
            Builds.Execute(
                Guid.NewGuid(),
                new BuildRequest
                {
                    Verb = BuildVerb.Cancel,
                    Station = station,
                    Player = entry.Player,
                },
                (_, message) =>
                {
                    tell(message);
                    Refresh(station);
                }
            );
        else if (repairs.TryGetValue(shipyard.EntityId, out var slot) && slot.Id == entry.Id)
            EndRepair(shipyard, slot, "you canceled it");
        else if (orders.TryGetValue(shipyard.EntityId, out var order) && order.Id == entry.Id)
        {
            order.Canceled = true;
            End(shipyard, order, BlocksDone(shipyard, order));
        }
        else if (docks.ContainsKey(shipyard.EntityId))
            tell($"'{entry.Ship}' is finished, it can't be canceled now.");
        else
            tell("The shipyard is busy, try again in a few seconds.");
    }

    // --- repair slots ---

    private static void BookRepair(
        long player,
        string station,
        MyProjectorBase table,
        Action<string> tell
    )
    {
        if (Config.ShipyardRepairIdleMinutes <= 0)
        {
            tell("Load a blueprint into the hologram table first.");
            return;
        }
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Order,
                Station = station,
                Player = player,
                Ship = RepairName,
                Repair = true,
            },
            (ok, message) =>
            {
                if (!ok)
                {
                    tell(message);
                    return;
                }
                tell(
                    "You have booked a Repair slot in this station's shipyard queue, as you have no "
                        + "blueprint on the hologram table. Press the button again to cancel it."
                );
                placed[table.EntityId] = player;
                Refresh(station);
            }
        );
    }

    private static void BeginRepair(string station, MyCubeGrid shipyard, BuildOrder next)
    {
        if (
            orders.ContainsKey(shipyard.EntityId)
            || docks.ContainsKey(shipyard.EntityId)
            || !starting.Add(shipyard.EntityId)
        )
            return;
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
                    Plugin.Log.Warning($"Repair slot {next.Id} did not start: {message}");
                    return;
                }
                ClearForTurn(shipyard);
                RunRepair(station, shipyard, next);
                Tell(
                    next.Player,
                    $"{shipyard.DisplayName} is yours and your faction's now: dock your ship with its "
                        + "repair projector and components to one of its connectors. The slot ends after "
                        + $"{Config.ShipyardRepairIdleMinutes} min without building or welding, or when you "
                        + "press the button again."
                );
            }
        );
    }

    // Also picks a running slot up after a restart or a move of the station to this node
    private static void RunRepair(string station, MyCubeGrid shipyard, BuildOrder entry)
    {
        ShipyardTurn.Give(shipyard, entry.Player);
        repairs[shipyard.EntityId] = new RepairSlot
        {
            Id = entry.Id,
            Station = station,
            Player = entry.Player,
            LastWork = DateTime.UtcNow,
        };
        lost.Remove(shipyard.EntityId);
        Welders(shipyard, true);
        Plugin.Log.Info(
            $"Repair slot {entry.Id} of {Name(entry.Player)} runs at {shipyard.DisplayName}"
        );
    }

    // The welders of a shipyard in a repair slot built or welded something
    internal static void Worked(MyEntity tool)
    {
        if (
            repairs.Count > 0
            && (tool as MyCubeBlock)?.CubeGrid is { } grid
            && repairs.TryGetValue(grid.EntityId, out var slot)
        )
            slot.LastWork = DateTime.UtcNow;
    }

    // As a build's end: the player has their window to take what is in the shipyard
    private static void EndRepair(MyCubeGrid shipyard, RepairSlot slot, string why)
    {
        repairs.Remove(shipyard.EntityId);
        Welders(shipyard, false);
        ending.Add(slot.Id);
        Plugin.Log.Info(
            $"Repair slot {slot.Id} of {Name(slot.Player)} at {shipyard.DisplayName} ended: {why}"
        );
        After(
            new BuildOrder
            {
                Id = slot.Id,
                Station = slot.Station,
                Player = slot.Player,
                Ship = RepairName,
            },
            0,
            false,
            new Dictionary<string, long>(),
            $"Your repair slot at {shipyard.DisplayName} ended: {why}. "
                + string.Format(TakeWithin(), shipyard.DisplayName),
            () =>
            {
                ending.Remove(slot.Id);
                Refresh(slot.Station);
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
        )
        {
            Quote(
                plan,
                quote =>
                    tell(
                        quote == null && !plan.Free
                            ? "The station's market did not answer, try again."
                            : LeftOutText(plan)
                                + $"Your quote for '{plan.Ship}' is on the hologram table's screen"
                                + (
                                    plan.Free
                                        ? ", a free build."
                                        : $": a paid build ({plan.Paid}), {quote.Total:N0} SC if you buy everything at your turn."
                                )
                                + " Press the button to order."
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
            Exempt = plan.Exempt,
        };
        if (!plan.Free)
        {
            request.Payer = plan.Payer;
            request.Fee = plan.Fee;
            request.Needed = plan.Needed;
        }
        Builds.Order(
            Guid.NewGuid(),
            request,
            (ok, message, order) =>
            {
                tell(ok ? LeftOutText(plan) + message : message);
                if (ok)
                {
                    if (plan.Exempt)
                        Plugin.Log.Info(
                            $"Free builds: {Name(plan.Player)} ignores PCU limits, '{plan.Ship}' is free "
                                + $"({plan.Pcu} PCU) without the PCU limits, cool-down or blocked blocks"
                        );
                    Plugin.Log.Info(
                        $"Hologram table {plan.Table.EntityId}: blueprint cleared, '{plan.Ship}' is in the queue"
                    );
                    placed[plan.Table.EntityId] = plan.Player;
                    ClearTable(plan.Table);
                    // In the player's turn: the shipyard is theirs, so it starts at once
                    if (order?.Phase == BuildPhase.Loading)
                        Begin(plan.Table, order, tell);
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
        private static void Postfix(
            MyProjectorBase __instance,
            List<MyObjectBuilder_CubeGrid> gridBuilders
        )
        {
            if (!Sync.IsServer || MyEventContext.Current.IsLocallyInvoked)
                return;
            var identity = MySession.Static.Players.TryGetIdentityId(
                MyEventContext.Current.Sender.Value
            );
            // Any projector ends up here: only a station's table is held
            if (identity == 0 || !IsTable(__instance) || StationOf(__instance.CubeGrid) == null)
                return;
            workshopTables.Remove(__instance.EntityId);
            // Not from Multigrid Projector: the subgrids may be damaged, so no ship comes of it
            if (Unproven(gridBuilders))
            {
                Tell(
                    identity,
                    $"'{gridBuilders[0].DisplayName}' has subgrids, which your game damages when it sends a blueprint "
                        + $"without the Multigrid Projector plugin, so the shipyard took it off the table. {WorkshopHint}"
                );
                // Once the game has passed the blueprint on to the other clients
                var table = __instance;
                MySandboxGame.Static.Invoke(
                    () =>
                    {
                        if (!table.MarkedForClose)
                            ClearTable(table);
                    },
                    "Octahedron damaged blueprint"
                );
                return;
            }
            loaded[__instance.EntityId] = identity;
            loadedAt[__instance.EntityId] = DateTime.UtcNow;
            Hold(__instance, identity);
        }
    }

    private static void Quote(Plan plan, Action<BuildQuote> done)
    {
        if (plan.Error != null)
        {
            ShowQuote(plan, null);
            done(null);
            return;
        }
        Builds.Read(
            new BuildRequest { Verb = BuildVerb.Queue, Station = plan.Station },
            (text, _) =>
            {
                var queue = text == null ? [] : DecodeQueue(text);
                var mine = Mine(queue, plan.Player);
                plan.Blocker =
                    mine?.Phase == BuildPhase.After && !plan.Free ? null
                    : mine != null
                        ? mine.Active
                                ? $"'{mine.Ship}' is running for you. Press the button to cancel it first."
                            : $"'{mine.Ship}' is in the queue for you. Press the button to cancel it first."
                    : queue.Count >= Config.ShipyardQueueMax
                        ? $"The shipyard's queue is full, {Config.ShipyardQueueMax} ships."
                    : null;
                if (plan.Free)
                {
                    ShowQuote(plan, null);
                    done(null);
                }
                else
                    PaidQuote(plan, done);
            }
        );
    }

    private static void PaidQuote(Plan plan, Action<BuildQuote> done)
    {
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

    private static string LeftOutText(Plan plan) =>
        (
            plan.Unknown == null
                ? ""
                : $"Left out, this server doesn't have their mods: {plan.Unknown}. "
        ) + (plan.LeftOut == null ? "" : $"Left out, you don't own their DLCs: {plan.LeftOut}. ");

    private static void ShowQuote(Plan plan, BuildQuote quote)
    {
        quotes[plan.Table.EntityId] = new TableQuote
        {
            Player = plan.Player,
            Key = plan.Key,
            Quote = quote,
        };
        var text = new StringBuilder();
        if (plan.Blueprint != null)
            text.AppendLine($"'{plan.Ship}' for {Name(plan.Player)}");
        else
            text.AppendLine($"Shipyard, for {Name(plan.Player)}");
        text.AppendLine();
        if (plan.Error != null)
            text.AppendLine(plan.Error);
        else if (plan.Free)
            text.AppendLine("A free build.");
        else if (quote != null)
            text.AppendLine($"A paid build: {plan.Paid}.")
                .AppendLine($"Bring the parts at your turn, or {quote.Total:N0} SC.");
        if (plan.Error == null)
        {
            if (plan.LeftOut != null || plan.Unknown != null)
                text.AppendLine(LeftOutText(plan).TrimEnd());
            text.AppendLine();
            if (plan.Blocker != null)
                text.AppendLine(plan.Blocker);
            else if (quote?.Short(Hydrogen) != null)
                text.AppendLine("Not everything is for sale here: bring the rest at your turn.");
            else
                text.AppendLine("Press the button to confirm the build.");
        }
        var parts = plan.Blueprint == null ? null : Parts(plan, quote);
        if (
            ShowDetails(plan.Table, parts ?? DetailsIdle, quote != null ? PaidColumns : FreeColumns)
        )
            WriteTable(plan.Table, text.ToString());
        else
            // A station from before the billboard, until the world is wiped
            Write(
                plan.Table,
                Config.ShipyardQuoteSurface,
                $"{PanelText.Wrap(text.ToString(), TableColumns)}\n\n{PanelText.Wrap(parts ?? "", PaidColumns)}",
                2f
            );
    }

    // The ship's bill of materials, with where each item comes from once a paid build's quote is in
    private static string Parts(Plan plan, BuildQuote quote)
    {
        var text = new StringBuilder($"'{plan.Ship}', {plan.Pcu:N0} PCU\n\n");
        if (quote != null)
        {
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
        }
        else
            text.Append(
                BuildBook.PartsText(
                    Components(plan.Blueprint, skipFirst: false)
                        .ToDictionary(
                            c => Market.ItemTitle(Stations.ItemName(c.Key)),
                            c => (long)c.Value
                        )
                )
            );
        return text.ToString();
    }

    private static MyLCDPanelsBlock DetailsPanel(MyProjectorBase table)
    {
        if (string.IsNullOrWhiteSpace(Config.ShipyardDetailsPanelName))
            return null;
        var position = table.PositionComp.GetPosition();
        return Grids(table.CubeGrid)
            .SelectMany(g => g.GetFatBlocks<MyLCDPanelsBlock>())
            .Where(p =>
                string.Equals(
                    p.CustomName?.ToString(),
                    Config.ShipyardDetailsPanelName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .OrderBy(p => Vector3D.Distance(p.PositionComp.GetPosition(), position))
            .FirstOrDefault();
    }

    // The billboard's first line stays what the blueprint has; false when the station has no billboard
    private static bool ShowDetails(MyProjectorBase table, string text, int columns = FreeColumns)
    {
        var panel = DetailsPanel(table);
        if (panel == null)
            return false;
        var provider = (IMyTextSurfaceProvider)panel;
        // GetText only has what a script wrote since the world's load
        var shown = ((MyTextPanelComponent)provider.GetSurface(0)).Text;
        var first = (shown ?? "").Split('\n')[0].TrimEnd();
        // Not wrapped: the first line has to stay one for the next write to find it
        Write(provider, 0, $"{first}\n\n{PanelText.Wrap(text, columns)}", 0.7f);
        return true;
    }

    private static void WriteTable(MyProjectorBase table, string text) =>
        Write(table, Config.ShipyardQuoteSurface, PanelText.Wrap(text, TableColumns), 2f);

    private static void Write(
        IMyTextSurfaceProvider provider,
        int index,
        string text,
        float maxSize = 0.7f
    )
    {
        if (index < 0 || index >= provider.SurfaceCount)
            return;
        var surface = provider.GetSurface(index);
        surface.ContentType = ContentType.TEXT_AND_IMAGE;
        surface.Font = "Monospace";
        surface.FontSize = PanelText.FontSize(
            text,
            surface.SurfaceSize.X,
            surface.SurfaceSize.Y,
            Math.Min(surface.TextureSize.X, surface.TextureSize.Y),
            maxSize
        );
        surface.TextPadding = 2;
        if (surface.GetText() != text)
            surface.WriteText(text);
    }

    // --- the update ---

    public static void Update()
    {
        ticks++;
        PurgeCache();
        ShipyardAway.Update();
        ShipyardArm.Update();
        if (docks.Count > 0)
            UpdateDocks();
        if (trials.Count > 0)
            WatchTrials();
        if (ticks % QuoteTicks == 0 && loaded.Count > 0)
        {
            foreach (var (tableId, player) in loaded.ToList())
                if (
                    MyEntities.TryGetEntityById(tableId, out MyProjectorBase table)
                    && IsTable(table)
                )
                    Quote(MakePlan(player, table), _ => { });
            loaded.Clear();
            // A blueprint someone removed in the terminal
            foreach (var tableId in quotes.Keys.ToList())
                if (
                    MyEntities.TryGetEntityById(tableId, out MyProjectorBase table)
                    && TableHologram.Blueprint(table) == null
                )
                {
                    Plugin.Log.Info($"Hologram table {tableId}: blueprint removed");
                    Open(table);
                    quotes.Remove(tableId);
                    if (StationOf(table.CubeGrid) is { } station)
                        Refresh(station);
                }
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
        ShipyardAway.Working(
            orders
                .Values.Where(o => !o.Free)
                .Select(o => o.Player)
                .Concat(repairs.Values.Select(r => r.Player))
        );
        foreach (var station in StationTables())
        {
            Expire(station.Value);
            Refresh(station.Key, station.Value);
        }
    }

    // A table's blueprint is cleared and the table opened when the player who holds it leaves the
    // station, or ShipyardTableMinutes after it was loaded
    private static void Expire(List<MyProjectorBase> tables)
    {
        BoundingSphereD? sphere = null;
        foreach (var table in tables)
        {
            if (!Plugin.Simulated(table.CubeGrid))
                continue;
            if (TableHologram.Blueprint(table) == null)
            {
                Open(table);
                continue;
            }
            if (!loadedAt.TryGetValue(table.EntityId, out var at))
                loadedAt[table.EntityId] = at = DateTime.UtcNow;
            var holder = Holder(table);
            string why = null;
            if (DateTime.UtcNow - at >= TimeSpan.FromMinutes(Config.ShipyardTableMinutes))
                why = $"loaded {Config.ShipyardTableMinutes} min ago";
            else if (holder != 0)
            {
                sphere ??= StationSphere(table.CubeGrid);
                if (Left(holder, sphere.Value))
                    why = $"{Name(holder)} left the station";
            }
            if (why == null)
                continue;
            Plugin.Log.Info($"Hologram table {table.EntityId}: blueprint cleared, {why}");
            ClearTable(table);
        }
    }

    // Away from this node (logged off, or handed over to another node) or out of the sphere;
    // a dead player may still respawn at the station
    private static bool Left(long player, BoundingSphereD sphere)
    {
        if (!MySession.Static.Players.IsPlayerOnline(player))
            return true;
        var character = MySession.Static.Players.TryGetIdentity(player)?.Character;
        return character != null
            && !character.IsDead
            && sphere.Contains(character.PositionComp.GetPosition()) == ContainmentType.Disjoint;
    }

    private static BoundingSphereD StationSphere(MyCubeGrid grid)
    {
        var box = BoundingBoxD.CreateInvalid();
        foreach (var g in Grids(grid))
            box.Include(g.PositionComp.WorldAABB);
        return BoundingSphereD.CreateFromBoundingBox(box);
    }

    private static void ClearTable(MyProjectorBase table)
    {
        quotes.Remove(table.EntityId);
        ((Sandbox.ModAPI.IMyProjector)table).SetProjectedGrid(null);
        Open(table);
        ShowDetails(table, DetailsIdle);
        WriteTable(table, TableIdle);
    }

    // A table without a blueprint offers the player who just queued a ship the cancel
    private static void ShowEmpty(MyProjectorBase table, List<BuildOrder> queue)
    {
        if (TableHologram.Blueprint(table) != null)
            return;
        var turn = queue.FirstOrDefault(o => o.Phase == BuildPhase.Loading);
        if (turn != null)
        {
            ShowTurn(table, turn);
            return;
        }
        ShowDetails(table, DetailsIdle);
        placed.TryGetValue(table.EntityId, out var player);
        var order = queue.FirstOrDefault(o => o.Player == player && !o.Started);
        if (order == null)
        {
            placed.Remove(table.EntityId);
            WriteTable(table, TableIdle);
            return;
        }
        WriteTable(
            table,
            $"'{order.Ship}' is in the queue for {Name(player)}.\n\n"
                + $"{Name(player)} can cancel it by pressing the button again."
        );
    }

    // The turn's player sees what is still missing, and what the press costs
    private static void ShowTurn(MyProjectorBase table, BuildOrder order)
    {
        var shipyard = FindShipyard(table);
        if (shipyard == null)
            return;
        var lacking = Lacking(shipyard, order);
        Builds.Read(
            new BuildRequest
            {
                Verb = BuildVerb.Quote,
                Station = order.Station,
                Payer = order.Payer,
                Fee = order.Fee,
                Needed = lacking,
            },
            (text, _) =>
            {
                if (text == null || table.MarkedForClose || TableHologram.Blueprint(table) != null)
                    return;
                var quote = BuildQuote.Decode(text);
                ShowDetails(
                    table,
                    $"'{order.Ship}', {order.Pcu:N0} PCU\n\nMissing from the containers:\n"
                        + BuildBook.QuoteText(quote, Hydrogen, 0, Market.ItemTitle, Units),
                    PaidColumns
                );
                WriteTable(
                    table,
                    $"{Name(order.Player)}'s turn for '{order.Ship}'.\n\n"
                        + $"Load the parts within {Left(order.TurnEnds)}, then press the button: it buys what is missing."
                );
            }
        );
    }

    /// <summary>The shipyard has a job, or one coming, as of the queue's last answer.</summary>
    internal static bool Busy(long shipyard) =>
        queued.Contains(shipyard)
        || orders.ContainsKey(shipyard)
        || repairs.ContainsKey(shipyard)
        || docks.ContainsKey(shipyard)
        || starting.Contains(shipyard);

    private static void Check(MyCubeGrid shipyard)
    {
        if (!Plugin.Simulated(shipyard))
        {
            orders.Remove(shipyard.EntityId);
            repairs.Remove(shipyard.EntityId);
            queued.Remove(shipyard.EntityId);
            return;
        }
        if (docks.ContainsKey(shipyard.EntityId))
            return;
        if (repairs.TryGetValue(shipyard.EntityId, out var slot))
        {
            if (
                DateTime.UtcNow - slot.LastWork
                >= TimeSpan.FromMinutes(Config.ShipyardRepairIdleMinutes)
            )
                EndRepair(
                    shipyard,
                    slot,
                    $"no building or welding for {Config.ShipyardRepairIdleMinutes} min"
                );
            else
                Welders(shipyard, true);
            return;
        }
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
            else if (!starting.Contains(shipyard.EntityId))
                ClearHeads(shipyard);
            return;
        }
        if (order?.Projector != projector)
        {
            order = Restore(projector);
            if (order == null)
                return;
            orders[shipyard.EntityId] = order;
            Plugin.Log.Info(
                $"Shipyard order '{order.Name}' ({projector.CubeGrid.EntityId}) "
                    + $"of {Name(order.Player)} picked up at {shipyard.DisplayName}, until {order.Ends:u}"
            );
        }
        if (!order.Free && ShipyardTurn.Holder(shipyard) != order.Player)
            ShipyardTurn.Give(shipyard, order.Player);

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
            // The arm first: the projection starts when all of its blocks are complete
            if (!ArmComplete(order))
            {
                if (DateTime.UtcNow >= order.Ends)
                    End(shipyard, order, "the support arm unfinished");
                else if (ArmStalled(order))
                    FinishArm(shipyard, order);
                else
                    Welders(shipyard, true);
                return;
            }
            if (!projector.Enabled)
            {
                projector.Enabled = true;
                Plugin.Log.Info(
                    $"Shipyard order {order.Id} at {shipyard.DisplayName}: the arm is complete, projecting"
                );
            }
        }
        if (trials.ContainsKey(order))
            return;
        if (Complete(shipyard, order) >= order.Blocks)
            End(shipyard, order, null);
        else if (DateTime.UtcNow >= order.Ends)
            End(shipyard, order, BlocksDone(shipyard, order));
        else if (Stalled(shipyard, order))
            Stall(shipyard, order);
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
            if (
                TableHologram.Blueprint(table) != null
                && quotes.TryGetValue(table.EntityId, out var shown)
            )
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
                Inspect(shipyard, station, text);
                if (queue.Count > 0)
                    queued.Add(shipyard.EntityId);
                else
                    queued.Remove(shipyard.EntityId);
                ShowQueue(grids, shipyard, queue);
                foreach (var table in tables)
                    ShowEmpty(table, queue);
                // No turn: the shipyard is off, also right after a start and in a world from before
                // this rule. A turn the queue doesn't know of ends, as after a crash between the
                // two saves. A free build keeps its welders, whose switch Check holds on.
                if (
                    !queue.Any(o => o.Active && (!o.Free || o.Repair))
                    && !repairs.ContainsKey(shipyard.EntityId)
                    && !starting.Contains(shipyard.EntityId)
                    && (
                        ShipyardTurn.Holder(shipyard) != 0
                        || !(
                            queue.Any(o => o.Active)
                            || orders.ContainsKey(shipyard.EntityId)
                            || docks.ContainsKey(shipyard.EntityId)
                        )
                    )
                )
                    FloatFree(shipyard, 0);
                if (queue.Count == 0)
                {
                    // A ship an order built has its deadline also when no order waits for it
                    if (
                        Obstacle(shipyard) is { } obstacle
                        && BuiltShipOf(shipyard, obstacle) != null
                    )
                        ClearWay(station, shipyard, obstacle, false);
                    else
                        blockers.Remove(shipyard.EntityId);
                    return;
                }
                var next = queue[0];
                if (next.Phase == BuildPhase.After)
                {
                    if (!ending.Contains(next.Id))
                        CheckTurn(shipyard, next);
                }
                else if (next.Repair && next.Started)
                {
                    if (!repairs.ContainsKey(shipyard.EntityId) && !ending.Contains(next.Id))
                        RunRepair(station, shipyard, next);
                }
                else if (next.Repair)
                    BeginRepair(station, shipyard, next);
                else if (next.Started)
                    Lost(shipyard, next);
                else if (next.Active)
                    CheckTurn(shipyard, next);
                else
                    Start(station, shipyard, queue[0]);
            }
        );
    }

    private static List<BuildOrder> DecodeQueue(string text) =>
        text.Split('\n').Where(l => l.StartsWith("O\t")).Select(BuildBook.DecodeOrder).ToList();

    private static void ShowQueue(
        List<MyCubeGrid> grids,
        MyCubeGrid shipyard,
        List<BuildOrder> queue
    )
    {
        if (string.IsNullOrWhiteSpace(Config.ShipyardQueuePanelName))
            return;
        var text = new StringBuilder(
            $"{PanelText.Clip($"{shipyard.DisplayName} queue", QueueColumns, 1)}\n\n"
        );
        if (queue.Count == 0)
            text.AppendLine("No ships in the queue.");
        orders.TryGetValue(shipyard.EntityId, out var running);
        int place = 1;
        foreach (var o in queue)
        {
            if (o.Repair)
            {
                text.AppendLine(
                    PanelText.Item(
                        $"{place++}. ",
                        $"{RepairName} for {Name(o.Player)}: {(o.Started ? "repairing" : o.Active ? "taking parts" : "waiting")}",
                        QueueColumns,
                        QueueEntryLines
                    )
                );
                continue;
            }
            var state = o.Phase switch
            {
                BuildPhase.Building when running != null =>
                    $"building, done within {PanelText.Left(running.Ends, DateTime.UtcNow)}",
                BuildPhase.Building => o.Grid != 0 ? "breaking down" : "building",
                BuildPhase.Loading => $"loading parts within {Left(o.TurnEnds)}",
                BuildPhase.After => $"taking what is left within {Left(o.TurnEnds)}",
                _ when o.Grid != 0 => "waiting, to break down",
                _ when o.Free => "waiting, free",
                _ => "waiting, paid",
            };
            text.AppendLine(
                PanelText.Item(
                    $"{place++}. ",
                    $"'{o.Ship}' ({o.Pcu:N0} PCU) for {Name(o.Player)}: {state}",
                    QueueColumns,
                    QueueEntryLines
                )
            );
        }
        var obstacle =
            running == null && queue.FirstOrDefault()?.Repair != true ? Obstacle(shipyard) : null;
        if (obstacle != null && queue.Count > 0)
            text.AppendLine()
                .AppendLine(
                    PanelText.Clip(
                        $"Waiting for '{obstacle.DisplayName}' to leave the shipyard.",
                        QueueColumns,
                        QueueEntryLines
                    )
                );
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

    private static void Start(string station, MyCubeGrid shipyard, BuildOrder front)
    {
        if (
            orders.ContainsKey(shipyard.EntityId)
            || docks.ContainsKey(shipyard.EntityId)
            || !starting.Add(shipyard.EntityId)
        )
            return;
        Sweep(shipyard, true);
        if (Obstacle(shipyard) is { } obstacle)
        {
            ClearWay(station, shipyard, obstacle);
            starting.Remove(shipyard.EntityId);
            return;
        }
        blockers.Remove(shipyard.EntityId);
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
                Builds.ReadBlueprint(
                    next.Id,
                    encoded =>
                    {
                        // Still in the queue if only the read failed: the next update tries again
                        if (encoded == null || shipyard.MarkedForClose)
                            starting.Remove(shipyard.EntityId);
                        else
                            StartOrder(shipyard, station, next, encoded);
                    }
                );
            }
        );
    }

    private static void StartOrder(
        MyCubeGrid shipyard,
        string station,
        BuildOrder next,
        string encoded
    )
    {
        var blueprint = DecodeBlueprint(encoded);
        ShipyardArm.Layout layout = null;
        var pending = false;
        var error =
            blueprint == null || blueprint.Count == 0
                ? "its blueprint could not be read"
                : Fits(shipyard, blueprint, next.Exempt, out layout, out pending);
        if (pending)
        {
            // Nothing has started yet: the next check starts it, once the plan is done
            starting.Remove(shipyard.EntityId);
            ShipyardArm.WhenDone(() => Refresh(station));
            return;
        }
        if (error != null)
        {
            // Not started: nothing left the order
            Finish(next, $"'{next.Ship}' can't be built: {error}");
            starting.Remove(shipyard.EntityId);
            return;
        }
        if (!next.Free)
        {
            StartTurn(shipyard, next);
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
                    Plugin.Log.Warning($"Shipyard order {next.Id} did not start: {message}");
                    return;
                }
                error = Create(shipyard, next, blueprint, layout);
                if (error != null)
                    Finish(next, $"'{next.Ship}' can't be built: {error}");
            }
        );
    }

    // The ship's first block and the scaffold, the components loaded; null when it started
    private static string Create(
        MyCubeGrid shipyard,
        BuildOrder next,
        List<MyObjectBuilder_CubeGrid> blueprint,
        ShipyardArm.Layout layout
    )
    {
        var space = FreeSpace(shipyard);
        var obstacle = Obstacle(shipyard);
        if (obstacle != null)
            return $"'{obstacle.DisplayName}' is in {shipyard.DisplayName}";
        if (layout != null)
            return CreateWithArm(shipyard, next, blueprint, layout);
        var main = blueprint[0];
        FindStart(main, out var start, out var outward);
        // The projector anchors the projection's first block, so the start block goes first
        main.CubeBlocks.Remove(start);
        main.CubeBlocks.Insert(0, start);
        Vector3I startCell = start.Min;
        var scaffold = new[] { startCell + outward, startCell + 2 * outward };
        var size = Size(
            blueprint,
            start,
            outward,
            space.HalfExtent * 2,
            out var bounds,
            out var turn
        );

        var components = Components(blueprint, skipFirst: true);
        var containers = Containers(shipyard).ToList();
        // A free build's are all loaded; a paid build's are there, but for what the player bought
        var load = next.Free ? components : Bought(next);
        var needed = load.Sum(c =>
            MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value
        );
        var free = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        if (needed > free)
            return $"the components take {needed:N0} m³, the containers have room for {free:N0} m³";

        var npc = ShipyardTurn.Npc(shipyard);
        var ends = DateTime.UtcNow.AddMinutes(Config.ShipyardOrderMinutes);
        var world = turn * shipyard.WorldMatrix.GetOrientation();
        var ship = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_CubeGrid>();
        ship.GridSizeEnum = main.GridSizeEnum;
        ship.IsStatic = true;
        ship.DisplayName = main.DisplayName ?? "Ship";
        ship.PersistentFlags =
            MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
        ship.PositionAndOrientation = new MyPositionAndOrientation(
            ShipCenter(shipyard, space, size.Z) - Vector3D.TransformNormal(bounds.Center, world),
            (Vector3)world.Forward,
            (Vector3)world.Up
        );
        var first = (MyObjectBuilder_CubeBlock)start.Clone();
        first.SetupForProjector();
        first.IntegrityPercent = first.BuildPercent = 1;
        first.ConstructionStockpile = null;
        first.Owner = first.BuiltBy = next.Player;
        first.ShareMode = OctFaction.BuildShare(next.Player);
        TargetNeutrals.Clear(first);
        ship.CubeBlocks =
        [
            first,
            Projector(main.GridSizeEnum, scaffold[0], outward, blueprint, npc),
            Battery(main.GridSizeEnum, scaffold[1], npc),
        ];
        MyEntities.RemapObjectBuilder(ship);
        if (MyEntities.CreateFromObjectBuilderAndAdd(ship, true) is not MyCubeGrid grid)
            return "the ship could not be started";

        var hydrogen = BuildHydrogen(next, blueprint);
        var projector = grid.GetFatBlocks<MyProjectorBase>().First();
        projector.CustomData = string.Join(
            "\n",
            $"Player={next.Player}",
            $"Ends={ends.ToString("o", CultureInfo.InvariantCulture)}",
            $"Build={next.Id}",
            $"Free={(next.Free ? 1 : 0)}",
            $"Exempt={(next.Exempt ? 1 : 0)}",
            $"Hydrogen={hydrogen.ToString("R", CultureInfo.InvariantCulture)}"
        );
        Load(containers, load);
        orders[shipyard.EntityId] = new Order
        {
            Projector = projector,
            Player = next.Player,
            Ends = ends,
            Blocks = blueprint.Sum(g => g.CubeBlocks.Count),
            Components = components,
            Id = next.Id,
            Free = next.Free,
            Exempt = next.Exempt,
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
        var message =
            $"Your order '{running.Ship}' at {shipyard.DisplayName} ended: the ship is gone.";
        if (running.Grid != 0)
            Breakdown(shipyard, running);
        else if (running.Free)
            Finish(running, message);
        else
            After(
                running,
                0,
                false,
                new Dictionary<string, long>(),
                $"{message} {string.Format(TakeWithin(), shipyard.DisplayName)}"
            );
    }

    // ship: the grid the order leaves in the build space
    private static void Finish(BuildOrder order, string message, long ship = 0)
    {
        Builds.Execute(
            Guid.NewGuid(),
            new BuildRequest
            {
                Verb = BuildVerb.Finish,
                Id = order.Id,
                Station = order.Station,
                Grid = ship,
            },
            (ok, answer) =>
            {
                if (!ok)
                    Plugin.Log.Error($"Shipyard order {order.Id} could not finish: {answer}");
                if (message != null)
                    Tell(
                        order.Player,
                        ok && answer.Length > 0 ? $"{message.TrimEnd('.')}. {answer}" : message
                    );
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
        trials.Remove(order);
        forced.Remove(order);
        if (finished.Remove(order, out var count))
            Plugin.Log.Info(
                $"Shipyard order {order.Id} at {shipyard.DisplayName}: the shipyard finished {count} blocks itself"
            );
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
            NoteFreeEnd(order, name);
            if (order.Hydrogen > 0)
                filled = order.Free
                    ? FillFree(shipyard, ship, order)
                    : FillHydrogen(ship, order.Hydrogen);
            Dynamic(shipyard, ship);
        }
        if (order.Free)
            RemoveLeftovers(shipyard, order.Components);
        Report(shipyard, order, ship, name, gone ? "gone" : null, why, filled, "");
    }

    // Tells the player how the build ended, and a paid build goes on to the last part of the turn
    private static void Report(
        MyCubeGrid shipyard,
        Order order,
        MyCubeGrid ship,
        string name,
        string missing,
        string why,
        double filled,
        string extra
    )
    {
        var gone = missing != null;
        if (gone && order.Canceled)
            NoteFreeEnd(order, name);
        var stalled =
            $"{shipyard.DisplayName} stopped building '{name}': its welders made no progress for {StallChecks * UpdateTicks / 60} s";
        var message = order.Canceled
            ? missing != null
                ? $"You canceled '{name}' at {shipyard.DisplayName}, no ship was built."
                : $"You canceled '{name}' at {shipyard.DisplayName} with {why}. The unfinished ship is yours."
            : order.Clang != null
                ? $"{shipyard.DisplayName} stopped building '{name}': {order.Clang} placed where another part of the ship is made it shake. The ship was removed, "
                    + (
                        order.Free
                            ? "and the free build doesn't count."
                            : "its components are in the containers."
                    )
                : order.Refund
                    ? $"{stalled}, with {order.Missing} missing. The ship was removed, "
                        + (
                            order.Free
                                ? "and the free build doesn't count."
                                : "its components are in the containers."
                        )
                    : order.Missing != null && !gone
                        ? $"{stalled}. The ship is yours without {order.Missing}."
                        : missing == "unbuilt"
                            ? $"{shipyard.DisplayName} stopped after {Config.ShipyardOrderMinutes} min with {why} of '{name}', no ship was built."
                            : gone
                                ? $"Your order '{name}' at {shipyard.DisplayName} ended: the ship is gone."
                                : why == null
                                    ? $"Your ship '{name}' is ready in {shipyard.DisplayName}. Fly it out to make room for the next order."
                                    : $"{shipyard.DisplayName} stopped after {Config.ShipyardOrderMinutes} min with {why} of '{name}'. "
                                        + "The unfinished ship is yours.";
        if (!order.Free)
        {
            if (order.Hydrogen > 0 && !gone)
                message +=
                    filled <= 0
                        ? " Warning: the ship's H2 tanks took no hydrogen, the ice for it is sold back."
                    : filled < order.Hydrogen - 1
                        ? $" Its H2 tanks took {filled:N0} of {order.Hydrogen:N0} L, the ice for the rest is sold back."
                    : $" Its H2 tanks have {filled:N0} L of hydrogen.";
        }
        else if (filled > 0)
            message += $" Its H2 tanks have {filled:N0} L of hydrogen.";
        message += extra;
        var left = gone || order.Refund || ship == null ? 0 : ship.EntityId;
        var ended = new BuildOrder
        {
            Id = order.Id,
            Station = StationOf(shipyard),
            Player = order.Player,
            Ship = name,
        };
        if (order.Id.Length > 0 && !order.Free)
            After(
                ended,
                filled > 0
                    ? Math.Min(IceFor(filled, up: true), IceFor(order.Hydrogen, up: true))
                    : 0,
                order.Refund,
                order.Sold,
                $"{message} {string.Format(TakeWithin(), shipyard.DisplayName)}",
                ship: left,
                percent: why == null || left == 0 || order.Blocks == 0
                    ? 100
                    : Math.Min(100, Built(ship) * 100 / order.Blocks)
            );
        else if (order.Id.Length > 0)
            Finish(ended, message, left);
        else
        {
            Tell(order.Player, message);
            Plugin.Log.Info($"Shipyard order '{name}' of {Name(order.Player)} ended: {message}");
        }
    }

    /// <summary>Removes every unit of the item from the station shipyard's containers and welders.</summary>
    public static string Strip(string station, string item)
    {
        if (!Stations.TryItemId(item, out var id))
            return $"Unknown item {item}.";
        var shipyard = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g =>
                ShipyardZoneWelding.IsShipyard(g)
                && g.DisplayName.StartsWith(station + " ", StringComparison.OrdinalIgnoreCase)
            );
        if (shipyard == null)
            return $"No shipyard of {station} on this node.";
        MyFixedPoint taken = 0;
        foreach (
            var inventory in Containers(shipyard)
                .Concat(Beams(shipyard).Select(b => b.GetInventory()))
        )
        {
            var amount = inventory.GetItemAmount(id);
            if (amount <= 0)
                continue;
            inventory.RemoveItemsOfType(amount, id);
            taken += amount;
        }
        return $"Took {taken} {item} out of {shipyard.DisplayName}.";
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
            if (repairs.TryGetValue(shipyard.EntityId, out var slot))
                yield return $"  repair slot of {Name(slot.Player)}, "
                    + $"{(DateTime.UtcNow - slot.LastWork).TotalSeconds:0} s since the last work";
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
        ShipyardArm.Layout layout
    )
    {
        var main = blueprint[0];
        if (layout.Grid.MarkedForClose)
            return "its support arm's rotor is gone";

        // The ship's components, the given up block's included, and the arm's, which OCT pays
        var components = Components(blueprint, skipFirst: false);
        // A paid build's are there, but for what the player bought
        var load = new Dictionary<MyDefinitionId, int>(next.Free ? components : Bought(next));
        foreach (var c in layout.Components)
            load[c.Key] = (load.TryGetValue(c.Key, out var had) ? had : 0) + c.Value;
        var containers = Containers(shipyard).ToList();
        var needed = load.Sum(c =>
            MyDefinitionManager.Static.GetPhysicalItemDefinition(c.Key).Volume * c.Value
        );
        var free = containers.Sum(i => (double)(i.MaxVolume - i.CurrentVolume));
        if (needed > free)
            return $"the components take {needed:N0} m³, the containers have room for {free:N0} m³";

        var npc = ShipyardTurn.Npc(shipyard);
        var ends = DateTime.UtcNow.AddMinutes(Config.ShipyardOrderMinutes);
        var hydrogen = BuildHydrogen(next, blueprint);
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
            $"Exempt={(next.Exempt ? 1 : 0)}",
            $"Hydrogen={hydrogen.ToString("R", CultureInfo.InvariantCulture)}",
            $"Arm={string.Join(";", arm.Select(Format))}",
            // Restore names the order by it, not by the saved projection's grid
            $"Ship={(main.DisplayName ?? "Ship").Replace('\n', ' ')}",
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
            Exempt = next.Exempt,
            Hydrogen = hydrogen,
            Arm = arm,
            Connector = connector,
            GivenUp = givenUp,
            Name = main.DisplayName ?? "Ship",
        };
        orders[shipyard.EntityId] = order;
        lost.Remove(shipyard.EntityId);
        // The arm is built at once, with the components in the containers: the welders took
        // too long on it, a small block arm most of all
        var built = WeldArm(shipyard, order, arm);
        Welders(shipyard, true);
        var plan = layout.Plan;
        Plugin.Log.Info(
            $"Shipyard order {next.Id} '{order.Name}' ({next.Pcu} PCU, "
                + $"{(next.Free ? "free" : "paid by " + next.Payer)}) for {Name(next.Player)} at {shipyard.DisplayName}, until {ends:u}: "
                + $"arm on '{layout.Rotor.CustomName}' of {plan.Blocks.Count} blocks, cost {plan.Cost}, "
                + (plan.Connector != null ? "docking at a connector" : "with a cap")
                + $", {built} arm blocks built at once"
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

    private static bool ArmComplete(Order order)
    {
        var grid = order.Projector.CubeGrid;
        return order.Arm.All(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true);
    }

    // The arm is built at once when it is placed. Should a block be left incomplete, the arm
    // stalls after ArmIdleChecks checks without a newly completed block: the plugin welds it then
    private const int ArmIdleChecks = 3;

    private static bool ArmStalled(Order order)
    {
        var grid = order.Projector.CubeGrid;
        var built = order.Arm.Count(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true);
        if (built > order.ArmBuilt)
        {
            order.ArmBuilt = built;
            order.ArmIdle = 0;
            return false;
        }
        return ++order.ArmIdle >= ArmIdleChecks;
    }

    private static void FinishArm(MyCubeGrid shipyard, Order order)
    {
        var left = WeldArm(shipyard, order, order.Arm);
        order.ArmIdle = 0;
        Plugin.Log.Info(
            $"Shipyard order {order.Id} at {shipyard.DisplayName}: the welders stalled on {left} arm blocks, welded them"
        );
    }

    // Welds the arm's blocks at the cells with the components in the shipyard's containers, as
    // a welder of the shipyard would; returns how many it welded
    private static int WeldArm(MyCubeGrid shipyard, Order order, IEnumerable<Vector3I> cells)
    {
        var grid = order.Projector.CubeGrid;
        var containers = Containers(shipyard).ToList();
        var welded = 0;
        foreach (var cell in cells)
        {
            var block = grid.GetCubeBlock(cell);
            if (block == null || block.IsFullIntegrity)
                continue;
            foreach (var inventory in containers)
                block.MoveItemsToConstructionStockpile(inventory);
            block.IncreaseMountLevel(
                block.MaxIntegrity / block.BlockDefinition.IntegrityPointsPerSec,
                order.Projector.OwnerId,
                containers.FirstOrDefault(),
                isHelping: true
            );
            welded++;
        }
        return welded;
    }

    private static int Complete(MyCubeGrid shipyard, Order order) =>
        order.Arm.Count > 0
            ? ShipBlocks(shipyard, order).Count(b => b.IsFullIntegrity)
            : Built(order.Projector.CubeGrid);

    private static string BlocksDone(MyCubeGrid shipyard, Order order) =>
        $"{Complete(shipyard, order)} of {order.Blocks} blocks complete";

    // A build stalls after StallChecks checks (60 s) in which its ship's integrity didn't grow
    private const int StallChecks = 6;

    private static bool Stalled(MyCubeGrid shipyard, Order order)
    {
        var progress = ShipSoFar(shipyard, order).Sum(b => b.Integrity);
        if (progress > order.Progress + 0.01f)
        {
            order.Progress = progress;
            order.Idle = 0;
            order.ShortSince = null;
            return false;
        }
        return ++order.Idle >= StallChecks;
    }

    // The ship's blocks so far, the scaffold left out
    private static IEnumerable<MySlimBlock> ShipSoFar(MyCubeGrid shipyard, Order order) =>
        order.Arm.Count > 0
            ? ShipBlocks(shipyard, order)
            : Group(order.Projector.CubeGrid)
                .SelectMany(g => ((MyCubeGrid)g).GetBlocks())
                .Where(b => !(b.FatBlock is MyTerminalBlock t && IsScaffold(t)));

    // The welders stopped. Blocks only a collision keeps them from are placed by force first
    // (ShipyardForceBuild), and the build goes on. Then the shipyard finishes the blocks the
    // welders left as far as the containers have their components (ShipyardFinish), and the build
    // goes on. A paid build short of components waits for them. A ship missing only blocks no welder can place is delivered without them. Else its
    // main grid decides: missing only armor and other plain blocks, at most MaxMissingPlain of
    // them, the ship is delivered; missing a functional block or more of the plain ones, it is
    // removed and refunded. What a subgrid misses never removes a ship.
    private static void Stall(MyCubeGrid shipyard, Order order)
    {
        if (Force(shipyard, order) || Finish(shipyard, order))
            return;
        var projected = (
            (MyObjectBuilder_ProjectorBase)order.Projector.GetObjectBuilderCubeBlock(true)
        ).ProjectedGrids;
        var unbuilt = new Dictionary<MyDefinitionId, int>();
        foreach (var block in projected?.SelectMany(g => g.CubeBlocks) ?? [])
            unbuilt[block.GetId()] = (unbuilt.TryGetValue(block.GetId(), out var n) ? n : 0) + 1;
        var blocks = ShipSoFar(shipyard, order).ToList();
        foreach (var block in blocks)
            if (unbuilt.TryGetValue(block.BlockDefinition.Id, out var n))
                unbuilt[block.BlockDefinition.Id] = n - 1;
        // By name; a block of a mod this server doesn't have as Unknown names it (SE-1086)
        var missing = new Dictionary<string, int>();
        var parts = new Dictionary<string, int>();
        foreach (var (id, count) in unbuilt.Select(u => (u.Key, u.Value)).Where(u => u.Value > 0))
        {
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out var definition))
            {
                missing[Unknown(id)] =
                    (missing.TryGetValue(Unknown(id), out var had) ? had : 0) + count;
                continue;
            }
            missing[definition.DisplayNameText] =
                (missing.TryGetValue(definition.DisplayNameText, out var was) ? was : 0) + count;
            foreach (var c in definition.Components)
                parts[c.Definition.Id.SubtypeName] =
                    (parts.TryGetValue(c.Definition.Id.SubtypeName, out var had) ? had : 0)
                    + c.Count * count;
        }
        foreach (var block in blocks.Where(b => !b.IsFullIntegrity))
        {
            var name = block.BlockDefinition.DisplayNameText;
            missing[name] = (missing.TryGetValue(name, out var had) ? had : 0) + 1;
            block.GetMissingComponents(parts);
        }
        var shortOf = Short(shipyard, parts);
        var leftOut = shortOf == null ? LeftOutGrids(order, blocks) : null;
        var waited = order.ShortSince != null;
        order.ShortSince ??= DateTime.UtcNow;
        var until = order.ShortSince.Value.AddMinutes(Config.ShipyardTopUpMinutes);
        if (!order.Free && DateTime.UtcNow < until && shortOf != null)
        {
            // Short of components: it waits for a top-up, and says so once
            order.Idle = 0;
            if (!waited)
                Tell(
                    order.Player,
                    $"{shipyard.DisplayName} waits for {shortOf} for '{order.Name}', up to {PanelText.Left(until, DateTime.UtcNow)}. "
                        + "You and your faction can load them into its containers."
                );
            return;
        }
        order.Missing = string.Join(
            ", ",
            missing.OrderByDescending(m => m.Value).Select(m => $"{m.Value} {m.Key}")
        );
        if (leftOut != null)
            order.Missing +=
                (order.Missing.Length > 0 ? ", left out" : "blocks left out")
                + $" on {leftOut}, where no welder can place them";
        if (order.Missing.Length == 0)
            order.Missing = "the blocks its welders couldn't place";
        order.Refund = leftOut == null && MainGridFails(order, projected?.FirstOrDefault(), blocks);
        Plugin.Log.Info(
            $"Shipyard order {order.Id} at {shipyard.DisplayName}: no progress for {StallChecks * UpdateTicks / 60} s, "
                + $"missing {order.Missing}: {(order.Refund ? "removed and refunded" : "delivered")}"
        );
        LogStall(shipyard, order, blocks, parts);
        if (order.Refund)
        {
            if (!order.Free)
                KeepParts(shipyard, order);
            Scrap(shipyard, order);
        }
        End(shipyard, order, "stalled");
    }

    // The main grid, the blueprint's first, is built on the projector's grid: true if it misses
    // a functional block or more than MaxMissingPlain of its plain ones
    private static bool MainGridFails(
        Order order,
        MyObjectBuilder_CubeGrid main,
        List<MySlimBlock> blocks
    )
    {
        var unbuilt = (main?.CubeBlocks ?? [])
            .GroupBy(b => b.GetId())
            .ToDictionary(g => g.Key, g => g.Count());
        var plain = unbuilt.Where(u => !Functional(u.Key)).Sum(u => u.Value);
        foreach (var block in blocks.Where(b => b.CubeGrid == order.Projector.CubeGrid))
            if (block.IsFullIntegrity && unbuilt.TryGetValue(block.BlockDefinition.Id, out var n))
                unbuilt[block.BlockDefinition.Id] = n - 1;
        var missing = unbuilt.Where(u => u.Value > 0).ToList();
        return missing.Any(u => Functional(u.Key))
            || missing.Where(u => !Functional(u.Key)).Sum(u => u.Value) > MaxMissingPlain * plain;
    }

    // The grids with blocks that can't be placed as the ship stands, like a grid docked by a
    // connector or a part overlapping another: null if a block could still be placed or finished,
    // or if no ship was started
    private static string LeftOutGrids(Order order, List<MySlimBlock> blocks)
    {
        if (blocks.Count == 0 || blocks.Any(b => !b.IsFullIntegrity))
            return null;
        var grids = new List<string>();
        foreach (var preview in order.Projector.Clipboard.PreviewGrids)
        {
            var left = 0;
            foreach (var block in preview.GetBlocks())
            {
                var result = order.Projector.CanBuild(block, true);
                if (result == BuildCheckResult.OK)
                    return null;
                if (result != BuildCheckResult.AlreadyBuilt)
                    left++;
            }
            if (left > 0)
                grids.Add($"{preview.DisplayName} ({left} of {preview.BlocksCount} blocks)");
        }
        return grids.Count == 0 ? null : string.Join(", ", grids);
    }

    // What the welders had when they stopped, for finding out why
    private static void LogStall(
        MyCubeGrid shipyard,
        Order order,
        List<MySlimBlock> blocks,
        Dictionary<string, int> parts
    )
    {
        try
        {
            var prefix = $"Shipyard order {order.Id} stalled:";
            Plugin.Log.Info(
                $"{prefix} built so far "
                    + string.Join(
                        ", ",
                        Grids(order.Projector.CubeGrid)
                            .Select(g =>
                                $"{g.DisplayName} {g.GetBlocks().Count(b => b.IsFullIntegrity)}/{g.BlocksCount}"
                            )
                    )
            );
            var stock = new Dictionary<string, double>();
            foreach (var item in Containers(shipyard).SelectMany(i => i.GetItems()))
                stock[item.Content.SubtypeName] =
                    (stock.TryGetValue(item.Content.SubtypeName, out var had) ? had : 0)
                    + (double)item.Amount;
            Plugin.Log.Info(
                $"{prefix} needs {string.Join(", ", parts.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"))}"
            );
            Plugin.Log.Info(
                $"{prefix} containers hold {string.Join(", ", stock.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value:0}"))}"
            );
            var beams = Beams(shipyard).ToList();
            Plugin.Log.Info(
                $"{prefix} {beams.Count} welders, {beams.Count(b => b.Enabled)} on, {beams.Count(b => b.IsWorking)} working"
            );
            var held = new Dictionary<string, double>();
            foreach (var item in beams.SelectMany(b => b.GetInventory().GetItems()))
                held[item.Content.SubtypeName] =
                    (held.TryGetValue(item.Content.SubtypeName, out var had) ? had : 0)
                    + (double)item.Amount;
            Plugin.Log.Info(
                $"{prefix} welders hold {string.Join(", ", held.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value:0}"))}, "
                    + $"{beams.Sum(b => (double)b.GetInventory().CurrentVolume):0.##} of {beams.Sum(b => (double)b.GetInventory().MaxVolume):0.##} m³"
            );
            foreach (var beam in beams)
                Plugin.Log.Info($"{prefix} welder {beam.Position}: {AimOf(beam)}");
            foreach (var block in blocks.Where(b => !b.IsFullIntegrity).Take(20))
            {
                var needs = new Dictionary<string, int>();
                block.GetMissingComponents(needs);
                var at = block.CubeGrid.GridIntegerToWorld(block.Position);
                var nearest =
                    beams.Count == 0
                        ? -1
                        : beams.Min(b => Vector3D.Distance(b.PositionComp.GetPosition(), at));
                Plugin.Log.Info(
                    $"{prefix} {block.BlockDefinition.Id.SubtypeName} on {block.CubeGrid.DisplayName}, "
                        + $"integrity {block.Integrity:0}/{block.MaxIntegrity:0}, "
                        + $"missing {string.Join(" ", needs.Select(n => $"{n.Key} {n.Value}"))}, "
                        + $"{nearest:0} m from the nearest welder"
                );
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Logging a stalled order failed", e);
        }
    }

    // What a ToolCore turret aims at, read from its component: diagnostics only
    private static string AimOf(MyFunctionalBlock beam)
    {
        try
        {
            return Aim(beam);
        }
        catch (Exception e)
        {
            return $"unknown, {e.GetType().Name}";
        }
    }

    private static string Aim(MyFunctionalBlock beam)
    {
        object comp = null;
        foreach (var c in beam.Components)
            if (c.GetType().Name == "ToolComp")
                comp = c;
        if (comp == null)
            return "no ToolComp";
        var tool = Traverse.Create(comp);
        var mode = tool.Field("Mode").GetValue();
        var data = ((System.Collections.IDictionary)tool.Field("ModeMap").GetValue())[mode];
        var turret = Traverse.Create(Traverse.Create(data).Field("Turret").GetValue());
        var targets = turret.Field("Targets").GetValue<List<IMySlimBlock>>()?.Count ?? -1;
        var aim =
            $"targets {targets}, aligned {turret.Field("Aligned").GetValue()}, "
            + $"out of bounds {turret.Field("Part1").Field("OutOfBounds").GetValue()}/"
            + $"{turret.Field("Part2").Field("OutOfBounds").GetValue()}";
        if (
            !turret.Field("HasTarget").GetValue<bool>()
            || turret.Field("ActiveTarget").GetValue() is not MySlimBlock target
        )
            return $"no target, {aim}";
        var projector = target.CubeGrid.Projector;
        var state =
            projector == null
                ? $"integrity {target.Integrity:0}/{target.MaxIntegrity:0}"
                : $"projected, {ShipyardZoneWelding.Unzoned(() => projector.CanBuild(target, true))}";
        var at = target.CubeGrid.GridIntegerToWorld(target.Position);
        return $"{target.BlockDefinition.Id.SubtypeName} on {target.CubeGrid.DisplayName} {state}, "
            + $"{Vector3D.Distance(beam.PositionComp.GetPosition(), at):0} m, {aim}";
    }

    // A ship missing more than this share of its plain blocks is not delivered
    private const double MaxMissingPlain = 0.1;

    // Every block of its own type is functional: only the plain blocks, armor and decorations,
    // are a slim block alone, at most with a bare MyCubeBlock for their model
    private static bool Functional(MyDefinitionId id) =>
        id.TypeId != typeof(MyObjectBuilder_CubeBlock);

    // Removes what the welders built of the ship
    private static void Scrap(MyCubeGrid shipyard, Order order)
    {
        var grid = order.Projector.CubeGrid;
        if (order.Arm.Count == 0)
        {
            foreach (MyCubeGrid g in Group(grid))
                g.Close();
            return;
        }
        var own = Own(shipyard);
        foreach (var g in Grids(grid).Where(g => g != grid && !own.Contains(g)))
            g.Close();
        Raze(grid, ShipBlocksOnArm(order).Select(b => b.Position).ToList());
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

    /// <summary>
    /// True while the shipyard on this node builds a ship for an admin who ignores PCU limits, so
    /// GroupPcuLimit lets its blocks be welded on a node that has no copy of their admin settings.
    /// </summary>
    internal static bool Exempt(long player) =>
        orders.Values.Any(o => o.Exempt && o.Player == player);

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
            order.ArmGrid = grid;
            // The projector stays on the arm, under an unfinished ship too, and comes down
            // with it; its projection must not show the ship again once the blocks leave
            // Not a copy: that gets new entity ids, and AddGroups goes by the blocks' ids
            var blueprint = (
                (MyObjectBuilder_ProjectorBase)order.Projector.GetObjectBuilderCubeBlock(false)
            ).ProjectedGrids;
            var projector = (Sandbox.ModAPI.IMyProjector)order.Projector;
            projector.SetProjectedGrid(null);
            projector.Enabled = false;
            var blocks = ShipBlocksOnArm(order);
            if (blocks.Count > 0 && Config.ShipyardRepasteShips)
                ship = Repaste(shipyard, order, grid, blocks, blueprint);
            else if (blocks.Count > 0)
            {
                // A split of a static grid is static, and the clients may miss an event on
                // the new grid: the arm's grid turns dynamic first, in the same order on the
                // clients, so the ship is born dynamic and docks to the arm as one
                if (grid.IsStatic)
                    MyMultiplayer.RaiseEvent(grid, x => x.OnConvertToDynamic);
                ship = MyCubeGrid.CreateSplit(grid, blocks);
                ship?.ChangeDisplayNameRequest(order.Name);
            }
            if (ship != null)
                Dynamic(shipyard, ship);
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
                filled = order.Free
                    ? FillFree(shipyard, ship, order)
                    : FillHydrogen(ship, order.Hydrogen);
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
        var armLeft = RemoveArm(shipyard, order);
        if (alive)
        {
            HandOver(ship, order.Player);
            NoteFreeEnd(order, order.Name);
            Dynamic(shipyard, ship);
        }
        // What the arm didn't use is OCT's
        RemoveLeftovers(shipyard, armLeft);
        if (order.Free)
            RemoveLeftovers(shipyard, order.Components);
        var extra = "";
        if (alive && order.GivenUp is { } givenUp)
            extra +=
                $" One {(MyDefinitionManager.Static.TryGetCubeBlockDefinition(givenUp, out var d) ? d.DisplayNameText : givenUp.SubtypeName)} "
                + "is missing where the arm's projector was: build it yourself.";
        Report(
            shipyard,
            order,
            ship,
            order.Name,
            alive ? null
                : ship == null && why != null && why != "the ship is gone" ? "unbuilt"
                : "gone",
            why,
            filled,
            extra
        );
    }

    // Destroys the arm's blocks, never the rotor head; returns the components they still miss
    private static Dictionary<MyDefinitionId, int> RemoveArm(MyCubeGrid shipyard, Order order)
    {
        var missing = new Dictionary<MyDefinitionId, int>();
        var grid = order.ArmGrid ?? order.Projector.CubeGrid;
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
        Dismantle(grid, order.Arm);
        return missing;
    }

    // The arm comes down at once, and the rotor head's grid turns dynamic again
    private static void Dismantle(MyCubeGrid grid, List<Vector3I> cells)
    {
        Raze(grid, cells);
        if (grid.IsStatic)
            MyMultiplayer.RaiseEvent(grid, x => x.OnConvertToDynamic);
    }

    // An idle shipyard's rotor heads carry nothing else: an arm left over comes down now
    private static void ClearHeads(MyCubeGrid shipyard)
    {
        var players = MySession.Static.Players;
        foreach (var rotor in ShipyardArm.Rotors(shipyard))
        {
            var grid = (MyCubeGrid)rotor.TopGrid;
            var left = grid.GetBlocks()
                .Where(b =>
                    b.FatBlock != rotor.TopBlock
                    && (b.BuiltBy == 0 || players.IdentityIsNpc(b.BuiltBy))
                )
                .Select(b => b.Position)
                .ToList();
            if (left.Count == 0)
                continue;
            Plugin.Log.Info(
                $"Shipyard {shipyard.DisplayName}: removing {left.Count} blocks left on '{rotor.CustomName}'"
            );
            Dismantle(grid, left);
            return;
        }
    }

    // RazeBlocks would ask the safe zone, which forbids grinding
    private static void Raze(MyCubeGrid grid, List<Vector3I> cells)
    {
        var present = cells.Where(c => grid.CubeExists(c)).ToList();
        grid.RazeGeneratedBlocks(present);
        MyMultiplayer.RaiseEvent(grid, x => x.RazeBlocksClient, present);
    }

    // A free build starts the player's cool-down, unless an admin who ignores PCU limits ordered it
    private static void NoteFreeEnd(Order order, string name)
    {
        if (order.Exempt)
            Plugin.Log.Info(
                $"Free builds: {Name(order.Player)} ignores PCU limits, no cool-down after '{name}'"
            );
        else if (order.Free)
            FreePrints.Ended(order.Player);
    }

    // A ship split off the arm was unusable: CreateSplit splits the clients' copy of the
    // arm too (SE-746), and a re-paste in the same tick left them that copy as a ghost over
    // the new ship. The ship leaves the arm as Essentials' !fixship re-pastes one instead,
    // with no split: its blocks and subgrids are saved, removed and pasted anew as a
    // dynamic ship. Returns the new ship grid.
    private static MyCubeGrid Repaste(
        MyCubeGrid shipyard,
        Order order,
        MyCubeGrid arm,
        List<MySlimBlock> blocks,
        List<MyObjectBuilder_CubeGrid> blueprint
    )
    {
        var own = Own(shipyard);
        var subgrids = Grids(arm).Where(g => g != arm && !own.Contains(g)).ToList();
        var cells = new HashSet<Vector3I>(blocks.Select(b => b.Min));
        var positions = new HashSet<Vector3I>(blocks.Select(b => b.Position));
        // !fixship pastes a grid's own builder; the ship's is the arm's, cut down to the
        // ship's blocks and its block groups, which the projector put on the arm as it
        // welded their blocks (SE-772). What belongs to the arm's cells or to the arm
        // itself goes.
        var saved = (MyObjectBuilder_CubeGrid)arm.GetObjectBuilder(true);
        saved.Name = null;
        saved.DisplayName = order.Name;
        saved.IsStatic = false;
        saved.DampenersEnabled = true;
        saved.Editable = true;
        saved.DestructibleBlocks = true;
        saved.Immune = false;
        saved.IsRespawnGrid = false;
        saved.CubeBlocks = saved.CubeBlocks.Where(b => cells.Contains(b.Min)).ToList();
        saved.BlockGroups = saved
            .BlockGroups.Select(g => new MyObjectBuilder_BlockGroup
            {
                Name = g.Name,
                Blocks = g.Blocks.Where(positions.Contains).ToList(),
            })
            .Where(g => g.Blocks.Count > 0)
            .ToList();
        saved.ConveyorLines = [];
        saved.Skeleton = null;
        saved.OxygenAmount = null;
        saved.OxygenRooms = null;
        AddGroups(saved, blueprint, blocks);
        var builders = new List<MyObjectBuilder_EntityBase> { saved };
        foreach (var grid in subgrids)
        {
            var builder = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(true);
            builder.IsStatic = false;
            AddGroups(builder, blueprint, grid.GetBlocks());
            builders.Add(builder);
        }
        // As !fixship does
        foreach (
            var block in builders.Cast<MyObjectBuilder_CubeGrid>().SelectMany(g => g.CubeBlocks)
        )
        {
            if (block is MyObjectBuilder_ProjectorBase projector)
                projector.Enabled = false;
            if (block is MyObjectBuilder_OxygenTank tank)
                tank.AutoRefill = false;
        }
        foreach (var grid in subgrids)
            grid.Close();
        Raze(arm, blocks.Select(b => b.Position).ToList());
        MyAPIGateway.Entities.RemapObjectBuilderCollection(builders);
        MyCubeGrid ship = null;
        foreach (var builder in builders)
        {
            var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            grid?.DetectDisconnectsAfterFrame();
            ship ??= grid;
        }
        Plugin.Log.Info(
            $"Shipyard order {order.Id} '{order.Name}': {blocks.Count} blocks and {subgrids.Count} subgrids "
                + $"off the arm, pasted anew as {ship?.EntityId} with {saved.BlockGroups.Count} block groups"
        );
        return ship;
    }

    // The blueprint's block groups, on the blocks welded from it: the projector reserves the
    // blueprint's entity ids for them. Vanilla adds a group to the projector's grid as a block
    // of it is welded, which the shipyard's welding doesn't go through (SE-772).
    private static void AddGroups(
        MyObjectBuilder_CubeGrid builder,
        List<MyObjectBuilder_CubeGrid> blueprint,
        IEnumerable<MySlimBlock> blocks
    )
    {
        if (blueprint == null)
            return;
        var welded = blocks
            .Where(b => b.FatBlock != null)
            .ToDictionary(b => b.FatBlock.EntityId, b => b.Position);
        var names = new HashSet<string>(builder.BlockGroups.Select(g => g.Name));
        foreach (var grid in blueprint)
        {
            var ids = new Dictionary<Vector3I, long>();
            foreach (var block in grid.CubeBlocks)
            {
                if (
                    block.EntityId == 0
                    || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                        block.GetId(),
                        out var definition
                    )
                )
                    continue;
                var position = MySlimBlock.ComputePositionInGrid(
                    new MatrixI(block.BlockOrientation),
                    definition,
                    block.Min
                );
                ids[position] = block.EntityId;
            }
            foreach (var group in grid.BlockGroups)
            {
                if (names.Contains(group.Name))
                    continue;
                var members = group
                    .Blocks.Select(p => ids.TryGetValue(p, out var id) ? id : 0)
                    .Where(welded.ContainsKey)
                    .Select(id => welded[id])
                    .Distinct()
                    .ToList();
                if (members.Count == 0)
                    continue;
                builder.BlockGroups.Add(
                    new MyObjectBuilder_BlockGroup { Name = group.Name, Blocks = members }
                );
                names.Add(group.Name);
            }
        }
    }

    // The ship and its subgrids: rotor and piston tops built on the static arm may be static
    private static void Dynamic(MyCubeGrid shipyard, MyCubeGrid ship)
    {
        var own = Own(shipyard);
        foreach (MyCubeGrid grid in Group(ship))
            if (grid.IsStatic && !own.Contains(grid))
                MyMultiplayer.RaiseEvent(grid, x => x.OnConvertToDynamic);
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
            grid.ChangeGridOwnership(player, OctFaction.BuildShare(player));
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
            Exempt = values.TryGetValue("Exempt", out var x) && x == "1",
            Hydrogen = hydrogen,
            Name = values.TryGetValue("Ship", out var ship) ? ship : grids[0].DisplayName ?? "Ship",
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

    // A docked ship keeps this share of each H2 tank, for its way home
    private const double DockedTankKeep = 0.5;

    private static long IceFor(double liters, bool up)
    {
        var raw = liters / Config.ShipyardHydrogenLitersPerIce * Amounts.Unit;
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

    // The liters of a new ship: a paid build's came with its bill, a free build's are all it holds
    private static double BuildHydrogen(BuildOrder order, List<MyObjectBuilder_CubeGrid> blueprint)
    {
        if (order.Free)
            return HydrogenCapacity(blueprint) * Config.ShipyardHydrogenFillPercent / 100;
        return order.Items.TryGetValue(Hydrogen, out var ice)
            ? ice / (double)Amounts.Unit * Config.ShipyardHydrogenLitersPerIce
            : 0;
    }

    // The player's ships docked at the station; not the ship the build gives them
    private static List<MyCubeGrid> DockedShips(
        MyProjectorBase table,
        long player,
        MyCubeGrid built = null
    )
    {
        var station = Grids(table.CubeGrid);
        if (FindShipyard(table) is { } shipyard)
            station = station.Concat(Grids(shipyard)).ToList();
        if (built != null)
            station = station.Concat(Grids(built)).ToList();
        return station
            .SelectMany(g => g.GetFatBlocks<MyShipConnector>())
            .Where(c => c.Connected && c.Other != null)
            .SelectMany(c => Grids(c.Other.CubeGrid))
            .Where(g => !station.Contains(g) && g.BigOwners.Contains(player))
            .Distinct()
            .ToList();
    }

    private static double Spare(MyGasTank tank) =>
        Math.Max(0, tank.FilledRatio - DockedTankKeep) * tank.Capacity;

    private static IEnumerable<MyGasTank> SpareTanks(IEnumerable<MyCubeGrid> ships) =>
        ships
            .SelectMany(g => g.GetFatBlocks<MyGasTank>())
            .Where(t => IsHydrogen(t) && t.IsFunctional && Spare(t) > 0);

    private static IEnumerable<MyInventory> Cargo(IEnumerable<MyCubeGrid> ships) =>
        ships.SelectMany(g => g.GetFatBlocks<MyCargoContainer>()).Select(c => c.GetInventory());

    // Takes up to the liters out of what the docked ships' tanks can spare
    private static List<(MyGasTank Tank, double Liters)> Drain(
        IEnumerable<MyCubeGrid> ships,
        double liters
    )
    {
        var taken = new List<(MyGasTank, double)>();
        var left = liters;
        foreach (var tank in SpareTanks(ships))
        {
            if (left <= 0)
                break;
            var take = Math.Min(Spare(tank), left);
            ((IMyGasTank)tank).ChangeFilledRatio(tank.FilledRatio - take / tank.Capacity, true);
            taken.Add((tank, take));
            left -= take;
        }
        return taken;
    }

    // Takes up to the raw amount of ice out of the inventories, in their order
    private static List<(MyInventory Inventory, long Raw)> TakeIce(
        IEnumerable<MyInventory> inventories,
        long raw
    )
    {
        var taken = new List<(MyInventory, long)>();
        var id = ItemId(Hydrogen);
        foreach (var inventory in inventories)
        {
            if (raw <= 0)
                break;
            var have = inventory.GetItemAmount(id).RawValue;
            if (have <= 0)
                continue;
            var take = Math.Min(have, raw);
            inventory.RemoveItemsOfType(new MyFixedPoint { RawValue = take }, id);
            taken.Add((inventory, take));
            raw -= take;
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

    // A free ship's hydrogen: the player's docked ships give what they can spare, then their ice;
    // OCT gives the rest, as it gives the components. Returns the liters that went in.
    private static double FillFree(MyCubeGrid shipyard, MyCubeGrid ship, Order order)
    {
        var filled = FillHydrogen(ship, order.Hydrogen);
        // A shipyard isn't on its station's grids: its station is the one whose table it serves
        var tables = StationTables().Values.FirstOrDefault(t => FindShipyard(t[0]) == shipyard);
        if (filled > 0 && tables != null)
        {
            var ships = DockedShips(tables[0], order.Player, ship);
            var drained = Drain(ships, filled).Sum(d => d.Liters);
            TakeIce(Cargo(ships), IceFor(filled - drained, up: false));
        }
        return filled;
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
        Grids(grid).Select(Stations.StationOf).FirstOrDefault(s => s != null);

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

    // Where the center of a ship of this length goes: on the free space's center line, a short
    // ship toward the end nearer the station
    private static Vector3D ShipCenter(
        MyCubeGrid shipyard,
        MyOrientedBoundingBoxD space,
        double length
    )
    {
        var offset = ShipyardPlacement.Offset(length, space.HalfExtent.Z * 2);
        var station = offset > 0 ? NearestStation(shipyard) : null;
        if (station == null)
            return space.Center;
        var along = shipyard.WorldMatrix.Forward;
        var toStation = station.PositionComp.WorldAABB.Center - space.Center;
        return space.Center + along * (Vector3D.Dot(toStation, along) > 0 ? offset : -offset);
    }

    // The grid of the nearest station hologram table within 1 km of the shipyard
    private static MyCubeGrid NearestStation(MyCubeGrid shipyard)
    {
        var position = shipyard.PositionComp.GetPosition();
        var sphere = new BoundingSphereD(position, 1000);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var table = entities
            .OfType<MyCubeGrid>()
            .SelectMany(g => g.GetFatBlocks<MyProjectorBase>())
            .Where(IsTable)
            .OrderBy(t => Vector3D.DistanceSquared(t.PositionComp.GetPosition(), position))
            .FirstOrDefault();
        entities.Clear();
        return table?.CubeGrid;
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

    // A grid with a block in the shipyard's free space that isn't the shipyard or on its rotors.
    // A ship docked at the connectors is outside the walls, so outside the free space.
    private static MyCubeGrid Obstacle(MyCubeGrid shipyard)
    {
        var own = Grids(shipyard);
        var space = FreeSpace(shipyard);
        return GridsIn(space).FirstOrDefault(g => !own.Contains(g) && HasBlockIn(g, space));
    }

    // GridsIn goes by the world axis box around each grid, which at a shipyard lying at an angle
    // holds much that is outside the shipyard
    private static bool HasBlockIn(MyCubeGrid grid, MyOrientedBoundingBoxD box)
    {
        var toGrid = grid.PositionComp.WorldMatrixNormalizedInv;
        var local = new MyOrientedBoundingBoxD(
            Vector3D.Transform(box.Center, toGrid),
            box.HalfExtent,
            Quaternion.CreateFromRotationMatrix(
                MatrixD.CreateFromQuaternion(box.Orientation) * toGrid
            )
        );
        var cube = grid.GridSize;
        foreach (var block in grid.GetBlocks())
        {
            var cells = new BoundingBoxD(
                ((Vector3D)block.Min - 0.5) * cube,
                ((Vector3D)block.Max + 0.5) * cube
            );
            if (local.Intersects(ref cells))
                return true;
        }
        return false;
    }

    // Whether the grid's center is in the build space of a shipyard, which builds, moves and tows
    // what is there
    internal static bool InBuildSpace(MyCubeGrid grid)
    {
        var center = grid.PositionComp.WorldAABB.Center;
        var sphere = new BoundingSphereD(center, 1000);
        var entities = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
        var inside = entities
            .OfType<MyCubeGrid>()
            .Where(ShipyardZoneWelding.IsShipyard)
            .Any(s => FreeSpace(s).Contains(ref center));
        entities.Clear();
        return inside;
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

    // SE-1086: a block of a mod the world doesn't load has no definition here, so no projection
    // shows it and no welder builds it, and the build stalled without it, listing nothing missing.
    // Such blocks are left out of the blueprint up front, as DLC blocks are (ShipyardDlc), and the
    // quote says so: what they were (Unknown), or null if none.
    private static string LeaveOutUnknown(List<MyObjectBuilder_CubeGrid> grids)
    {
        var left = new Dictionary<string, int>();
        foreach (var grid in grids)
            grid.CubeBlocks.RemoveAll(block =>
            {
                if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out _))
                    return false;
                var name = Unknown(block.GetId());
                left[name] = (left.TryGetValue(name, out var n) ? n : 0) + 1;
                return true;
            });
        grids.RemoveAll(g => g.CubeBlocks.Count == 0);
        return left.Count == 0
            ? null
            : string.Join(
                ", ",
                left.OrderByDescending(l => l.Value).Select(l => $"{l.Value} {l.Key}")
            );
    }

    // A block without a definition here by its subtype, or by its type when the subtype came from
    // a client as a hash this server never had the name of
    private static string Unknown(MyDefinitionId id) =>
        string.IsNullOrEmpty(id.SubtypeName)
            ? $"of type {id.TypeId.ToString().Replace("MyObjectBuilder_", "")}"
            : id.SubtypeName;

    // The blueprint's blocks that a free build can't have, counted by name: "2 Prototech Battery"
    private static List<string> Blocked(List<MyObjectBuilder_CubeGrid> grids)
    {
        var blocks = PatternRegex(Config.FreeBuildBlockedBlocks);
        var components = PatternRegex(Config.FreeBuildExcludedComponents);
        return grids
            .SelectMany(g => g.CubeBlocks)
            .GroupBy(b => b.GetId())
            .Select(g =>
            {
                MyDefinitionManager.Static.TryGetCubeBlockDefinition(g.Key, out var definition);
                return (Id: g.Key, Definition: definition, Count: g.Count());
            })
            .Where(b =>
                blocks?.IsMatch(
                    $"{b.Id.TypeId.ToString().Replace("MyObjectBuilder_", "")}/{b.Id.SubtypeName}"
                ) == true
                || (
                    components != null
                    && b.Definition?.Components?.Any(c =>
                        components.IsMatch(c.Definition.Id.SubtypeName)
                    ) == true
                )
            )
            .GroupBy(b => b.Definition?.DisplayNameText ?? b.Id.SubtypeName, b => b.Count)
            .Select(g => (Count: g.Sum(), Name: g.Key))
            .OrderByDescending(g => g.Count)
            .Select(g => $"{g.Count} {g.Name}")
            .ToList();
    }

    // A comma separated list of names where * matches any text, null when it is empty
    private static Regex PatternRegex(string patterns)
    {
        if (patternRegexes.TryGetValue(patterns, out var regex))
            return regex;
        var globs = patterns
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => Regex.Escape(p).Replace(@"\*", ".*"))
            .ToList();
        regex =
            globs.Count == 0
                ? null
                : new Regex(
                    $"^(?:{string.Join("|", globs)})$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
                );
        return patternRegexes[patterns] = regex;
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
    // welders; returns what it took, raw by the market's item name
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
                var name = Stations.ItemName(id);
                taken[name] = (taken.TryGetValue(name, out var had) ? had : 0) + amount.RawValue;
            }
        }
        return taken;
    }

    private static MyDefinitionId ItemId(string item) =>
        Stations.TryItemId(item, out var id) ? id : default;

    private static IEnumerable<MyInventory> Containers(MyCubeGrid shipyard) =>
        shipyard.GetFatBlocks<MyCargoContainer>().Select(c => c.GetInventory());

    // The shipyard's welders. Not its other ToolCore tools: its deconstructors stay OCT's and
    // never shoot, so they can't grind a ship being built (SE-983). They stay on as links of
    // the welders' conveyor lines (build_world.py).
    internal static IEnumerable<MyFunctionalBlock> Beams(MyCubeGrid shipyard) =>
        shipyard
            .GetFatBlocks<MyFunctionalBlock>()
            .Where(b => ((IMyTerminalBlock)b).GetProperty(ShootProperty) != null)
            .Where(b => WelderSubtypes.Contains(b.BlockDefinition.Id.SubtypeName));

    // The OctahedronFilaments mod's welder (SE-1067), and the OmniArc welder it copies, which
    // worlds built before it have
    private static readonly HashSet<string> WelderSubtypes = new HashSet<string>
    {
        "OctahedronFilamentWelder",
        "OmniArcBeam",
    };

    // Through the tools' terminal switches, which ToolCore also sends to the clients. In a
    // no-welding zone the clients would turn the tools off again and show no beams (SE-441):
    // ShowBeams keeps them on, and ShowShooting turns them on again on every client that has the
    // shipyard (SE-782, SE-1126).
    private static void Welders(MyCubeGrid shipyard, bool on)
    {
        ShipyardZoneWelding.ShowBeams(shipyard, on);
        if (on)
        {
            Fuel(shipyard);
            Sweep(shipyard, false);
            WakeBeams(shipyard);
            if (Waking(shipyard))
                return;
        }
        else
            waking.Remove(shipyard.EntityId);
        foreach (var beam in Beams(shipyard))
        {
            var block = (IMyTerminalBlock)beam;
            if (block.GetValueBool(TrackProperty) != on)
                block.SetValueBool(TrackProperty, on);
            if (block.GetValueBool(ShootProperty) != on)
                block.SetValueBool(ShootProperty, on);
        }
        ShipyardZoneWelding.ShowShooting(Beams(shipyard), on);
    }

    // A client's ToolCore drops the switch of a tool it still has off, and the block's Enabled
    // reaches it another way, so it may come after the switch. Between the two, nothing tells the
    // client to shoot again: the switch waits for a later check of the build.
    private const int WakeSeconds = 3;
    private static readonly Dictionary<long, DateTime> waking = [];

    internal static void WakeBeams(MyCubeGrid shipyard)
    {
        foreach (var beam in Beams(shipyard).Where(b => !b.Enabled))
        {
            beam.Enabled = true;
            waking[shipyard.EntityId] = DateTime.UtcNow.AddSeconds(WakeSeconds);
        }
    }

    private static bool Waking(MyCubeGrid shipyard)
    {
        if (!waking.TryGetValue(shipyard.EntityId, out var until))
            return false;
        if (DateTime.UtcNow < until)
            return true;
        waking.Remove(shipyard.EntityId);
        return false;
    }

    // The welders stop without power: the shipyard's reactors are topped up to ReactorUranium
    // while a build runs, at OCT's cost
    private const int ReactorUranium = 1000;
    private static readonly MyDefinitionId uranium = new(typeof(MyObjectBuilder_Ingot), "Uranium");

    private static void Fuel(MyCubeGrid shipyard)
    {
        foreach (var reactor in shipyard.GetFatBlocks<MyReactor>())
        {
            var inventory = reactor.GetInventory();
            var missing = ReactorUranium - inventory.GetItemAmount(uranium);
            if (missing > 0)
                inventory.AddItems(
                    MyFixedPoint.Min(missing, inventory.ComputeAmountThatFits(uranium)),
                    MyObjectBuilderSerializer.CreateNewObject(uranium)
                );
        }
    }

    // Removes the garbage in the shipyard main grid's box that could block a build: floating
    // objects, and before a build the grids no player owns and the players' wrecks, unpowered
    // and without a cockpit or remote control. Characters, the shipyard and the grids on its
    // rotors stay.
    private static void Sweep(MyCubeGrid shipyard, bool grids)
    {
        var box = new MyOrientedBoundingBoxD(
            shipyard.PositionComp.LocalAABB,
            shipyard.PositionComp.WorldMatrixRef
        );
        var entities = MyEntities.GetEntitiesInOBB(ref box);
        var floating = entities.OfType<MyFloatingObject>().ToList();
        var found = grids
            ? entities
                .OfType<MyCubeGrid>()
                .Where(g => !g.IsPreview && HasBlockIn(g, box))
                .Select(Grids)
                .ToList()
            : [];
        entities.Clear();
        foreach (var item in floating)
            MyFloatingObjects.RemoveFloatingObject(item);
        var own = Grids(shipyard);
        var removed = new HashSet<MyCubeGrid>();
        foreach (var group in found)
        {
            if (
                group.Any(g =>
                    own.Contains(g)
                    || removed.Contains(g)
                    || (towGiven.TryGetValue(g.EntityId, out var until) && DateTime.UtcNow < until)
                    || (
                        builtShips.TryGetValue(shipyard.EntityId, out var built)
                        && built.Any(b => b.Grid == g.EntityId)
                    )
                ) || !Garbage(group)
            )
                continue;
            foreach (var grid in group)
            {
                removed.Add(grid);
                grid.Close();
            }
        }
        if (floating.Count > 0 || removed.Count > 0)
            Plugin.Log.Info(
                $"Shipyard {shipyard.DisplayName}: removed {floating.Count} floating objects and "
                    + $"{removed.Count} grids: {string.Join(", ", removed.Select(g => g.DisplayName))}"
            );
    }

    private static bool Garbage(List<MyCubeGrid> group)
    {
        var players = MySession.Static.Players;
        if (!group.SelectMany(g => g.BigOwners).Any(o => !players.IdentityIsNpc(o)))
            return true;
        return !group.Any(g =>
            g.GetFatBlocks<MyShipController>().Any()
            || g.GridSystems.ResourceDistributor?.ResourceStateByType(
                MyResourceDistributorComponent.ElectricityId,
                grid: g
            )
                is not (null or MyResourceStateEnum.NoPower)
        );
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

    internal static bool IsScaffold(MyTerminalBlock block) =>
        block.CustomName.ToString() == ScaffoldName
        && MySession.Static.Players.IdentityIsNpc(block.OwnerId);

    private static string Name(long identityId) =>
        MySession.Static.Players.TryGetIdentity(identityId)?.DisplayName ?? "someone";

    private static void Tell(long identityId, string text) => ShipyardAway.Tell(identityId, text);

    // An amount in its item's unit: whole components, kg of ore and ingots
    private static string Units(string item, long raw)
    {
        var units = (decimal)raw / Amounts.Unit;
        return item.StartsWith("Component/")
            ? units.ToString("N0", CultureInfo.InvariantCulture)
            : units.ToString("N0", CultureInfo.InvariantCulture) + " kg";
    }

    private static string Meters(Vector3D size) => $"{size.X:0} x {size.Y:0} x {size.Z:0} m";
}
