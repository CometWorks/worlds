using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;

namespace ServerPlugin;

// The stations' connectors trade (SE-936): a ship docked there gets no power, hydrogen or conveyor
// access from the station. They are the station owner's and shared with nobody, not even its
// faction, which every player is in (OctFaction), so no player can open their terminal and turn
// trading off. A world from before this had them shared
// with everyone, and players turned trading off to charge and fuel for free; each connector is put
// right as it is found, letting go of a ship docked to one that didn't trade.
internal static class StationConnectors
{
    private const int CheckSeconds = 5;
    private static DateTime nextCheck;

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (
            now < nextCheck
            || MySession.Static?.Ready != true
            || !Plugin.Config.StationConnectorsTrade
        )
            return;
        nextCheck = now.AddSeconds(CheckSeconds);

        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
            if (
                MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid)
                && Plugin.Simulated(grid)
            )
                foreach (var connector in grid.GetFatBlocks<MyShipConnector>())
                    Trade(connector);
    }

    private static void Trade(MyShipConnector connector)
    {
        if (!MySession.Static.Players.IdentityIsNpc(connector.OwnerId))
            return;
        ShipyardTurn.SetOwner(connector, connector.OwnerId, MyOwnershipShareModeEnum.None);
        if (connector.TradingEnabled)
            return;
        if (connector.Connected)
            ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Disconnect();
        connector.TradingEnabled.Value = true;
    }
}
