using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lima.API;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;

namespace OctahedronKiosk
{
    /// <summary>
    /// The kiosk's screens: Storage moves items between the docked ship (or, From hand, the
    /// character) and the ledger the kiosk works on, the operator's or, with the Faction
    /// switch, the faction's; Transfer moves items between the operator's and the faction's
    /// ledger; Market buys and sells for that ledger and lists offers and orders. The server
    /// checks and does every request; the screens only show its answers.
    /// </summary>
    public class KioskApp : TouchApp
    {
        private const int Storage = 0;
        private const int Transfer = 1;
        private const int Market = 2;
        private static readonly string[] Amounts = { "1", "10", "100", "1000", "All" };
        private static readonly string[] Prices =
        {
            "60%",
            "70%",
            "80%",
            "90%",
            "100%",
            "110%",
            "120%",
            "130%",
            "140%",
        };

        private readonly long kiosk;
        private KioskState state;
        private long pending;
        private int ticksToRefresh;
        private bool rebuild;

        private int page = Storage;
        private bool hand;
        private bool faction;
        private string ship = "";
        private string selected = "";
        private int amount;
        private int price = 4;
        private bool listings;
        private string listing = "";

        private readonly Label title;
        private readonly Switch pages;
        private readonly View controls;
        private readonly Switch sourceSwitch;
        private readonly Switch ledgerSwitch;
        private readonly View shipRow;
        private readonly ScrollView list;
        private readonly View actions;
        private readonly View marketActions;
        private readonly Label status;

        // Everything clickable on the screens, for /kiosk screen
        private readonly List<KeyValuePair<string, ElementBase>> fixedClicks =
            new List<KeyValuePair<string, ElementBase>>();
        private readonly List<KeyValuePair<string, ElementBase>> clicks =
            new List<KeyValuePair<string, ElementBase>>();

        public KioskApp(IMyCubeBlock block, IMyTextSurface surface)
            : base(block, surface)
        {
            kiosk = block.EntityId;
            DefaultBg = true;

            var bar = new WindowBar("Octahedron Kiosk");
            AddChild(bar);
            title = new Label("", 0.5f, TextAlignment.LEFT);
            title.Margin = new Vector4(8, 4, 8, 0);
            AddChild(title);

            pages = new Switch(
                new[] { "Storage", "Transfer", "Market" },
                0,
                i =>
                {
                    page = i;
                    Refresh();
                }
            );
            pages.Pixels = new Vector2(0, 28);
            pages.Flex = new Vector2(1, 0);
            AddChild(pages);

            controls = new View(ViewDirection.Row);
            controls.Flex = new Vector2(1, 0);
            controls.Pixels = new Vector2(0, 26);
            controls.Gap = 6;
            controls.Alignment = ViewAlignment.Center;
            AddChild(controls);
            sourceSwitch = new Switch(
                new[] { "Ship", "From hand" },
                0,
                i =>
                {
                    hand = i == 1;
                    Refresh();
                }
            );
            controls.AddChild(sourceSwitch);
            ledgerSwitch = new Switch(
                new[] { "Mine", "Faction" },
                0,
                i =>
                {
                    faction = i == 1;
                    Refresh();
                }
            );
            controls.AddChild(ledgerSwitch);

            shipRow = new View(ViewDirection.Row);
            shipRow.Flex = new Vector2(1, 0);
            shipRow.Pixels = new Vector2(0, 26);
            shipRow.Alignment = ViewAlignment.Center;
            AddChild(shipRow);

            list = new ScrollView();
            list.Flex = new Vector2(1, 1);
            list.Gap = 2;
            AddChild(list);

            actions = new View(ViewDirection.Row);
            actions.Flex = new Vector2(1, 0);
            actions.Pixels = new Vector2(0, 28);
            actions.Gap = 6;
            actions.Alignment = ViewAlignment.Center;
            AddChild(actions);

            marketActions = new View(ViewDirection.Row);
            marketActions.Flex = new Vector2(1, 0);
            marketActions.Pixels = new Vector2(0, 28);
            marketActions.Gap = 6;
            marketActions.Alignment = ViewAlignment.Center;
            AddChild(marketActions);

            status = new Label("Connecting to the station...", 0.45f, TextAlignment.LEFT);
            status.Margin = new Vector4(8, 2, 8, 4);
            AddChild(status);

            TrackSwitch(fixedClicks, "page", pages, "Storage", "Transfer", "Market");
            TrackSwitch(fixedClicks, "source", sourceSwitch, "Ship", "From hand");
            TrackSwitch(fixedClicks, "ledger", ledgerSwitch, "Mine", "Faction");

            KioskSession.Instance.StateReceived += OnState;
            Refresh();
        }

