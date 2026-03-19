namespace FileWatcher;

/// <summary>Root configuration object deserialized from <c>watchconfig.json</c>.</summary>
public sealed record WatchConfig
{
    /// <summary>Global settings such as debounce delay and dashboard port.</summary>
    public WatchSettings Settings { get; set; } = new();

    /// <summary>Lifecycle hooks: arrays of startup commands and per-file update entries.</summary>
    public WatchHooks? Hooks { get; set; }

    /// <summary>Returns a minimal sample configuration suitable for first-run seeding.</summary>
    public static WatchConfig CreateSample() =>
        new()
        {
            Settings = new() { DebounceMs = 1000, DashboardPort = 5002 },
            Hooks = new()
            {
                OnStartup = [new() { Command = "echo started" }],
                OnUpdate =
                [
                    new()
                    {
                        Source = "src/file.js",
                        CopyTo = "out/file.js",
                        Command = "echo updated",
                        Description = "App script",
                    },
                ],
            },
        };
}
