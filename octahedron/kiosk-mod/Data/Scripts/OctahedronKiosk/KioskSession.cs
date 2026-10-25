using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lima.API;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace OctahedronKiosk
{
    /// <summary>
    /// The client side of the station kiosks: sends the player's requests to the Octahedron
    /// server plugin, which owns the ledgers and moves the items, and hands its answers to the
    /// kiosk screens. Admins also get the chat command /kiosk, the same requests as text,
    /// for tests and support.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class KioskSession : MySessionComponentBase
    {
        // The server plugin's Kiosks.Channel and KioskScript
        public const ushort Channel = 40683;
        public const string Script = "Octahedron_Kiosk";

        public static KioskSession Instance;
        public TouchUiKit Api;

        public event Action<KioskState> StateReceived;

        private long sequence;
        private readonly HashSet<long> chatRequests = new HashSet<long>();

        public override void LoadData()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            Instance = this;
            Api = new TouchUiKit();
            Api.Load();
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, OnMessage);
            MyAPIGateway.Utilities.MessageEnteredSender += OnChat;
        }

        protected override void UnloadData()
        {
            if (Instance != this)
                return;
            MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(Channel, OnMessage);
            MyAPIGateway.Utilities.MessageEnteredSender -= OnChat;
            Api?.Unload();
            Api = null;
            Instance = null;
        }

        /// <summary>Sends a request; the answer comes as <see cref="StateReceived"/> with the returned sequence number.</summary>
        public long Send(
            string verb,
            long kiosk,
            bool faction = false,
            string source = "",
            string item = "",
            string amount = "",
            string price = "",
            string listing = ""
        )
        {
            long number = ++sequence;
            var line = string.Join(
                "\t",
                verb,
                number.ToString(CultureInfo.InvariantCulture),
                kiosk.ToString(CultureInfo.InvariantCulture),
                faction ? "1" : "0",
                source ?? "",
                item ?? "",
                amount ?? "",
                price ?? "",
                listing ?? ""
            );
            MyAPIGateway.Multiplayer.SendMessageToServer(Channel, Encoding.UTF8.GetBytes(line));
            return number;
        }

        private void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (!fromServer)
                return;
            try
            {
                var state = KioskState.Parse(Encoding.UTF8.GetString(data));
                StateReceived?.Invoke(state);
                if (chatRequests.Remove(state.Sequence))
                    Print(state);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole($"Octahedron kiosk: {e}");
            }
        }

        // /kiosk <state|deposit|withdraw|give|take|members|market|buy|sell|offer|order|cancel> [faction] [hand|ship=ID]
        //   [item=Type/Subtype] [amount=N|all] [price=SC|N%] [id=listing] [kiosk=ID]
        // The kiosk is the nearest one within 20 m unless given.
        private void OnChat(ulong sender, string text, ref bool sendToOthers)
        {
            if (!text.StartsWith("/kiosk", StringComparison.OrdinalIgnoreCase))
                return;
            sendToOthers = false;
            if (MyAPIGateway.Session.PromoteLevel < MyPromoteLevel.Admin)
            {
                MyAPIGateway.Utilities.ShowMessage("Kiosk", "Use the kiosk's touch screen.");
                return;
            }
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .ToList();
            string verb = words.Count > 0 ? words[0] : "state";
            bool faction = false;
            string source = "",
                item = "",
                amount = "",
                price = "",
                listing = "";
            long kiosk = 0;
            foreach (var word in words.Skip(1))
            {
                if (word == "faction")
                    faction = true;
                else if (word == "hand")
                    source = "hand";
                else if (word.StartsWith("ship="))
                    source = word.Substring(5);
                else if (word.StartsWith("item="))
                    item = word.Substring(5);
                else if (word.StartsWith("amount="))
                    amount = word.Substring(7);
                else if (word.StartsWith("price="))
                    price = word.Substring(6);
                else if (word.StartsWith("id="))
                    listing = word.Substring(3);
                else if (word.StartsWith("kiosk="))
                    long.TryParse(word.Substring(6), out kiosk);
            }
            if (kiosk == 0)
                kiosk = NearestKiosk();
            if (kiosk == 0)
            {
                MyAPIGateway.Utilities.ShowMessage("Kiosk", "No kiosk near.");
                return;
            }
            chatRequests.Add(Send(verb, kiosk, faction, source, item, amount, price, listing));
        }

        // A kiosk is a text panel showing this mod's screen; the server plugin selects it
        public static long NearestKiosk(double range = 20)
        {
            var player = MyAPIGateway.Session.Player;
            if (player == null)
                return 0;
            var position = player.GetPosition();
            var sphere = new BoundingSphereD(position, range);
            long nearest = 0;
            double best = double.MaxValue;
            var blocks = new List<IMySlimBlock>();
            foreach (var entity in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere))
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null)
                    continue;
                blocks.Clear();
                grid.GetBlocks(blocks, b => (b.FatBlock as IMyTextPanel)?.Script == Script);
                foreach (var block in blocks)
                {
                    double distance = Vector3D.Distance(block.FatBlock.GetPosition(), position);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = block.FatBlock.EntityId;
                    }
                }
            }
            return nearest;
        }

        // The answer as chat lines, one per record, for the /kiosk command
        private static void Print(KioskState state)
        {
            Action<string> say = line => MyAPIGateway.Utilities.ShowMessage("Kiosk", line);
            say(
                (state.Ok ? "ok" : "refused")
                    + (state.Message.Length > 0 ? ": " + state.Message : "")
            );
            say(
                $"kiosk {state.Kiosk} at {state.Station}, reach {state.Reach.ToString(CultureInfo.InvariantCulture)} m, source {(state.Source.Length > 0 ? state.Source : "none")}"
            );
            if (state.InFaction)
                say(
                    $"faction {state.FactionTag} trade={(state.MayTrade ? 1 : 0)} members={(state.MembersTrade ? 1 : 0)} set={(state.MaySetMembers ? 1 : 0)}"
                );
            foreach (var ship in state.Ships)
                say($"ship {ship.Key} {ship.Value}");
            foreach (var row in state.Items)
                say(
                    $"item {row.Item} mine={KioskState.Amount(row.Mine)} faction={KioskState.Amount(row.Faction)} source={KioskState.Amount(row.Source)}"
                );
            if (state.HasMarket)
                say($"balance mine={state.Balance} faction={state.FactionBalance}");
            // Only the rows with something to trade, so the chat stays short
            foreach (var row in state.Market)
                if (row.OctStock > 0 || row.OfferAmount > 0 || row.OrderAmount > 0)
                    say(
                        $"market {row.Item} buys={row.OctBuys} sells={row.OctSells} stock={KioskState.Amount(row.OctStock)} "
                            + $"offer={row.Offer}x{KioskState.Amount(row.OfferAmount)} order={row.Order}x{KioskState.Amount(row.OrderAmount)} special={(row.Specialty ? 1 : 0)}"
                    );
            foreach (var l in state.Listings)
                say(
                    $"listing {l.Id} {l.Kind} {l.Item} amount={KioskState.Amount(l.Amount)} price={l.Price} owner={(l.Mine ? "mine" : "faction")}"
                );
        }
    }
}
