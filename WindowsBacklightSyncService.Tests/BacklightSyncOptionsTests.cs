using WindowsBacklightSyncService;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class BacklightSyncOptionsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var o = new BacklightSyncOptions();

        Assert.Equal(500, o.DebounceMilliseconds);
        Assert.Equal(10, o.PollingIntervalSeconds);
        Assert.True(o.InitialSyncOnStart);
        Assert.True(o.SyncAcValue);
        Assert.True(o.SyncDcValue);
        Assert.True(o.WriteOnlyWhenChanged);
        Assert.True(o.ReapplyActiveScheme);
        Assert.False(o.IgnoreAdaptiveChanges);
        Assert.True(o.IgnoreSystemDimming);   // the whole point of the feature
        Assert.Equal(60, o.PeriodicResyncSeconds);
        Assert.Equal(1000, o.SuppressEventsAfterApplyMilliseconds);
    }

    [Fact]
    public void Defaults_MatchShippedAppSettings()
    {
        // Guard against appsettings.json and the code defaults drifting apart.
        var o = new BacklightSyncOptions();
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        Assert.Equal(o.DebounceMilliseconds.ToString(), ExtractJsonInt(json, "DebounceMilliseconds"));
        Assert.Equal(o.PollingIntervalSeconds.ToString(), ExtractJsonInt(json, "PollingIntervalSeconds"));
        Assert.Equal(o.IgnoreSystemDimming.ToString().ToLowerInvariant(), ExtractJsonBool(json, "IgnoreSystemDimming"));
        Assert.Equal(o.PeriodicResyncSeconds.ToString(), ExtractJsonInt(json, "PeriodicResyncSeconds"));
        Assert.Equal(o.SuppressEventsAfterApplyMilliseconds.ToString(), ExtractJsonInt(json, "SuppressEventsAfterApplyMilliseconds"));
    }

    private static string ExtractJsonInt(string json, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, $@"""{key}""\s*:\s*(\d+)");
        Assert.True(match.Success, $"key {key} not found in appsettings.json");
        return match.Groups[1].Value;
    }

    private static string ExtractJsonBool(string json, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, $@"""{key}""\s*:\s*(true|false)");
        Assert.True(match.Success, $"key {key} not found in appsettings.json");
        return match.Groups[1].Value;
    }
}
