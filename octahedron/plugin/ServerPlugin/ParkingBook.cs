using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ServerPlugin;

// What the bank's keeper (a plain server, or the World Authority of a cluster) knows about offline
// characters: when each player was last online and the inventories of the characters taken out of
// the world. Without the game, so tests/offline-characters can check it alone. See
// OfflineCharacters for the game's side. Times are unix seconds.
internal enum ParkingVerdict
{
    // Nothing to do yet
    Wait,

    // An offline body whose time is up: send its inventory
    Park,

    // An offline body whose inventory is saved: remove it
    Remove,

    // An offline body whose inventory is sent, the save that holds it isn't done
    Hold,

    // A player's body with no inventory waiting
    None,

    // A player's body with an inventory waiting
    Take,

    // A player's body that was kept and taken out, brought back by a node that died before its
    // save: what it holds goes at once, it is in the bank and here already. Then as Take.
    TakeBack,
}

internal enum ParkOutcome
{
    // Stored: the money goes to the bank now
    Added,

    // This body was stored before, its money is in the bank
    Known,

    // The player is back, or the time isn't up
    Refused,
}

internal sealed class ParkedCharacter
{
    public long Identity;
    public long Body;

    // Space credits moved to the bank, to tell the player
    public long Credits;

    // The inventory as the game's side encoded it, no space credits in it
    public string Items;

    // The body it was last handed to, 0 when none
    public long Taker;
    public bool Delivered;

    // The first save that holds the last change: compared with what is saved. Zero after a load:
    // it came from a save.
    public long Order;
}

internal sealed class TakeAnswer
{
    // The save isn't done, ask again
    public bool Wait;

    // What to hand over; empty when nothing waits
    public List<ParkedCharacter> Parked = [];
}

internal sealed class ParkingBook
{
    public sealed class Seen
    {
        public long Seconds;

        // Near a station when last seen, so its partition is worth loading once the time is up
        public bool NearStation;

        // Its partition was asked to load since the player left
        public bool Woken;
    }

    private const long ForgetSeconds = 30 * 24 * 3600;

    public readonly Dictionary<long, Seen> Players = new();
    public readonly List<ParkedCharacter> Parked = [];
    private HashSet<long> online = [];

    // The players online right now, the ones near a station told apart
    public void Observe(IEnumerable<(long Identity, bool NearStation)> players, long now)
    {
        online = [];
        foreach (var (identity, nearStation) in players)
        {
            online.Add(identity);
            Players[identity] = new Seen { Seconds = now, NearStation = nearStation };
        }
        foreach (var gone in Players.Where(p => now - p.Value.Seconds > ForgetSeconds).ToList())
            Players.Remove(gone.Key);
    }

    private bool Due(long identity, long now, long limit)
    {
        if (online.Contains(identity))
            return false;
        if (Players.TryGetValue(identity, out var seen))
            return now - seen.Seconds >= limit;
        // Not seen since this started: from now on, so nobody loses a body at the first look
        Players[identity] = new Seen { Seconds = now };
        return false;
    }

    public ParkingVerdict Status(
        long identity,
        long body,
        bool bodyOnline,
        long now,
        long limit,
        long durable,
        bool force = false
    )
    {
        if (bodyOnline && !force)
        {
            if (Parked.Any(p => p.Identity == identity && p.Body == body && !p.Delivered))
                return ParkingVerdict.TakeBack;
            return Parked.Any(p => p.Identity == identity)
                ? ParkingVerdict.Take
                : ParkingVerdict.None;
        }
        var parked = Parked.Find(p => p.Identity == identity && p.Body == body);
        if (parked != null)
            return parked.Order <= durable ? ParkingVerdict.Remove : ParkingVerdict.Hold;
        return force || Due(identity, now, limit) ? ParkingVerdict.Park : ParkingVerdict.Wait;
    }

    public ParkOutcome Park(
        long identity,
        long body,
        long credits,
        string items,
        long now,
        long limit,
        long order,
        bool force = false
    )
    {
        if (Parked.Any(p => p.Identity == identity && p.Body == body))
            return ParkOutcome.Known;
        if (!force && !Due(identity, now, limit))
            return ParkOutcome.Refused;
        Parked.Add(
            new ParkedCharacter
            {
                Identity = identity,
                Body = body,
                Credits = credits,
                Items = items,
                Order = order,
            }
        );
        // The partition may go offline before the body is removed, then it needs waking again
        if (Players.TryGetValue(identity, out var seen))
            seen.Woken = false;
        return ParkOutcome.Added;
    }

    // Hands the player's parked inventories to the body once a save knows they are on their way:
    // a World Authority restored from an older save would give them a second time.
    public TakeAnswer Take(long identity, long body, long order, long durable)
    {
        var answer = new TakeAnswer();
        var mine = Parked.Where(p => p.Identity == identity).ToList();
        foreach (var parked in mine)
        {
            if (parked.Taker == body)
                continue;
            parked.Taker = body;
            parked.Delivered = false;
            parked.Order = order;
        }
        // Delivered to this body before and the node forgot it asked, by a restart: it has them
        foreach (var delivered in mine.Where(p => p.Delivered).ToList())
        {
            Parked.Remove(delivered);
            mine.Remove(delivered);
        }
        if (mine.Any(p => p.Order > durable))
        {
            answer.Wait = true;
            return answer;
        }
        foreach (var parked in mine)
            parked.Delivered = true;
        answer.Parked = mine;
        return answer;
    }

    // The body has them: they are gone from here, once
    public int Consume(long identity, long body) =>
        Parked.RemoveAll(p => p.Identity == identity && p.Taker == body && p.Delivered);

    // Offline players whose time is up near a station, whose partition may be offline with the body
    public List<long> Wakes(long now, long limit, int count) =>
        Players
            .Where(p =>
                p.Value.NearStation
                && !p.Value.Woken
                && !online.Contains(p.Key)
                && now - p.Value.Seconds >= limit
            )
            .OrderBy(p => p.Value.Seconds)
            .Take(count)
            .Select(p => p.Key)
            .ToList();

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long Number(string text) => long.Parse(text, CultureInfo.InvariantCulture);

    // One line each: S identity seconds nearStation woken, P identity body credits taker delivered items
    public string Serialize() =>
        string.Join(
            "\n",
            Players
                .Select(p =>
                    $"S {N(p.Key)} {N(p.Value.Seconds)} {(p.Value.NearStation ? 1 : 0)} {(p.Value.Woken ? 1 : 0)}"
                )
                .Concat(
                    Parked.Select(p =>
                        $"P {N(p.Identity)} {N(p.Body)} {N(p.Credits)} {N(p.Taker)} {(p.Delivered ? 1 : 0)} {p.Items}"
                    )
                )
        );

    public static ParkingBook Deserialize(string text)
    {
        var book = new ParkingBook();
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ');
            if (f[0] == "S" && f.Length == 5)
                book.Players[Number(f[1])] = new Seen
                {
                    Seconds = Number(f[2]),
                    NearStation = f[3] == "1",
                    Woken = f[4] == "1",
                };
            else if (f[0] == "P" && f.Length == 7)
                book.Parked.Add(
                    new ParkedCharacter
                    {
                        Identity = Number(f[1]),
                        Body = Number(f[2]),
                        Credits = Number(f[3]),
                        Taker = Number(f[4]),
                        Delivered = f[5] == "1",
                        Items = f[6],
                    }
                );
            else if (line.Length > 0)
                throw new FormatException($"Unknown parking line: {line}");
        }
        return book;
    }
}
