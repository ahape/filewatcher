namespace FileWatcher;

/// <summary>
/// Watches a source file and, on change, optionally copies it to <see cref="CopyTo"/>
/// and/or executes <see cref="Command"/>. Both actions are opt-in.
/// </summary>
public sealed record UpdateEntry
{
    /// <summary>Absolute or relative path to the source file to watch.</summary>
    public string Source { get; set; } = "";

    /// <summary>When <c>false</c> the entry is skipped entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Destination path to copy the source file to on change. Omit to skip copying.</summary>
    public string? CopyTo { get; set; }

    /// <summary>Shell command to run after a change (and after copying, if applicable). Omit to skip.</summary>
    public string? Command { get; set; }

    /// <summary>Working directory for <see cref="Command"/>. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";

    /// <summary>Human-readable description shown in copy summaries and warnings.</summary>
    public string Description { get; set; } = "";
}
