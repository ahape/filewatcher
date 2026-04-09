namespace FileWatcher;

public sealed record StartupEntry(
    string Command,
    string Name = "",
    string Location = "",
    LogLevel LogLevel = LogLevel.Info,
    bool? FireAndForget = null,
    bool? Enabled = true,
    string? CopyTo = null
) : HookEntry(Command, Name, Location, LogLevel, FireAndForget, CopyTo);
