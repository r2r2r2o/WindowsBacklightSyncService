using WindowsBacklightSyncService.Services;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class PlanWriteDeciderTests
{
    [Fact]
    public void ShouldWrite_DisabledSync_NeverWrites()
    {
        Assert.False(PlanWriteDecider.ShouldWrite(60, storedBrightness: null, syncEnabled: false, writeOnlyWhenChanged: false));
        Assert.False(PlanWriteDecider.ShouldWrite(60, storedBrightness: 30, syncEnabled: false, writeOnlyWhenChanged: true));
    }

    [Fact]
    public void ShouldWrite_WriteOnlyWhenChanged_MatchingValue_Skips()
    {
        Assert.False(PlanWriteDecider.ShouldWrite(60, storedBrightness: 60, syncEnabled: true, writeOnlyWhenChanged: true));
    }

    [Fact]
    public void ShouldWrite_WriteOnlyWhenChanged_DifferentValue_Writes()
    {
        Assert.True(PlanWriteDecider.ShouldWrite(60, storedBrightness: 30, syncEnabled: true, writeOnlyWhenChanged: true));
    }

    [Fact]
    public void ShouldWrite_UnreadableStored_Writes()
    {
        // A plan whose stored value cannot be read must still be written (fail-safe).
        Assert.True(PlanWriteDecider.ShouldWrite(60, storedBrightness: null, syncEnabled: true, writeOnlyWhenChanged: true));
    }

    [Fact]
    public void ShouldWrite_WriteOnlyWhenChangedDisabled_AlwaysWrites()
    {
        Assert.True(PlanWriteDecider.ShouldWrite(60, storedBrightness: 60, syncEnabled: true, writeOnlyWhenChanged: false));
    }
}
