namespace FileWatcher;

/// <summary>Global application settings read from the <c>settings</c> object in the config file.</summary>
public sealed record WatchSettings
{
    /// <summary>Milliseconds to wait after a file change before acting, to coalesce rapid saves.</summary>
    public int DebounceMs { get; set; } = 1000;

    /// <summary>Controls debug output visibility. Set to <c>"Debug"</c> or <c>"Trace"</c> to enable verbose diagnostic logging.</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>TCP port for the web dashboard. Defaults to 5002 if zero or omitted.</summary>
    public int DashboardPort { get; set; } = 5002;

    /// <summary>Milliseconds to wait for startup hooks before considering them successfully started in the background.</summary>
    public int StartupTimeoutMs { get; set; } = 2000;
}
