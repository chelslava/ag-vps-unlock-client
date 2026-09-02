using AgVpsUnlock.Core;
using Xunit;

namespace AgVpsUnlock.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void RoutedHosts_ContainsCoreGoogleEndpoints()
    {
        var config = new ConfigStore();
        var hosts = config.RoutedHosts();

        Assert.Contains("cloudcode-pa.googleapis.com", hosts);
        Assert.Contains("daily-cloudcode-pa.googleapis.com", hosts);
        Assert.Contains("generativelanguage.googleapis.com", hosts);
        Assert.Contains("antigravity-unleash.goog", hosts);
        Assert.Contains("www.googleapis.com", hosts);
        Assert.Contains("oauth2.googleapis.com", hosts);
        Assert.Contains("cloudaicompanion.googleapis.com", hosts);
        Assert.Contains("aiplatform.googleapis.com", hosts);
    }

    [Fact]
    public void RoutedHosts_IncludesCleanedAndTrimmedExtraHosts()
    {
        var config = new ConfigStore
        {
            ExtraHosts = new List<string>
            {
                "  Custom-Api.Google.Com  ",
                "",
                "   ",
                "test.example.com"
            }
        };

        var hosts = config.RoutedHosts();

        Assert.Contains("custom-api.google.com", hosts);
        Assert.Contains("test.example.com", hosts);
        Assert.DoesNotContain("", hosts);
    }

    [Fact]
    public void ConfigStore_SerializationRoundtrip()
    {
        var original = new ConfigStore
        {
            VpsIp = "203.0.113.5",
            VpsToken = "secrettoken123",
            ExtraHosts = new List<string> { "custom.host.com" },
            CustomInstallPaths = new List<string> { @"C:\Custom\Antigravity.exe" }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ConfigStore>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.VpsIp, restored.VpsIp);
        Assert.Equal(original.VpsToken, restored.VpsToken);
        Assert.Equal(original.ExtraHosts, restored.ExtraHosts);
        Assert.Equal(original.CustomInstallPaths, restored.CustomInstallPaths);
    }
}
