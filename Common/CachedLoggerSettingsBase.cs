using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

public class CachedLoggerSettingsBase
{
    public CachedLoggerSettingsBase() : this(null, LogLevel.Information) { }
    public CachedLoggerSettingsBase(string configurationSection, LogLevel minimumLogLevel)
    {
        ConfigurationSection = configurationSection;
        MinimumLogLevel = minimumLogLevel;
    }

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
    public string? ConfigurationSection { get; init; } = null;

    public override string ToString()
    {
        return $"{nameof(MinimumLogLevel)}={MinimumLogLevel}, {nameof(ConfigurationSection)}={ConfigurationSection ?? "null"}";
    }
}
