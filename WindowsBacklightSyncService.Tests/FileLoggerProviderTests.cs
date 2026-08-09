using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WindowsBacklightSyncService.Services;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

/// <summary>
/// Tests for the file logger: writes, rotation, and the fallback path.
/// Uses temp directories only — no real ProgramData involved.
/// </summary>
public class FileLoggerProviderTests
{
    private static FileLoggerOptions Options(string path, long maxSize = 5 * 1024 * 1024, int backups = 3)
        => new() { Path = path, MaxSizeBytes = maxSize, MaxBackupFiles = backups };

    private static Func<string, LogLevel, bool> AcceptAll => (_, _) => true;

    [Fact]
    public void WritesLines_ToConfiguredPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N"));
        string logFile = Path.Combine(dir, "test.log");

        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("hello {Number}", 42);
        }

        Assert.True(File.Exists(logFile));
        Assert.Contains("hello 42", File.ReadAllText(logFile));
    }

    [Fact]
    public void CreatesMissingDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N"), "nested");
        string logFile = Path.Combine(dir, "test.log");

        using var provider = new FileLoggerProvider(Options(logFile), AcceptAll);
        provider.CreateLogger("Test").LogInformation("x");

        Assert.True(File.Exists(logFile));
    }

    [Fact]
    public void Rotates_WhenExceedingMaxSize()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N"));
        string logFile = Path.Combine(dir, "test.log");

        using (var provider = new FileLoggerProvider(Options(logFile, maxSize: 256, backups: 3), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            for (int i = 0; i < 100; i++)
                logger.LogInformation("line {Number} padding padding padding", i);
        }

        // The active file exists and at least one rotated backup was created.
        Assert.True(File.Exists(logFile));
        Assert.True(Directory.GetFiles(dir, "test-*.log").Length > 0);
    }

    [Fact]
    public void PrunesOldBackups_BeyondMaxBackupFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N"));
        string logFile = Path.Combine(dir, "test.log");

        using (var provider = new FileLoggerProvider(Options(logFile, maxSize: 128, backups: 2), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            for (int i = 0; i < 200; i++)
                logger.LogInformation("line {Number} padding padding padding", i);
        }

        Assert.True(Directory.GetFiles(dir, "test-*.log").Length <= 2);
    }

    [Fact]
    public void FallsBack_ToLocalAppData_WhenPrimaryPathUnusable()
    {
        // Make the primary path unusable: point it into an existing FILE.
        string blocker = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");
        string unusable = Path.Combine(blocker, "logs", "test.log");

        var provider = new FileLoggerProvider(Options(unusable), AcceptAll);

        try
        {
            Assert.True(provider.UsedFallbackPath);
            Assert.NotNull(provider.ActivePath);
            Assert.NotEqual(Environment.ExpandEnvironmentVariables(unusable), provider.ActivePath);

            provider.CreateLogger("Test").LogInformation("fell back");
            Assert.True(File.Exists(provider.ActivePath));
        }
        finally
        {
            provider.Dispose();
            if (provider.ActivePath is not null && File.Exists(provider.ActivePath))
                File.Delete(provider.ActivePath);
            File.Delete(blocker);
        }
    }

    [Fact]
    public void NeverThrows_WhenNothingWritable()
    {
        // Even with a hopeless path, the provider must construct and logging must no-op.
        string root = Path.GetTempPath();
        string baseDir = Path.Combine(root, "bls-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        string hopeless = Path.Combine(baseDir, "dir-as-file");
        // Make the parent path a file so creating the log dir fails.
        File.WriteAllText(hopeless, "blocker");
        string logFile = Path.Combine(hopeless, "sub", "test.log");

        using var provider = new FileLoggerProvider(Options(logFile), AcceptAll);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("must not throw");

        Assert.True(true); // reached without exception
    }

    // ---------- FromConfiguration ----------

    [Fact]
    public void FromConfiguration_ParsesAllKeys()
    {
        string json = """
            {
              "Logging": {
                "File": {
                  "Enabled": true,
                  "Path": "C:\\custom\\dir\\my.log",
                  "MaxSizeBytes": 12345,
                  "MaxBackupFiles": 7
                }
              }
            }
            """;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = FileLoggerOptions.FromConfiguration(config);

        Assert.True(options.Enabled);
        Assert.Equal(@"C:\custom\dir\my.log", options.Path);
        Assert.Equal(12345, options.MaxSizeBytes);
        Assert.Equal(7, options.MaxBackupFiles);
    }

    [Fact]
    public void FromConfiguration_Defaults_WhenSectionMissing()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var options = FileLoggerOptions.FromConfiguration(config);

        Assert.False(options.Enabled); // production default: off
        Assert.Contains("%ProgramData%", options.Path);
        Assert.Equal(5 * 1024 * 1024, options.MaxSizeBytes);
        Assert.Equal(3, options.MaxBackupFiles);
    }

    [Fact]
    public void FromConfiguration_InvalidValues_AreIgnored()
    {
        string json = """
            { "Logging": { "File": { "Enabled": "not-a-bool", "MaxSizeBytes": "-5", "MaxBackupFiles": "x" } } }
            """;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = FileLoggerOptions.FromConfiguration(config);

        Assert.False(options.Enabled);          // unparsable -> default
        Assert.Equal(5 * 1024 * 1024, options.MaxSizeBytes);
        Assert.Equal(3, options.MaxBackupFiles);
    }

    // ---------- Logging behavior ----------

    [Fact]
    public void RespectsLevelFilter()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), (_, level) => level >= LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("info line"); // filtered out
            logger.LogWarning("warn line");     // passes
            logger.LogError("error line");      // passes
        }

        string content = File.ReadAllText(logFile);
        Assert.DoesNotContain("info line", content);
        Assert.Contains("warn line", content);
        Assert.Contains("error line", content);
    }

    [Fact]
    public void IncludesExceptionDetails()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogError(new InvalidOperationException("boom"), "sync failed");
        }

        string content = File.ReadAllText(logFile);
        Assert.Contains("sync failed", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void IncludesLevelCodeAndTimestamp()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            provider.CreateLogger("Test").LogInformation("hello");
        }

        string line = File.ReadAllLines(logFile).Single();
        Assert.Contains("[INF]", line);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}", line);
    }

    [Fact]
    public void FileCanBeReadWhileLogging_FileShareReadWrite()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            provider.CreateLogger("Test").LogInformation("first");
            // The writer holds the file with FileShare.ReadWrite — a reader must be able
            // to open and read it (this is what Get-Content -Wait does while diagnosing).
            using var reader = new StreamReader(new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string content = reader.ReadToEnd();
            Assert.Contains("first", content);
        }
    }

    // ---------- FileLogger edge cases ----------

    [Fact]
    public void FileLogger_AppendsExceptionDetails()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            logger.Log(LogLevel.Information, 0, "state", new InvalidOperationException("boom"),
                (state, ex) => $"msg {state} ex={ex?.Message}");
        }

        string content = File.ReadAllText(logFile);
        Assert.Contains("msg state ex=boom", content);
    }

    [Fact]
    public void FileLogger_IsEnabled_RespectsFilterAndNone()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), (_, level) => level >= LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Test");

            Assert.True(logger.IsEnabled(LogLevel.Error));
            Assert.False(logger.IsEnabled(LogLevel.Information));
            Assert.False(logger.IsEnabled(LogLevel.None));
            Assert.False(logger.IsEnabled(LogLevel.Trace)); // below Warning
        }
    }

    [Fact]
    public void FileLogger_BeginScope_ReturnsNullScope()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            var logger = provider.CreateLogger("Test");
            Assert.Null(logger.BeginScope("scope"));
        }
    }

    [Fact]
    public void FileLogger_CriticalLevel_WritesCritCode()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        using (var provider = new FileLoggerProvider(Options(logFile), AcceptAll))
        {
            provider.CreateLogger("Test").LogCritical("critical!");
        }
        string line = File.ReadAllLines(logFile).Single();
        Assert.Contains("[CRT]", line);
    }

    [Fact]
    public void Provider_Dispose_Twice_DoesNotThrow()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        var provider = new FileLoggerProvider(Options(logFile), AcceptAll);
        provider.Dispose();
        provider.Dispose(); // second dispose must be a no-op
    }

    [Fact]
    public void Write_AfterDispose_DoesNotThrow()
    {
        string logFile = Path.Combine(Path.GetTempPath(), "bls-test-" + Guid.NewGuid().ToString("N") + ".log");
        var provider = new FileLoggerProvider(Options(logFile), AcceptAll);
        var logger = provider.CreateLogger("Test");
        provider.Dispose();

        logger.LogInformation("after dispose"); // must not throw
        Assert.True(true);
    }
}
