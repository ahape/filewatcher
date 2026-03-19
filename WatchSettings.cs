namespace FileWatcher;

/// <summary>Global application settings read from the <c>settings</c> object in the config file.</summary>
public sealed record WatchSettings
{
    /// <summary>Milliseconds to wait after a file change before acting, to coalesce rapid saves.</summary>
    public int DebounceMs { get; set; } = 1000;

    /// <summary>Reserved for future log-verbosity filtering; not yet enforced.</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>TCP port for the web dashboard. Defaults to 50022 if zero or omitted.</summary>
    public int DashboardPort { get; set; } = 5002;
}
