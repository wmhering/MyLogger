using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

public abstract class CachedLoggerConfigurationBase
{
    public int MaximumLogEntriesBeforeFlush { get; init; } = 100;
    public int MaximumMillisecondsBeforeFlush { get; init; } = 100;
    public int MaximumLogEntriesPerBatch { get; init; } = 100;
    internal LogLevelEntry[] LogLevels { get; set; } = [new LogLevelEntry("", LogLevel.None)];
    public abstract bool ValidateConfig();
}
