namespace FileWatcher;

/// <summary>
/// Abstract base for hook entries that execute shell commands, providing shared
/// properties for command execution and output control.
/// </summary>
public abstract record HookEntry
{
    /// <summary>
    /// Optional name shown in log prefixes to identify which subprocess produced the output
    /// (e.g., <c>tsc-watcher</c>). When empty, logs use the generic <c>[Hook]</c> prefix.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Shell command to execute.</summary>
    public string Command { get; set; } = "";

    /// <summary>Working directory for <see cref="Command"/>. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";

    /// <summary>
    /// Controls the log level for this entry's hook output.
    /// Set to <c>None</c> to suppress all stdout. Defaults to <c>Info</c>.
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    public bool? FireAndForget { get; set; }
}
