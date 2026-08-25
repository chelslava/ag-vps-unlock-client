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
    /// <summary>Custom Antigravity directory or executable paths provided by the user.</summary>
    public List<string> CustomInstallPaths { get; set; } = new();

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

    /// <summary>All routed hostnames: the Antigravity endpoints plus extras.</summary>
    public IReadOnlyList<string> RoutedHosts()
    {
        var core = new[]
        {
            "cloudcode-pa.googleapis.com",
            "daily-cloudcode-pa.googleapis.com",
            "generativelanguage.googleapis.com",
            "antigravity-unleash.goog",
            // Called by Antigravity's language_server outside the core four.
            // Unrouted, they leave with the client's real region IP and Google
            // answers 400 "User location is not supported" (FAILED_PRECONDITION).
            "www.googleapis.com",
            "oauth2.googleapis.com",
            "cloudaicompanion.googleapis.com",
            // Real API hosts of the cloudaicompanion backend follow the same
            // "-pa" pattern as cloudcode-pa; X-Cloudaicompanion-Trace-Id
            // responses come from this family.
            "cloudaicompanion-pa.googleapis.com",
            "daily-cloudaicompanion-pa.googleapis.com",
            "aiplatform.googleapis.com",
            // Service endpoints of the Electron/Chromium layer (2.10.0):
            // telemetry, optimization guides and client analytics. Unrouted,
            // they open DIRECT connections from the home IP while API calls
            // go through the relay - Google sees one account from two
            // locations and rejects the API side with FAILED_PRECONDITION.
            "play.googleapis.com",
            "optimizationguide-pa.googleapis.com",
            "waa-pa.clients6.google.com",
            "signaler-pa.clients6.google.com"
        };
        return core.Concat(ExtraHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)).ToList();
    }
}
