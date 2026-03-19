namespace FileWatcher;

/// <summary>A single timestamped log entry captured by <see cref="LogService"/>.</summary>
public record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
