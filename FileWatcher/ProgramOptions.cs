using System;
using System.Linq;

namespace FileWatcher;

internal sealed record ProgramOptions
{
    public bool DisableWeb { get; init; }
    public bool ExitAfterStartup { get; init; }
    public string? ConfigPath { get; init; }

    public static ProgramOptions Parse(string[] args) =>
        new()
        {
            DisableWeb = Array.Exists(args, a => string.Equals(a, Constants.ArgNoWeb, StringComparison.OrdinalIgnoreCase)),
            ExitAfterStartup = Array.Exists(args, a => string.Equals(a, Constants.ArgExitAfterStartup, StringComparison.OrdinalIgnoreCase)),
            ConfigPath = args.FirstOrDefault(a => !a.StartsWith('-'))
        };
}
