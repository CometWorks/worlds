namespace ServerPlugin;

// Where a ship goes along the shipyard, without the game, so tests/builds can check it alone.
internal static class ShipyardPlacement
{
    // How far from the shipyard's middle a ship's center goes. A ship shorter than a quarter of
    // the shipyard sits around a 1/3 point, 1/6 of the length from the middle, where it shows
    // better from the lobby; a longer one in the middle.
    public static double Offset(double shipLength, double shipyardLength) =>
        shipLength < shipyardLength / 4 ? shipyardLength / 6 : 0;
}
