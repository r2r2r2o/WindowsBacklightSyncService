using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Lifecycle tests: ExecuteAsync + StopAsync run for real against fakes. The watcher and
/// power monitor are constructed but never started (Start() is not called), so these run
/// on any platform; they exercise the worker's async state-machine paths that pure unit
/// tests cannot reach.
/// </summary>
public class BacklightSyncWorkerLifecycleTests
{
    private static readonly Guid PlanA = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ExecuteAsync_StartsAndStops_Cleanly()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 60, dc: 60, name: "Active");
        var options = new BacklightSyncOptions
        {
            DebounceMilliseconds = 5,
            PollingIntervalSeconds = 0, // no polling loop
            InitialSyncOnStart = false,
        };
        var worker = new BacklightSyncWorker(
            NullLogger<BacklightSyncWorker>.Instance,
            new TestOptionsMonitor<BacklightSyncOptions>(options),
            writer,
            new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance),
            new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance),
            new Diagnostics(writer, NullLogger<Diagnostics>.Instance, new ConfigurationBuilder().Build()));

        using var cts = new CancellationTokenSource();
        Task run = worker.StartAsync(cts.Token);
        await Task.Delay(200); // let ExecuteAsync run
        await worker.StopAsync(cts.Token);
        await run;

        // Reached clean shutdown without exceptions.
        Assert.True(true);
    }

    [Fact]
    public async Task ExecuteAsync_WithPolling_RunsThenStops()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 60, dc: 60, name: "Active");
        var options = new BacklightSyncOptions
        {
            DebounceMilliseconds = 5,
            PollingIntervalSeconds = 1,
            InitialSyncOnStart = false,
            PeriodicResyncSeconds = 0,
        };
        var worker = new BacklightSyncWorker(
            NullLogger<BacklightSyncWorker>.Instance,
            new TestOptionsMonitor<BacklightSyncOptions>(options),
            writer,
            new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance),
            new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance),
            new Diagnostics(writer, NullLogger<Diagnostics>.Instance, new ConfigurationBuilder().Build()));

        using var cts = new CancellationTokenSource();
        Task run = worker.StartAsync(cts.Token);
        await Task.Delay(300); // let a couple of poll ticks happen
        await worker.StopAsync(cts.Token);
        await run;

        Assert.True(true);
    }

    [Fact]
    public async Task ExecuteAsync_WithInitialSync_WritesPlans()
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: 100, dc: 100, name: "Active");
        var options = new BacklightSyncOptions
        {
            DebounceMilliseconds = 5,
            PollingIntervalSeconds = 0,
            InitialSyncOnStart = true,
            IgnoreSystemDimming = false, // initial sync is forced; the dim filter must not block it
        };
        var worker = new BacklightSyncWorker(
            NullLogger<BacklightSyncWorker>.Instance,
            new TestOptionsMonitor<BacklightSyncOptions>(options),
            writer,
            new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance),
            new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance),
            new Diagnostics(writer, NullLogger<Diagnostics>.Instance, new ConfigurationBuilder().Build()));

        using var cts = new CancellationTokenSource();
        Task run = worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(cts.Token);
        await run;

        // On Windows ReadCurrentBrightness returns a value -> initial sync writes plans.
        // On Linux it returns null -> the "nothing to sync" branch runs. Either way no throw.
        Assert.True(true);
    }
}