        public void Dispose()
        {
            if (KioskSession.Instance != null)
                KioskSession.Instance.StateReceived -= OnState;
            ForceDispose();
        }

        // Called by the screen script every 10 ticks: the state again 5 s after each answer while
        // someone looks at it. On a cluster an answer can take longer than that; asking again
        // sooner would only get "Still working" and drop the answer on its way.
        public void Tick()
        {
            if (state != null)
                Screen.InteractiveDistance = (float)state.Reach;
            if (rebuild)
            {
                rebuild = false;
                Rebuild();
            }
            if (--ticksToRefresh <= 0 && Screen.IsOnScreen)
                Refresh();
        }

        private string Source => hand ? "hand" : ship;

        private string Units => Amounts[amount] == "All" ? "all" : Amounts[amount];

        private void Refresh() => Request(page == Market ? "market" : "state");

        private void Request(
            string verb,
            string item = "",
            string units = "",
            string price = "",
            string id = "",
            bool? asFaction = null
        )
        {
            ticksToRefresh = 360;
            pending = KioskSession.Instance.Send(
                verb,
                kiosk,
                asFaction ?? faction,
                Source,
                item,
                units,
                price,
                id
            );
        }

        private void OnState(KioskState answer)
        {
            if (answer.Kiosk != kiosk || answer.Sequence != pending)
                return;
            ticksToRefresh = 30;
            status.Text =
                answer.Message.Length > 0 ? answer.Message
                : answer.Ok ? ""
                : "The station refused.";
            status.TextColor = answer.Ok ? Theme.WhiteColor : Color.OrangeRed;
            // A refusal before the station looked (still busy, too fast) has no screens and no
            // reach: keep the last ones, or the touch cursor goes away
            if (answer.Station.Length == 0 && state != null)
                return;
            state = answer;
            if (!hand)
                ship = answer.Source;
            if (!answer.InFaction)
                faction = false;
            rebuild = true;
        }

        // --- the screens, built anew from every answer ---

        // Only from Tick: the callbacks of a click run inside TouchScreenAPI's update of the very
        // lists Rebuild changes, which would end that update with an exception

        private void Rebuild()
        {
            if (state == null)
                return;
            title.Text = state.Station + (state.InFaction ? "  [" + state.FactionTag + "]" : "");
            ledgerSwitch.Enabled = state.InFaction && page != Transfer;
            ledgerSwitch.Index = faction ? 1 : 0;
            sourceSwitch.Enabled = page == Storage;
            sourceSwitch.Index = hand ? 1 : 0;

            clicks.Clear();
            Clear(shipRow);
            Clear(list, 1); // the first child of a ScrollView is its scroll bar
            Clear(actions);
            Clear(marketActions);
            if (page == Market)
            {
                BuildMarket();
                return;
            }
            if (page == Storage)
                BuildShips();
            else if (!state.InFaction)
            {
                list.AddChild(new Label("Join a faction to share items with it.", 0.5f));
                return;
            }
            BuildList();
            BuildActions();
        }

