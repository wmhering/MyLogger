﻿using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

internal class CachedLogger : ILogger
{
    private readonly string _category;
    private readonly ICreateLogEntries _provider;

    internal CachedLogger(ICreateLogEntries provider, string category, LogLevel initialLogLevel)
    {
        _category = category;
        _provider = provider;
        LogLevel = initialLogLevel;
    }

    internal LogLevel LogLevel { get; set; }

    #region ILogger interface
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= LogLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            _provider.CreateLogEntry(logLevel, _category, formatter(state, exception), exception);
    }
    #endregion
}
