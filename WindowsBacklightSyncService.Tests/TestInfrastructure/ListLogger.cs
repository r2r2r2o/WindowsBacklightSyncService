using Microsoft.Extensions.Logging;

namespace WindowsBacklightSyncService.Tests.TestInfrastructure;

/// <summary>ILogger that captures formatted messages into a list for assertions.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        Messages.Add(message);
        Entries.Add((logLevel, message));
    }
}
