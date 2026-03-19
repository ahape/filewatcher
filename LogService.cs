using System.Collections.Concurrent;

namespace FileWatcher;

public enum LogLevel { Info, Success, Warning, Error, Copy }
public record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

public static class LogService
{
    private static readonly ConcurrentQueue<LogEntry> _logs = new();
    public static event Action<LogEntry>? OnLog;

    public static void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _logs.Enqueue(entry);
        while (_logs.Count > 500) _logs.TryDequeue(out _);

        var oldColor = Console.ForegroundColor;
        Console.ForegroundColor = level switch { LogLevel.Info => ConsoleColor.Cyan, LogLevel.Success or LogLevel.Copy => ConsoleColor.Green, LogLevel.Warning => ConsoleColor.Yellow, LogLevel.Error => ConsoleColor.Red, _ => ConsoleColor.Gray };
        Console.WriteLine(message);
        Console.ForegroundColor = oldColor;

        OnLog?.Invoke(entry);
    }

    public static IEnumerable<LogEntry> GetRecentLogs() => _logs;
}