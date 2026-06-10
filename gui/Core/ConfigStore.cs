namespace PadBridge.Gui.Core;

/// <summary>
/// Library of named configs in ~/.config/padbridge/configs/*.conf.
/// The daemon only ever reads the "active" file (padbridge.conf); selecting
/// or saving a config copies it there, so hand-editing either file works.
/// </summary>
public sealed class ConfigStore
{
    public static string ConfigsDir =>
        Path.Combine(Path.GetDirectoryName(ConfigFile.DefaultPath)!, "configs");

    public static string PathFor(string name) => Path.Combine(ConfigsDir, name + ".conf");

    public List<string> ListNames() =>
        Directory.Exists(ConfigsDir)
            ? Directory.GetFiles(ConfigsDir, "*.conf")
                .Select(p => Path.GetFileNameWithoutExtension(p)!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

    public BridgeConfig Load(string name)
    {
        var config = ConfigFile.Load(PathFor(name));
        config.Name = name;   // the filename is authoritative
        return config;
    }

    /// <summary>Make this config the one the daemon runs.</summary>
    public void Activate(BridgeConfig config) =>
        ConfigFile.Save(ConfigFile.DefaultPath, config);

    /// <summary>Write to the library and activate. Config must be named.</summary>
    public void Save(BridgeConfig config)
    {
        ConfigFile.Save(PathFor(config.Name!), config);
        Activate(config);
    }

    /// <summary>Guarantee at least one config exists, importing any
    /// pre-existing active file as "Default".</summary>
    public void EnsureInitialized()
    {
        Directory.CreateDirectory(ConfigsDir);
        if (ListNames().Count > 0) return;
        var config = ConfigFile.Load(ConfigFile.DefaultPath);
        config.Name = "Default";
        Save(config);
    }
}