        private void BuildShips()
        {
            if (hand)
            {
                shipRow.AddChild(
                    new Label("Your character's inventory", 0.45f, TextAlignment.LEFT)
                );
                return;
            }
            if (state.Ships.Count == 0)
            {
                shipRow.AddChild(
                    new Label(
                        "Dock a ship of yours at the station, or use From hand.",
                        0.45f,
                        TextAlignment.LEFT
                    )
                );
                return;
            }
            var names = state.Ships.Select(s => s.Value).ToList();
            var selector = new Selector(
                names,
                (i, name) =>
                {
                    ship = state.Ships[i].Key.ToString();
                    Refresh();
                }
            );
            int index = state.Ships.FindIndex(s => s.Key.ToString() == ship);
            Show(selector, names, index < 0 ? 0 : index);
            selector.Flex = new Vector2(1, 0);
            shipRow.AddChild(selector);
            TrackSelector("ship", selector);
        }

        private void BuildList()
        {
            bool transfer = page == Transfer;
            string[] heads = transfer
                ? new[] { "Item", "Mine", "Faction" }
                : new[]
                {
                    "Item",
                    faction ? "Faction's ledger" : "My ledger",
                    hand ? "Carried" : "In the ship",
                };
            list.AddChild(Row(heads, false, null));
            foreach (var row in state.Items)
            {
                long ledger =
                    transfer ? row.Mine
                    : faction ? row.Faction
                    : row.Mine;
                long other = transfer ? row.Faction : row.Source;
                if (ledger == 0 && other == 0)
                    continue;
                var item = row.Item;
                list.AddChild(
                    Row(
                        new[] { row.Name, KioskState.Units(ledger), KioskState.Units(other) },
                        item == selected,
                        () =>
                        {
                            selected = item;
                            rebuild = true;
                        },
                        "row:" + item
                    )
                );
            }
        }

        private View Row(
            string[] cells,
            bool highlighted,
            System.Action onClick,
            string name = null
        )
        {
            View row;
            if (onClick != null)
            {
                var button = new EmptyButton(onClick);
                button.Direction = ViewDirection.Row;
                row = button;
                clicks.Add(new KeyValuePair<string, ElementBase>(name, button));
            }
            else
                row = new View(ViewDirection.Row);
            row.Flex = new Vector2(1, 0);
            row.Pixels = new Vector2(0, 20);
            row.Alignment = ViewAlignment.Center;
            if (highlighted)
            {
                row.UseThemeColors = false;
                row.BgColor = Theme.GetMainColorDarker(7);
            }
            for (int i = 0; i < cells.Length; i++)
            {
                var label = new Label(
                    cells[i],
                    0.45f,
                    i == 0 ? TextAlignment.LEFT : TextAlignment.RIGHT
                );
                label.Flex = new Vector2(i == 0 ? 2 : 1, 0);
                label.AutoEllipsis = LabelEllipsis.Right;
                label.Margin = new Vector4(4, 0, 4, 0);
                row.AddChild(label);
            }
            return row;
        }

        private void BuildActions()
        {
            var choice = new Selector(Amounts.ToList(), (i, text) => amount = i);
            Show(choice, Amounts.ToList(), amount);
            choice.Flex = new Vector2(1, 0);
            actions.AddChild(choice);
            TrackSelector("amount", choice);
            if (page == Storage)
            {
                actions.AddChild(Action("Deposit", () => Request("deposit", selected, Units)));
                actions.AddChild(Action("Withdraw", () => Request("withdraw", selected, Units)));
                return;
            }
            actions.AddChild(Action("Give", () => Request("give", selected, Units)));
            if (state.MayTrade)
                actions.AddChild(Action("Take", () => Request("take", selected, Units)));
            if (state.MaySetMembers)
            {
                var members = new Checkbox(
                    on => Request("members", "", on ? "1" : ""),
                    state.MembersTrade
                );
                actions.AddChild(members);
                clicks.Add(new KeyValuePair<string, ElementBase>("members", members));
                actions.AddChild(new Label("Members trade", 0.4f));
            }
        }

        private Button Action(string text, System.Action onClick)
        {
            var button = new Button(
                text,
                () =>
                {
                    if (selected.Length == 0)
                        status.Text = "Choose an item first.";
                    else
                        onClick();
                }
            );
            button.Flex = new Vector2(0.6f, 0);
            clicks.Add(new KeyValuePair<string, ElementBase>("action:" + text, button));
            return button;
        }

