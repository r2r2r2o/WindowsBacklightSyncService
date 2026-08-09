using Microsoft.Extensions.Options;

namespace WindowsBacklightSyncService.Tests.TestInfrastructure;

/// <summary>Simple in-memory IOptionsMonitor for tests.</summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    private T _current;

    public TestOptionsMonitor(T value) => _current = value;

    public T CurrentValue => _current;

    public T Get(string? name) => _current;

    public IDisposable? OnChange(Action<T, string?> listener) => null;

    public void Update(T value) => _current = value;
}
