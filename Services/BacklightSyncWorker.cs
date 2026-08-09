using System.Security.Principal;
using Microsoft.Extensions.Options;

namespace WindowsBacklightSyncService.Services;

/// <summary>
/// Core service logic: on every display backlight change (debounced), write the current level
/// to the AC and DC brightness value indexes of every Windows power plan, then re-apply the
/// active plan so the value takes effect immediately.
/// </summary>
public sealed class BacklightSyncWorker : BackgroundService
{
    private readonly ILogger<BacklightSyncWorker> _logger;
    private readonly IOptionsMonitor<BacklightSyncOptions> _options;
    private readonly IPowerPlanBrightnessWriter _writer;
    private readonly BrightnessWatcher _watcher;
    private readonly PowerEventMonitor _powerEventMonitor;
    private readonly Diagnostics _diagnostics;

    private readonly object _gate = new();
    private readonly HashSet<Guid> _knownSchemes = new();
    private CancellationTokenSource? _debounceCts;
    private int _lastAppliedBrightness = -1;
    private int _lastObservedBrightness = -1;
    private long _lastApplyTick;
    private long _lastPeriodicCheckTick;
    private bool _pollFailureLogged;
    private long _syncCount;
    private long _eventCount;
    private int _pollCount;
    private int _lastSchemeCount;
    private DateTime _lastSyncTime;
    private int _lastSyncBrightness = -1;