        // --- the market ---

        private void BuildMarket()
        {
            if (!state.HasMarket)
            {
                list.AddChild(new Label("The market is closed.", 0.5f));
                return;
            }
            var views = new Switch(
                new[] { "Prices", "My listings" },
                listings ? 1 : 0,
                i =>
                {
                    listings = i == 1;
                    rebuild = true;
                }
            );
            shipRow.AddChild(views);
            TrackSwitch(clicks, "view", views, "Prices", "My listings");
            long balance = faction ? state.FactionBalance : state.Balance;
            var money = new Label(
                balance < 0
                    ? "No account"
                    : (faction ? "Faction: " : "Account: ")
                        + KioskState.Price(balance * 1000)
                        + " SC",
                0.45f,
                TextAlignment.RIGHT
            );
            money.Flex = new Vector2(1, 0);
            shipRow.AddChild(money);
            if (listings)
                BuildListings();
            else
                BuildPrices();
        }

        private void BuildPrices()
        {
            list.AddChild(
                Row(
                    new[] { "Item", "OCT buys", "OCT sells", "Offer", "Order", "Ledger" },
                    false,
                    null
                )
            );
            foreach (var row in state.Market)
            {
                var item = row.Item;
                var mine = state.Items.FirstOrDefault(i => i.Item == item);
                long ledger =
                    mine == null ? 0
                    : faction ? mine.Faction
                    : mine.Mine;
                list.AddChild(
                    Row(
                        new[]
                        {
                            row.Name + (row.Specialty ? " *" : ""),
                            Money(row.OctBuys),
                            row.OctStock > 0 ? Money(row.OctSells) : "-",
                            row.OfferAmount > 0 ? Money(row.Offer) : "-",
                            row.OrderAmount > 0 ? Money(row.Order) : "-",
                            KioskState.Units(ledger),
                        },
                        item == selected,
                        () =>
                        {
                            selected = item;
                            rebuild = true;
                        },
                        "row:" + item
                    )
                );
            }

            var choice = new Selector(Amounts.ToList(), (i, text) => amount = i);
            Show(choice, Amounts.ToList(), amount);
            choice.Flex = new Vector2(1, 0);
            actions.AddChild(choice);
            TrackSelector("amount", choice);
            actions.AddChild(Action("Buy", () => Request("buy", selected, Units)));
            actions.AddChild(Action("Sell", () => Request("sell", selected, Units)));

            var prices = new Selector(Prices.ToList(), (i, text) => price = i);
            Show(prices, Prices.ToList(), price);
            prices.Flex = new Vector2(1, 0);
            marketActions.AddChild(prices);
            TrackSelector("price", prices);
            marketActions.AddChild(
                Action("Offer", () => Request("offer", selected, Units, Prices[price]))
            );
            marketActions.AddChild(
                Action("Order", () => Request("order", selected, Units, Prices[price]))
            );
        }

        private void BuildListings()
        {
            list.AddChild(Row(new[] { "Listing", "Kind", "Left", "Price", "Whose" }, false, null));
            if (state.Listings.Count == 0)
                list.AddChild(new Label("No listings here.", 0.45f));
            foreach (var l in state.Listings)
            {
                var id = l.Id;
                list.AddChild(
                    Row(
                        new[]
                        {
                            l.Name,
                            l.Kind,
                            KioskState.Units(l.Amount),
                            Money(l.Price),
                            l.Mine ? "Mine" : "Faction",
                        },
                        id == listing,
                        () =>
                        {
                            listing = id;
                            rebuild = true;
                        },
                        "listing:" + id
                    )
                );
            }
            var cancel = new Button(
                "Cancel listing",
                () =>
                {
                    var chosen = state.Listings.FirstOrDefault(l => l.Id == listing);
                    if (chosen == null)
                        status.Text = "Choose a listing first.";
                    else
                        Request("cancel", "", "", "", listing, !chosen.Mine);
                }
            );
            cancel.Flex = new Vector2(1, 0);
            actions.AddChild(cancel);
            clicks.Add(new KeyValuePair<string, ElementBase>("action:Cancel listing", cancel));
        }

