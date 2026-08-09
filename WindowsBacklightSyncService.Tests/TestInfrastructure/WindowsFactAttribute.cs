using Xunit;

namespace WindowsBacklightSyncService.Tests.TestInfrastructure;

/// <summary>
/// A [Fact] that only runs on Windows. On non-Windows platforms (Linux/macOS dev machines)
/// xUnit reports the test as SKIPPED, so the suite stays green everywhere; on Windows
/// (including the windows-latest CI runners) the test executes for real.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only test — skipped on non-Windows platforms";
    }
}

/// <summary>A [Theory] counterpart of <see cref="WindowsFactAttribute"/>.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only test — skipped on non-Windows platforms";
    }
}
