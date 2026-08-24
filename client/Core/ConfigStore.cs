namespace AgVpsUnlock.Core;

/// <summary>
/// Persisted client settings. Lives under ProgramData so it survives reinstalls
/// of the app and stays readable for diagnostics.
/// </summary>
public sealed class ConfigStore
{
    public string VpsIp { get; set; } = "";
    /// <summary>Shared relay secret from setup-vps.sh (`status` prints it).
    /// Empty when the server runs unlocked - no knock is sent then.</summary>
    public string VpsToken { get; set; } = "";
    /// <summary>Extra hostnames to route, when the upstream product grows new
    /// endpoints. The core four are always included.</summary>
    public List<string> ExtraHosts { get; set; } = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "AgVpsUnlock");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static ConfigStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return System.Text.Json.JsonSerializer.Deserialize<ConfigStore>(File.ReadAllText(FilePath))
                       ?? new ConfigStore();
        }
        catch
        {
            // A corrupt config must not brick the app; defaults are fine.
        }
        return new ConfigStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath,
            System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    /// <summary>All routed hostnames: the four Antigravity endpoints plus extras.</summary>
    public IReadOnlyList<string> RoutedHosts()
    {
        var core = new[]
        {
            "cloudcode-pa.googleapis.com",
            "daily-cloudcode-pa.googleapis.com",
            "generativelanguage.googleapis.com",
            "antigravity-unleash.goog"
        };
        return core.Concat(ExtraHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)).ToList();
    }
}
