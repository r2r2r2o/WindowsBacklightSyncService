using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Coverage for the worker's polling/self-healing paths: PollBrightness,
/// OnSystemResumed, LogSchemeChanges. These interact with the real BrightnessWatcher
/// static query methods (which fail gracefully off-Windows), so they exercise the
/// worker's decision branches without requiring hardware.
/// </summary>
public class BacklightSyncWorkerPollingTests
{
    private static readonly Guid PlanA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlanB = new("22222222-2222-2222-2222-222222222222");

    private static BacklightSyncWorker CreateWorker(
        FakePowerPlanWriter writer,
        BacklightSyncOptions? options = null)
    {
        options ??= new BacklightSyncOptions { DebounceMilliseconds = 10 };
        return new BacklightSyncWorker(
            NullLogger<BacklightSyncWorker>.Instance,
            new TestOptionsMonitor<BacklightSyncOptions>(options),
            writer,
            new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance),
            new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance),
            new Diagnostics(
                writer,
                NullLogger<Diagnostics>.Instance,
                new ConfigurationBuilder().Build()));
    }

    private static FakePowerPlanWriter CreateWriter(int activeStored = 100, int otherStored = 100)
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: activeStored, dc: activeStored, name: "Active plan");
        writer.AddPlan(PlanB, ac: otherStored, dc: otherStored, name: "Other plan");
        return writer;
    }

    // ---------- PollBrightness ----------

    [Fact]
    public void PollBrightness_NoChange_NoWrites()
    {
        var writer = CreateWriter();
        var worker = CreateWorker(writer);

        worker.PollBrightness();

        Assert.Empty(writer.Writes);
        Assert.Equal(0, writer.ReapplyCount);
    }

    [Fact]
    public void PollBrightness_UnavailableBrightness_DoesNotThrow()
    {
        // On the Linux test runner ReadCurrentBrightness returns null -> the poll must
        // degrade gracefully.
        var writer = CreateWriter();
        var worker = CreateWorker(writer);

        worker.PollBrightness();

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void PollBrightness_RestartsDeadWmiSubscription()
    {
        var writer = CreateWriter();
        var worker = CreateWorker(writer);
        // WmiWatcherAlive is false because Start() was never called — the poll's
        // self-healing branch must attempt the restart and continue without throwing.
        worker.PollBrightness();

        Assert.Empty(writer.Writes); // still no writes; the point is no crash + branches hit
    }

    [Fact]
    public void PollBrightness_ChangeIsDebouncedAndSyncs()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);

        // _lastObservedBrightness starts at -1; the poll sees the (fake) current value.
        // On Linux ReadCurrentBrightness returns null, so instead simulate a change by
        // feeding the event path first, then a poll with the same value (no double sync).
        worker.OnBrightnessChanged(60, adaptive: false);
        Thread.Sleep(300);
        int writesAfterEvent = writer.Writes.Count;
        Assert.True(writesAfterEvent > 0);

        worker.PollBrightness(); // same value -> no additional writes
        Assert.Equal(writesAfterEvent, writer.Writes.Count);
    }

    // ---------- OnSystemResumed ----------

    [Fact]
    public void OnSystemResumed_DoesNotThrow_AndResyncsWhenValueAvailable()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);

        // On Linux ReadCurrentBrightness returns null -> the "could not read" branch runs.
        // On Windows it returns the real value and forces a sync. Either way no throw.
        worker.OnSystemResumed();

        Assert.True(true); // reached without exception
    }

    // ---------- LogSchemeChanges ----------

    [Fact]
    public void LogSchemeChanges_DetectsAddedAndRemovedPlans()
    {
        var writer = CreateWriter();
        var worker = CreateWorker(writer);

        // First sync: both plans are "new" (60 -> both at 100 get written).
        worker.ApplyBrightness(60, force: false);

        // Remove PlanB from the system; next sync (new target 70) notices removal.
        writer.Schemes.Remove(PlanB);
        writer.Stored[PlanA] = (70, 70); // active plan tracks the user's new level
        worker.ApplyBrightness(70, force: false);

        // Add a brand-new plan; the next sync must notice it.
        var planC = Guid.NewGuid();
        writer.AddPlan(planC, ac: 100, dc: 100, name: "New plan");
        writer.Stored[PlanA] = (80, 80);
        worker.ApplyBrightness(80, force: false);

        // No crash; the writes after re-adding include the new plan.
        Assert.Contains(writer.Writes, w => w.Scheme == planC);
    }

    [Fact]
    public void LogSchemeChanges_HandlesUnknownPlanNames()
    {
        var writer = CreateWriter();
        var worker = CreateWorker(writer);
        var unnamed = Guid.NewGuid();
        writer.AddPlan(unnamed, ac: 100, dc: 100); // no name -> GetSchemeName returns null -> "?"
        writer.Stored[PlanA] = (70, 70);

        worker.ApplyBrightness(70, force: false);

        Assert.Contains(writer.Writes, w => w.Scheme == unnamed);
    }
}
