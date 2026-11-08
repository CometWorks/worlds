using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace ServerPlugin;

// Cabs between the stations (SE-1279). A cab is a grid named TaxiGridName* with someone in a seat
// that controls it, the driver: a sebot taxi driver, or a player. Whoever sits down in one of its
// other seats is told where the cab goes and how to pay; "!oct taxi <station>" takes the fare from
// the passenger and pays it to the driver, through the bank as Hangar's fee does, and tells the
// driver where to go. The driver hears when a passenger gets in and out. A fare is never refunded.
//
// What a seat's passenger was asked and paid is written into the seat's custom data, "<identity>
// <station>" ("-" before paying): it moves with the grid when a cluster node hands the cab's
// partition to another node, so that node neither asks again nor misses the passenger leaving.
// Faking it gains nothing: the driver flies only when told that a fare was paid. The passenger
// seats and O2/H2 generator of a bot's cab (ShipyardBotSteamIds) are opened to everyone, so a
// passenger can take or fill a bottle there; a player driving a cab sets its share modes as they like.
//
// "!oct taxi call" calls the nearest free cab (one with a driver and nobody else in it, standing at
// a station) to where the caller is, in space only. The driver hears "Call from NAME at X Y Z" and
// flies there. When the caller sits down in that cab they pay TaxiCallFare to the driver, or all
// they have when that is less, for the ride to the nearest station, and the driver hears the usual
// "Fare paid by". A caller who dies before pays TaxiCancelFee and the call ends ("Call cancelled:
// NAME"), as it does free after TaxiCallTimeout or when the cab or its driver is gone. Calls live
// in this process: on a cluster a call reaches the cabs of the caller's node only.
internal static class Taxi
{
    private const string Author = "Taxi";
    private const string Unpaid = "-";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(10);
    private const double AtStation = 3_000; // m: a cab this near a station is at it

    private static DateTime nextTick,
        nextScan;
    private static readonly HashSet<long> cabs = new();

    // The passengers this node saw seated at its last tick: identity -> (cab, seat)
    private static readonly Dictionary<long, (long Cab, long Seat)> seated = new();

    private sealed class Call
    {
        public long Cab;
        public long Driver;
        public long Body; // the caller's character then: another one now means they died and respawned
        public Vector3D At;
        public DateTime Until;
    }

    // Open calls by caller
    private static readonly Dictionary<long, Call> calls = new();

