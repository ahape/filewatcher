namespace FileWatcher;

public sealed record UpdateEntry(
    string Source,
    string Command = "",
    string Name = "",
    string Location = "",
    LogLevel LogLevel = LogLevel.Info,
    bool? FireAndForget = null,
    bool Enabled = true,
    string? CopyTo = null,
    string Description = ""
) : HookEntry(Command, Name, Location, LogLevel, FireAndForget, CopyTo)
{
    public string Source { get; set; } = Source;
}
