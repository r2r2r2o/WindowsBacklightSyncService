using Microsoft.Extensions.Logging.Abstractions;
using WindowsBacklightSyncService.Services;
using WindowsBacklightSyncService.Tests.TestInfrastructure;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Tests for the WMI-event extraction logic via the IBrightnessEventData abstraction
/// (runs on every platform — no live WMI needed).
/// </summary>
public class BrightnessEventExtractionTests
{
    private sealed class FakeEventData : BrightnessWatcher.IBrightnessEventData
    {
        private readonly Dictionary<string, object?> _props = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _throwing = new(StringComparer.OrdinalIgnoreCase);
        public string? ClassName { get; set; } = "WmiMonitorBrightnessEvent";
        public string? Mof { get; set; }
        public IEnumerable<string> PropertyNames => _props.Keys;

        public FakeEventData With(string name, object? value)
        {
            _props[name] = value;
            return this;
        }

        public FakeEventData Throwing(string name)
        {
            _throwing.Add(name);
            return this;
        }

        public int? GetIntProperty(string name)
        {
            if (_throwing.Contains(name))
                throw new InvalidOperationException("property read failed (test)");
            if (!_props.TryGetValue(name, out var raw) || raw is null || raw is DBNull)
                return null;
            return Convert.ToInt32(raw);
        }

        public bool? GetBoolProperty(string name)
        {
            if (_throwing.Contains(name))
                throw new InvalidOperationException("property read failed (test)");
            return _props.TryGetValue(name, out var raw) && raw is bool b ? b : null;
        }

        public string? GetMofText() => Mof;
    }

    // ---------- TryExtractBrightness ----------

    [Fact]
    public void Extracts_FromBrightnessProperty()
    {
        var data = new FakeEventData().With("Brightness", 75);

        int? value = BrightnessWatcher.TryExtractBrightness(data, out bool adaptive);

        Assert.Equal(75, value);
        Assert.False(adaptive);
    }

    [Fact]
    public void Extracts_FromFallbackPropertyNames()
    {
        foreach (string name in new[] { "CurrentBrightness", "Value", "Level", "BrightnessLevel" })
        {
            var data = new FakeEventData().With(name, 40);
            Assert.Equal(40, BrightnessWatcher.TryExtractBrightness(data, out _));
        }
    }

    [Fact]
    public void Reads_AdaptiveFlag()
    {
        var data = new FakeEventData().With("Brightness", 60).With("Adaptive", true);
        Assert.Equal(60, BrightnessWatcher.TryExtractBrightness(data, out bool adaptive));
        Assert.True(adaptive);
    }

    [Fact]
    public void FallsBack_ToMof_WhenNoProperty()
    {
        var data = new FakeEventData { Mof = "instance of WmiMonitorBrightnessEvent { Brightness = 70; };" };
        Assert.Equal(70, BrightnessWatcher.TryExtractBrightness(data, out _));
    }

    [Fact]
    public void ThrowingPropertyAccess_FallsThroughToNextCandidate()
    {
        var data = new FakeEventData()
            .Throwing("Brightness")
            .With("CurrentBrightness", 55);

        Assert.Equal(55, BrightnessWatcher.TryExtractBrightness(data, out _));
    }

    [Fact]
    public void OutOfRange_NotAccepted_ReturnsNull()
    {
        var data = new FakeEventData().With("Brightness", 250);
        Assert.Null(BrightnessWatcher.TryExtractBrightness(data, out _));
    }

    [Fact]
    public void NoUsableValue_ReturnsNull()
    {
        var data = new FakeEventData().With("Brightness", "not-a-number").Throwing("Brightness");
        Assert.Null(BrightnessWatcher.TryExtractBrightness(data, out _));
    }

    // ---------- LogEventSchema ----------

    [Fact]
    public void LogEventSchema_LogsClassAndProperties()
    {
        var logger = new ListLogger<BrightnessWatcher>();
        var watcher = new BrightnessWatcher(logger);
        var data = new FakeEventData()
            .With("Brightness", 50)
            .With("InstanceName", "LCD");

        watcher.LogEventSchema(data);

        Assert.Contains(logger.Messages, m =>
            m.Contains("WmiMonitorBrightnessEvent") &&
            m.Contains("Brightness") &&
            m.Contains("InstanceName"));
    }

    [Fact]
    public void LogEventSchema_ThrowingSchema_DoesNotThrow()
    {
        var logger = new ListLogger<BrightnessWatcher>();
        var watcher = new BrightnessWatcher(logger);
        var data = new FakeEventData { ClassName = null };

        watcher.LogEventSchema(data); // null class name -> "?"; must not throw
        Assert.Contains(logger.Messages, m => m.Contains("class=?"));
    }

    // ---------- RaiseChanged ----------

    [Fact]
    public void RaiseChanged_FiresEventWithValues()
    {
        var watcher = new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance);
        int? seenBrightness = null;
        bool? seenAdaptive = null;
        watcher.BrightnessChanged += (b, a) => { seenBrightness = b; seenAdaptive = a; };

        watcher.RaiseChanged(42, adaptive: true);

        Assert.Equal(42, seenBrightness);
        Assert.True(seenAdaptive);
    }
}
