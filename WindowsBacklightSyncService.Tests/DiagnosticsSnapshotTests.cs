using Microsoft.Extensions.Configuration;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class DiagnosticsSnapshotTests
{
    [Fact]
    public void LogEnvironmentSnapshot_AlwaysLogsHeaderAndEnd_WithoutThrowing()
    {
        // No hardware: WMI/registry/powrprof are unavailable on the test runner, so the
        // snapshot must degrade gracefully (report unavailable/failed) — but never throw,
        // and always delimit the snapshot with header + end markers.
        var writer = new FakePowerPlanWriter();
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.LogEnvironmentSnapshot();

        Assert.Contains(logger.Messages, m => m.Contains("diagnostic snapshot ==="));
        Assert.Contains(logger.Messages, m => m.Contains("=== end of snapshot ==="));
        Assert.Contains(logger.Messages, m => m.StartsWith("Version"));
        Assert.Contains(logger.Messages, m => m.StartsWith("OS"));
        Assert.Contains(logger.Messages, m => m.StartsWith("Brightness"));
    }

    [Fact]
    public void Snapshot_ReportsPlans_WhenWriterSucceeds()
    {
        var writer = new FakePowerPlanWriter { Active = Guid.NewGuid() };
        writer.AddPlan(writer.Active.Value, ac: 80, dc: 70, name: "Balanced");
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.LogEnvironmentSnapshot();

        // Plan line contains name, GUID, AC and DC values.
        Assert.Contains(logger.Messages, m => m.Contains("Balanced") && m.Contains("AC=80") && m.Contains("DC=70"));
        Assert.Contains(logger.Messages, m => m.StartsWith("Plans") && m.Contains("1"));
    }
}
