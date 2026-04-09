using System.Collections.Generic;

namespace FileWatcher;

public sealed record WatchHooks(
    List<StartupEntry>? OnStartup = null,
    List<UpdateEntry>? OnUpdate = null
)
{
    public WatchHooks() : this([], []) { }
    public List<StartupEntry> OnStartup { get; init; } = OnStartup ?? [];
    public List<UpdateEntry> OnUpdate { get; init; } = OnUpdate ?? [];
}