        // --- /kiosk screen: what the screen shows, for tests and support ---

        private static void TrackSwitch(
            List<KeyValuePair<string, ElementBase>> into,
            string name,
            Switch control,
            params string[] labels
        )
        {
            for (int i = 0; i < labels.Length; i++)
                into.Add(
                    new KeyValuePair<string, ElementBase>(
                        name + ":" + labels[i],
                        control.Buttons[i]
                    )
                );
        }

        private void TrackSelector(string name, Selector selector)
        {
            // Selector's wrapper has no property for its arrows: its children are arrow, label, arrow
            var children = selector.Children;
            clicks.Add(
                new KeyValuePair<string, ElementBase>(name + ":prev", new EmptyButton(children[0]))
            );
            clicks.Add(
                new KeyValuePair<string, ElementBase>(name + ":next", new EmptyButton(children[2]))
            );
        }

        /// <summary>The frame as text, one record per line: see tests/test_kiosk_screen.py.</summary>
        public void Describe(StringBuilder text, List<MySprite> sprites)
        {
            var cursor = Screen.CursorPosition;
            text.Append(
                $"screen\tpage={page}\thand={hand}\tfaction={faction}\tselected={selected}\tamount={Amounts[amount]}\tprice={Prices[price]}\tlistings={listings}\tlisting={listing}\tstatus={status.Text}\n"
            );
            text.Append($"cursor\t{F(cursor.X)}\t{F(cursor.Y)}\t{Screen.IsOnScreen}\n");
            var viewport = Viewport;
            text.Append(
                $"viewport\t{F(viewport.X)}\t{F(viewport.Y)}\t{F(viewport.Width)}\t{F(viewport.Height)}\t{F(Theme.Scale)}\n"
            );
            foreach (var click in fixedClicks.Concat(clicks))
            {
                var position = click.Value.Position;
                var button = click.Value as EmptyButton;
                // A checkbox has no handler in the API's wrapper: its own area is its hit area
                var hit =
                    button != null
                        ? button.Handler.HitArea
                        : new Vector4(
                            position.X,
                            position.Y,
                            position.X + click.Value.GetBoundaries().X,
                            position.Y + click.Value.GetBoundaries().Y
                        );
                bool over = button != null && button.Handler.Mouse1.IsOver;
                text.Append(
                    $"hit\t{click.Key}\t{F(hit.X)}\t{F(hit.Y)}\t{F(hit.Z)}\t{F(hit.W)}\t{over}\t{F(position.X)}\t{F(position.Y)}\t{IsShown(click.Value)}\n"
                );
            }
            foreach (var sprite in sprites)
            {
                var position = sprite.Position ?? Vector2.Zero;
                var size = sprite.Size ?? Vector2.Zero;
                var color = sprite.Color ?? Color.White;
                text.Append(
                    $"sprite\t{sprite.Type}\t{(sprite.Data ?? "").Replace('\n', ' ')}\t{F(position.X)}\t{F(position.Y)}\t{F(size.X)}\t{F(size.Y)}\t{F(sprite.RotationOrScale)}\t{color.PackedValue}\t{sprite.Alignment}\n"
                );
            }
        }

        private static bool IsShown(ElementBase element)
        {
            for (var e = element; e != null; e = e.Parent)
                if (!e.Enabled)
                    return false;
            return true;
        }

        private static string F(float value) =>
            value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Money(long milli) => KioskState.Price(milli);

        // A new Selector draws its first label until it is clicked, whatever Selected says.
        // Its children are arrow, label, arrow.
        private static void Show(Selector selector, List<string> labels, int index)
        {
            selector.Selected = index;
            new Label(selector.Children[1]).Text = labels[index];
        }

        private static void Clear(ContainerBase view, int keep = 0)
        {
            var children = view.Children;
            for (int i = children.Count - 1; i >= keep; i--)
                view.RemoveChild(children[i]);
        }
    }
}
