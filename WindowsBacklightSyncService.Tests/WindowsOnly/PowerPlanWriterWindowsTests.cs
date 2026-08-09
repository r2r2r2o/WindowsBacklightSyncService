using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests.WindowsOnly;

/// <summary>
/// Windows-only tests for the powrprof.dll power-plan API surface.
/// READ-ONLY by design: they enumerate/read plans and the active scheme but never write,
/// so running them (e.g. on the T520 or on CI) cannot modify any power plan.
/// Skipped automatically on non-Windows platforms.
/// </summary>
public class PowerPlanWriterWindowsTests
{
    [WindowsFact]
    public void EnumeratePowerSchemes_ReturnsAtLeastOne()
    {
        var writer = new PowerPlanBrightnessWriter();
        var schemes = writer.EnumeratePowerSchemes();

        Assert.NotEmpty(schemes); // every Windows machine has at least one power plan
    }

    [WindowsFact]
    public void GetActiveScheme_ReturnsNonEmptyGuid()
    {
        var writer = new PowerPlanBrightnessWriter();
        var active = writer.GetActiveScheme();

        Assert.NotNull(active);
        Assert.NotEqual(Guid.Empty, active.Value);
    }

    [WindowsFact]
    public void GetSchemeName_ActiveScheme_IsNotNull()
    {
        var writer = new PowerPlanBrightnessWriter();
        var active = writer.GetActiveScheme();
        Assert.NotNull(active);

        string? name = writer.GetSchemeName(active.Value);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [WindowsFact]
    public void ReadBrightnessValue_ActiveScheme_IsInRangeOrNull()
    {
        var writer = new PowerPlanBrightnessWriter();
        var active = writer.GetActiveScheme();
        Assert.NotNull(active);

        foreach (bool ac in new[] { true, false })
        {
            int? value = writer.ReadBrightnessValue(active.Value, ac);
            if (value is not null)
                Assert.InRange(value.Value, 0, 100);
        }
    }

    [WindowsFact]
    public void ReadBrightnessValue_UnknownScheme_ReturnsNull_DoesNotThrow()
    {
        var writer = new PowerPlanBrightnessWriter();
        Assert.Null(writer.ReadBrightnessValue(Guid.NewGuid(), ac: true));
    }
}
