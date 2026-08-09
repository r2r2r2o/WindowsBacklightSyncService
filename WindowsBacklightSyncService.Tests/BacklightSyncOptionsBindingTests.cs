using Microsoft.Extensions.Configuration;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Verifies that appsettings.json-style configuration actually binds to BacklightSyncOptions
/// the way the DI container binds it at startup (same ConfigurationBinder used by Configure<T>).
/// </summary>
public class BacklightSyncOptionsBindingTests
{
    private static BacklightSyncOptions Bind(string json)
    {
        // Bind-into-instance matches Configure<TOptions> semantics: a missing section
        // leaves the code defaults untouched (Get<T>() would return null instead).
        var options = new BacklightSyncOptions();
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection("BacklightSync")
            .Bind(options);
        return options;
    }

    [Fact]
    public void Binds_AllShippedAppSettingsKeys()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var o = Bind(json);

        Assert.Equal(500, o.DebounceMilliseconds);
        Assert.Equal(10, o.PollingIntervalSeconds);
        Assert.True(o.InitialSyncOnStart);
        Assert.True(o.SyncAcValue);
        Assert.True(o.SyncDcValue);
        Assert.True(o.WriteOnlyWhenChanged);
        Assert.True(o.ReapplyActiveScheme);
        Assert.False(o.IgnoreAdaptiveChanges);
        Assert.True(o.IgnoreSystemDimming);
        Assert.Equal(1000, o.SuppressEventsAfterApplyMilliseconds);
        Assert.Equal(60, o.PeriodicResyncSeconds);
    }

    [Fact]
    public void EnvironmentVariablePrefix_OverridesJson()
    {
        // Faithful to the app: the DI host uses AddEnvironmentVariables(), where the
        // "__" separator maps to ":" in the configuration key.
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        const string varName = "BacklightSync__DebounceMilliseconds";
        try
        {
            Environment.SetEnvironmentVariable(varName, "300");
            var options = new BacklightSyncOptions();
            new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                .AddEnvironmentVariables()
                .Build()
                .GetSection("BacklightSync")
                .Bind(options);

            Assert.Equal(300, options.DebounceMilliseconds);
            Assert.Equal(10, options.PollingIntervalSeconds); // untouched key keeps the JSON value
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void MissingSection_KeepsCodeDefaults()
    {
        var o = Bind("{}");

        Assert.Equal(500, o.DebounceMilliseconds);
        Assert.True(o.IgnoreSystemDimming);
        Assert.Equal(60, o.PeriodicResyncSeconds);
    }
}
