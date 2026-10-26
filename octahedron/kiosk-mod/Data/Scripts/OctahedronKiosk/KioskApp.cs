using System.Collections.Generic;
using System.Linq;
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
    /// ledger. The server checks and does every request; the screens only show its answers.
    /// </summary>
    public class KioskApp : TouchApp
    {
        private const int Storage = 0;
        private const int Transfer = 1;
        private const int Market = 2;
        private static readonly string[] Amounts = { "1", "10", "100", "1000", "All" };

        private readonly long kiosk;
        private KioskState state;
        private long pending;
        private int ticksToRefresh;

        private int page = Storage;
        private bool hand;
        private bool faction;
        private string ship = "";
        private string selected = "";
        private int amount;

        private readonly Label title;
        private readonly Switch pages;
        private readonly View controls;
        private readonly Switch sourceSwitch;
        private readonly Switch ledgerSwitch;
        private readonly View shipRow;
        private readonly ScrollView list;
        private readonly View actions;
        private readonly Label status;

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
                    Rebuild();
                }
            );
            pages.Pixels = new Vector2(0, 28);
            pages.Flex = new Vector2(1, 0);
            AddChild(pages);

            controls = new View(ViewDirection.Row);
            controls.Flex = new Vector2(1, 0);
            controls.Pixels = new Vector2(0, 26);
            controls.Gap = 6;
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
            AddChild(shipRow);

            list = new ScrollView();
            list.Flex = new Vector2(1, 1);
            list.Gap = 2;
            AddChild(list);

            actions = new View(ViewDirection.Row);
            actions.Flex = new Vector2(1, 0);
            actions.Pixels = new Vector2(0, 28);
            actions.Gap = 6;
            AddChild(actions);

            status = new Label("Connecting to the station...", 0.45f, TextAlignment.LEFT);
            status.Margin = new Vector4(8, 2, 8, 4);
            AddChild(status);

            KioskSession.Instance.StateReceived += OnState;
            Refresh();
        }

        public void Dispose()
        {
            if (KioskSession.Instance != null)
                KioskSession.Instance.StateReceived -= OnState;
            ForceDispose();
        }

        // Called by the screen script every 10 ticks: the state again every 5 s while someone looks at it
        public void Tick()
        {
            if (state != null)
                Screen.InteractiveDistance = (float)state.Reach;
            if (--ticksToRefresh <= 0 && Screen.IsOnScreen)
                Refresh();
        }

        private string Source => hand ? "hand" : ship;

        private void Refresh() => Request("state");

        private void Request(string verb, string item = "", string units = "")
        {
            ticksToRefresh = 30;
            pending = KioskSession.Instance.Send(verb, kiosk, faction, Source, item, units);
        }

        private void OnState(KioskState answer)
        {
            if (answer.Kiosk != kiosk || answer.Sequence != pending)
                return;
            state = answer;
            if (!hand)
                ship = answer.Source;
            if (!answer.InFaction)
                faction = false;
            status.Text =
                answer.Message.Length > 0 ? answer.Message
                : answer.Ok ? ""
                : "The station refused.";
            status.TextColor = answer.Ok ? Theme.WhiteColor : Color.OrangeRed;
            Rebuild();
        }

        // --- the screens, built anew from every answer ---

        private void Rebuild()
        {
            if (state == null)
                return;
            title.Text = state.Station + (state.InFaction ? "  [" + state.FactionTag + "]" : "");
            ledgerSwitch.Enabled = state.InFaction && page == Storage;
            ledgerSwitch.Index = faction ? 1 : 0;
            sourceSwitch.Enabled = page == Storage;
            sourceSwitch.Index = hand ? 1 : 0;

            Clear(shipRow);
            Clear(list);
            Clear(actions);
            if (page == Market)
            {
                list.AddChild(new Label("The market opens here soon.", 0.5f));
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
            selector.Selected = index < 0 ? 0 : index;
            selector.Flex = new Vector2(1, 1);
            shipRow.AddChild(selector);
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
                            Rebuild();
                        }
                    )
                );
            }
        }

        private View Row(string[] cells, bool highlighted, System.Action onClick)
        {
            View row;
            if (onClick != null)
            {
                var button = new EmptyButton(onClick);
                button.Direction = ViewDirection.Row;
                row = button;
            }
            else
                row = new View(ViewDirection.Row);
            row.Flex = new Vector2(1, 0);
            row.Pixels = new Vector2(0, 20);
            if (highlighted)
            {
                row.UseThemeColors = false;
                row.BgColor = Theme.GetMainColorDarker(4);
            }
            for (int i = 0; i < cells.Length; i++)
            {
                var label = new Label(
                    cells[i],
                    0.45f,
                    i == 0 ? TextAlignment.LEFT : TextAlignment.RIGHT
                );
                label.Flex = new Vector2(i == 0 ? 2 : 1, 1);
                label.AutoEllipsis = LabelEllipsis.Right;
                label.Margin = new Vector4(4, 0, 4, 0);
                row.AddChild(label);
            }
            return row;
        }

        private void BuildActions()
        {
            var choice = new Selector(Amounts.ToList(), (i, text) => amount = i);
            choice.Selected = amount;
            choice.Flex = new Vector2(1, 1);
            actions.AddChild(choice);
            string units = Amounts[amount] == "All" ? "all" : Amounts[amount];
            if (page == Storage)
            {
                actions.AddChild(Action("Deposit", () => Request("deposit", selected, units)));
                actions.AddChild(Action("Withdraw", () => Request("withdraw", selected, units)));
                return;
            }
            actions.AddChild(Action("Give", () => Request("give", selected, units)));
            if (state.MayTrade)
                actions.AddChild(Action("Take", () => Request("take", selected, units)));
            if (state.MaySetMembers)
            {
                var members = new Checkbox(
                    on => Request("members", "", on ? "1" : ""),
                    state.MembersTrade
                );
                actions.AddChild(members);
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
            button.Flex = new Vector2(0.6f, 1);
            return button;
        }

        private static void Clear(ContainerBase view)
        {
            var children = view.Children;
            for (int i = children.Count - 1; i >= 0; i--)
                view.RemoveChild(children[i]);
        }
    }
}
