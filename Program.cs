using WindowsBacklightSyncService;
using WindowsBacklightSyncService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

// Version banner — printed on every console run so it is always verifiable which
// build is deployed (e.g. "WindowsBacklightSyncService v1.2.0").
string appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
Console.WriteLine($"WindowsBacklightSyncService v{appVersion} — synchronizes display backlight across all Windows power plans.");

try
{
    var builder = Host.CreateApplicationBuilder(args);

// "--check" prints its own human-readable output to the console; silence the default
// console logger (above Warning) so lines are not printed twice.
if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
{
    builder.Logging.AddFilter("Default", LogLevel.Warning);
}

// Run as a Windows service (LocalSystem by default when installed via SCM).
// The same executable also runs as a console app for debugging.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WindowsBacklightSyncService";
});

builder.Services.Configure<BacklightSyncOptions>(builder.Configuration.GetSection("BacklightSync"));

// File logger: when running as a service there is no console and the Event Log only
// receives Information+ by default — Debug/Trace diagnostics (WMI events, per-plan
// writes, exceptions) would otherwise be lost.
// Feature flag: set "Logging:File:Enabled" to false (appsettings.json or the
// Logging__File__Enabled environment variable) to turn file logging off entirely.
// Never fatal: if the configured path is not writable (e.g. the log file was created by
// the LocalSystem service under %ProgramData% and you run the exe without elevation),
// the logger falls back to %LOCALAPPDATA%; if that fails too, file logging is disabled.
var fileLoggerOptions = FileLoggerOptions.FromConfiguration(builder.Configuration);
if (fileLoggerOptions.Enabled)
{
    var fileLoggerProvider = new FileLoggerProvider(fileLoggerOptions, LogFilterBuilder.Build(builder.Configuration));
    builder.Logging.AddProvider(fileLoggerProvider);

    if (fileLoggerProvider.ActivePath is null)
    {
        ReportDiagnostic($"File logging disabled: no writable log location. Configured path: '{fileLoggerOptions.Path}'.");
    }
    else if (fileLoggerProvider.UsedFallbackPath)
    {
        ReportDiagnostic($"Log path '{fileLoggerOptions.Path}' is not writable for the current user; writing to '{fileLoggerProvider.ActivePath}' instead.");
    }
}

builder.Services.AddSingleton<BrightnessWatcher>();
builder.Services.AddSingleton<PowerEventMonitor>();
builder.Services.AddSingleton<IPowerPlanBrightnessWriter, PowerPlanBrightnessWriter>();
builder.Services.AddSingleton<Diagnostics>();
builder.Services.AddHostedService<BacklightSyncWorker>();

var host = builder.Build();

// "--check": one-shot diagnostic mode (no service loop). Options:
//   --check                read-only snapshot (WMI availability, brightness, plans)
//   --check --write-test   additionally write current brightness into every plan and read back
//   --check --listen       additionally listen for WMI brightness events for 10 seconds
if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
{
    var diagnostics = host.Services.GetRequiredService<Diagnostics>();
    int exitCode = await diagnostics.RunCheckAsync(
        writeTest: args.Contains("--write-test", StringComparer.OrdinalIgnoreCase),
        listen: args.Contains("--listen", StringComparer.OrdinalIgnoreCase));
    return exitCode;
}

await host.RunAsync();
return 0;
}
catch (Exception ex)
{
    // No startup failure may ever surface as a raw crash dump: report it in a friendly
    // way (console + Event Log) and exit non-zero.
    ReportDiagnostic($"Fatal error: {ex}");
    return 1;
}

// Best-effort reporting of logger/configuration problems: console (when run interactively)
// + Event Log. Must never throw.
static void ReportDiagnostic(string message)
{
    try { Console.Error.WriteLine($"WindowsBacklightSyncService: {message}"); }
    catch { /* ignore */ }

    try
    {
        System.Diagnostics.EventLog.WriteEntry(
            "WindowsBacklightSyncService", message, System.Diagnostics.EventLogEntryType.Warning);
    }
    catch
    {
        try
        {
            System.Diagnostics.EventLog.WriteEntry(
                "Application", message, System.Diagnostics.EventLogEntryType.Warning);
        }
        catch { /* ignore */ }
    }
}
