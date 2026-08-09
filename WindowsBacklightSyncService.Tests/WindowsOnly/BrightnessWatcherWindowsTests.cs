using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests.WindowsOnly;

/// <summary>
/// Windows-only tests for the WMI/registry brightness signal surface. Read-only — they
/// never subscribe to events or modify anything; they verify the code behaves correctly
/// (never throws, values in range) against the real Windows WMI/registry stack.
/// Skipped automatically on non-Windows platforms.
/// </summary>
public class BrightnessWatcherWindowsTests
{
    [WindowsFact]
    public void WmiClassExists_DoesNotThrow_ForBrightnessClasses()
    {
        // The classes may or may not be present (driver dependent) — but the check itself
        // must never throw on Windows.
        foreach (string cls in new[] { "WmiMonitorBrightness", "WmiMonitorBrightnessEvent", "WmiMonitorBrightnessMethods" })
        {
            _ = BrightnessWatcher.WmiClassExists(cls);
        }
    }

    [WindowsFact]
    public void ReadCurrentBrightness_ReturnsNullOrValidRange()
    {
        int? brightness = BrightnessWatcher.ReadCurrentBrightness();
        if (brightness is not null)
            Assert.InRange(brightness.Value, 0, 100);
    }

    [WindowsFact]
    public void ResolveBrightnessRegistryRoots_DoesNotThrow()
    {
        var roots = BrightnessWatcher.ResolveBrightnessRegistryRoots();
        Assert.NotNull(roots);
    }

    [WindowsFact]
    public void ReadRegistryBrightness_DoesNotThrow()
    {
        int? brightness = BrightnessWatcher.ReadRegistryBrightness();
        if (brightness is not null)
            Assert.InRange(brightness.Value, 0, 100);
    }
}
