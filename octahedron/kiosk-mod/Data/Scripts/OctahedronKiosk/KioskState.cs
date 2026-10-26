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

        // The request's form of an amount: plain units, no thousands separators
        public static string Amount(long raw) =>
            ((decimal)raw / Unit).ToString("0.######", CultureInfo.InvariantCulture);
    }
}
