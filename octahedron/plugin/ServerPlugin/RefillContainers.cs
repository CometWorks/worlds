using System;
using System.Collections.Generic;
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

namespace ServerPlugin;

// Tops up NPC-owned blocks of a configured name on a timer: a block with an inventory (cargo
// container, reactor, ...) to a minimum stock, a battery to a full charge.
// Player-owned containers are skipped, so players can't name their own after a rule and farm it.
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
                || !grid.BigOwners.Exists(MySession.Static.Players.IdentityIsNpc)
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
        foreach (var item in rule.Items)
        {
            if (item.MinimumAmount <= 0 || !TryGetItem(item.Item, out var id))
                continue;
            RemoveUsedBottles(inventory, id);
            var missing = (MyFixedPoint)item.MinimumAmount - inventory.GetItemAmount(id);
            if (missing <= 0)
                continue;
            var content = MyObjectBuilderSerializer.CreateNewObject(id);
            // New gas bottles would be empty
            if (content is MyObjectBuilder_GasContainerObject bottle)
                bottle.GasLevel = 1f;
            inventory.AddItems(missing, content);
        }
    }

    // Empty or partly used bottles of a rule's item are taken out, so they neither pile up
    // nor count toward the minimum
    private static void RemoveUsedBottles(MyInventory inventory, MyDefinitionId id)
    {
        var items = inventory.GetItems();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (
                items[i].Content is MyObjectBuilder_GasContainerObject bottle
                && bottle.GetObjectId() == id
                && bottle.GasLevel < FullGasLevel
            )
                inventory.RemoveItemsAt(i);
        }
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
