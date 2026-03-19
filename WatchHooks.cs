using System.Collections.Generic;

namespace FileWatcher;

/// <summary>Container for lifecycle hook arrays.</summary>
public sealed record WatchHooks
{
    /// <summary>Commands executed once when the application starts (or after a config reload).</summary>
    public List<StartupEntry> OnStartup { get; set; } = [];

    /// <summary>Entries executed after each debounced file change.</summary>
    public List<UpdateEntry> OnUpdate { get; set; } = [];
}
