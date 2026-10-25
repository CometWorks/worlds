using System.Collections.Generic;
using System.Globalization;

namespace OctahedronKiosk
{
    public class KioskItemRow
    {
        public string Item = "";
        public string Name = "";
        public long Mine;
        public long Faction;
        public long Source;
    }

    /// <summary>One item of the station's market; prices in milli-SC per unit, amounts raw.</summary>
    public class KioskMarketRow
    {
        public string Item = "";
        public string Name = "";
        public long OctBuys;
        public long OctSells;
        public long OctStock;
        public long Offer;
        public long OfferAmount;
        public long Order;
        public long OrderAmount;
        public bool Specialty;
    }

    /// <summary>A listing of the operator or their faction.</summary>
    public class KioskListing
    {
        public string Id = "";
        public string Kind = "";
        public string Item = "";
        public string Name = "";
        public long Amount;
        public long Price;
        public bool Mine;
        public long Expires;
    }

    /// <summary>
    /// The server's answer to a kiosk request, as KioskReply of the Octahedron server plugin
    /// writes it (octahedron/plugin/ServerPlugin/Kiosks/KioskProtocol.cs): one record per line,
    /// tab-separated, amounts as millionths of a unit.
    /// </summary>
    public class KioskState
    {
        public const long Unit = 1000000;

        public long Sequence;
        public bool Ok;
        public string Message = "";
        public long Kiosk;
        public string Station = "";
        public double Reach;
        public string FactionTag;
        public bool MayTrade;
        public bool MembersTrade;
        public bool MaySetMembers;
        public List<KeyValuePair<long, string>> Ships = new List<KeyValuePair<long, string>>();
        public string Source = "";
        public List<KioskItemRow> Items = new List<KioskItemRow>();

        // After a market request: the station's market, the listings, the balances (-1: none)
        public bool HasMarket;
        public List<KioskMarketRow> Market = new List<KioskMarketRow>();
        public List<KioskListing> Listings = new List<KioskListing>();
        public long Balance = -1;
        public long FactionBalance = -1;

        public bool InFaction => FactionTag != null;

        public static KioskState Parse(string text)
        {
            var state = new KioskState();
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "reply":
                        if (f.Length < 4)
                            break;
                        state.Sequence = Long(f[1]);
                        state.Ok = f[2] == "1";
                        state.Message = f[3];
                        break;
                    case "kiosk":
                        if (f.Length < 4)
                            break;
                        state.Kiosk = Long(f[1]);
                        state.Station = f[2];
                        double.TryParse(
                            f[3],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out state.Reach
                        );
                        break;
                    case "faction":
                        if (f.Length < 5)
                            break;
                        state.FactionTag = f[1];
                        state.MayTrade = f[2] == "1";
                        state.MembersTrade = f[3] == "1";
                        state.MaySetMembers = f[4] == "1";
                        break;
                    case "ship":
                        if (f.Length >= 3)
                            state.Ships.Add(new KeyValuePair<long, string>(Long(f[1]), f[2]));
                        break;
                    case "source":
                        if (f.Length >= 2)
                            state.Source = f[1];
                        break;
                    case "item":
                        if (f.Length >= 6)
                            state.Items.Add(
                                new KioskItemRow
                                {
                                    Item = f[1],
                                    Name = f[2],
                                    Mine = Long(f[3]),
                                    Faction = Long(f[4]),
                                    Source = Long(f[5]),
                                }
                            );
                        break;
                    case "market":
                        if (f.Length >= 11)
                            state.Market.Add(
                                new KioskMarketRow
                                {
                                    Item = f[1],
                                    Name = f[2],
                                    OctBuys = Long(f[3]),
                                    OctSells = Long(f[4]),
                                    OctStock = Long(f[5]),
                                    Offer = Long(f[6]),
                                    OfferAmount = Long(f[7]),
                                    Order = Long(f[8]),
                                    OrderAmount = Long(f[9]),
                                    Specialty = f[10] == "1",
                                }
                            );
                        break;
                    case "listing":
                        if (f.Length >= 9)
                            state.Listings.Add(
                                new KioskListing
                                {
                                    Id = f[1],
                                    Kind = f[2],
                                    Item = f[3],
                                    Name = f[4],
                                    Amount = Long(f[5]),
                                    Price = Long(f[6]),
                                    Mine = f[7] == "mine",
                                    Expires = Long(f[8]),
                                }
                            );
                        break;
                    case "balance":
                        if (f.Length >= 3)
                        {
                            state.HasMarket = true;
                            state.Balance = Long(f[1]);
                            state.FactionBalance = Long(f[2]);
                        }
                        break;
                }
            }
            return state;
        }

        private static long Long(string text)
        {
            long value;
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return value;
        }

        public static string Units(long raw) =>
            ((decimal)raw / Unit).ToString("#,0.##", CultureInfo.InvariantCulture);

        /// <summary>A price in milli-SC per unit as SC.</summary>
        public static string Price(long milli) =>
            ((decimal)milli / 1000).ToString("#,0.##", CultureInfo.InvariantCulture);

        // The request's form of an amount: plain units, no thousands separators
        public static string Amount(long raw) =>
            ((decimal)raw / Unit).ToString("0.######", CultureInfo.InvariantCulture);
    }
}
