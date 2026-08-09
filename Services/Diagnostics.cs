using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BacklightSyncService.Services;

/// <summary>
/// One-shot diagnostics (console mode "--check"): reports what the service sees — WMI brightness
/// class availability, current brightness, all power plans with their stored AC/DC brightness
/// values — and optionally performs a write test and/or listens for brightness events.
/// Everything is also appended to the log file, and <see cref="LogEnvironmentSnapshot"/> is
/// called at service start so every log file contains the full picture.
/// </summary>
public sealed class Diagnostics
{
    private readonly IPowerPlanBrightnessWriter _writer;
    private readonly ILogger<Diagnostics> _logger;
    private readonly IConfiguration _configuration;

    public Diagnostics(IPowerPlanBrightnessWriter writer, ILogger<Diagnostics> logger, IConfiguration configuration)
    {
        _writer = writer;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>Logs the environment snapshot to the log file (used at service start).</summary>
    public void LogEnvironmentSnapshot() => WriteSnapshot(consoleOut: false, writeTest: false);

    /// <summary>Runs the full "--check" diagnostic pass. Returns a process exit code.</summary>
    public async Task<int> RunCheckAsync(bool writeTest, bool listen)
    {
        await Task.Yield();
        WriteSnapshot(consoleOut: true, writeTest: writeTest);

        if (listen)
            await ListenForEventsAsync();

        return 0;
    }

    private void WriteSnapshot(bool consoleOut, bool writeTest)
    {
        void Out(string line)
        {
            if (consoleOut)
                Console.WriteLine(line);
            _logger.LogInformation("{Line}", line);
        }

        Out("=== BacklightSyncService diagnostic snapshot ===");
        Out($"Version   : {typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?"}");
        Out($"Time      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Out($"OS        : {Environment.OSVersion}");
        Out($"Process   : {Environment.ProcessPath ?? "?"} (x64: {Environment.Is64BitProcess})");
        Out($"User      : {WindowsIdentity.GetCurrent()?.Name ?? "?"} (elevated: {IsElevated()})");
        Out($"Power     : {ReadPowerState()}");
        string? logPath = _configuration["Logging:File:Path"];
        Out($"Log file  : {(string.IsNullOrWhiteSpace(logPath) ? "(default)" : Environment.ExpandEnvironmentVariables(logPath))}");

            Out("WMI classes in root\\wmi:");
            Out($"  WmiMonitorBrightness        : {(BrightnessWatcher.WmiClassExists("WmiMonitorBrightness") ? "available" : "NOT available")}");
            Out($"  WmiMonitorBrightnessEvent   : {(BrightnessWatcher.WmiClassExists("WmiMonitorBrightnessEvent") ? "available" : "NOT available")}");
            Out($"  WmiMonitorBrightnessMethods : {(BrightnessWatcher.WmiClassExists("WmiMonitorBrightnessMethods") ? "available" : "NOT available")}");
            Out("Registry fallback (brightness keys still work when WMI classes are missing):");
            var regHives = BrightnessWatcher.ResolveBrightnessRegistryRoots();
            if (regHives.Count > 0)
            {
                Out($"  Brightness registry hives found ({regHives.Count}):");
                foreach (var hive in regHives)
                    Out($"    {hive.SubPath}");
            }
            else
            {
                Out("  Brightness registry hives: NONE found");
            }

        int? current = null;
        try
        {
            current = BrightnessWatcher.ReadCurrentBrightness();
            Out(current is { } b
                ? $"Brightness : {b}%"
                : "Brightness : UNAVAILABLE (no brightness-capable display/driver — the service has nothing to sync)");
        }
        catch (Exception ex)
        {
            Out($"Brightness : read FAILED — {ex.Message}");
        }

        Guid? active = null;
        try
        {
            active = _writer.GetActiveScheme();
            if (active is { } a)
                Out($"Active plan: {a:D}");
        }
        catch (Exception ex)
        {
            Out($"Active plan: read FAILED — {ex.Message}");
        }

        try
        {
            var schemes = _writer.EnumeratePowerSchemes();
            Out($"Plans      : {schemes.Count}");
            foreach (Guid scheme in schemes)
            {
                string name = _writer.GetSchemeName(scheme) ?? "?";
                int? ac = null, dc = null;
                string acNote = "", dcNote = "";
                try { ac = _writer.ReadBrightnessValue(scheme, ac: true); }
                catch (Exception ex) { acNote = $" (read failed: {ex.Message})"; }
                try { dc = _writer.ReadBrightnessValue(scheme, ac: false); }
                catch (Exception ex) { dcNote = $" (read failed: {ex.Message})"; }

                string marker = scheme == active ? "*" : " ";
                Out($"  {marker} {name,-30} {scheme:D}  AC={(ac?.ToString() ?? "?")}{acNote}  DC={(dc?.ToString() ?? "?")}{dcNote}");
            }

            Out("Note: every plan listed above is synchronized on each brightness change — including user-created custom plans. Plans created while the service runs are picked up automatically (see log: 'New power scheme detected').");

            if (writeTest)
            {
                int target = current ?? 50;
                Out($"--- WRITE TEST: writing {target}% (AC+DC) into every plan and reading back ---");
                foreach (Guid scheme in schemes)
                {
                    string name = _writer.GetSchemeName(scheme) ?? scheme.ToString("D");
                    try
                    {
                        _writer.WriteBrightnessValue(scheme, target, ac: true);
                        _writer.WriteBrightnessValue(scheme, target, ac: false);
                        int? ac2 = _writer.ReadBrightnessValue(scheme, ac: true);
                        int? dc2 = _writer.ReadBrightnessValue(scheme, ac: false);
                        string result = ac2 == target && dc2 == target ? "OK" : "MISMATCH";
                        Out($"  {name}: wrote {target}% -> read back AC={ac2?.ToString() ?? "?"} DC={dc2?.ToString() ?? "?"} ({result})");
                    }
                    catch (Exception ex)
                    {
                        Out($"  {name}: WRITE FAILED — {ex.Message}");
                    }
                }
                if (active is { } activeGuid && schemes.Contains(activeGuid))
                {
                    try
                    {
                        _writer.SetActiveScheme(activeGuid);
                        Out("Active plan re-applied.");
                    }
                    catch (Exception ex)
                    {
                        Out($"Re-apply of active plan FAILED — {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Out($"Plan enumeration FAILED — {ex}");
        }

        Out("=== end of snapshot ===");
    }

    private async Task ListenForEventsAsync()
    {
        void Out(string line)
        {
            Console.WriteLine(line);
            _logger.LogInformation("{Line}", line);
        }

        Out("=== Listening for WMI brightness events for 10 seconds ===");
        Out("Press a brightness key or move the brightness slider NOW...");
        Out("");

        int received = 0;
        int adaptive = 0;
        using var watcher = new BrightnessWatcher(NullLogger<BrightnessWatcher>.Instance);
        watcher.BrightnessChanged += (brightness, isAdaptive) =>
        {
            received++;
            if (isAdaptive)
                adaptive++;
            Out($"[{DateTime.Now:HH:mm:ss.fff}] Brightness event: {brightness}% (adaptive: {isAdaptive})");
        };
        watcher.Start();

        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
            await Task.Delay(250);

        watcher.Stop();
        Out("");
        Out($"Events received: {received} (adaptive: {adaptive}).");
        if (received == 0)
        {
            Out("No events arrived. If 'WmiMonitorBrightnessEvent' was reported available above, the driver");
            Out("delivers events but they did not arrive within the window; if it was NOT available, the");
            Out("service's polling fallback (WmiMonitorBrightness) is the active path — check the log for");
            Out("'Poll #...' lines and consider lowering PollingIntervalSeconds to 2.");
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

    private static string ReadPowerState()
    {
        try
        {
            if (Native.GetSystemPowerStatus(out Native.SystemPowerStatus status))
            {
                string line = status.ACLineStatus == 1 ? "On AC power" : "On battery";
                if (status.BatteryLifePercent <= 100)
                    line += $", battery {status.BatteryLifePercent}%";
                return line;
            }
        }
        catch
        {
            // fall through
        }
        return "unknown";
    }

    private static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }
    }
}
