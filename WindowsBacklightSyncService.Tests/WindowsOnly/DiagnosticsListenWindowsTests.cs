using Microsoft.Extensions.Configuration;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests.WindowsOnly;

/// <summary>
/// Windows-only tests for the interactive "--check --listen" event-listener path.
/// Runs with a short window; on CI (or any machine without brightness activity) it
/// verifies the no-events guidance output. Skipped on non-Windows platforms.
/// </summary>
public class DiagnosticsListenWindowsTests
{
    [WindowsFact]
    public async Task Listen_ShortWindow_PrintsSummaryAndGuidance()
    {
        var writer = new FakePowerPlanWriter();
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        await diagnostics.ListenForEventsAsync(TimeSpan.FromMilliseconds(800));

        Assert.Contains(logger.Messages, m => m.Contains("Listening for WMI brightness events"));
        Assert.Contains(logger.Messages, m => m.StartsWith("Events received:"));

        // On a quiet machine (typical CI) the guidance about no events is printed.
        Assert.Contains(logger.Messages, m => m.Contains("No events arrived"));
    }
}
