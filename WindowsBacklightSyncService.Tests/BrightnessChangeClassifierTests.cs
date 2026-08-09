using WindowsBacklightSyncService.Services;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class BrightnessChangeClassifierTests
{
    // ---------- IsSystemDimming ----------

    [Theory]
    [InlineData(40, 100, 100, true)]   // stored 100, incoming 40 -> dim
    [InlineData(100, 100, 100, false)] // matches stored -> user change
    [InlineData(40, 40, 100, false)]   // matches AC stored -> user change
    [InlineData(40, 100, 40, false)]   // matches DC stored -> user change
    public void IsSystemDimming_MatchesStoredValue_IsUserChange(
        int brightness, int? storedAc, int? storedDc, bool expectDimming)
    {
        bool result = BrightnessChangeClassifier.IsSystemDimming(
            brightness, ignoreSystemDimming: true, storedAc, storedDc);
        Assert.Equal(expectDimming, result);
    }

    [Fact]
    public void IsSystemDimming_Disabled_AlwaysUserChange()
    {
        bool result = BrightnessChangeClassifier.IsSystemDimming(10, ignoreSystemDimming: false, 100, 100);
        Assert.False(result);
    }

    [Fact]
    public void IsSystemDimming_FailsOpen_WhenNothingStored()
    {
        // Both stored values unreadable -> treated as user change (never block a real sync).
        bool result = BrightnessChangeClassifier.IsSystemDimming(10, ignoreSystemDimming: true, null, null);
        Assert.False(result);
    }

    [Fact]
    public void IsSystemDimming_OneStoredValueNull_UsesTheOther()
    {
        // DC unreadable, AC stored=100, incoming=40 -> dim.
        Assert.True(BrightnessChangeClassifier.IsSystemDimming(40, true, 100, null));
        // AC unreadable, DC stored=40, incoming=40 -> user change.
        Assert.False(BrightnessChangeClassifier.IsSystemDimming(40, true, null, 40));
    }

    // ---------- IsLoopEcho ----------

    [Fact]
    public void IsLoopEcho_InsideWindow_SameValue_IsEcho()
    {
        Assert.True(BrightnessChangeClassifier.IsLoopEcho(60, inSuppressionWindow: true, lastAppliedBrightness: 60));
    }

    [Fact]
    public void IsLoopEcho_InsideWindow_DifferentValue_IsNotEcho()
    {
        Assert.False(BrightnessChangeClassifier.IsLoopEcho(70, inSuppressionWindow: true, lastAppliedBrightness: 60));
    }

    [Fact]
    public void IsLoopEcho_OutsideWindow_IsNotEcho()
    {
        Assert.False(BrightnessChangeClassifier.IsLoopEcho(60, inSuppressionWindow: false, lastAppliedBrightness: 60));
    }

    // ---------- ShouldIgnoreAdaptive ----------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldIgnoreAdaptive_Combinations(bool adaptive, bool ignore, bool expected)
    {
        Assert.Equal(expected, BrightnessChangeClassifier.ShouldIgnoreAdaptive(adaptive, ignore));
    }
}
