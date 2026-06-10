using PadBridge.Gui.Evdev;

namespace PadBridge.Gui.Core;

public sealed class BridgeConfig
{
    /// <summary>Display name; null for a new config that hasn't been named yet.</summary>
    public string? Name { get; set; }
    public string Device { get; set; } = "";
    /// <summary>Exclusive mode: daemon grabs the controller and forwards it through a remapped clone.</summary>
    public bool Grab { get; set; }
    /// <summary>source code -> output code</summary>
    public Dictionary<int, int> Mappings { get; } = new();
}

public static class ConfigFile
{
    public static string DefaultPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(baseDir, "padbridge", "padbridge.conf");
        }
    }

    public static BridgeConfig Load(string path)
    {
        var config = new BridgeConfig();
        if (!File.Exists(path)) return config;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("# config:"))
            {
                config.Name = line["# config:".Length..].Trim();
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key == "device")
            {
                config.Device = val;
            }
            else if (key == "grab")
            {
                config.Grab = val is "true" or "on" or "yes" or "1";
            }
            else if (key.StartsWith("map ") || key.StartsWith("map\t"))
            {
                var srcName = key[4..].Trim();
                if (EventCodes.NameToCode.TryGetValue(srcName, out var src) &&
                    EventCodes.NameToCode.TryGetValue(val, out var dst))
                    config.Mappings[src] = dst;
            }
        }
        return config;
    }

    public static void Save(string path, BridgeConfig config)
    {
        var lines = new List<string>
        {
            "# PadBridge configuration",
            "# Maps controller buttons to keyboard keys / other buttons.",
            "# Edited by hand or via the PadBridge app; the daemon reloads it automatically.",
            "#",
            "# Format:",
            "#   device = <exact input device name>",
            "#   grab = true|false   (exclusive mode: remap buttons in place)",
            "#   map <source button> = <output key or button>",
            "",
        };
        if (config.Name is { } name) lines.Insert(1, $"# config: {name}");
        lines.AddRange(new[]
        {
            $"device = {config.Device}",
            $"grab = {(config.Grab ? "true" : "false")}",
            "",
        });
        foreach (var (src, dst) in config.Mappings.OrderBy(kv => kv.Key))
            lines.Add($"map {EventCodes.CanonicalName(src)} = {EventCodes.CanonicalName(dst)}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write then rename so the daemon's inotify reload never sees a half-written file.
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }
}
