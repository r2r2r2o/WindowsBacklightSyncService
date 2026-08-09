using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace BacklightSyncService.Services;

/// <summary>
/// Watches display backlight level changes.
///
/// Change detection is layered so it works regardless of what the display driver exposes:
///   1. WMI event class WmiMonitorBrightnessEvent (root\wmi) — the primary, event-driven
///      signal on drivers that provide it (the typical laptop case).
///   2. Registry watch on the display-brightness values under
///      HKU\&lt;user&gt;\Software\Microsoft\Windows\CurrentVersion\Brightness — a fallback that keeps
///      working even when the WMI brightness classes are missing entirely (e.g. the Intel
///      HD 3000 driver on Windows 10), because Windows itself updates these keys whenever
///      the backlight changes (brightness keys, slider, power plan switch).
///   3. Polling of WmiMonitorBrightness / the registry values — final fallback.
///
/// Requires elevation for the WMI parts (LocalSystem is fine). The registry watch resolves
/// the logged-on user's hive (HKU\&lt;SID&gt;) so it also works when running as a LocalSystem
/// service, where HKCU would point at the service account's own (never-changing) hive.
/// This class never throws on start: any unavailable signal degrades to the remaining ones.
/// </summary>
public sealed class BrightnessWatcher : IDisposable
{
    private readonly ILogger<BrightnessWatcher> _logger;
    private ManagementEventWatcher? _watcher;
    private readonly List<IntPtr> _registryKeyHandles = new();
    private readonly List<IntPtr> _registryEventHandles = new();
    private readonly List<Thread> _registryThreads = new();
    private volatile bool _registryRunning;
    private long _eventsReceived;

    /// <summary>Raised when the backlight level changes. Arguments: (brightnessPercent 0-100, isAdaptiveChange).</summary>
    public event Action<int, bool>? BrightnessChanged;

    /// <summary>Registry sub-path (relative to the hive) of the display brightness values.</summary>
    internal const string BrightnessRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Brightness";

