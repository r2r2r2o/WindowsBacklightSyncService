namespace WindowsBacklightSyncService.Services;

/// <summary>
/// Pure decision logic for classifying brightness changes and per-plan writes.
/// No I/O, no Windows APIs, no logging — fully unit-testable.
/// </summary>
public static class BrightnessChangeClassifier
{
    /// <summary>
    /// True when the incoming brightness level is likely system-initiated — the screen
    /// dimming after inactivity, its restore, or battery-saver dimming. Discriminator:
    /// Windows records deliberate user changes (brightness keys, slider) in the ACTIVE
    /// plan's stored brightness, but system dimming changes only the screen level.
    /// Therefore a level that matches the active plan's stored AC or DC value is treated
    /// as user-initiated; anything else as system-initiated.
    /// Fails open: if no stored value is readable, the change is treated as user-initiated.
    /// </summary>
    public static bool IsSystemDimming(int brightnessPercent, bool ignoreSystemDimming, int? activePlanAcStored, int? activePlanDcStored)
    {
        if (!ignoreSystemDimming)
            return false;

        // Fails open: nothing readable -> assume user-initiated.
        if (activePlanAcStored is null && activePlanDcStored is null)
            return false;

        if (activePlanAcStored == brightnessPercent || activePlanDcStored == brightnessPercent)
            return false;

        return true;
    }

    /// <summary>
    /// Loop protection: re-applying the active scheme can emit a brightness event with the
    /// value we just applied. Only such SAME-VALUE events inside the post-apply window are
    /// suppressed — a real user change (different value) must pass through.
    /// </summary>
    public static bool IsLoopEcho(int brightnessPercent, bool inSuppressionWindow, int lastAppliedBrightness)
    {
        return inSuppressionWindow && brightnessPercent == lastAppliedBrightness;
    }

    /// <summary>True when an adaptive (sensor-driven) change should be ignored.</summary>
    public static bool ShouldIgnoreAdaptive(bool adaptive, bool ignoreAdaptiveChanges)
        => adaptive && ignoreAdaptiveChanges;
}

/// <summary>
/// Decides whether a single AC/DC value index of a plan needs to be written.
/// </summary>
public static class PlanWriteDecider
{
    public static bool ShouldWrite(
        int targetBrightness,
        int? storedBrightness,
        bool syncEnabled,
        bool writeOnlyWhenChanged)
    {
        if (!syncEnabled)
            return false;
        if (!writeOnlyWhenChanged)
            return true;

        // Unreadable stored value (null) -> write to be safe.
        return storedBrightness is null || storedBrightness.Value != targetBrightness;
    }
}
