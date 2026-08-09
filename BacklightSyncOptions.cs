namespace BacklightSyncService;

/// <summary>
/// Configuration for the backlight synchronization service.
/// Bound from the "BacklightSync" section of appsettings.json (or environment
/// variables using the "BacklightSync__" prefix).
/// </summary>
public sealed class BacklightSyncOptions
{
    /// <summary>
    /// How long to wait after the last brightness change before writing to the power plans.
    /// Brightness keys / slider drags fire many events in a row; this collapses them into one sync.
    /// </summary>
    public int DebounceMilliseconds { get; set; } = 500;

    /// <summary>
    /// Polling fallback interval in seconds. 0 disables polling.
    /// WMI events are the primary change signal; polling catches changes WMI events miss.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 10;

    /// <summary>On service start, immediately synchronize the current brightness to all power plans.</summary>
    public bool InitialSyncOnStart { get; set; } = true;

    /// <summary>Write the AC (plugged in) brightness value index of every plan.</summary>
    public bool SyncAcValue { get; set; } = true;

    /// <summary>Write the DC (on battery) brightness value index of every plan.</summary>
    public bool SyncDcValue { get; set; } = true;

    /// <summary>
    /// Only write a value when it differs from the value already stored in the plan.
    /// Reduces writes and avoids needless re-activation of the active scheme.
    /// </summary>
    public bool WriteOnlyWhenChanged { get; set; } = true;

    /// <summary>
    /// Re-apply the active power scheme after writing, so the new brightness value takes
    /// effect immediately (the same thing powercfg /setactive does).
    /// </summary>
    public bool ReapplyActiveScheme { get; set; } = true;

    /// <summary>
    /// Ignore brightness changes caused by the adaptive (sensor-based) brightness mechanism.
    /// </summary>
    public bool IgnoreAdaptiveChanges { get; set; } = false;

    /// <summary>
    /// How often (seconds) the self-healing check runs: verifies the screen brightness still
    /// matches the stored plan values and re-syncs if they drifted (e.g. brightness changed
    /// during sleep/wake without an event). 0 disables the check.
    /// </summary>
    public int PeriodicResyncSeconds { get; set; } = 60;

    /// <summary>
    /// Ignore brightness events arriving shortly after we applied a sync.
    /// Loop protection: re-applying the active scheme can itself emit a brightness event.
    /// </summary>
    public int SuppressEventsAfterApplyMilliseconds { get; set; } = 1000;
}
