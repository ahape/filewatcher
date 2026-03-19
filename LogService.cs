using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FileWatcher;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Copy
}

public record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

public static class LogService
{
    private static readonly ConcurrentQueue<LogEntry> _logs = new();
    private const int MaxLogs = 500;

    public static event Action<LogEntry> OnLog;

    public static void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _logs.Enqueue(entry);

        while (_logs.Count > MaxLogs)
        {
            _logs.TryDequeue(out _);
        }

        // Print to console as before
        var color = level switch
        {
            LogLevel.Info => ConsoleColor.Cyan,
            LogLevel.Success => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Copy => ConsoleColor.Green,
            _ => ConsoleColor.Gray
        };

        var oldColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = oldColor;

        OnLog?.Invoke(entry);
    }

    public static IEnumerable<LogEntry> GetRecentLogs() => _logs.ToArray();
}
