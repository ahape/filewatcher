namespace FileWatcher;

public record WatchConfig(Settings Settings, Hooks Hooks);
public record Settings(int DebounceMs = 1000);
public record Hooks(Hook[] OnStartup, Hook[] OnUpdate);
public record Hook(
    string Command = "",
    string Name = "",
    string Source = "",
    string CopyTo = "",
    string Location = "",
    string LogLevel = "Info",
    bool Enabled = true
)
{
    public string Source { get; set; } = Source;
    public string? CopyTo { get; set; } = CopyTo;
}
