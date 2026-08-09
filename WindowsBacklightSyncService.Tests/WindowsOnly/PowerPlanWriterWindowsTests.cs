using System.ComponentModel;
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

    [WindowsFact]
    public void AllSchemes_ReadableAndInRange()
    {
        var writer = new PowerPlanBrightnessWriter();
        foreach (Guid scheme in writer.EnumeratePowerSchemes())
        {
            foreach (bool ac in new[] { true, false })
            {
                int? value = writer.ReadBrightnessValue(scheme, ac);
                if (value is not null)
                    Assert.InRange(value.Value, 0, 100);
            }
        }
    }

    [WindowsFact]
    public void AllSchemes_HaveFriendlyNames()
    {
        var writer = new PowerPlanBrightnessWriter();
        foreach (Guid scheme in writer.EnumeratePowerSchemes())
        {
            string? name = writer.GetSchemeName(scheme);
            Assert.False(string.IsNullOrWhiteSpace(name), $"scheme {scheme:D} has no name");
        }
    }

    [WindowsFact]
    public void WriteBrightnessValue_InvalidScheme_Throws()
    {
        // A non-existent scheme must produce a Win32 error (no mutation possible).
        var writer = new PowerPlanBrightnessWriter();
        Assert.Throws<Win32Exception>(() => writer.WriteBrightnessValue(Guid.NewGuid(), 50, ac: true));
    }

    [WindowsFact]
    public void WriteReadRoundtrip_OnNonActiveScheme_RestoresOriginal()
    {
        var writer = new PowerPlanBrightnessWriter();
        Guid active = writer.GetActiveScheme()!.Value;
        Guid target = writer.EnumeratePowerSchemes().First(s => s != active);

        int? originalAc = writer.ReadBrightnessValue(target, ac: true);
        int? originalDc = writer.ReadBrightnessValue(target, ac: false);

        try
        {
            // Writing a value to a NON-active plan has no visible effect on the screen.
            writer.WriteBrightnessValue(target, 42, ac: true);
            writer.WriteBrightnessValue(target, 42, ac: false);
            Assert.Equal(42, writer.ReadBrightnessValue(target, ac: true));
            Assert.Equal(42, writer.ReadBrightnessValue(target, ac: false));
        }
        finally
        {
            if (originalAc is not null) writer.WriteBrightnessValue(target, originalAc.Value, ac: true);
            if (originalDc is not null) writer.WriteBrightnessValue(target, originalDc.Value, ac: false);
        }

        Assert.Equal(originalAc, writer.ReadBrightnessValue(target, ac: true));
        Assert.Equal(originalDc, writer.ReadBrightnessValue(target, ac: false));
    }

    [WindowsFact]
    public void SetActiveScheme_Roundtrip_RestoresOriginal()
    {
        var writer = new PowerPlanBrightnessWriter();
        Guid original = writer.GetActiveScheme()!.Value;
        var schemes = writer.EnumeratePowerSchemes();
        Assert.True(schemes.Count >= 2, "need at least 2 plans for the roundtrip");
        Guid other = schemes.First(s => s != original);

        try
        {
            writer.SetActiveScheme(other);
            Assert.Equal(other, writer.GetActiveScheme());
        }
        finally
        {
            writer.SetActiveScheme(original); // restore immediately (may cause a brief brightness re-apply)
        }

        Assert.Equal(original, writer.GetActiveScheme());
    }
}
