using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests.WindowsOnly;

/// <summary>
/// Windows-only tests for the hidden-window power-event monitor. These verify the start/
/// dispose lifecycle — importantly that the monitor can be RESTARTED after disposal (the
/// P2 fix: _running is reset on every exit path) — and inject real WM_POWERBROADCAST
/// messages into the hidden window to verify resume/suspend handling and the 2 s
/// de-duplication window. Skipped on non-Windows platforms.
/// </summary>
public class PowerEventMonitorWindowsTests
{
    private const uint WmPowerBroadcast = 0x0218;
    private const int PbtApmsuspend = 0x0004;
    private const int PbtApmresumeautomatic = 0x0012;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static PowerEventMonitor StartWithWindow()
    {
        var monitor = new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance);
        monitor.Start();
        // Wait for the message-loop thread to create the hidden window.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.WindowHandle == IntPtr.Zero && DateTime.UtcNow < deadline)
            Thread.Sleep(25);
        Assert.NotEqual(IntPtr.Zero, monitor.WindowHandle);
        return monitor;
    }
    [WindowsFact]
    public void Start_ThenDispose_CompletesCleanly()
    {
        var monitor = new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance);
        try
        {
            monitor.Start();
            Assert.True(monitor.IsRunning);
            Thread.Sleep(300); // give the message loop time to create the hidden window
        }
        finally
        {
            monitor.Dispose();
        }

        Assert.False(monitor.IsRunning);
    }

    [WindowsFact]
    public void Start_AfterDispose_StartsAgain()
    {
        // Regression test for the restartability fix: a disposed monitor must be startable
        // again (previously _running stayed true forever after a failed/ended loop).
        var monitor = new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance);

        monitor.Start();
        Thread.Sleep(300);
        monitor.Dispose();
        Assert.False(monitor.IsRunning);

        monitor.Start();
        Assert.True(monitor.IsRunning);
        Thread.Sleep(300);
        monitor.Dispose();
        Assert.False(monitor.IsRunning);
    }

    [WindowsFact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var monitor = new PowerEventMonitor(NullLogger<PowerEventMonitor>.Instance);
        monitor.Dispose(); // no-op, must not throw
    }

    [WindowsFact]
    public void ResumeMessage_RaisesResumedEvent()
    {
        var monitor = StartWithWindow();
        try
        {
            using var raised = new ManualResetEventSlim();
            monitor.Resumed += raised.Set;

            bool posted = PostMessage(monitor.WindowHandle, WmPowerBroadcast, (IntPtr)PbtApmresumeautomatic, IntPtr.Zero);
            Assert.True(posted);

            Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "Resumed event was not raised");
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [WindowsFact]
    public void SuspendMessage_DoesNotRaiseResumed()
    {
        var monitor = StartWithWindow();
        try
        {
            using var raised = new ManualResetEventSlim();
            monitor.Resumed += raised.Set;

            PostMessage(monitor.WindowHandle, WmPowerBroadcast, (IntPtr)PbtApmsuspend, IntPtr.Zero);

            Assert.False(raised.Wait(TimeSpan.FromMilliseconds(800)), "Resumed must not fire on suspend");
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [WindowsFact]
    public void DuplicateResumeMessages_WithinWindow_RaiseOnce()
    {
        var monitor = StartWithWindow();
        try
        {
            int count = 0;
            monitor.Resumed += () => Interlocked.Increment(ref count);

            // Windows fires both PBT_APMRESUMESUSPEND and PBT_APMRESUMEAUTOMATIC for one
            // wake-up; the 2 s de-duplication must collapse them into a single event.
            PostMessage(monitor.WindowHandle, WmPowerBroadcast, (IntPtr)0x0007 /* PBT_APMRESUMESUSPEND */, IntPtr.Zero);
            PostMessage(monitor.WindowHandle, WmPowerBroadcast, (IntPtr)PbtApmresumeautomatic, IntPtr.Zero);

            Thread.Sleep(1200);
            Assert.Equal(1, count);
        }
        finally
        {
            monitor.Dispose();
        }
    }
}
