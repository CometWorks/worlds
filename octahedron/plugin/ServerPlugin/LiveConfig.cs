using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using PluginSdk.Config;
using PluginSdk.Storage;

namespace ServerPlugin;

/// <summary>
/// Option values an admin set on the running server with !oct config set (SE-1474), over the ones
/// the server started with. They live in live.cfg in the plugin's shared storage directory, which
/// every process of a cluster sees and Quasar keeps across restarts, keep-world updates and backups.
/// Every process reads the file every 15 s and applies it to its config, so the World Authority and
/// all nodes, hot spares too, run the same values without a restart. The file has the elements of
/// octahedron.cfg, so a tuned value can be copied back into the worlds repo as it is.
/// </summary>
internal static class LiveConfig
{
    public const string FileName = "live.cfg";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private static DateTime nextRead;
    private static string readText;
    private static OctahedronConfig appliedTo;
    private static Dictionary<string, string> overrides = new();

    // What each overridden option had before, to go back to on a reset
    private static readonly Dictionary<string, string> deployed = new();
    private static string directory;
    private static bool unavailable;

    private static readonly Dictionary<string, PropertyInfo> Options = typeof(OctahedronConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p =>
            p.CanWrite
            && p.GetCustomAttribute<ConfigOptionAttribute>() != null
            && (
                p.PropertyType.IsEnum
                || p.PropertyType == typeof(bool)
                || p.PropertyType == typeof(int)
                || p.PropertyType == typeof(long)
                || p.PropertyType == typeof(float)
                || p.PropertyType == typeof(double)
                || p.PropertyType == typeof(string)
            )
        )
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private static string FilePath
    {
        get
        {
            if (directory == null && !unavailable)
            {
                try
                {
                    directory = PluginStorage.GetSharedDirectory(Plugin.Id);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException)
                {
                    unavailable = true;
                    Plugin.Log.Warning($"Live config: no shared storage ({e.Message}), off");
                }
            }
            return directory == null ? null : Path.Combine(directory, FileName);
        }
    }

