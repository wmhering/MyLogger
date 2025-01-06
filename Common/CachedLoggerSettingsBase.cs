using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

public abstract class CachedLoggerSettingsBase
{
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
    public string? LoggerConfigurationSection { get; init; } = null;
}
