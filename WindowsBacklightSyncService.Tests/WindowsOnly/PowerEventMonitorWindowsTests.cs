using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests.WindowsOnly;

/// <summary>
/// Windows-only tests for the hidden-window power-event monitor. These verify the start/
/// dispose lifecycle and — importantly — that the monitor can be RESTARTED after disposal
/// (the P2 fix: _running is reset on every exit path). Skipped on non-Windows platforms.
/// </summary>
public class PowerEventMonitorWindowsTests
{
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
}
