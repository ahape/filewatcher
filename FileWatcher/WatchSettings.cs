namespace FileWatcher;

public sealed record WatchSettings(
    int DebounceMs = 1000,
    string LogLevel = "Info",
    int DashboardPort = 5002,
    int StartupTimeoutMs = 2000
);