    private static OctahedronConfig Config => Plugin.Config;

    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextTick || MySession.Static?.Ready != true)
            return;
        nextTick = now + Tick;
        if (!Config.TaxiEnabled || string.IsNullOrEmpty(Config.TaxiGridName))
        {
            seated.Clear();
            calls.Clear();
            return;
        }
        if (now >= nextScan)
        {
            nextScan = now + Rescan;
            Scan();
        }

        var seen = new Dictionary<long, (long Cab, long Seat)>();
        foreach (var id in cabs)
        {
            if (!MyEntities.TryGetEntityById(id, out MyCubeGrid cab) || !Plugin.Simulated(cab))
                continue;
            var driver = Driver(cab);
            foreach (var generator in cab.GetFatBlocks().OfType<MyGasGenerator>())
                if (ShipyardOrders.IsBot(generator.OwnerId))
                    ShipyardTurn.SetOwner(
                        generator,
                        generator.OwnerId,
                        MyOwnershipShareModeEnum.All
                    );
            foreach (var seat in PassengerSeats(cab))
            {
                // A bot's cab is built with the bot's faction share: open its passenger seats to all
                if (ShipyardOrders.IsBot(seat.OwnerId))
                    ShipyardTurn.SetOwner(seat, seat.OwnerId, MyOwnershipShareModeEnum.All);
                var passenger = seat.Pilot?.GetPlayerIdentityId() ?? 0;
                if (passenger == 0)
                    continue;
                seen[passenger] = (cab.EntityId, seat.EntityId);
                if (Record(seat).Passenger != passenger)
                    GotIn(cab, seat, passenger, driver);
            }
        }

        foreach (var (passenger, at) in seated)
        {
            if (seen.ContainsKey(passenger))
                continue;
            // Gone from this node with its cab (another node simulates it now): that one goes on
            if (
                !MyEntities.TryGetEntityById(at.Cab, out MyCubeGrid cab)
                || !Plugin.Simulated(cab)
                || !MyEntities.TryGetEntityById(at.Seat, out MyCockpit seat)
            )
                continue;
            GotOut(cab, seat, passenger);
        }
        seated.Clear();
        foreach (var (passenger, at) in seen)
            seated[passenger] = at;
        UpdateCalls(now);
    }

    // !oct taxi call, the reply goes to the caller
    public static string CallCab(long caller)
    {
        if (!Config.TaxiEnabled)
            return "There are no cabs.";
        var character = Body(caller);
        if (character == null)
            return "Call a cab when you are out in space.";
        if (SeatOf(caller).Seat != null)
            return "You sit in a cab already.";
        if (calls.TryGetValue(caller, out var open))
            return $"{Name(open.Driver)} is on the way already. Stay where you called from.";
        var at = character.PositionComp.GetPosition();
        if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(at).LengthSquared() > 0)
            return "Cabs don't fly into gravity yet: call one from space.";

        MyCubeGrid best = null;
        long bestDriver = 0;
        var nearest = double.MaxValue;
        foreach (var id in cabs)
        {
            if (
                !MyEntities.TryGetEntityById(id, out MyCubeGrid cab)
                || !Plugin.Simulated(cab)
                || calls.Values.Any(c => c.Cab == id)
                || PassengerSeats(cab).Any(s => s.Pilot != null)
                || (cab.Physics?.LinearVelocity.Length() ?? 0) > 1
                || StationAt(cab.PositionComp.GetPosition()) == null
            )
                continue;
            var driver = Driver(cab);
            var d = Vector3D.Distance(cab.PositionComp.GetPosition(), at);
            if (driver != 0 && d < nearest)
                (best, bestDriver, nearest) = (cab, driver, d);
        }
        if (best == null)
            return "No cab is free right now. Try again later.";

        calls[caller] = new Call
        {
            Cab = best.EntityId,
            Driver = bestDriver,
            Body = character.EntityId,
            At = at,
            Until = DateTime.UtcNow + TimeSpan.FromSeconds(Config.TaxiCallTimeout),
        };
        var from = StationAt(best.PositionComp.GetPosition());
        Plugin.Log.Info(
            $"Taxi: {Name(caller)} ({caller}) called '{best.DisplayName}' of {Name(bestDriver)} from {from}, "
                + $"{nearest / 1000:F1} km"
        );
        Tell(bestDriver, $"Call from {Name(caller)} at {at.X:F0} {at.Y:F0} {at.Z:F0}");
        return $"{Name(bestDriver)} is on the way from {from}, {nearest / 1000:F1} km. Stay here and sit down in "
            + $"the cab when it stops by you: the ride to the nearest station, {NearestStation(at)}, costs "
            + $"{Sc(Config.TaxiCallFare)} then, or all you have when that is less. If you die before, the "
            + $"call is cancelled and you pay {Sc(Config.TaxiCancelFee)}.";
    }

    private static void UpdateCalls(DateTime now)
    {
        foreach (var (caller, call) in calls.ToList())
        {
            if (now > call.Until)
                EndCall(caller, call, "nobody sat down in the cab in time", 0);
            else if (
                !MyEntities.TryGetEntityById(call.Cab, out MyCubeGrid cab)
                || Driver(cab) != call.Driver
            )
                EndCall(caller, call, "the cab or its driver is gone", 0);
            else if (
                MySession.Static.Players.TryGetIdentity(caller) is { } identity
                && (
                    identity.IsDead
                    || identity.Character?.IsDead == true
                    || (identity.Character != null && identity.Character.EntityId != call.Body)
                )
            )
                EndCall(caller, call, "you died before it came", Config.TaxiCancelFee);
        }
    }

    private static void EndCall(long caller, Call call, string why, long fee)
    {
        calls.Remove(caller);
        var paid = Charge(caller, fee, call.Driver);
        Plugin.Log.Info($"Taxi: call of {Name(caller)} ({caller}) cancelled, {why}, fee {paid} SC");
        Tell(call.Driver, $"Call cancelled: {Name(caller)}");
        Tell(
            caller,
            $"Your cab call is cancelled: {why}." + (paid > 0 ? $" You paid {Sc(paid)}." : "")
        );
    }

    // The caller sat down in the called cab: the ride to the nearest station, paid as far as they can
    private static void PickUp(MyCubeGrid cab, MyCockpit seat, long passenger, long driver)
    {
        calls.Remove(passenger);
        var station = NearestStation(cab.PositionComp.GetPosition());
        var paid = Charge(passenger, Config.TaxiCallFare, driver);
        Write(seat, passenger, station);
        var name = Name(passenger);
        Plugin.Log.Info(
            $"Taxi: {name} ({passenger}) picked up by {Name(driver)} ({driver}), paid {paid} SC, to {station}"
        );
        Tell(driver, $"Passenger in: {name}");
        Tell(driver, $"Fare paid by {name}. Destination: {station}");
        Tell(passenger, $"You paid {Sc(paid)}. {Name(driver)} takes you to {station}.");
    }

    // Up to 'amount' SC from the payer to the driver, as much as the payer has; what was paid
    private static long Charge(long payer, long amount, long driver)
    {
        var balance = MyBankingSystem.Static.TryGetAccountInfo(payer, out var account)
            ? account.Balance
            : 0;
        amount = Math.Min(amount, Math.Max(balance, 0));
        if (amount <= 0 || !MyBankingSystem.ChangeBalance(payer, -amount))
            return 0;
        if (!MyBankingSystem.ChangeBalance(driver, amount))
            Plugin.Log.Error(
                $"Taxi: {amount} SC taken from {payer} but not paid to the driver {driver}"
            );
        return amount;
    }

    private static MyCharacter Body(long identity) =>
        MySession.Static.Players.TryGetIdentity(identity)?.Character is { IsDead: false } body
            ? body
            : null;

    // !oct taxi <station>, the reply goes to the passenger
    public static string Pay(long passenger, string station)
    {
        if (!Config.TaxiEnabled)
            return "There are no cabs.";
        var (cab, seat) = SeatOf(passenger);
        if (seat == null)
            return $"Sit in a cab's passenger seat first: a ship named {Config.TaxiGridName} with a driver.";
        var driver = Driver(cab);
        if (driver == 0)
            return "This cab has no driver right now.";
        var here = StationAt(cab.PositionComp.GetPosition());
        var destinations = Destinations(here);
        if (string.IsNullOrWhiteSpace(station))
            return Question(destinations);
        var record = Record(seat);
        if (record.Passenger == passenger && record.Station != Unpaid)
            return $"You paid for {record.Station} already.";
        if (!Stations.Find(ref station, out var error))
            return error;
        if (station == here)
            return $"You are at {here}. {Question(destinations)}";

        var fare = Config.TaxiFare;
        // Refused when the balance is short; on a cluster the World Authority decides
        if (fare > 0 && !MyBankingSystem.ChangeBalance(passenger, -fare))
        {
            var balance = MyBankingSystem.Static.TryGetAccountInfo(passenger, out var account)
                ? account.Balance
                : 0;
            return $"The fare is {Sc(fare)}, you have {Sc(balance)}.";
        }
        if (fare > 0 && !MyBankingSystem.ChangeBalance(driver, fare))
            Plugin.Log.Error(
                $"Taxi: {fare} SC taken from {passenger} but not paid to the driver {driver}"
            );

        Write(seat, passenger, station);
        var name = Name(passenger);
        Plugin.Log.Info(
            $"Taxi: {name} ({passenger}) paid {fare} SC to {Name(driver)} ({driver}) in '{cab.DisplayName}'"
                + $" from {here ?? "space"} to {station}"
        );
        Tell(driver, $"Fare paid by {name}. Destination: {station}");
        return $"You paid {Sc(fare)}. {Name(driver)} takes you to {station}. "
            + "The fare is not refunded if you get out before the cab arrives.";
    }

    private static void GotIn(MyCubeGrid cab, MyCockpit seat, long passenger, long driver)
    {
        if (driver != 0 && calls.TryGetValue(passenger, out var call) && call.Cab == cab.EntityId)
        {
            PickUp(cab, seat, passenger, driver);
            return;
        }
        if (driver == 0)
        {
            // asked once a driver sits down, at a later tick
            if (!seated.ContainsKey(passenger))
                Tell(passenger, "This cab has no driver right now.");
            return;
        }
        Write(seat, passenger, Unpaid);
        var here = StationAt(cab.PositionComp.GetPosition());
        Tell(passenger, Question(Destinations(here)));
        Tell(driver, $"Passenger in: {Name(passenger)}");
        Plugin.Log.Info(
            $"Taxi: {Name(passenger)} ({passenger}) got into '{cab.DisplayName}' at {here ?? "space"}"
        );
    }

    private static void GotOut(MyCubeGrid cab, MyCockpit seat, long passenger)
    {
        var record = Record(seat);
        if (record.Passenger == passenger)
            seat.CustomData = "";
        var driver = Driver(cab);
        if (driver != 0)
            Tell(driver, $"Passenger out: {Name(passenger)}");
        Plugin.Log.Info(
            $"Taxi: {Name(passenger)} ({passenger}) got out of '{cab.DisplayName}'"
                + (
                    record.Passenger == passenger && record.Station != Unpaid
                        ? $", paid for {record.Station}"
                        : ""
                )
        );
    }

    private static string Question(List<string> destinations) =>
        $"Where to? {string.Join(", ", destinations)}. Type !oct taxi <station> to go. The fare, "
        + $"{Sc(Config.TaxiFare)}, goes to the driver then and is not refunded if you get out early.";

    // The cabs among the grids this node has, by name
    private static void Scan()
    {
        cabs.Clear();
        foreach (var entity in MyEntities.GetEntities())
            if (
                entity is MyCubeGrid grid
                && !grid.IsStatic
                && grid.DisplayName?.StartsWith(
                    Config.TaxiGridName,
                    StringComparison.OrdinalIgnoreCase
                ) == true
            )
                cabs.Add(grid.EntityId);
    }

    // The identity at the cab's controls, 0 when nobody
    private static long Driver(MyCubeGrid cab)
    {
        foreach (var block in cab.GetFatBlocks())
            if (
                block is MyCockpit seat
                && seat.BlockDefinition.EnableShipControl
                && seat.Pilot != null
            )
                return seat.Pilot.GetPlayerIdentityId();
        return 0;
    }

    private static IEnumerable<MyCockpit> PassengerSeats(MyCubeGrid cab) =>
        cab.GetFatBlocks().OfType<MyCockpit>().Where(s => !s.BlockDefinition.EnableShipControl);

    private static (MyCubeGrid Cab, MyCockpit Seat) SeatOf(long passenger)
    {
        foreach (var id in cabs)
            if (MyEntities.TryGetEntityById(id, out MyCubeGrid cab))
                foreach (var seat in PassengerSeats(cab))
                    if (seat.Pilot?.GetPlayerIdentityId() == passenger)
                        return (cab, seat);
        return (null, null);
    }

    private static (long Passenger, string Station) Record(MyCockpit seat)
    {
        var parts = (seat.CustomData ?? "").Split(new[] { ' ' }, 2);
        return parts.Length == 2 && long.TryParse(parts[0], out var id)
            ? (id, parts[1])
            : (0, null);
    }

    private static void Write(MyCockpit seat, long passenger, string station) =>
        seat.CustomData = $"{passenger} {station}";

    // The station within AtStation of the point, else null
    private static string StationAt(Vector3D position) => NearestStation(position, AtStation);

    private static string NearestStation(Vector3D position, double within = double.MaxValue)
    {
        var names = Stations.StationNames().ToHashSet();
        string best = null;
        var nearest = within;
        foreach (var faction in MySession.Static.Factions)
        foreach (var station in faction.Value.Stations)
        {
            var d = Vector3D.Distance(station.Position, position);
            if (d < nearest && names.Contains(station.Name))
                (best, nearest) = (station.Name, d);
        }
        return best;
    }

    private static List<string> Destinations(string here) =>
        Stations.StationNames().Where(n => n != here).OrderBy(n => n).ToList();

    private static void Tell(long identity, string text) =>
        MyVisualScriptLogicProvider.SendChatMessage(text, Author, identity);

    private static string Name(long identity) =>
        MySession.Static.Players.TryGetIdentity(identity)?.DisplayName ?? identity.ToString();

    private static string Sc(long amount) =>
        amount.ToString("N0", CultureInfo.InvariantCulture) + " SC";
}
