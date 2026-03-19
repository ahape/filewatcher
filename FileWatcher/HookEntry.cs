namespace FileWatcher;

/// <summary>
/// Abstract base for hook entries that execute shell commands, providing shared
/// properties for command execution and output control.
/// </summary>
public abstract record HookEntry
{
    // ── Instance, Public ─────────────────────────────────────────────

    /// <summary>Shell command to execute.</summary>
    public string Command { get; set; } = "";

    /// <summary>Working directory for <see cref="Command"/>. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";

    /// <summary>
    /// Controls the log level for this entry's hook output.
    /// Set to <c>None</c> to suppress all stdout. Defaults to <c>Info</c>.
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}
