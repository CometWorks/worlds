using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerPlugin;

// Where a ship goes along the shipyard, without the game, so tests/builds can check it alone.
internal static class ShipyardPlacement
{
    // Which of the ship's axes goes across, up and along the shipyard, nearest the cockpit's
    // frame first: as it is, turned about its up, rolled, pitched, then the turns of two axes.
    private static readonly int[][] Turns =
    [
        [0, 1, 2],
        [2, 1, 0],
        [1, 0, 2],
        [0, 2, 1],
        [1, 2, 0],
        [2, 0, 1],
    ];

    // The first of the turns that fits the room, or the one that misses it the least. Both are
    // width, height, length.
    public static int[] Align(double[] size, double[] room) =>
        Turns
            .OrderBy(turn => Math.Max(1, Enumerable.Range(0, 3).Max(i => size[turn[i]] / room[i])))
            .First();

    // Which of a blueprint's ship controllers sets its frame: the main cockpit, else the first
    // that flies the ship, null if none does. A cryo chamber, a passenger seat or a bed is a ship
    // controller too and faces anywhere, so it never counts, even flagged as main.
    public static T FrameBlock<T>(
        IEnumerable<T> controllers,
        Func<T, bool> flies,
        Func<T, bool> main
    )
        where T : class
    {
        var flying = controllers.Where(flies).ToList();
        return flying.FirstOrDefault(main) ?? flying.FirstOrDefault();
    }

    // How far from the shipyard's middle a ship's center goes. A ship shorter than a quarter of
    // the shipyard sits around a 1/3 point, 1/6 of the length from the middle, where it shows
    // better from the lobby; a longer one in the middle.
    public static double Offset(double shipLength, double shipyardLength) =>
        shipLength < shipyardLength / 4 ? shipyardLength / 6 : 0;

    // Where a bot's finished ship goes once the delay is up. Only the node that simulates the
    // yard can store it in the bot's Hangar, and only when another job waits. A ship closed there
    // is gone and gets no word; one that left with the yard, in a handover to another node, or
    // that can't be stored is ready in the shipyard, so the bot never waits in vain while its
    // ship blocks the yard.
    public static BotShip BotShipEnd(bool closed, bool yardHere, bool storable, bool waiting) =>
        !yardHere ? BotShip.Ready
        : closed ? BotShip.Gone
        : storable && waiting ? BotShip.Hangar
        : BotShip.Ready;
}

internal enum BotShip
{
    Gone,
    Ready,
    Hangar,
}
