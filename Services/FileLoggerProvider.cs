using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WindowsBacklightSyncService.Services;

/// <summary>Options for the file logger, read from the "Logging:File" configuration section.</summary>
public sealed class FileLoggerOptions
{
    /// <summary>
    /// Master feature flag for the diagnostic file logger.
    /// Shipped DISABLED (production default) so the service writes nothing to disk while
    /// running in the background. Set to true (appsettings.json "Logging:File:Enabled" or
    /// the environment variable "Logging__File__Enabled") to enable diagnostics.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Full path of the log file. Environment variables (%ProgramData% etc.) are expanded.</summary>
    public string Path { get; set; } = @"%ProgramData%\WindowsBacklightSyncService\logs\windows-backlight-sync.log";

    /// <summary>Maximum file size in bytes before rotation (default 5 MB).</summary>
    public long MaxSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>How many rotated backups to keep.</summary>
    public int MaxBackupFiles { get; set; } = 3;

    public static FileLoggerOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new FileLoggerOptions();
        if (bool.TryParse(configuration["Logging:File:Enabled"], out bool enabled))
            options.Enabled = enabled;
        string? path = configuration["Logging:File:Path"];
        if (!string.IsNullOrWhiteSpace(path))
            options.Path = path;
        if (long.TryParse(configuration["Logging:File:MaxSizeBytes"], out long maxSize) && maxSize > 0)
            options.MaxSizeBytes = maxSize;
        if (int.TryParse(configuration["Logging:File:MaxBackupFiles"], out int backups) && backups >= 0)
            options.MaxBackupFiles = backups;
        return options;
    }
}

/// <summary>
/// Minimal, dependency-free file logger provider. Needed because when the process runs as a
/// Windows service there is no console and the Event Log only receives Information+ by default —
/// Debug/Trace diagnostics (WMI events, per-plan writes, exceptions) would otherwise be lost.
/// </summary>
/// <remarks>
/// The provider NEVER throws: if the configured log path cannot be opened (e.g. access denied
/// because the file was created by the LocalSystem service and the current user runs the exe
/// without elevation), it falls back to %LOCALAPPDATA%\WindowsBacklightSyncService\logs\windows-backlight-sync.log.
/// If that fails too, file logging is silently disabled — the host keeps running either way.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly FileLoggerOptions _options;
    private readonly Func<string, LogLevel, bool> _filter;
    private StreamWriter? _writer;
    private long _currentSize;

    /// <summary>Path the logger is actually writing to (may differ from the configured one when a fallback was used).</summary>
    public string? ActivePath { get; private set; }

    /// <summary>True when the configured path was not writable and the %LOCALAPPDATA% fallback is in use.</summary>
    public bool UsedFallbackPath { get; private set; }

    public FileLoggerProvider(FileLoggerOptions options, Func<string, LogLevel, bool> filter)
    {
        _options = options;
        _filter = filter;

        string primaryPath = Environment.ExpandEnvironmentVariables(options.Path);
        _writer = TryOpenWriter(primaryPath);
        if (_writer is not null)
        {
            ActivePath = primaryPath;
        }
        else
        {
            string fallbackPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsBacklightSyncService", "logs", "windows-backlight-sync.log");
            _writer = TryOpenWriter(fallbackPath);
            if (_writer is not null)
            {
                ActivePath = fallbackPath;
                UsedFallbackPath = true;
            }
        }

        if (_writer is not null)
        {
            try { _currentSize = new FileInfo(ActivePath!).Length; }
            catch { _currentSize = 0; } // a transient stat failure must never abort startup
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _filter);

    /// <summary>Attempts to open (create if needed) the given log path for appending. Never throws.</summary>
    private static StreamWriter? TryOpenWriter(string fullPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            return new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch
        {
            return null;
        }
    }

    internal void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine(line);
                _currentSize += line.Length + 2;
                if (_currentSize > _options.MaxSizeBytes)
                    Rotate();
            }
            catch
            {
                // Logging must never break the service.
            }
        }
    }

    private void Rotate()
    {
        if (_writer is null || ActivePath is null)
            return;

        string fullPath = ActivePath;
        string? dir = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileNameWithoutExtension(fullPath);
        string ext = Path.GetExtension(fullPath);

        _writer.Dispose();
        _writer = null;

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = Path.Combine(dir ?? ".", $"{name}-{stamp}{ext}");
            if (File.Exists(fullPath))
                File.Move(fullPath, backup);

            // Prune old backups beyond MaxBackupFiles.
            var backups = Directory.GetFiles(dir ?? ".", $"{name}-*{ext}")
                .OrderBy(f => f)
                .ToList();
            while (backups.Count >= _options.MaxBackupFiles && backups.Count > 0)
            {
                File.Delete(backups[0]);
                backups.RemoveAt(0);
            }
        }
        catch
        {
            // Rotation failure is non-fatal.
        }

        // Re-open the active path after rotation.
        _writer = TryOpenWriter(fullPath);
        _currentSize = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _categoryName;
    private readonly Func<string, LogLevel, bool> _filter;

    public FileLogger(FileLoggerProvider provider, string categoryName, Func<string, LogLevel, bool> filter)
    {
        _provider = provider;
        _categoryName = categoryName;
        _filter = filter;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && _filter(_categoryName, logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        string message = formatter(state, exception);
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelCode(logLevel)}] {_categoryName}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        _provider.Write(line);
    }

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };
}
