using Microsoft.Extensions.Configuration;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class DiagnosticsSnapshotTests
{
    private static readonly Guid PlanA = new("11111111-1111-1111-1111-111111111111");

    private static Diagnostics Create(FakePowerPlanWriter writer, IConfiguration? config = null)
        => new(writer, new ListLogger<Diagnostics>(), config ?? new ConfigurationBuilder().Build());

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
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 80, dc: 70, name: "Balanced");
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.LogEnvironmentSnapshot();

        // Plan line contains name, GUID, AC and DC values, and the active marker.
        Assert.Contains(logger.Messages, m => m.Contains("Balanced") && m.Contains("AC=80") && m.Contains("DC=70") && m.Contains("*"));
        Assert.Contains(logger.Messages, m => m.StartsWith("Plans") && m.Contains("1"));
    }

    [Fact]
    public void WriteTest_ReportsOk_WhenWriteAndReadBackMatch()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 100, dc: 100, name: "Balanced");
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.WriteSnapshot(consoleOut: false, writeTest: true);

        Assert.Contains(logger.Messages, m => m.Contains("WRITE TEST"));
        Assert.Contains(logger.Messages, m => m.Contains("(OK)"));
        Assert.Contains(logger.Messages, m => m.Contains("Active plan re-applied."));
    }

    [Fact]
    public void WriteTest_ReportsMismatch_WhenReadBackDiffers()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 100, dc: 100, name: "Balanced");
        // Simulate a driver that ignores writes: reset the stored value back to 100 after
        // each write so the read-back (100) differs from the target (50).
        writer.WriteInterceptor = (scheme, _, ac) =>
        {
            var (existingAc, existingDc) = writer.Stored[scheme];
            writer.Stored[scheme] = (ac ? 100 : existingAc, ac ? existingDc : 100);
        };
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.WriteSnapshot(consoleOut: false, writeTest: true);

        Assert.Contains(logger.Messages, m => m.Contains("(MISMATCH)"));
    }

    [Fact]
    public void WriteTest_ReportsFailure_WhenWriteThrows()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 100, dc: 100, name: "Balanced");
        writer.ThrowOnWrite = true;
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.WriteSnapshot(consoleOut: false, writeTest: true);

        Assert.Contains(logger.Messages, m => m.Contains("WRITE FAILED"));
    }

    [Fact]
    public void Snapshot_ReportsPlanEnumerationFailure()
    {
        var writer = new FakePowerPlanWriter();
        writer.ThrowOnEnumerate = true;
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.LogEnvironmentSnapshot();

        Assert.Contains(logger.Messages, m => m.Contains("Plan enumeration FAILED"));
    }

    [Fact]
    public void Snapshot_ReportsActivePlanReadFailure()
    {
        var writer = new FakePowerPlanWriter();
        writer.AddPlan(PlanA, ac: 80, dc: 80, name: "Balanced");
        writer.ThrowOnRead = true;
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, new ConfigurationBuilder().Build());

        diagnostics.LogEnvironmentSnapshot();

        // With a read failure, the plan line carries a "(read failed)" note.
        Assert.Contains(logger.Messages, m => m.Contains("read failed"));
    }

    [Fact]
    public void Snapshot_ShowsConfiguredLogPath()
    {
        var writer = new FakePowerPlanWriter();
        // Use an env var that exists on every platform so expansion is deterministic.
        string path = "%TEMP%\\Custom\\test.log";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:File:Path"] = path,
            })
            .Build();
        var logger = new ListLogger<Diagnostics>();
        var diagnostics = new Diagnostics(writer, logger, config);

        diagnostics.LogEnvironmentSnapshot();

        string expanded = Environment.ExpandEnvironmentVariables(path);
        Assert.Contains(logger.Messages, m => m.Contains(expanded));
    }
}
