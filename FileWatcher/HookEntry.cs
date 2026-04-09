namespace FileWatcher;

public abstract record HookEntry(
    string Command,
    string Name = "",
    string Location = "",
    LogLevel LogLevel = LogLevel.Info,
    bool? FireAndForget = null,
    string? CopyTo = null
)
{
    public string? CopyTo { get; set; } = CopyTo;
}
