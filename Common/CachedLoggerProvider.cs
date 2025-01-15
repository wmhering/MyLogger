using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MyLogger.Common
{
    public abstract class CachedLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, CachedLogger> _loggers = new();
        private readonly ConcurrentQueue<LogEntry> _logEntries = new();
        private readonly CachedLoggerSettings _settingValues;
        private readonly IApplicationContext _context;
        private volatile CachedLoggerConfigurationBase _configValues;
        private volatile IConfigurationRoot _configRoot;
        private bool _disposing = false;
        private readonly Timer _timer;

        public CachedLoggerProvider(CachedLoggerSettings settings, IApplicationContext context, IConfigurationRoot configuration)
        {
            _settingValues = SetDefaultSettings(settings);
            _context = context;
            _configRoot = configuration ?? throw new ArgumentNullException(nameof(configuration));
            LoadConfiguration();
            _configRoot.GetReloadToken().RegisterChangeCallback(ConfigurationChanged, null);
        }

        CachedLoggerSettings SetDefaultSettings(CachedLoggerSettings settings)
        {
            if (settings == null)
                settings = new CachedLoggerSettings();
            if (string.IsNullOrWhiteSpace(settings.LoggerName))
                settings.LoggerName = GetLoggerName();
            if (settings.MinimumLogLevel < LogLevel.Trace)
                settings.MinimumLogLevel = LogLevel.Trace;
            if (settings.MinimumLogLevel > LogLevel.Error)
                settings.MinimumLogLevel = LogLevel.Error;
            return settings;
        }
        

        private string GetLoggerName()
        {
            var type = GetType();
            var attribute = type.GetCustomAttribute<ProviderAliasAttribute>();
            if (attribute != null)
                return attribute.Alias;
            var name = type.Name;
            if (name.EndsWith("LoggerProvider", StringComparison.InvariantCultureIgnoreCase) && name.Length > 14)
                return name.Substring(0, name.Length - 14);
            return name;
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

        #region Load configuration
        private void ConfigurationChanged(object state = null)
        {
            _configRoot.Reload();
            LoadConfiguration();
            _configRoot.GetReloadToken().RegisterChangeCallback(ConfigurationChanged, null);
        }

        private void LoadConfiguration()
        {
            var loggerSectionName = $"Logging:{_settingValues.LoggerName}";
            var loggerSection = _configRoot.GetSection(loggerSectionName);
            if (!loggerSection.Exists())
            {
                DisableLogging(errorMessage: $"Unable to fetch configuration at '{loggerSectionName}'");
                return;
            }
            var levelsSection = loggerSection.GetSection("LogLevel");
            if (!levelsSection.Exists())
                levelsSection = _configRoot.GetSection("Logging:LogLevel");
            var logLevels = LoadLogLevels(levelsSection);


            // Get provider configuration section
            // Load log levels
            // Get config values
            //_configValues = GetConfiguration(section, name => _configRoot.GetConnectionString(name));
        }

        private ImmutableList<LogLevelEntry> LoadLogLevels(IConfigurationSection section)
        {
            var levels = new List<LogLevelEntry>();
            bool haveDefault = false;
            foreach(var entry in section.GetChildren())
            {
                var categoryPrefix = entry.Key;
                if(! Enum.TryParse<LogLevel>( entry.Value, out var level))
                {
                    // Write message
                    continue;
                }
                if (categoryPrefix == "Default")
                {
                    categoryPrefix = "";
                    haveDefault = true;
                }
                levels.Add(new LogLevelEntry(categoryPrefix, level));
            }
            if (!haveDefault)
            {
                // Write message
                levels.Add(new LogLevelEntry("", LogLevel.None));
            }
            return ImmutableList.Create(levels.OrderByDescending(e => e.CategoryPrefix.Length).ToArray());
        }

        private LogLevel LogLevelFor(string category)
        {
            foreach (var entry in _logLevels)
                if (category.StartsWith(entry.CategoryPrefix))
                    return entry.LogLevel;
            return LogLevel.None;
        }

        public abstract CachedLoggerConfigurationBase GetConfiguration(IConfigurationSection configurationSection, Func<string, string> getConnectionString);

        #endregion

        #region Save log entries to persistant storage
        private void StartBackgroundTaskToFlushQueue()
        {
            var callback = new WaitCallback((object obj) => FlushQueue());
            ThreadPool.QueueUserWorkItem(callback);
        }

        private SpinLock _lock = new();
        // This method can block and should not be called 
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
                PersistLogEntries(logEntries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: Unexpected error while saving log entries in {this.GetType().FullName}, {logEntries.Count()} log entries lost.");
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: Unexpected {ex.GetType().FullName}: {ex.Message}.");
            }
        }

        public abstract void PersistLogEntries(IEnumerable<LogEntry> logEntries);
        #endregion

        public ILogger CreateLogger(string category) =>
            _loggers.GetOrAdd(category, (name) => new CachedLogger(this, category, LogLevelFor(category)));

        public void Dispose()
        {
            _disposing = true;
        }

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

        private record LogLevelEntry(string CategoryPrefix, LogLevel LogLevel);
    }
}
