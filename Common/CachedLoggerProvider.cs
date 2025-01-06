using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MyLogger.Common
{
    public abstract class CachedLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, CachedLogger> _loggers = new();
        private readonly ConcurrentQueue<LogEntry> _logEntries = new();
        private readonly CachedLoggerSettingsBase _settingValues;
        private readonly IApplicationContext _context;
        private volatile CachedLoggerConfigurationBase _configValues;
        private bool _disposing = false;
        private readonly Timer _timer;

        public CachedLoggerProvider(CachedLoggerSettingsBase settings, IApplicationContext context, IConfiguration configuration)
        {
            _settingValues = settings; // ToDo: Verify that settings are set
            _context = context;
            var changeToken = configuration.GetReloadToken();
            configuration.
        }

        public void CreateLogEntry(LogLevel level, string category, string message, Exception? exception)
        {
            if (_disposing)
                return;
            var userID = _context?.UserID ?? "";
            var threadID = _context?.ThreadID ?? "";
            var exceptionText = GetExceptionText(exception);
            _logEntries.Enqueue(new LogEntry(DateTime.Now, level, category, userID, threadID, message, exceptionText));
            if (_logEntries.Count >= _configValues.MaximumLogEntriesBeforeFlush)
                FlushQueue();
        }



        private void FlushQueueInBackground()
        {
            var callback = new WaitCallback((object obj) => FlushQueue());
            ThreadPool.QueueUserWorkItem(callback);
        }

        private SpinLock _lock = new();
        private void FlushQueue()
        {
            bool lockTaken = false;
            _lock.TryEnter(millisecondsTimeout: 0, ref lockTaken);
            if (!lockTaken)
                return;
            try
            {
                var batchSize = _configValues.MaximumLogEntriesPerBatch;
                while (_logEntries.Count > batchSize)
                    PersistLogEntriesWithErrorHandling(DequeueLogEntries(batchSize));
                if (_logEntries.Count > 0)
                    PersistLogEntriesWithErrorHandling(DequeueLogEntries(batchSize));
            }
            finally
            {
                _lock.Exit();
            }
        }

        private IReadOnlyList<LogEntry> DequeueLogEntries(int batchSize)
        {
            List<LogEntry> result = new(batchSize);
            while (result.Count < batchSize && _logEntries.Count > 0)
                if (_logEntries.TryDequeue(out LogEntry logEntry))
                    result.Add(logEntry);
            return result;
        }

        private void PersistLogEntriesWithErrorHandling(IEnumerable<LogEntry> logEntries)
        {
            try
            {
                Persist(logEntries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: Unexpected error while saving log entries in {this.GetType().FullName}, {logEntries.Count} log entries lost.");
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: Unexpected {ex.GetType().FullName}: {ex.Message}.");
            }
        }

        public ILogger CreateLogger(string category) =>
            _loggers.GetOrAdd(category, (name) => new CachedLogger(this, category, LogLevelFor(category)));

        public void Dispose()
        {
            _disposing = true;
        }

        public abstract CachedLoggerConfigurationBase GetConfiguration(IConfigurationSection configurationSection, Func<string, string> getConnectionString);

        public abstract void Persist(IEnumerable<LogEntry> logEntries);

        public virtual string? GetExceptionText(Exception? exception)
        {
            if (exception == null)
                return null;
            return new StringBuilder(5000)
                .Append(exception.GetType().FullName).Append(": ").AppendLine(exception.Message)
                .AppendLine("Stack trace:")
                .Append(exception.StackTrace)
                .ToString();
        }
    }
}
