using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

public class CachedLoggerSettings
{
    public LogLevel MinimumLogLevel { get; internal set; } = LogLevel.Information;
    public string? LoggerName { get; internal set; } = null;
}
