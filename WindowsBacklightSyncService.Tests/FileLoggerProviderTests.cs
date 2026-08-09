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
}
