using System;
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

    // How far from the shipyard's middle a ship's center goes. A ship shorter than a quarter of
    // the shipyard sits around a 1/3 point, 1/6 of the length from the middle, where it shows
    // better from the lobby; a longer one in the middle.
    public static double Offset(double shipLength, double shipyardLength) =>
        shipLength < shipyardLength / 4 ? shipyardLength / 6 : 0;
}
