using WindowsBacklightSyncService.Services;

namespace WindowsBacklightSyncService.Tests.TestInfrastructure;

/// <summary>
/// In-memory IPowerPlanBrightnessWriter. Records every write and re-apply so tests can
/// assert exactly what the worker did. All failure switches default to off.
/// </summary>
public sealed class FakePowerPlanWriter : IPowerPlanBrightnessWriter
{
    public List<Guid> Schemes { get; } = new();
    public Dictionary<Guid, (int? Ac, int? Dc)> Stored { get; } = new();
    public Dictionary<Guid, string> Names { get; } = new();
    public Guid? Active { get; set; }
    public List<(Guid Scheme, int Value, bool Ac)> Writes { get; } = new();
    public int ReapplyCount { get; private set; }

    public bool ThrowOnRead { get; set; }
    public bool ThrowOnWrite { get; set; }
    public bool ThrowOnEnumerate { get; set; }

    public void AddPlan(Guid guid, int? ac, int? dc, string? name = null)
    {
        Schemes.Add(guid);
        Stored[guid] = (ac, dc);
        if (name is not null)
            Names[guid] = name;
    }

    public IReadOnlyList<Guid> EnumeratePowerSchemes()
    {
        if (ThrowOnEnumerate)
            throw new InvalidOperationException("enumerate failed (test)");
        return Schemes.ToList();
    }

    public void WriteBrightnessValue(Guid schemeGuid, int brightnessPercent, bool ac)
    {
        if (ThrowOnWrite)
            throw new InvalidOperationException("write failed (test)");
        Writes.Add((schemeGuid, brightnessPercent, ac));
        var (existingAc, existingDc) = Stored.TryGetValue(schemeGuid, out var s) ? s : ((int?)null, (int?)null);
        Stored[schemeGuid] = (ac ? brightnessPercent : existingAc, ac ? existingDc : brightnessPercent);
    }

    public int? ReadBrightnessValue(Guid schemeGuid, bool ac)
    {
        if (ThrowOnRead)
            throw new InvalidOperationException("read failed (test)");
        if (!Stored.TryGetValue(schemeGuid, out var s))
            return null;
        return ac ? s.Ac : s.Dc;
    }

    public Guid? GetActiveScheme() => Active;

    public void SetActiveScheme(Guid schemeGuid)
    {
        ReapplyCount++;
        Active = schemeGuid;
    }

    public string? GetSchemeName(Guid schemeGuid)
        => Names.TryGetValue(schemeGuid, out var name) ? name : schemeGuid.ToString("D");
}
