using System;
using System.Collections.Generic;
using PluginSdk.Commands;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ServerPlugin;

[CommandRoot("oct", "Octahedron", "Octahedron world commands")]
public sealed class Commands : CommandModule
{
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
        "Orders the blueprint loaded into the nearest station hologram table from the station's shipyard",
        "!oct order [table name]: stand by a station's hologram table with a blueprint loaded. "
            + "The station's shipyard builds the ship for you."
    )]
    [Permission(MyPromoteLevel.None)]
    public CommandReply Order(params string[] table) =>
        ShipyardOrders.Request(
            Context.Caller.IdentityId,
            table.Length == 0 ? null : string.Join(" ", table)
        );

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
