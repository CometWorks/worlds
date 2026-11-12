using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ServerPlugin;

[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class Commands : CommandModule
{
    [Command(
        "taxi",
        "Pays a cab's fare and names where it takes you, or calls a cab to you",
        "!oct taxi <station>: sit in a cab's passenger seat first. The fare goes to the driver now and is not "
            + "refunded when you get out before the cab arrives. !oct taxi call: a free cab that can fly at your location comes to you. "
            + "Sitting down in it pays the ride to the nearest station. !oct taxi giveup: a driver "
            + "who can't come ends their calls, free. !oct taxi cargo: a driver asks whether a passenger "
            + "left cargo in the cab's connector."
    )]
    [Permission(MyPromoteLevel.None)]
    public string Taxi(params string[] station) =>
        station switch
        {
            [var w] when w.Equals("call", StringComparison.OrdinalIgnoreCase) =>
                ServerPlugin.Taxi.CallCab(Context.Caller.IdentityId),
            ["accept-pickup", var token] => ServerPlugin.Taxi.AnswerPickup(
                Context.Caller.IdentityId,
                token,
                true
            ),
            ["decline-pickup", var token] => ServerPlugin.Taxi.AnswerPickup(
                Context.Caller.IdentityId,
                token,
                false
            ),
            [var w] when w.Equals("cargo", StringComparison.OrdinalIgnoreCase) =>
                ServerPlugin.Taxi.Cargo(Context.Caller.IdentityId),
            [var w] when w.Equals("giveup", StringComparison.OrdinalIgnoreCase) =>
                ServerPlugin.Taxi.GiveUp(Context.Caller.IdentityId),
            _ => ServerPlugin.Taxi.Pay(Context.Caller.IdentityId, string.Join(" ", station)),
        };

    [Command(
        "police",
        "Police bots go on or off duty at a station; admins list the officers and targets",
        "!oct police duty <station>|off: police bots only. !oct police list: admins."
    )]
    [Permission(MyPromoteLevel.None)]
    public string Police(params string[] words) =>
        words switch
        {
            ["duty", .. var station] when station.Length > 0 => ServerPlugin.Police.Duty(
                Context.Caller.IdentityId,
                string.Join(" ", station)
            ),
            ["list"] when Context.Caller.PromoteLevel >= MyPromoteLevel.Admin =>
                ServerPlugin.Police.Describe(),
            _ => "Usage: !oct police duty <station>|off",
        };

    [Command("reload", "Rereads the plugin's config file")]
    [Permission(MyPromoteLevel.Admin)]
    public string Reload()
    {
        Plugin.LoadConfig();
        return "Octahedron config reloaded.";
    }

    [Command("edge", "Lists the grids and characters in the world edge's braking zone")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> Edge() => WorldEdge.Describe(Plugin.Config);

    [Command(
        "ship",
        "Builds the blueprint loaded into the nearest station hologram table as your starter ship",
        "!oct ship [table name]: stand by a station's hologram table with a blueprint loaded. "
            + "The ship appears outside the station's safe zone, owned by you."
    )]
    [Permission(MyPromoteLevel.None)]
    public CommandReply Ship(params string[] table) =>
        StarterShip.Request(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "order",
        "Presses the shipyard button of the nearest station hologram table for you",
        "!oct order [table name]: stand by a station's hologram table with a blueprint loaded. "
            + "Orders the blueprint you loaded, cancels your build or repair slot, or books a repair slot, as the button next to the table does."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Order(params string[] table) =>
        ShipyardOrders.Request(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "table hold",
        "Makes you the player who loaded the nearest hologram table's blueprint, for tests",
        "!oct table hold [table name]: the blueprint is then cleared when you leave the station, as if you had loaded it."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string TableHold(params string[] table) =>
        ShipyardOrders.Hold(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "table show",
        "Shows what the nearest hologram table's screen and billboard show",
        "!oct table show [table name]"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string TableShow(params string[] table) =>
        ShipyardOrders.Show(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "table remove",
        "Removes the blueprint of the nearest hologram table, as the Remove button of its terminal does",
        "!oct table remove [table name]: only the player who holds the table can do this in its terminal."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string TableRemove(params string[] table) =>
        ShipyardOrders.Remove(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "workshop",
        "Loads a blueprint from the Steam Workshop into the nearest station hologram table for you",
        "!oct workshop <link or id> [table name]: stand by a station's hologram table. The server downloads the blueprint, "
            + "so a ship with subgrids arrives whole without the Multigrid Projector plugin."
    )]
    [Permission(MyPromoteLevel.None)]
    public string Workshop(string link, params string[] table) =>
        ShipyardOrders.Workshop(
            Context.Caller.IdentityId,
            link,
            table.Length == 0 ? null : string.Join(" ", table)
        );

    [Command(
        "shipyard done",
        "Ends your turn at a shipyard now: what is left in its containers is sold to the station for you",
        "!oct shipyard done: your turn would otherwise end by itself at its deadline, or once the containers are empty. "
            + "A running repair slot ends with it; a build must have ended first."
    )]
    [Permission(MyPromoteLevel.None)]
    public string ShipyardDone() => ShipyardOrders.Done(Context.Caller.IdentityId);

    [Command(
        "shipyard clear",
        "Ends every order and turn in a station's shipyard queue, as each player's cancel or !oct shipyard done would",
        "!oct shipyard clear [station]: run it on the node that simulates the station. Without a station, the one whose "
            + "hologram table is in the admin safe zone you stand in. Waiting orders leave the queue, "
            + "a running build is canceled (a paid one's unfinished ship is handed over, a free one's removed), a repair "
            + "slot ends, and each turn ends with what is left in the shipyard sold for its player."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ShipyardClear(string station = null)
    {
        var admin = Context.Caller.IdentityId;
        string error;
        if (station == null)
        {
            station = ShipyardOrders.StationAround(admin, out error);
            if (station == null)
                return error;
        }
        else if (!Stations.Find(ref station, out error))
            return error;
        var answers = new List<string>();
        var synchronous = true;
        var reply = ShipyardOrders.Clear(
            station,
            text =>
            {
                if (synchronous)
                    answers.Add(text);
                else
                    ShipyardAway.Tell(admin, text);
            }
        );
        synchronous = false;
        return answers.Count > 0 ? string.Join("\n", answers) : reply;
    }

    [Command(
        "patch apply",
        "Applies a station patch now, on the process that simulates the grid",
        "!oct patch apply <id> \"<station or shipyard grid>\" \"<ops>\": checks every op first and applies all or none. "
            + "Ops: set:X,Y,Z:Expected:enabled=0|1|name=Text|data=Text, del:X,Y,Z:Expected, "
            + "add:X,Y,Z:Type/Subtype:Forward:Up[:Name], swap:X,Y,Z:Expected:Type/Subtype, separated by ';'. "
            + "The grid's ledger keeps the id, so a patch applies once."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string PatchApply(string id, string grid, string ops) =>
        StationPatches.Apply(id, grid, ops, true);

    [Command(
        "patch check",
        "Checks a station patch without applying it",
        "!oct patch check <id> \"<grid>\" \"<ops>\""
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string PatchCheck(string id, string grid, string ops) =>
        StationPatches.Apply(id, grid, ops, false);

    [Command(
        "patch replace",
        "Turns a station or shipyard grid built from one blueprint into another, in place",
        "!oct patch replace <id> \"<grid>\" <from.sbc> <to.sbc>: the blueprints are found next to the config file. "
            + "Only the blocks and grid settings that differ between them change, checked first, all or none."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string PatchReplace(string id, string grid, string from, string to) =>
        StationPatches.Replace(id, grid, from, to, true);

    [Command(
        "patch diff",
        "Checks a blueprint replacement without applying it and lists its ops",
        "!oct patch diff <id> \"<grid>\" <from.sbc> <to.sbc>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string PatchDiff(string id, string grid, string from, string to) =>
        StationPatches.Replace(id, grid, from, to, false);

    [Command(
        "patch status",
        "A station or shipyard grid's block count, block list hash, patch ledger and islands",
        "!oct patch status \"<grid>\""
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string PatchStatus(string grid) => StationPatches.Status(grid);

    [Command(
        "shipyard tow",
        "Breaks down a grid for you at a station's shipyard, as a tow over your limits does, for tests",
        "!oct shipyard tow <station> <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ShipyardTow(string station, long grid)
    {
        if (!Stations.Find(ref station, out string error))
            return error;
        if (!MyEntities.TryGetEntityById(grid, out MyCubeGrid found) || found.MarkedForClose)
            return $"No grid {grid} on this node.";
        ShipyardOrders.Tow(station, Context.Caller.IdentityId, found);
        return "The shipyard answers in chat.";
    }

    [Command(
        "shipyard given",
        "Gives you a grid as a tow does: a warning, and Sweep leaves it alone for a while, for tests",
        "!oct shipyard given <grid id>"
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ShipyardGiven(long grid)
    {
        if (!MyEntities.TryGetEntityById(grid, out MyCubeGrid found) || found.MarkedForClose)
            return $"No grid {grid} on this node.";
        ShipyardOrders.GivenByTow(found, Context.Caller.IdentityId);
        return "Done.";
    }

    [Command("shipyards", "Lists the shipyards, their arm rotors and the builds running in them")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> Shipyards() => ShipyardOrders.Describe();

    [Command(
        "shipyard strip",
        "Takes an item out of a shipyard's containers and welders, for tests",
        "!oct shipyard strip <station> <item>: item like Component/Display. "
            + "A running build then stalls on the blocks that need it."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ShipyardStrip(string station, string item) => ShipyardOrders.Strip(station, item);

    [Command(
        "encounters",
        "Lists the encounters that could spawn where you are",
        "!oct encounters [type count]: with a type and a count, the share of each mod in that "
            + "many spawn attempts of the type where you are, nothing spawned."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> EncounterList(string type = null, int count = 1000)
    {
        if (CallerCharacter() is not { } character)
            return ["Spawn first."];
        var position = character.PositionComp.GetPosition();
        if (type == null)
            return Encounters.Describe(position);
        return Enum.TryParse<EncounterType>(type, out var kind)
            ? Encounters.Sample(kind, position, count)
            : [$"Unknown encounter type {type}."];
    }

    [Command(
        "encounter",
        "Spawns an encounter near you, as the spawner would, without its caps and timers",
        "!oct encounter <type> [spawn group]: type is SpaceCargoShip, SpaceRandomEncounter, "
            + "AtmosphericCargoShip, GravityCargoShip, PlanetaryInstallation, BossEncounterSpace or BossEncounterAtmo."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Encounter(string type, params string[] group)
    {
        if (CallerCharacter() is not { } character)
            return "Spawn first.";
        if (!Enum.TryParse<EncounterType>(type, out var kind))
            return $"Unknown encounter type {type}.";
        var name = group.Length == 0 ? null : string.Join(" ", group);
        return Encounters.TrySpawn(
            kind,
            Spot.At(character.PositionComp.GetPosition()),
            name,
            force: true
        )
            ? "Encounter spawned, see the server log."
            : "No encounter could spawn here.";
    }

    private MyCharacter CallerCharacter() =>
        MySession.Static.Players.TryGetIdentity(Context.Caller.IdentityId)?.Character;

    [Command(
        "rep",
        "Shows your reputation in OCT",
        "!oct rep: damaging grids of other members lowers it, paying into OCT's account and sharing your grids with OCT raise it."
    )]
    [Permission(MyPromoteLevel.None)]
    public void Rep()
    {
        var context = Context;
        Reputation.Read(
            Context.Caller.IdentityId,
            (text, error) =>
            {
                if (text != null)
                    context.Respond(text);
                else
                    context.Respond(CommandReply.Error(error));
            }
        );
    }

    [Command(
        "rep add",
        "Changes a member's reputation in OCT, for tests",
        "!oct rep add <amount> [player name]: yours without a name."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string RepAdd(long amount, params string[] name)
    {
        var identity = Context.Caller.IdentityId;
        if (name.Length > 0)
        {
            var wanted = string.Join(" ", name);
            identity =
                MySession
                    .Static.Players.GetAllIdentities()
                    .FirstOrDefault(i => i.DisplayName == wanted)
                    ?.IdentityId
                ?? 0;
            if (identity == 0)
                return $"No player named {wanted}.";
        }
        return Reputation.Change(identity, amount);
    }

    [Command("money", "Shows OCT's balance, yours and the state of the tax and the payouts")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> MoneyStatus() => Money.Describe(Context.Caller.IdentityId);

    [Command(
        "payday",
        "Pays the hourly payout now, as if a new hour had started",
        "!oct payday: on a cluster only on the World Authority."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Payday() => Money.Force(tax: false);

    [Command(
        "taxday",
        "Collects the daily wealth tax now and closes the day, as if a new day had started",
        "!oct taxday: on a cluster only on the World Authority."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Taxday() => Money.Force(tax: true);

    [Command(
        "leaderboard",
        "Counts the leaderboard now and lists it, as the station panels will show it",
        "!oct leaderboard: on a cluster on a node, which counts its own grids."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public void LeaderboardNow()
    {
        var context = Context;
        Leaderboard.CountNow(
            (ranked, error) =>
            {
                if (ranked == null)
                {
                    context.Respond(CommandReply.Error(error));
                    return;
                }
                if (ranked.Count == 0)
                    context.Respond("Nobody on the leaderboard.");
                for (int i = 0; i < ranked.Count; i++)
                {
                    var r = ranked[i];
                    context.Respond(
                        $"{i + 1}. {(r.Tag == "" ? "" : $"[{r.Tag}] ")}{r.Name}: {LeaderboardMath.N(r.Total)} SC "
                            + $"(cash {LeaderboardMath.N(r.Cash)} SC, grids {LeaderboardMath.N(r.Grids)} SC), "
                            + $"{LeaderboardMath.N(r.Pcu)} PCU"
                    );
                }
            }
        );
    }

    [Command(
        "contracts",
        "Lists OCT's contracts at every station",
        "!oct contracts [post]: post also posts the missing ones and checks bounties and salvage now."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> ContractList(string post = null) =>
        post == "post" ? [Contracts.Run()] : Contracts.Describe();

    [Command(
        "contract",
        "Takes, delivers or completes one of OCT's contracts as the caller, for tests and support",
        "!oct contract accept <id> [identity] | deliver <id> [fill] | complete <id>: accept takes it as the caller or "
            + "the identity, deliver hands in a hauling or acquisition contract from the caller's inventory, fill adds "
            + "the items first; complete finishes a bounty or salvage."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string ContractAction(string action, long id, string fill = null) =>
        action switch
        {
            "accept" => Contracts.Accept(
                long.TryParse(fill, out var identity) ? identity : Context.Caller.IdentityId,
                id
            ),
            "deliver" => Contracts.Deliver(Context.Caller.IdentityId, id, fill == "fill"),
            "complete" => Contracts.Complete(id),
            _ => "accept, deliver or complete",
        };

    [Command(
        "offline",
        "Shows the offline characters' times and kept inventories",
        "!oct offline: on the server or the World Authority, who was last online when and whose inventory is kept."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> Offline() => OfflineCharacters.Describe();

    [Command("holograms", "Fits the blueprints in the station hologram tables to the tables again")]
    [Permission(MyPromoteLevel.Admin)]
    public string Holograms() => $"Fitted {TableHologram.FitAll()} hologram tables.";

    [Command(
        "drop",
        "Spawns a container drop for you, as the game's drop timer does",
        "!oct drop [strong]: a personal Unknown Signal, or with strong a Strong Unknown Signal. "
            + "ContainerDropsPersonal off refuses the personal one."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Drop(string kind = "") =>
        ContainerDrops.Spawn(Context.Caller.IdentityId, kind == "strong");
}

// !bp, short for !oct workshop: what the shipyard tells a player whose blueprint with subgrids it took off the table
[CommandRoot("bp", "Octahedron", "Loads a Steam Workshop blueprint into a station hologram table")]
public sealed class BlueprintCommand : CommandModule
{
    [Command(
        "",
        "Loads a blueprint from the Steam Workshop into the nearest station hologram table for you",
        "!bp <link or id> [table name]: stand by a station's hologram table. The same as !oct workshop."
    )]
    [Permission(MyPromoteLevel.None)]
    public string Load(string link, params string[] table) =>
        ShipyardOrders.Workshop(
            Context.Caller.IdentityId,
            link,
            table.Length == 0 ? null : string.Join(" ", table)
        );
}
