namespace MyLogger.Common;

public class CachedLoggerConfigurationBase
{
    public int MaximumLogEntriesBeforeFlush { get; init; } = 100;
    public int MaximumMillisecondsBeforeFlush { get; init; } = 100;
    public int MaximumLogEntriesPerBatch { get; init; } = 100;
}
