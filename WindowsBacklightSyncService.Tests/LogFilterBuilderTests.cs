using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WindowsBacklightSyncService.Services;
using Xunit;

namespace WindowsBacklightSyncService.Tests;

public class LogFilterBuilderTests
{
    private static IConfiguration ConfigWith(string json)
        => new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();

    [Fact]
    public void ExactCategoryMatch_AppliesConfiguredLevel()
    {
        var filter = LogFilterBuilder.Build(ConfigWith(
            """{ "Logging": { "File": { "LogLevel": { "Default": "Information", "My.Cat": "Debug" } } } }"""));

        Assert.True(filter("My.Cat", LogLevel.Debug));          // Debug >= Debug
        Assert.False(filter("My.Cat", LogLevel.Trace));         // Trace < Debug
    }

    [Fact]
    public void NamespaceChain_WalksToParentCategory()
    {
        var filter = LogFilterBuilder.Build(ConfigWith(
            """{ "Logging": { "File": { "LogLevel": { "Default": "Information", "WindowsBacklightSyncService": "Warning" } } } }"""));

        // "WindowsBacklightSyncService.Services.X" has no exact entry -> walk up to "WindowsBacklightSyncService".
        Assert.True(filter("WindowsBacklightSyncService.Services.BacklightSyncWorker", LogLevel.Warning));
        Assert.False(filter("WindowsBacklightSyncService.Services.BacklightSyncWorker", LogLevel.Information));
    }

    [Fact]
    public void UnknownCategory_FallsBackToDefault()
    {
        var filter = LogFilterBuilder.Build(ConfigWith(
            """{ "Logging": { "File": { "LogLevel": { "Default": "Error" } } } }"""));

        Assert.True(filter("Some.Unknown.Category", LogLevel.Error));
        Assert.False(filter("Some.Unknown.Category", LogLevel.Warning));
    }

    [Fact]
    public void MissingSection_UsesDebugDefault()
    {
        var filter = LogFilterBuilder.Build(ConfigWith("{}"));

        Assert.True(filter("Anything", LogLevel.Debug));
        Assert.False(filter("Anything", LogLevel.Trace));
    }

    [Fact]
    public void CategoryNames_AreCaseInsensitive()
    {
        var filter = LogFilterBuilder.Build(ConfigWith(
            """{ "Logging": { "File": { "LogLevel": { "my.category": "Warning" } } } }"""));

        Assert.True(filter("My.Category", LogLevel.Warning));
    }

    [Fact]
    public void LevelValues_AreCaseInsensitive()
    {
        var filter = LogFilterBuilder.Build(ConfigWith(
            """{ "Logging": { "File": { "LogLevel": { "Default": "debug" } } } }"""));

        Assert.True(filter("X", LogLevel.Debug));
    }
}