    public BacklightSyncWorker(
        ILogger<BacklightSyncWorker> logger,
        IOptionsMonitor<BacklightSyncOptions> options,
        IPowerPlanBrightnessWriter writer,
        BrightnessWatcher watcher,
        PowerEventMonitor powerEventMonitor,
        Diagnostics diagnostics)
    {
        _logger = logger;
        _options = options;
        _writer = writer;
        _watcher = watcher;
        _powerEventMonitor = powerEventMonitor;
        _diagnostics = diagnostics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        _logger.LogInformation(
            "Backlight sync starting. OS={Os}, user={User}, elevated={Elevated}, x64={X64}",
            Environment.OSVersion, WindowsIdentity.GetCurrent()?.Name ?? "?", IsElevated(), Environment.Is64BitProcess);
        _logger.LogInformation(
            "Configuration: debounce={Debounce}ms, polling={Poll}s, initialSync={Initial}, syncAC={Ac}, syncDC={Dc}, writeOnlyWhenChanged={Woc}, reapplyActive={Reapply}, ignoreAdaptive={IgnoreAdaptive}, suppressAfterApply={Suppress}ms",
            opts.DebounceMilliseconds, opts.PollingIntervalSeconds, opts.InitialSyncOnStart,
            opts.SyncAcValue, opts.SyncDcValue, opts.WriteOnlyWhenChanged,
            opts.ReapplyActiveScheme, opts.IgnoreAdaptiveChanges, opts.SuppressEventsAfterApplyMilliseconds);

        _watcher.BrightnessChanged += OnBrightnessChanged;
        _powerEventMonitor.Resumed += OnSystemResumed;
        _powerEventMonitor.Start();
        _watcher.Start();

        // Environment snapshot at startup — always available in the log file.
        try
        {
            _diagnostics.LogEnvironmentSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Environment snapshot failed.");
        }

        if (opts.InitialSyncOnStart)
        {
            try
            {
                int? initial = BrightnessWatcher.ReadCurrentBrightness();
                if (initial is { } b)
                {
                    _lastObservedBrightness = b;
                    _logger.LogInformation("Initial sync: current brightness is {Brightness}% — writing to all plans.", b);
                    ApplyBrightness(b, force: true);
                }
                else
                {
                    _logger.LogWarning("Initial sync: WmiMonitorBrightness returned no value (no brightness-capable display?) — nothing to sync on start.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial brightness read failed: {Message}", ex.Message);
            }
        }

        try
        {
            if (opts.PollingIntervalSeconds > 0)
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.PollingIntervalSeconds));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    PollBrightness();
                }
            }
            else
            {
                _logger.LogInformation("Polling is disabled (PollingIntervalSeconds=0); only WMI events trigger syncs.");
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _watcher.BrightnessChanged -= OnBrightnessChanged;
            _powerEventMonitor.Resumed -= OnSystemResumed;
            _watcher.Stop();
            _powerEventMonitor.Dispose();
        }

        _logger.LogInformation(
            "Backlight sync stopped. WMI events seen: {Events}, syncs performed: {Syncs}.",
            Interlocked.Read(ref _eventCount), Interlocked.Read(ref _syncCount));
    }

    /// <summary>
    /// The system woke up. Re-establish every change-detection signal (WMI subscriptions can
    /// silently die during sleep) and re-sync: Windows may have changed the brightness while
    /// the machine was asleep (e.g. AC/DC transition), so push the current value everywhere.
    /// </summary>
    private void OnSystemResumed()
    {
        try
        {
            _logger.LogInformation("Post-resume: re-establishing brightness signals.");
            _watcher.Stop();
            _watcher.Start();

            int? current = BrightnessWatcher.ReadCurrentBrightness();
            if (current is { } c)
            {
                _logger.LogInformation("Post-resume sync: brightness {Brightness}% — writing to all plans.", c);
                _lastObservedBrightness = c;
                ApplyBrightness(c, force: true);
            }
            else
            {
                _logger.LogWarning("Post-resume: could not read the current brightness.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-resume re-sync failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Fallback change detection: WMI events are the primary signal, but if they are unavailable
    /// (or miss a change), polling WmiMonitorBrightness still catches it.
    /// </summary>
    private void PollBrightness()
    {
        try
        {
            // Self-healing: if the WMI event subscription died (e.g. after sleep/wake),
            // restart it so event-driven syncs resume instead of relying on polling alone.
            if (!_watcher.WmiWatcherAlive)
            {
                _logger.LogWarning("WMI event subscription is not alive — restarting it.");
                try
                {
                    _watcher.Stop();
                    _watcher.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WMI event subscription restart failed (will retry on next poll).");
                }
            }

            int? current = BrightnessWatcher.ReadCurrentBrightness();
            _pollCount++;
            _logger.LogDebug(
                "Poll #{Poll}: brightness={Brightness}% (last observed={Last}).",
                _pollCount, current?.ToString() ?? "<unavailable>", _lastObservedBrightness);

            if (current is { } c)
            {
                if (c != _lastObservedBrightness)
                {
                    _logger.LogInformation("Polling detected brightness change: {Brightness}% (was {Last}%).", c, _lastObservedBrightness);
                    _lastObservedBrightness = c;
                    OnBrightnessChanged(c, adaptive: false);
                }
                else
                {
                    if (_pollCount % 20 == 0)
                    {
                        // Debug level: heartbeat is for log-file diagnosis, not for the Event Log.
                        _logger.LogDebug(
                            "Heartbeat: brightness={Brightness}%, events={Events}, syncs={Syncs}, lastSync={LastSync} ({LastBrightness}%), schemes={Schemes}",
                            c, Interlocked.Read(ref _eventCount), Interlocked.Read(ref _syncCount),
                            _lastSyncTime == default ? "never" : _lastSyncTime.ToString("HH:mm:ss"),
                            _lastSyncBrightness, _lastSchemeCount);
                    }

                    // Periodic self-healing: if the screen and the stored plan values drifted
                    // apart without any event (e.g. Windows changed brightness during sleep),
                    // re-sync. Normally nothing happens here because everything already matches.
                    var opts = _options.CurrentValue;
                    if (opts.PeriodicResyncSeconds > 0
                        && Environment.TickCount64 - _lastPeriodicCheckTick >= opts.PeriodicResyncSeconds * 1000L)
                    {
                        _lastPeriodicCheckTick = Environment.TickCount64;
                        if (c != _lastAppliedBrightness)
                        {
                            _logger.LogInformation(
                                "Periodic check: screen is {Brightness}% but last applied was {Last}% — re-syncing all plans.",
                                c, _lastAppliedBrightness);
                            _lastObservedBrightness = c;
                            ApplyBrightness(c, force: true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_pollFailureLogged)
            {
                _pollFailureLogged = true;
                _logger.LogWarning(ex, "Brightness polling failed: {Message}", ex.Message);
            }
            else
            {
                _logger.LogDebug(ex, "Brightness polling failed.");
            }
        }
    }

    private void OnBrightnessChanged(int brightness, bool adaptive)
    {
        var opts = _options.CurrentValue;
        long count = Interlocked.Increment(ref _eventCount);
        _logger.LogDebug("Brightness change #{Count}: {Brightness}% (adaptive={Adaptive}).", count, brightness, adaptive);

        if (adaptive && opts.IgnoreAdaptiveChanges)
        {
            _logger.LogDebug("Ignoring adaptive brightness change ({Brightness}%) — IgnoreAdaptiveChanges=true.", brightness);
            return;
        }

        if (IsInSuppressionWindow(opts))
        {
            // Loop protection: re-applying the active scheme can emit a brightness event with
            // the value we just applied. Only such SAME-VALUE events are suppressed — a real
            // user change (different value) inside the window must still pass through.
            int lastApplied = _lastAppliedBrightness;
            if (brightness == lastApplied)
            {
                _logger.LogDebug(
                    "Ignoring change ({Brightness}%) inside the post-apply suppression window — same as last applied ({Last}%); loop protection.",
                    brightness, lastApplied);
                return;
            }
            _logger.LogDebug(
                "Change ({Brightness}%) inside the suppression window differs from last applied ({Last}%) — processing it anyway.",
                brightness, lastApplied);
        }

        lock (_gate)
        {
            // Debounce: cancel and dispose any previous token source, then schedule a new one.
            var previous = _debounceCts;
            if (previous is not null)
            {
                try { previous.Cancel(); } catch { }
                try { previous.Dispose(); } catch { }
            }

            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _logger.LogTrace("Debounce scheduled: {Brightness}% in {Delay}ms.", brightness, opts.DebounceMilliseconds);

            // Keep the poll in sync so it does not re-report the same change.
            _lastObservedBrightness = brightness;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(opts.DebounceMilliseconds, cts.Token).ConfigureAwait(false);
                    ApplyBrightness(brightness, force: false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogTrace("Debounced change {Brightness}% superseded by a newer one.", brightness);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Brightness synchronization failed: {Message}", ex.Message);
                }
                finally
                {
                    // Clear and dispose the CTS we created if it's still the active one.
                    lock (_gate)
                    {
                        if (_debounceCts == cts)
                            _debounceCts = null;
                    }
                    try { cts.Dispose(); } catch { }
                }
            }, cts.Token);
        }
    }

    private bool IsInSuppressionWindow(BacklightSyncOptions opts)
    {
        long lastTick = Interlocked.Read(ref _lastApplyTick);
        if (lastTick == 0)
            return false;
        return Environment.TickCount64 - lastTick < opts.SuppressEventsAfterApplyMilliseconds;
    }

    /// <summary>
    /// PowerEnumerate returns every scheme registered in Windows — the default plans AND
    /// user-created custom plans (Control Panel → Power Options → Create a power plan,
    /// or "powercfg -duplicatescheme"). This detects plans added/removed while the service
    /// is running so custom plans are always included in the sync, and logs the change.
    /// </summary>
    private void LogSchemeChanges(IReadOnlyList<Guid> schemes)
    {
        var schemeSet = new HashSet<Guid>(schemes);

        var added = schemeSet.Where(s => !_knownSchemes.Contains(s)).ToList();
        foreach (Guid scheme in added)
        {
            _knownSchemes.Add(scheme);
            _logger.LogInformation(
                "New power scheme detected (user-created custom plan?): \"{Name}\" ({Guid}) — it will be synchronized on every brightness change.",
                _writer.GetSchemeName(scheme) ?? "?", scheme);
        }

        var removed = _knownSchemes.Where(s => !schemeSet.Contains(s)).ToList();
        foreach (Guid scheme in removed)
        {
            _knownSchemes.Remove(scheme);
            _logger.LogInformation("Power scheme no longer present on the system: {Guid} — it will no longer be synchronized.", scheme);
        }
    }

    private void ApplyBrightness(int brightnessPercent, bool force)
    {
        var opts = _options.CurrentValue;
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);

        lock (_gate)
        {
            if (!force && brightnessPercent == _lastAppliedBrightness)
            {
                _logger.LogTrace("Brightness {Brightness}% already synchronized (last applied); skipping.", brightnessPercent);
                return;
            }

            IReadOnlyList<Guid> schemes;
            try
            {
                schemes = _writer.EnumeratePowerSchemes();
                _lastSchemeCount = schemes.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate power schemes: {Message}", ex.Message);
                return;
            }

            if (schemes.Count == 0)
            {
                _logger.LogWarning("No power schemes found — nothing to synchronize.");
                return;
            }

            LogSchemeChanges(schemes);

            _logger.LogDebug("Sync #{Sync}: target brightness {Brightness}% over {Count} plan(s):",
                Interlocked.Read(ref _syncCount) + 1, brightnessPercent, schemes.Count);

            int updated = 0;
            foreach (Guid scheme in schemes)
            {
                try
                {
                    string name = _writer.GetSchemeName(scheme) ?? scheme.ToString("D");
                    bool writeAc = false, writeDc = false;
                    int? acStored = null, dcStored = null;

                    if (opts.SyncAcValue)
                    {
                        try { acStored = _writer.ReadBrightnessValue(scheme, ac: true); }
                        catch (Exception ex) { _logger.LogDebug(ex, "AC read failed for {Name} ({Guid}).", name, scheme); }
                        writeAc = !opts.WriteOnlyWhenChanged || acStored is null || acStored.Value != brightnessPercent;
                    }

                    if (opts.SyncDcValue)
                    {
                        try { dcStored = _writer.ReadBrightnessValue(scheme, ac: false); }
                        catch (Exception ex) { _logger.LogDebug(ex, "DC read failed for {Name} ({Guid}).", name, scheme); }
                        writeDc = !opts.WriteOnlyWhenChanged || dcStored is null || dcStored.Value != brightnessPercent;
                    }

                    if (writeAc)
                        _writer.WriteBrightnessValue(scheme, brightnessPercent, ac: true);
                    if (writeDc)
                        _writer.WriteBrightnessValue(scheme, brightnessPercent, ac: false);

                    _logger.LogDebug("  {Name}: stored AC={Ac} DC={Dc} -> wrote AC={WriteAc} DC={WriteDc}",
                        name, acStored?.ToString() ?? "?", dcStored?.ToString() ?? "?", writeAc, writeDc);

                    if (writeAc || writeDc)
                        updated++;
                }
                catch (Exception ex)
                {
                    // One broken scheme must not prevent the others from being synced.
                    _logger.LogWarning(ex, "Failed to sync brightness for power scheme {Scheme}: {Message}", scheme, ex.Message);
                }
            }

            if (updated > 0 && opts.ReapplyActiveScheme)
            {
                try
                {
                    Guid? active = _writer.GetActiveScheme();
                    if (active is { } activeGuid && schemes.Contains(activeGuid))
                    {
                        string activeName = _writer.GetSchemeName(activeGuid) ?? activeGuid.ToString("D");
                        _writer.SetActiveScheme(activeGuid);
                        _logger.LogDebug("Re-applied active scheme {Name} ({Guid}).", activeName, activeGuid);
                    }
                    else
                    {
                        _logger.LogDebug("Active scheme could not be determined ({Active}) — skipping re-apply.",
                            active?.ToString("D") ?? "null");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-apply the active power scheme: {Message}", ex.Message);
                }
            }
            else if (updated == 0)
            {
                _logger.LogDebug("All {Count} plan(s) already store {Brightness}% — nothing written.", schemes.Count, brightnessPercent);
            }

            _lastAppliedBrightness = brightnessPercent;
            _lastSyncBrightness = brightnessPercent;
            _lastSyncTime = DateTime.Now;
            Interlocked.Increment(ref _syncCount);
            Interlocked.Exchange(ref _lastApplyTick, Environment.TickCount64);

            _logger.LogInformation(
                "Synchronized display brightness {Brightness}% across {Plans} power plan(s) ({Updated} updated) — sync #{Sync}.",
                brightnessPercent, schemes.Count, updated, Interlocked.Read(ref _syncCount));
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stop requested — cancelling pending debounce.");
        lock (_gate)
        {
            var cts = _debounceCts;
            if (cts is not null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                _debounceCts = null;
            }
        }
        await base.StopAsync(cancellationToken);
    }
}
