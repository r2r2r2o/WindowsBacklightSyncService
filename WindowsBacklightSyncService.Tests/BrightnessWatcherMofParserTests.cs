using WindowsBacklightSyncService.Services;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class BrightnessWatcherMofParserTests
{
    [Fact]
    public void Parses_StandardEventMof()
    {
        const string mof = """
            instance of WmiMonitorBrightnessEvent
            {
                Active = TRUE;
                InstanceName = "LCD\\_SB.PCI0.GFX0.LCD0\\0";
                Brightness = 75;
            };
            """;
        Assert.Equal(75, BrightnessWatcher.ParseBrightnessFromMofText(mof));
    }

    [Fact]
    public void Parses_LowercaseAndWhitespaceVariants()
    {
        Assert.Equal(40, BrightnessWatcher.ParseBrightnessFromMofText("brightness   =   40"));
        Assert.Equal(0, BrightnessWatcher.ParseBrightnessFromMofText("Brightness=0"));
        Assert.Equal(100, BrightnessWatcher.ParseBrightnessFromMofText("Brightness=100"));
    }

    [Theory]
    [InlineData("Brightness = 101")]   // above range
    [InlineData("Brightness = -5")]    // negative
    [InlineData("Brightness = 9999")]  // too many digits
    [InlineData("NoBrightnessHere = 50")]
    [InlineData("")]
    [InlineData("Brightness = abc")]
    public void ReturnsNull_ForInvalidOrMissing(string mof)
    {
        Assert.Null(BrightnessWatcher.ParseBrightnessFromMofText(mof));
    }

    [Fact]
    public void Ignores_OtherPropertiesWithBrightnessInName()
    {
        // Must not match e.g. "BrightnessLevel" first — the regex anchors on the property
        // name token, and value must be 0-100.
        Assert.Null(BrightnessWatcher.ParseBrightnessFromMofText("BrightnessLevel = 50"));
    }
}