    public BrightnessWatcher(ILogger<BrightnessWatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks whether a WMI class exists in the root\wmi namespace by enumerating the
    /// schema (Meta_Class) and matching the name exactly. A direct "SELECT ... FROM &lt;class&gt;"
    /// would be misleading for event classes (no instances), and a "WHERE Name=..." query on
    /// Meta_Class is unreliable on root\wmi, so full enumeration is used.
    /// </summary>
    public static bool WmiClassExists(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT Name FROM Meta_Class");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                string? name = item["Name"]?.ToString();
                if (string.Equals(name, className, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Resolves every user hive (HKEY_USERS\S-1-5-21-...) that contains the display
    /// brightness values. On multi-account machines more than one hive can hold the key
    /// (e.g. T520 with "Archi" and "ArchiAdmin"); all of them are watched and the value
    /// is read from whichever hive actually changes. Falls back to HKEY_CURRENT_USER for
    /// interactive runs.
    /// </summary>
    internal static List<(IntPtr Root, string SubPath)> ResolveBrightnessRegistryRoots()
    {
        var result = new List<(IntPtr Root, string SubPath)>();

        try
        {
            using RegistryKey? users = Registry.Users;
            foreach (string sub in users.GetSubKeyNames())
            {
                if (!sub.StartsWith("S-1-5-21", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using RegistryKey? candidate = users.OpenSubKey(sub + @"\" + BrightnessRegistryKeyPath);
                    if (candidate is not null)
                        result.Add((Native.HkeyUsers, sub + @"\" + BrightnessRegistryKeyPath));
                }
                catch
                {
                    // skip unreadable hive
                }
            }
        }
        catch
        {
            // fall through to HKCU
        }

        if (result.Count == 0)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BrightnessRegistryKeyPath);
                if (key is not null)
                    result.Add((Native.HkeyCurrentUser, BrightnessRegistryKeyPath));
            }
            catch
            {
                // fall through
            }
        }

        return result;
    }

    /// <summary>Checks whether the display brightness registry values exist for any user.</summary>
    public static bool RegistryBrightnessKeyExists() => ResolveBrightnessRegistryRoots().Count > 0;

    /// <summary>Subscribes to WMI events and starts the registry watch. Safe to call once per instance.</summary>
    public void Start()
    {
        // Log availability of every signal — the most common failure point (driver-dependent).
        bool hasBrightness = WmiClassExists("WmiMonitorBrightness");
        bool hasEvents = WmiClassExists("WmiMonitorBrightnessEvent");
        bool hasMethods = WmiClassExists("WmiMonitorBrightnessMethods");
        bool hasRegistry = RegistryBrightnessKeyExists();
        _logger.LogInformation(
            "Signal availability: WmiMonitorBrightness={HasBrightness}, WmiMonitorBrightnessEvent={HasEvents}, WmiMonitorBrightnessMethods={HasMethods}, RegistryBrightnessKey={HasRegistry}.",
            hasBrightness, hasEvents, hasMethods, hasRegistry);

        StartRegistryWatch();

        if (!hasEvents)
        {
            _logger.LogWarning(
                "WmiMonitorBrightnessEvent is NOT available on this machine/driver — the registry watch and/or polling are used as the change signal.");
        }

        try
        {
            var scope = new ManagementScope(@"root\wmi");
            var query = new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += OnBrightnessEventArrived;
            _watcher.Stopped += OnWatcherStopped;
            _watcher.Start();
            _logger.LogInformation("Subscribed to WMI brightness events (WmiMonitorBrightnessEvent).");
        }
        catch (Exception ex)
        {
            _watcher?.Dispose();
            _watcher = null;
            _logger.LogWarning(ex, "Could not subscribe to WMI brightness events: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Watches the display-brightness registry values via RegNotifyChangeKeyValue (native,
    /// no WMI dependency). Windows updates these values on every backlight change even when
    /// the WMI brightness classes are absent, so this works on machines where the driver
    /// exposes nothing (e.g. Intel HD 3000 on Windows 10). All user hives that contain the
    /// key are watched (multi-account machines).
    /// </summary>
    private void StartRegistryWatch()
    {
        List<(IntPtr Root, string SubPath)> hives = ResolveBrightnessRegistryRoots();
        if (hives.Count == 0)
        {
            _logger.LogWarning(
                "Display brightness registry key not found (hives HKEY_USERS / HKEY_CURRENT_USER, path {Path}) — registry change detection unavailable.",
                BrightnessRegistryKeyPath);
            return;
        }

        foreach ((IntPtr root, string subPath) in hives)
        {
            try
            {
                int openResult = Native.RegOpenKeyEx(
                    root, subPath, 0, Native.KeyRead, out IntPtr keyHandle);
                if (openResult != 0)
                {
                    _logger.LogWarning("RegOpenKeyEx failed ({Result}) for {Path} — this hive will not be watched.",
                        openResult, subPath);
                    continue;
                }

                IntPtr eventHandle = Native.CreateEvent(IntPtr.Zero, false, false, null);
                if (eventHandle == IntPtr.Zero)
                {
                    Native.RegCloseKey(keyHandle);
                    _logger.LogWarning("CreateEvent failed for {Path} — this hive will not be watched.", subPath);
                    continue;
                }

                _registryKeyHandles.Add(keyHandle);
                _registryEventHandles.Add(eventHandle);
                _registryRunning = true;

                var thread = new Thread(() => RegistryNotifyLoop(keyHandle, eventHandle, subPath))
                {
                    IsBackground = true,
                    Name = "BrightnessRegistryWatch"
                };
                _registryThreads.Add(thread);
                thread.Start();

                _logger.LogInformation(
                    "Watching registry hive {Path} for brightness changes (RegNotifyChangeKeyValue).", subPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start registry brightness watch for {Path}: {Message}", subPath, ex.Message);
            }
        }

        if (_registryThreads.Count == 0)
        {
            _logger.LogWarning("No registry brightness hive could be watched — registry change detection unavailable.");
        }
    }

    private void RegistryNotifyLoop(IntPtr keyHandle, IntPtr eventHandle, string subPath)
    {
        while (_registryRunning)
        {
            // Arm the notification (async mode: the event is signaled on change).
            int status = Native.RegNotifyChangeKeyValue(
                keyHandle, true, Native.RegNotifyChangeLastSet, eventHandle, true);
            if (status != 0)
            {
                _logger.LogWarning("RegNotifyChangeKeyValue failed ({Status}) for {Path} — this registry watch stopped.", status, subPath);
                break;
            }

            // Wait up to 1 s so shutdown is responsive; WAIT_OBJECT_0 (0) = change detected.
            if (Native.WaitForSingleObject(eventHandle, 1000) == 0)
            {
                _logger.LogDebug("Registry brightness change detected in {Path}.", subPath);
                try
                {
                    int? brightness = ReadRegistryBrightness();
                    if (brightness is { } b)
                        RaiseChanged(b, adaptive: false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to process registry brightness change.");
                }
            }
        }
    }

    /// <summary>Reads the current brightness from the registry values (AutoAdaptive preferred), across all user hives.</summary>
    internal static int? ReadRegistryBrightness()
    {
        foreach ((IntPtr root, string subPath) in ResolveBrightnessRegistryRoots())
        {
            try
            {
                RegistryKey? key = root == Native.HkeyCurrentUser
                    ? Registry.CurrentUser.OpenSubKey(BrightnessRegistryKeyPath)
                    : Registry.Users.OpenSubKey(subPath);

                using (key)
                {
                    if (key is null)
                        continue;

                    foreach (string name in new[] { "AutoAdaptive", "Value", "SensorValue" })
                    {
                        object? raw = key.GetValue(name);
                        if (raw is not null)
                        {
                            int value = Convert.ToInt32(raw);
                            if (value >= 0 && value <= 100)
                                return value;
                        }
                    }
                }
            }
            catch
            {
                // try the next hive
            }
        }
        return null;
    }

    public void Stop()
    {
        _registryRunning = false;
        foreach (Thread thread in _registryThreads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _registryThreads.Clear();
        foreach (IntPtr handle in _registryKeyHandles)
        {
            Native.RegCloseKey(handle);
        }
        _registryKeyHandles.Clear();
        foreach (IntPtr handle in _registryEventHandles)
        {
            Native.CloseHandle(handle);
        }
        _registryEventHandles.Clear();

        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.EventArrived -= OnBrightnessEventArrived;
            watcher.Stopped -= OnWatcherStopped;
            try { watcher.Stop(); } catch { /* already stopped */ }
            watcher.Dispose();
        }
    }

    /// <summary>
    /// Queries the current backlight level (0-100): WMI WmiMonitorBrightness first, then the
    /// registry values. Returns null when no brightness source is available.
    /// </summary>
    public static int? ReadCurrentBrightness()
    {
        int? wmi = ReadWmiBrightness();
        if (wmi is not null)
            return wmi;
        return ReadRegistryBrightness();
    }

    private static int? ReadWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                object? raw = item["CurrentBrightness"];
                if (raw is not null && raw is not DBNull)
                    return Convert.ToInt32(raw);
            }
        }
        catch
        {
            // fall through to registry
        }
        return null;
    }

    private void OnBrightnessEventArrived(object sender, EventArrivedEventArgs e)
    {
        long count = Interlocked.Increment(ref _eventsReceived);
        try
        {
            // First few events: log the delivered event schema, so driver-specific
            // property layouts (e.g. Intel HD 3000) are visible in the log.
            if (count <= 5)
                LogEventSchema(e);

            int? brightness = TryExtractBrightness(e, out bool adaptive);
            if (brightness is null)
            {
                // Driver-specific schema: the event is still a reliable "brightness
                // changed" signal — read the current value the proven way (instance query).
                _logger.LogDebug("WMI event #{Count}: no readable brightness property — falling back to the instance query.", count);
                brightness = ReadCurrentBrightness();
            }

            if (brightness is { } b)
            {
                _logger.LogDebug("WMI brightness event #{Count}: Brightness={Brightness} (adaptive={Adaptive}).", count, b, adaptive);
                RaiseChanged(b, adaptive);
            }
            else
            {
                _logger.LogDebug("WMI event #{Count} was delivered, but no brightness value could be obtained from it.", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process WMI brightness event #{Count}.", count);
        }
    }

    /// <summary>
    /// Extracts the brightness value (0-100) from a delivered event, tolerating
    /// driver-specific property names. Tries known candidates, then parses the event's
    /// MOF text, and returns null only if nothing worked.
    /// </summary>
    private static int? TryExtractBrightness(EventArrivedEventArgs e, out bool adaptive)
    {
        adaptive = false;

        foreach (string name in new[] { "Brightness", "CurrentBrightness", "Value", "Level", "BrightnessLevel" })
        {
            int? value = TryGetEventPropertyInt(e, name);
            if (value is { } v && v >= 0 && v <= 100)
            {
                adaptive = TryGetEventPropertyBool(e, "Adaptive") ?? false;
                return v;
            }
        }

        int? fromMof = ParseBrightnessFromMof(e);
        if (fromMof is { } m)
        {
            adaptive = TryGetEventPropertyBool(e, "Adaptive") ?? false;
            return m;
        }

        return null;
    }

    private static int? TryGetEventPropertyInt(EventArrivedEventArgs e, string name)
    {
        try
        {
            object? raw = e.NewEvent.Properties[name]?.Value;
            if (raw is null || raw is DBNull)
                return null;
            return Convert.ToInt32(raw);
        }
        catch
        {
            return null; // property missing or unreadable — try the next candidate
        }
    }

    private static bool? TryGetEventPropertyBool(EventArrivedEventArgs e, string name)
    {
        try
        {
            return e.NewEvent.Properties[name]?.Value is bool value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ParseBrightnessFromMof(EventArrivedEventArgs e)
    {
        try
        {
            string mof = e.NewEvent.GetText(TextFormat.Mof);
            Match match = Regex.Match(mof, @"Brightness\s*=\s*(\d{1,3})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int value = int.Parse(match.Groups[1].Value);
                if (value >= 0 && value <= 100)
                    return value;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private void LogEventSchema(EventArrivedEventArgs e)
    {
        try
        {
            string className = e.NewEvent.ClassPath.ClassName;
            var names = new List<string>();
            foreach (PropertyData property in e.NewEvent.Properties)
            {
                try { names.Add(property.Name); }
                catch { names.Add("<unreadable>"); }
            }
            _logger.LogDebug("WMI event schema: class={Class}, properties=[{Names}].", className, string.Join(", ", names));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not log WMI event schema.");
        }
    }

    private void RaiseChanged(int brightness, bool adaptive)
    {
        _logger.LogDebug("Brightness signal: {Brightness}% (adaptive: {Adaptive}).", brightness, adaptive);
        BrightnessChanged?.Invoke(brightness, adaptive);
    }

    private void OnWatcherStopped(object sender, StoppedEventArgs e)
    {
        _logger.LogWarning("WMI event watcher stopped (status: {Status}).", e.Status);
    }

    public void Dispose() => Stop();

    private static class Native
    {
        internal static readonly IntPtr HkeyCurrentUser = new(0x80000001);
        internal static readonly IntPtr HkeyUsers = new(0x80000003);

        internal const int KeyRead = 0x20019;            // KEY_READ (includes KEY_NOTIFY)
        internal const uint RegNotifyChangeLastSet = 0x4; // REG_NOTIFY_CHANGE_LAST_SET

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, bool fAsynchronous);

        [DllImport("advapi32.dll")]
        internal static extern int RegCloseKey(IntPtr hKey);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
