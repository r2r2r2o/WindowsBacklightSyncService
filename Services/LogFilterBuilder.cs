using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WindowsBacklightSyncService.Services;

/// <summary>
/// Builds the per-category level filter for the file logger from "Logging:File:LogLevel".
/// Extracted from Program.cs so the category-chain logic is unit-testable.
/// </summary>
public static class LogFilterBuilder
{
    public static Func<string, LogLevel, bool> Build(IConfiguration configuration)
    {
        var levels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in configuration.GetSection("Logging:File:LogLevel").GetChildren())
        {
            if (Enum.TryParse<LogLevel>(child.Value, ignoreCase: true, out LogLevel level))
                levels[child.Key] = level;
        }
        if (!levels.ContainsKey("Default"))
            levels["Default"] = LogLevel.Debug;

        return (category, level) =>
        {
            // Walk the category namespace chain: "WindowsBacklightSyncService.Services.X" -> ... -> "Default".
            string current = category;
            while (!string.IsNullOrEmpty(current))
            {
                if (levels.TryGetValue(current, out LogLevel configured))
                    return level >= configured;
                int idx = current.LastIndexOf('.');
                current = idx > 0 ? current[..idx] : string.Empty;
            }
            return level >= levels["Default"];
        };
    }
}
