using System.Collections.Concurrent;

namespace FileWatcher;

/// <summary>
/// Centralised, thread-safe log sink. Writes to the console (with a text-level prefix
/// for colour-blind accessibility) and raises <see cref="OnLog"/> for web-dashboard streaming.
/// </summary>
public static class LogService
{
    /// <summary>Maximum number of log entries retained in the in-memory queue for the dashboard.</summary>
    internal const int MaxLogEntries = 500;

    private static readonly ConcurrentQueue<LogEntry> _logs = new();

    /// <summary>Fired on the calling thread each time a new entry is logged.</summary>
    public static event Action<LogEntry>? OnLog;

    /// <summary>
    /// Appends a new entry, trims the queue to <see cref="MaxLogEntries"/>, writes to the
    /// console, and notifies subscribers.
    /// </summary>
    public static void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogEntries)
            _logs.TryDequeue(out _);

        var oldColor = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            LogLevel.Debug => ConsoleColor.DarkGray,
            LogLevel.Info => ConsoleColor.Cyan,
            LogLevel.Success or LogLevel.Copy => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.Gray,
        };

        // Prefix every non-empty line with a text level indicator so the output is
        // meaningful to users who cannot distinguish colours (colour-blind accessibility).
        if (string.IsNullOrEmpty(message))
            Console.WriteLine();
        else
            Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {message}");

        Console.ForegroundColor = oldColor;

        OnLog?.Invoke(entry);
    }

    /// <summary>Returns all retained log entries, oldest first.</summary>
    public static IEnumerable<LogEntry> GetRecentLogs() => _logs;

    /// <summary>Removes all retained entries. Intended for test isolation only.</summary>
    internal static void Clear() => _logs.Clear();
}
