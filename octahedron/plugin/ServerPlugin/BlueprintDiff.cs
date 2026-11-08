using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace ServerPlugin;

/// <summary>
/// A station patch made from two blueprints of a grid (SE-1234): the one the live grid was built
/// from and the new one. Comparing the two blueprints, not the live grid, leaves out what the game
/// changed since (charge, inventories, timers, the plugin's panels), so only the designer's
/// changes become ops. Game-free: tests/patches/PatchTests runs it.
///
/// Blocks are matched by their min cell. A block only in the old blueprint is removed, one only in
/// the new one added. A block of another type or orientation is removed and the new one added. A
/// block of the same type and orientation whose saved settings differ (terminal properties, LCD
/// texts and other components, color, skin, a mod's storage keys) is built again from the new
/// blueprint in place, keeping the live block's inventories, timers, Store offers and the mod
/// storage keys the new blueprint doesn't change. A block whose name, Custom Data or on/off alone
/// changed is set in place. The grid's own settings and mod storage keys are set in place too.
/// </summary>
public static class BlueprintDiff
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // Components whose state the game changes as it runs: never compared, kept from the live block
    public static readonly HashSet<string> LiveComponents =
    [
        "MyInventoryBase",
        "MyTimerComponent",
        "MyItemProducerComponent",
        "MySolarFoodGenerator",
    ];

    private static readonly HashSet<string> StorageComponents =
    [
        "MyModStorageComponent",
        "MyModStorageComponentBase",
    ];

    // Block fields that are identity or run-time state, not the design
    private static readonly HashSet<string> LiveFields =
    [
        "EntityId",
        "Owner",
        "BuiltBy",
        "Min",
        "BlockOrientation",
        "IntegrityPercent",
        "BuildPercent",
        "DeformationRatio",
        "CurrentStoredPower",
        "FilledRatio",
        "PlayerItems",
        "Inventory",
        "ComponentContainer",
        "CustomName",
        "CustomData",
        "Enabled",
    ];

    // Grid settings a patch sets in place
    public static readonly string[] GridSettings =
    [
        "DestructibleBlocks",
        "Editable",
        "Immune",
        "IsRespawnGrid",
    ];

    // Grid settings no patch may change: the plugin and the cluster key on them
    private static readonly string[] FixedGridSettings =
    [
        "DisplayName",
        "IsStatic",
        "GridSizeEnum",
    ];

    public sealed class Block
    {
        public int Index;
        public Cell Min;
        public string Type;
        public string Orientation;
        public string Design;
        public Dictionary<string, string> Storage;
        public string Name;
        public string Data;
        public string Enabled;
    }

    /// <summary>The grid a blueprint file is of: its largest. Subgrids are left out.</summary>
    public static XElement MainGrid(XDocument blueprint) =>
        blueprint
            .Descendants("CubeGrid")
            .OrderByDescending(g => g.Element("CubeBlocks")?.Elements().Count() ?? 0)
            .FirstOrDefault();

    public static List<Block> Blocks(XElement grid) =>
        (grid.Element("CubeBlocks")?.Elements() ?? []).Select(Read).ToList();

    private static Block Read(XElement e, int index)
    {
        var type = (string)e.Attribute(Xsi + "type") ?? "MyObjectBuilder_CubeBlock";
        var orientation = e.Element("BlockOrientation");
        var design = new XElement(e);
        design.RemoveAttributes();
        foreach (
            var field in design
                .Elements()
                .Where(f => LiveFields.Contains(f.Name.LocalName))
                .ToList()
        )
            field.Remove();
        // The design's components, in a stable order, without the live ones and the mod storage
        var components = Components(e)
            .Where(c => !LiveComponents.Contains(c.Type) && !StorageComponents.Contains(c.Type))
            .Select(c => Canon(c.Data))
            .OrderBy(t => t, StringComparer.Ordinal);
        return new Block
        {
            Index = index,
            Min = MinOf(e),
            Type =
                type.Replace("MyObjectBuilder_", "")
                + "/"
                + ((string)e.Element("SubtypeName") ?? ""),
            Orientation =
                $"{(string)orientation?.Attribute("Forward") ?? "Forward"}/{(string)orientation?.Attribute("Up") ?? "Up"}",
            Design = (string)e.Attribute(Xsi + "type") + Canon(design) + string.Concat(components),
            Storage = Storage(e),
            Name = (string)e.Element("CustomName"),
            Data = (string)e.Element("CustomData") ?? "",
            Enabled = (string)e.Element("Enabled"),
        };
    }

    // An element as text that doesn't depend on how the file was written: <X></X> and <X />,
    // indentation, attribute order
    private static string Canon(XElement e) =>
        $"<{e.Name.LocalName}"
        + string.Concat(
            e.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .Select(a => $" {a.Name.LocalName}={a.Value}")
                .OrderBy(t => t, StringComparer.Ordinal)
        )
        + ">"
        + (e.HasElements ? string.Concat(e.Elements().Select(Canon)) : e.Value)
        + "</>";

    private static Cell MinOf(XElement e)
    {
        var min = e.Element("Min");
        int At(string axis) =>
            int.Parse((string)min?.Attribute(axis) ?? "0", CultureInfo.InvariantCulture);
        return new Cell(At("x"), At("y"), At("z"));
    }

    private static IEnumerable<(string Type, XElement Data)> Components(XElement owner) =>
        owner
            .Element("ComponentContainer")
            ?.Element("Components")
            ?.Elements("ComponentData")
            .Select(d => ((string)d.Element("TypeId") ?? "", d))
        ?? [];

    /// <summary>The mod storage keys and values of a block or grid, from all its storage components.</summary>
    public static Dictionary<string, string> Storage(XElement owner)
    {
        var storage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, data) in Components(owner))
            if (StorageComponents.Contains(type))
                foreach (var item in data.Descendants("item"))
                    storage[((string)item.Element("Key") ?? "").Trim()] =
                        (string)item.Element("Value") ?? "";
        return storage;
    }

    /// <summary>The keys whose values differ, with the new value or null where the new one has none.</summary>
    public static Dictionary<string, string> StorageChanges(
        Dictionary<string, string> from,
        Dictionary<string, string> to
    )
    {
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in to)
            if (!from.TryGetValue(key, out var old) || old != value)
                changes[key] = value;
        foreach (var key in from.Keys)
            if (!to.ContainsKey(key))
                changes[key] = null;
        return changes;
    }

    /// <summary>
    /// The ops that turn a grid built from the old blueprint into the new one, removals first so a
    /// new block can take their cells. Null with why in error when a fixed grid setting differs.
    /// </summary>
    public static List<PatchOp> Diff(XElement from, XElement to, out string error)
    {
        error = null;
        foreach (var name in FixedGridSettings)
            if ((string)from.Element(name) != (string)to.Element(name))
            {
                error = $"the new blueprint changes the grid's {name}";
                return null;
            }
        var old = Blocks(from).GroupBy(b => b.Min).ToDictionary(g => g.Key, g => g.First());
        var now = Blocks(to).GroupBy(b => b.Min).ToDictionary(g => g.Key, g => g.First());
        var removals = new List<PatchOp>();
        var rebuilds = new List<PatchOp>();
        var adds = new List<PatchOp>();
        var sets = new List<PatchOp>();

        PatchOp Del(Block b) =>
            new()
            {
                Kind = PatchOpKind.Del,
                At = b.Min,
                Expected = b.Type,
                ExpectedOrientation = b.Orientation,
                Text = $"del:{b.Min}:{b.Type}",
            };

        PatchOp Add(Block b)
        {
            var turn = b.Orientation.Split('/');
            return new PatchOp
            {
                Kind = PatchOpKind.Add,
                At = b.Min,
                Block = b.Type,
                Forward = turn[0],
                Up = turn[1],
                Target = b.Index,
                Text = $"add:{b.Min}:{b.Type}:{turn[0]}:{turn[1]}",
            };
        }

        PatchOp Set(Block b, string key, string value) =>
            new()
            {
                Kind = PatchOpKind.Set,
                At = b.Min,
                Expected = b.Type,
                ExpectedOrientation = b.Orientation,
                Key = key,
                Value = value,
                Text = $"set:{b.Min}:{b.Type}:{key}={value}",
            };

        foreach (var (cell, a) in old)
        {
            if (!now.TryGetValue(cell, out var b))
            {
                removals.Add(Del(a));
                continue;
            }
            if (a.Type != b.Type || a.Orientation != b.Orientation)
            {
                removals.Add(Del(a));
                adds.Add(Add(b));
                continue;
            }
            var storage = StorageChanges(a.Storage, b.Storage);
            // A name can be set in place, not taken away
            if (a.Design != b.Design || storage.Count > 0 || (a.Name != null && b.Name == null))
            {
                rebuilds.Add(
                    new PatchOp
                    {
                        Kind = PatchOpKind.Rebuild,
                        At = a.Min,
                        Expected = a.Type,
                        ExpectedOrientation = a.Orientation,
                        Block = b.Type,
                        Target = b.Index,
                        Storage = storage,
                        Text = $"rebuild:{a.Min}:{a.Type}",
                    }
                );
                continue;
            }
            if (a.Name != b.Name)
                sets.Add(Set(a, "name", b.Name));
            if (a.Data != b.Data)
                sets.Add(Set(a, "data", b.Data));
            if (a.Enabled != b.Enabled && b.Enabled != null)
                sets.Add(Set(a, "enabled", b.Enabled == "true" ? "1" : "0"));
        }
        foreach (var (cell, b) in now)
            if (!old.ContainsKey(cell))
                adds.Add(Add(b));

        var grid = new List<PatchOp>();
        foreach (var name in GridSettings)
        {
            var value = (string)to.Element(name);
            if (value != null && (string)from.Element(name) != value)
                grid.Add(
                    new PatchOp
                    {
                        Kind = PatchOpKind.Grid,
                        Key = name,
                        Value = value,
                        Text = $"grid:{name}={value}",
                    }
                );
        }
        foreach (var (key, value) in StorageChanges(Storage(from), Storage(to)))
            grid.Add(
                new PatchOp
                {
                    Kind = PatchOpKind.Grid,
                    Key = "storage:" + key,
                    Value = value,
                    Text = $"grid:storage:{key}{(value == null ? " removed" : "")}",
                }
            );
        return [.. removals, .. rebuilds, .. adds, .. sets, .. grid];
    }
}
