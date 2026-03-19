using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FileWatcher;

/// <summary>
/// Centralised, thread-safe log sink. Writes to the console (with a text-level prefix
/// for colour-blind accessibility) and raises <see cref="OnLog"/> for web-dashboard streaming.
/// </summary>
public static class LogService
{
    // ── Static, Public ───────────────────────────────────────────────

    /// <summary>Fired on the calling thread each time a new entry is logged.</summary>
    public static event Action<LogEntry>? OnLog;

    /// <summary>
    /// Appends a new entry, trims the queue to <see cref="MaxLogEntries"/>, writes to the
    /// console, and notifies subscribers.
    /// </summary>
    public static void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        s_logs.Enqueue(entry);
        while (s_logs.Count > MaxLogEntries)
            s_logs.TryDequeue(out _);

        WriteToConsole(level, message);
        OnLog?.Invoke(entry);
    }

    /// <summary>Returns all retained log entries, oldest first.</summary>
    public static IEnumerable<LogEntry> GetRecentLogs() => s_logs;

    // ── Static, Internal ─────────────────────────────────────────────

    /// <summary>Maximum number of log entries retained in the in-memory queue for the dashboard.</summary>
    internal const int MaxLogEntries = 500;

    /// <summary>Removes all retained entries. Intended for test isolation only.</summary>
    internal static void Clear() => s_logs.Clear();

    // ── Static, Private ──────────────────────────────────────────────

    private static readonly ConcurrentQueue<LogEntry> s_logs = new();

    private static void WriteToConsole(LogLevel level, string message)
    {
        ConsoleColor oldColor = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            LogLevel.Debug => ConsoleColor.DarkGray,
            LogLevel.Info => ConsoleColor.Cyan,
            LogLevel.Success or LogLevel.Copy => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.Gray,
        };

        if (string.IsNullOrEmpty(message))
            Console.WriteLine();
        else
            Console.WriteLine($"[{level.ToString().ToUpperInvariant()}] {message}");

        Console.ForegroundColor = oldColor;
    }
}