    /// <summary>Reads live.cfg every 15 s and applies it, and at once after a config reload.</summary>
    public static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < nextRead && ReferenceEquals(appliedTo, Plugin.Config))
            return;
        nextRead = now + Interval;
        var path = FilePath;
        if (path == null)
            return;
        string text;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException e)
        {
            Plugin.Log.Warning($"Live config: {path} unreadable: {e.Message}");
            return;
        }
        if (text != readText)
        {
            try
            {
                overrides = Parse(text);
                readText = text;
            }
            catch (XmlException e)
            {
                Plugin.Log.Error(
                    $"Live config: {path} broken, the values before stay: {e.Message}"
                );
                return;
            }
        }
        Apply();
    }

    // The overrides onto the config. Each change is logged, a value given back on a reset too.
    private static void Apply()
    {
        var config = Plugin.Config;
        if (!ReferenceEquals(appliedTo, config))
        {
            // A reloaded config starts from the file's values
            appliedTo = config;
            deployed.Clear();
        }
        foreach (var name in deployed.Keys.Where(n => !overrides.ContainsKey(n)).ToList())
        {
            Set(config, name, deployed[name]);
            Plugin.Log.Info($"Live config: {name} back to {deployed[name]}");
            deployed.Remove(name);
        }
        foreach (var pair in overrides)
        {
            string current = Text(config, pair.Key);
            if (current == pair.Value)
                continue;
            if (!deployed.ContainsKey(pair.Key))
                deployed[pair.Key] = current;
            Set(config, pair.Key, pair.Value);
            Plugin.Log.Info($"Live config: {pair.Key} = {pair.Value}");
        }
    }

    // --- commands ---

    public static string Show(string option)
    {
        if (FilePath == null)
            return "Live config: this server has no shared storage.";
        Update();
        if (option == null)
            return overrides.Count == 0
                ? "No live config values: the server runs its deployed config."
                : "Live config values, the deployed value in brackets:\n"
                    + string.Join(
                        "\n",
                        overrides
                            .OrderBy(p => p.Key)
                            .Select(p => $"{p.Key} = {p.Value} [{Deployed(p.Key)}]")
                    );
        if (!Options.TryGetValue(option, out var property))
            return Unknown(option);
        string name = property.Name;
        string value = Text(Plugin.Config, name);
        return overrides.ContainsKey(name)
            ? $"{name} = {value}, set live; deployed: {Deployed(name)}"
            : $"{name} = {value}";
    }

    public static string Set(string option, string value)
    {
        if (!Options.TryGetValue(option, out var property))
            return Unknown(option);
        string name = property.Name;
        if (!TryCanonical(property, value, out string canonical, out string error))
            return $"{name}: {error}";
        return Write(values => values[name] = canonical)
            ?? $"{name} = {canonical} on every process within 15 s, until !oct config reset {name}. "
                + "Copy it into octahedron.cfg to keep it after a wipe.";
    }

    public static string Reset(string option)
    {
        if (option.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Write(values => values.Clear())
                ?? "Every live config value goes back to the deployed one within 15 s.";
        if (!Options.TryGetValue(option, out var property))
            return Unknown(option);
        string name = property.Name;
        Update();
        if (!overrides.ContainsKey(name))
            return $"{name} has no live value.";
        return Write(values => values.Remove(name))
            ?? $"{name} goes back to {Deployed(name)} within 15 s.";
    }

    // Reads, changes and writes live.cfg, then applies it here at once. Null when that worked.
    private static string Write(Action<Dictionary<string, string>> change)
    {
        var path = FilePath;
        if (path == null)
            return "Live config: this server has no shared storage.";
        try
        {
            var values = Parse(File.Exists(path) ? File.ReadAllText(path) : "");
            change(values);
            // Into place by a rename: a process reading meanwhile gets the old file or the new one
            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, Format(values));
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
            Plugin.Log.Info(
                $"Live config written: {string.Join(", ", values.Select(p => $"{p.Key}={p.Value}"))}"
            );
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return $"Live config: {path}: {e.Message}";
        }
        nextRead = DateTime.MinValue;
        Update();
        return null;
    }

    // --- the file and its values ---

    internal static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(text))
            return values;
        foreach (var element in XDocument.Parse(text).Root!.Elements())
        {
            if (!Options.TryGetValue(element.Name.LocalName, out var property))
                Plugin.Log.Warning(
                    $"Live config: {element.Name.LocalName} can't be set live, skipped"
                );
            else if (!TryCanonical(property, element.Value, out string canonical, out string error))
                Plugin.Log.Warning($"Live config: {property.Name}: {error}, skipped");
            else
                values[property.Name] = canonical;
        }
        return values;
    }

    internal static string Format(Dictionary<string, string> values) =>
        new XDocument(
            new XElement(
                nameof(OctahedronConfig),
                values.OrderBy(p => p.Key).Select(p => new XElement(p.Key, p.Value))
            )
        ) + "\n";

    private static string Deployed(string name) =>
        deployed.TryGetValue(name, out string value) ? value : Text(Plugin.Config, name);

    private static string Unknown(string option) =>
        $"No option {option} to set live: only single values (number, text, true or false), named as in octahedron.cfg.";

    private static string Text(OctahedronConfig config, string name) =>
        Canonical(Options[name].GetValue(config));

    private static void Set(OctahedronConfig config, string name, string text) =>
        Options[name].SetValue(config, Value(Options[name].PropertyType, text));

    private static string Canonical(object value) =>
        value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private static object Value(Type type, string text)
    {
        if (type == typeof(string))
            return text;
        text = text.Trim();
        if (type.IsEnum)
            return Enum.Parse(type, text, true);
        if (type == typeof(bool))
            return bool.Parse(text);
        return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
    }

    // The value as the config file has it, refused out of the option's range
    private static bool TryCanonical(
        PropertyInfo property,
        string text,
        out string canonical,
        out string error
    )
    {
        canonical = null;
        error = null;
        object value;
        try
        {
            value = Value(property.PropertyType, text ?? "");
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            error = $"not a {property.PropertyType.Name}: {text}";
            return false;
        }
        if (value is int or long or float or double)
        {
            var attribute = property.GetCustomAttribute<ConfigOptionAttribute>();
            object min = attribute.GetType().GetProperty("Min")?.GetValue(attribute);
            object max = attribute.GetType().GetProperty("Max")?.GetValue(attribute);
            double x = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (
                min != null && x < Convert.ToDouble(min, CultureInfo.InvariantCulture)
                || max != null && x > Convert.ToDouble(max, CultureInfo.InvariantCulture)
            )
            {
                error = $"{text} is out of its range, {Canonical(min)} to {Canonical(max)}";
                return false;
            }
        }
        canonical = Canonical(value);
        return true;
    }
}
