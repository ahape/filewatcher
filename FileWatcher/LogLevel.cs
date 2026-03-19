namespace FileWatcher;

/// <summary>Log severity levels for console and web-dashboard output.</summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostic output, suppressed unless <c>logLevel</c> is <c>Debug</c> or <c>Trace</c>.</summary>
    Debug,

    /// <summary>General informational messages.</summary>
    Info,

    /// <summary>Positive outcome messages, displayed in green.</summary>
    Success,

    /// <summary>Non-fatal issues that the user should be aware of.</summary>
    Warning,

    /// <summary>Errors that prevented an action from completing.</summary>
    Error,

    /// <summary>File-copy completion messages, displayed in green alongside <see cref="Success"/>.</summary>
    Copy,

    /// <summary>Suppresses all output when used as a hook's log level.</summary>
    None,
}
