namespace FileWatcher;

/// <summary>A command to run on application startup or config reload.</summary>
public sealed record StartupEntry
{
    /// <summary>Shell command to execute.</summary>
    public string Command { get; set; } = "";

    /// <summary>Working directory for the command. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";
}
