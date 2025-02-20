using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MyLogger.Common
{
    public abstract class CachedLoggerProvider<TConfig> : ILoggerProvider, ICreateLogEntries where TConfig : CachedLoggerConfigurationBase, new()
    {
        private readonly ConcurrentDictionary<string, CachedLogger> _loggers = new();
        private readonly ConcurrentQueue<LogEntry> _logEntries = new();
        private readonly IApplicationContext _context;
        private readonly CachedLoggerSettingsBase _settingValues;
        private readonly Timer _timer;
        private CachedLoggerConfigurationBase _configurationValues;
        private bool _disposing = false;

        public CachedLoggerProvider(CachedLoggerSettingsBase settings, IApplicationContext context, IConfiguration configuration)
        {
            _settingValues = ValidatedSettings(settings);
            _context = context;
            _timer = new Timer(FlushQueueCallback, this, Timeout.Infinite, Timeout.Infinite);
            _configurationValues = DisableLoggingConfiguration();
            LoadConfiguration(configuration);
        }

        public void CreateLogEntry(LogLevel level, string category, string message, Exception? exception)
        {
            if (_disposing)
                return;
            var userID = _context?.UserID ?? "";
            var threadID = _context?.ThreadID ?? "";
            var exceptionText = GetExceptionText(exception);
            _logEntries.Enqueue(new LogEntry(DateTime.Now, level, category, userID, threadID, message, exceptionText));
            if (_logEntries.Count >= _configurationValues.MaximumLogEntriesBeforeFlush)
                FlushQueue();
        }

        private CachedLoggerSettingsBase ValidatedSettings(CachedLoggerSettingsBase settings)
        {
            const string suffixToRemove = "LoggerProvider";
            var loggerName = GetType().Name;
            if (loggerName.EndsWith(suffixToRemove) && loggerName != suffixToRemove)
                loggerName = loggerName.Substring(0, loggerName.Length - suffixToRemove.Length);
            if (settings == null)
            {
                settings = new CachedLoggerSettingsBase(loggerName, LogLevel.Information);
                LoggerError($"No settings, using {settings}");
            }
            if (string.IsNullOrWhiteSpace(settings.ConfigurationSection))
            {
                settings = new CachedLoggerSettingsBase(loggerName, settings.MinimumLogLevel);
                LoggerError($"Missing ConfigurationSection, using {settings.ConfigurationSection}");
            }
            if (!Enum.IsDefined<LogLevel>(settings.MinimumLogLevel))
            {
                settings = new CachedLoggerSettingsBase(settings.ConfigurationSection, LogLevel.Information);
                LoggerError($"Invalid MinimumLogLevel, using {settings.MinimumLogLevel}");
            }
            return settings;
        }

        private void LoadConfiguration(object state)
        {
            var loggerSectionName = "Logging:" + _settingValues.ConfigurationSection;
            var configuration = (state as IConfigurationRoot);
            var loggerSection = configuration?.GetSection(loggerSectionName);
            var logLevelSection = loggerSection?.GetSection("LogLevel")
                ?? configuration?.GetSection("Logging:LogLevel");
            if (loggerSection == null ||logLevelSection == null)
            {
                _configurationValues = DisableLoggingConfiguration(
                    $"Unable to get configuration values from {loggerSectionName} and/or Logging:LogLevel.");
                return;
            }
            
            var logLevels = GetLogLevels(logLevelSection);
            var loggerConfig = loggerSection.GetValue<TConfig>("");
            if (!BaseValidateConfig(loggerConfig))
            {
                _configurationValues = DisableLoggingConfiguration(
                    $"Configuration values from {loggerSectionName} are invalid");
                return;
            }

            loggerConfig.LogLevels = logLevels.ToArray();
            _configurationValues = loggerConfig;
            foreach (var logger in _loggers)
                logger.Value.LogLevel = LogLevelOf(logger.Key);
            
            configuration
                .GetReloadToken()
                .RegisterChangeCallback(LoadConfiguration, configuration);
        }

        private bool BaseValidateConfig(TConfig config)
        {
            if (config.MaximumMillisecondsBeforeFlush < 0 || config.MaximumMillisecondsBeforeFlush > 60000)
                return false;
            if (config.MaximumLogEntriesBeforeFlush < 1 || config.MaximumLogEntriesBeforeFlush > 10000)
                return false;
            if (config.MaximumLogEntriesPerBatch < 1 || config.MaximumLogEntriesPerBatch > 10000)
                return false;
            return ValidateConfig(config);
        }

        protected abstract bool ValidateConfig(TConfig config);

        private TConfig DisableLoggingConfiguration(string? errorMessage = null)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (!string.IsNullOrWhiteSpace(errorMessage))
                LoggerError(errorMessage);
            return new TConfig()
            {
                MaximumLogEntriesBeforeFlush = int.MaxValue,
                MaximumLogEntriesPerBatch = int.MaxValue,
                MaximumMillisecondsBeforeFlush = -1
            };
        }

        private LogLevel LogLevelOf(string category)
        {
            foreach (var ll in _configurationValues.LogLevels)
                if (ll.CategoryPrefix == "")
                    return ll.LogLevel;
            else if (category.StartsWith(ll.CategoryPrefix))
                    return ll.LogLevel;
            return LogLevel.None;
        }

        private List<LogLevelEntry> GetLogLevels(IConfiguration configuration)
        {
            var result = configuration.GetValue<Dictionary<string, LogLevel>>("")
                .Select(e => new LogLevelEntry(e.Key == "Default" ? "" : e.Key, e.Value))
                .OrderByDescending(e => e.CategoryPrefix.Length)
                .ToList();
            if (result.Count == 0 || result.Last().CategoryPrefix.Length != 0)
                result.Add(new LogLevelEntry("", LogLevel.None));
            return result;
        }

        protected void LoggerError(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
            Console.WriteLine(line);
        }

        private static void FlushQueueCallback(object state) { }

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
                var batchSize = _configurationValues.MaximumLogEntriesPerBatch;
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
                LoggerError($"Unexpected error while saving log entries in {this.GetType().FullName}, {logEntries.Count()} log entries lost.");
                LoggerError($"Unexpected {ex.GetType().FullName}: {ex.Message}.");
            }
        }

        public ILogger CreateLogger(string category) =>
            _loggers.GetOrAdd(category, (name) => new CachedLogger(this, category, LogLevelOf(category)));

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
