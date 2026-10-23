using System.Collections.Generic;
using PluginSdk.Commands;
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
