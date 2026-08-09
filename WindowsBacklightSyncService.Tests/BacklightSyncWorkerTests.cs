using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Tests the worker's sync behavior end-to-end against an in-memory plan writer
/// (no hardware, no WMI, no threads started — the watcher and power monitor are
/// constructed but never started).
///
/// Model note: a DELIBERATE user change is recorded by Windows in the ACTIVE plan's
/// stored value; the service syncs the other plans to match. A change that does NOT
/// match the active plan's stored value is treated as system dimming and filtered.
/// Tests simulate Windows recording the user's level by setting the fake writer's
/// active-plan stored value to the incoming brightness.
/// </summary>
public class BacklightSyncWorkerTests
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

    /// <summary>Two plans; the active one (PlanA) stores the given user brightness.</summary>
    private static FakePowerPlanWriter CreateWriter(int activeStored, int otherStored)
    {
        var writer = new FakePowerPlanWriter { Active = PlanA };
        writer.AddPlan(PlanA, ac: activeStored, dc: activeStored, name: "Active plan");
        writer.AddPlan(PlanB, ac: otherStored, dc: otherStored, name: "Other plan");
        return writer;
    }

    // ---------- ApplyBrightness ----------

    [Fact]
    public void ApplyBrightness_WritesPlans_DifferentFromTarget()
    {
        // User set 60 -> active plan already stores 60; the other plan (100) must be synced.
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(60, force: false);

        Assert.Equal(2, writer.Writes.Count); // PlanB AC + DC only
        Assert.All(writer.Writes, w => Assert.Equal(PlanB, w.Scheme));
        Assert.All(writer.Writes, w => Assert.Equal(60, w.Value));
    }

    [Fact]
    public void ApplyBrightness_SkipsPlans_AlreadyAtTarget()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 60);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(60, force: false);

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void ApplyBrightness_SameAsLastApplied_NonForced_IsSkipped()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(60, force: false);
        int writesAfterFirst = writer.Writes.Count;
        Assert.True(writesAfterFirst > 0);

        worker.ApplyBrightness(60, force: false); // same value, not forced

        Assert.Equal(writesAfterFirst, writer.Writes.Count);
    }

    [Fact]
    public void ApplyBrightness_ReappliesActiveScheme_WhenSomethingChanged()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(60, force: false);

        Assert.Equal(1, writer.ReapplyCount);
        Assert.Equal(PlanA, writer.Active);
    }

    [Fact]
    public void ApplyBrightness_NoReapply_WhenNothingChanged()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 60);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(60, force: false);

        Assert.Equal(0, writer.ReapplyCount);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void ApplyBrightness_Skips_SystemDimmingChange()
    {
        // Screen dimmed to 40 while the active plan still stores 100 -> system-initiated.
        var writer = CreateWriter(activeStored: 100, otherStored: 100);
        var worker = CreateWorker(writer, new BacklightSyncOptions
        {
            IgnoreSystemDimming = true,
            DebounceMilliseconds = 10
        });

        worker.ApplyBrightness(40, force: false);

        Assert.Empty(writer.Writes);
        Assert.Equal(0, writer.ReapplyCount);
    }

    [Fact]
    public void ApplyBrightness_Processes_ChangeMatchingStoredValue()
    {
        // Windows recorded the user's 40 in the active plan -> it's a user change.
        var writer = CreateWriter(activeStored: 40, otherStored: 100);
        var worker = CreateWorker(writer);

        worker.ApplyBrightness(40, force: false);

        // PlanB gets AC+DC writes; the active plan is skipped (already at 40).
        Assert.Equal(2, writer.Writes.Count);
        Assert.All(writer.Writes, w => Assert.Equal(PlanB, w.Scheme));
        Assert.All(writer.Writes, w => Assert.Equal(40, w.Value));
    }

    [Fact]
    public void ApplyBrightness_IgnoresSystemDimming_WhenDisabled()
    {
        var writer = CreateWriter(activeStored: 100, otherStored: 100);
        var worker = CreateWorker(writer, new BacklightSyncOptions
        {
            IgnoreSystemDimming = false,
            DebounceMilliseconds = 10
        });

        worker.ApplyBrightness(40, force: false); // dim filter off -> sync even a dim

        Assert.Equal(4, writer.Writes.Count); // both plans, AC+DC
    }

    [Fact]
    public void ApplyBrightness_Continues_WhenOnePlanWriteFails()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);
        writer.ThrowOnWrite = true;

        // A failing writer must not crash the worker (per-plan try/catch).
        worker.ApplyBrightness(60, force: false);

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void ApplyBrightness_Skips_WriterReadFailure_FailsOpen()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer);
        writer.ThrowOnRead = true;

        // Unreadable active-plan values -> not classified as dimming -> sync proceeds
        // (and unreadable per-plan values are written, fail-safe).
        worker.ApplyBrightness(60, force: false);

        Assert.Equal(4, writer.Writes.Count);
    }

    // ---------- OnBrightnessChanged (debounced path) ----------

    [Fact]
    public async Task OnBrightnessChanged_SyncsAfterDebounce()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer); // DebounceMilliseconds = 10

        worker.OnBrightnessChanged(60, adaptive: false);
        await Task.Delay(300);

        Assert.Equal(2, writer.Writes.Count);
    }

    [Fact]
    public async Task OnBrightnessChanged_IgnoresAdaptive_WhenConfigured()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer, new BacklightSyncOptions
        {
            IgnoreAdaptiveChanges = true,
            DebounceMilliseconds = 10
        });

        worker.OnBrightnessChanged(60, adaptive: true);
        await Task.Delay(300);

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task OnBrightnessChanged_IgnoresLoopEcho_InsideSuppressionWindow()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer, new BacklightSyncOptions
        {
            DebounceMilliseconds = 10,
            SuppressEventsAfterApplyMilliseconds = 30_000 // long window for the test
        });

        worker.ApplyBrightness(60, force: true); // sets last applied + apply tick
        int writesAfterApply = writer.Writes.Count;

        worker.OnBrightnessChanged(60, adaptive: false); // same value, inside window -> echo
        await Task.Delay(300);

        Assert.Equal(writesAfterApply, writer.Writes.Count);
    }

    [Fact]
    public async Task OnBrightnessChanged_ProcessesDifferentValue_InsideSuppressionWindow()
    {
        var writer = CreateWriter(activeStored: 60, otherStored: 100);
        var worker = CreateWorker(writer, new BacklightSyncOptions
        {
            DebounceMilliseconds = 10,
            SuppressEventsAfterApplyMilliseconds = 30_000
        });

        worker.ApplyBrightness(60, force: true);
        int writesAfterApply = writer.Writes.Count;

        // The user changes to 70; Windows records it in the active plan.
        writer.Stored[PlanA] = (70, 70);

        worker.OnBrightnessChanged(70, adaptive: false); // different value -> must process
        await Task.Delay(300);

        Assert.True(writer.Writes.Count > writesAfterApply);
        Assert.Contains(writer.Writes.Skip(writesAfterApply), w => w.Value == 70);
    }
}
