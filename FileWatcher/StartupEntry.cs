namespace FileWatcher;

/// <summary>A command to run on application startup or config reload.</summary>
public sealed record StartupEntry : HookEntry
{
    /// <summary>When <c>false</c> the entry is skipped entirely.</summary>
    public bool? Enabled { get; set; }
}
