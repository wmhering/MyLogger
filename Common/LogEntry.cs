using System;

using Microsoft.Extensions.Logging;

namespace MyLogger.Common;

public record LogEntry(DateTime EntryTime, LogLevel Level, string Category, string UserID, string ThreadID, string Message, string? Exception);

