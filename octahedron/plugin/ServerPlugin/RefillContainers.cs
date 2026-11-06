using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace ServerPlugin;

// Tops up NPC-owned blocks of a configured name on a timer: a block with an inventory (cargo
// container, reactor, ...) to a minimum stock, a battery to a full charge. An item with several
// ids counts them together, and each one missing is one of them at random.
// Player-owned containers are skipped, so players can't name their own after a rule and farm it.
// On a cluster a grid this node doesn't simulate is skipped too: a frozen one, or one still being
// imported, is compared to its source by the handover gate, and a refill there fails the handover
// (SE-1155).
internal sealed class RefillContainers
{
    private const string TypePrefix = "MyObjectBuilder_";
    private const float FullGasLevel = 0.999f;

    private readonly Dictionary<int, DateTime> nextRefill = new();
    private readonly HashSet<string> reportedInvalidItems = new();
    private readonly List<RefillContainer> due = new();
    private DateTime lastCheck;

    public void Update()
    {
        var now = DateTime.UtcNow;
        if ((now - lastCheck).TotalSeconds < 1)
            return;
        lastCheck = now;

        var rules = Plugin.Config.RefillContainers;
        if (rules == null || rules.Count == 0 || !Sync.IsServer || MySession.Static?.Ready != true)
            return;

        due.Clear();
        for (var i = 0; i < rules.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(rules[i].ContainerName))
                continue;
            if (nextRefill.TryGetValue(i, out var next) && now < next)
                continue;
            nextRefill[i] = now.AddSeconds(Math.Max(1, rules[i].IntervalSeconds));
            due.Add(rules[i]);
        }

        if (due.Count == 0)
            return;

        foreach (var entity in MyEntities.GetEntities())
        {
            if (
                entity is not MyCubeGrid grid
                || grid.MarkedForClose
                || !Plugin.Simulated(grid)
                || !ShipyardZoneWelding.Owners(grid).Any(MySession.Static.Players.IdentityIsNpc)
            )
                continue;
            foreach (var block in grid.GetFatBlocks<MyTerminalBlock>())
            {
                if (block.MarkedForClose || !MySession.Static.Players.IdentityIsNpc(block.OwnerId))
                    continue;
                var name = block.CustomName?.ToString().Trim();
                foreach (var rule in due)
                {
                    if (
                        string.Equals(
                            name,
                            rule.ContainerName.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        Refill(block, rule);
                }
            }
        }
    }

    private void Refill(MyTerminalBlock block, RefillContainer rule)
    {
        if (block is MyBatteryBlock battery)
            battery.CurrentStoredPower = battery.MaxStoredPower;
        if (rule.Items == null || block.InventoryCount == 0)
            return;
        var inventory = block.GetInventory();
        RemoveUsedBottles(inventory);
        foreach (var item in rule.Items)
        {
            if (item.MinimumAmount <= 0)
                continue;
            var ids = ParseItems(item.Item);
            if (ids.Count == 0)
                continue;
            MyFixedPoint missing = item.MinimumAmount;
            foreach (var id in ids)
                missing -= inventory.GetItemAmount(id);
            if (missing <= 0)
                continue;
            if (ids.Count == 1)
                AddItems(inventory, ids[0], missing);
            else
                for (var i = 0; i < (int)MyFixedPoint.Ceiling(missing); i++)
                    AddItems(inventory, ids[MyUtils.GetRandomInt(ids.Count)], 1);
        }
    }

    private static void AddItems(MyInventory inventory, MyDefinitionId id, MyFixedPoint amount)
    {
        var content = MyObjectBuilderSerializer.CreateNewObject(id);
        // New gas bottles would be empty
        if (content is MyObjectBuilder_GasContainerObject bottle)
            bottle.GasLevel = 1f;
        inventory.AddItems(amount, content);
    }

    // Every oxygen or hydrogen bottle that isn't full is taken out before the top-up, so a player
    // can leave a used one and take a full one, and used ones neither pile up nor count toward
    // the minimum
    private static void RemoveUsedBottles(MyInventory inventory)
    {
        var items = inventory.GetItems();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (
                items[i].Content is MyObjectBuilder_GasContainerObject bottle
                && bottle.GasLevel < FullGasLevel
            )
                inventory.RemoveItemsAt(i);
        }
    }

    private List<MyDefinitionId> ParseItems(string text)
    {
        var ids = new List<MyDefinitionId>();
        foreach (var part in (text ?? "").Split(','))
        {
            if (TryGetItem(part, out var id))
                ids.Add(id);
        }
        return ids;
    }

    private bool TryGetItem(string text, out MyDefinitionId id)
    {
        text = text?.Trim() ?? "";
        var full = text.StartsWith(TypePrefix) ? text : TypePrefix + text;
        if (
            MyDefinitionId.TryParse(full, out id)
            && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out _)
        )
            return true;
        if (reportedInvalidItems.Add(text))
            Plugin.Log.Warning(
                $"Refill containers: unknown item '{text}', expected Type/Subtype such as PhysicalGunObject/Welder4Item"
            );
        return false;
    }
}
