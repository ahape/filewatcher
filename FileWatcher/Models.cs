using System.Collections.Generic;

namespace FileWatcher;

public record WatchConfig(Settings? Settings = null, Hooks? Hooks = null, Dictionary<string, string>? Env = null)
{
    public Settings Settings { get; init; } = Settings ?? new();
    public Hooks Hooks { get; init; } = Hooks ?? new();
    public Dictionary<string, string> Env { get; init; } = Env ?? [];
}

public record Settings(int DebounceMs = 1000);

public record Hooks(Hook[]? OnStartup = null, Hook[]? OnUpdate = null)
{
    public Hook[] OnStartup { get; init; } = OnStartup ?? [];
    public Hook[] OnUpdate { get; init; } = OnUpdate ?? [];
}

public record Hook(string Command = "", string Name = "", string Source = "", string? CopyTo = null, string Location = "", string LogLevel = "Info", bool Enabled = true)
{
    public string Command { get; set; } = Command;
    public string Source { get; set; } = Source;
    public string? CopyTo { get; set; } = CopyTo;
    public string Location { get; set; } = Location;
}
