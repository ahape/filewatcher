using System;

namespace FileWatcher;

internal sealed record ProgramOptions
{
    public bool DisableWeb { get; init; }
    public bool ExitAfterStartup { get; init; }

    public static ProgramOptions Parse(string[] args) =>
        new()
        {
            DisableWeb = Array.Exists(
                args,
                arg => string.Equals(arg, "--no-web", StringComparison.OrdinalIgnoreCase)
            ),
            ExitAfterStartup = Array.Exists(
                args,
                arg => string.Equals(arg, "--exit-after-startup", StringComparison.OrdinalIgnoreCase)
            ),
        };
}
