using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher.Tests;

/// <summary>
/// A controllable IProcessRunner for unit tests. Records every call and
/// returns configurable output lines and exit codes without spawning real processes.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public record Call(string Command, string WorkingDirectory);

    /// <summary>All RunAsync invocations in order.</summary>
    public List<Call> Calls { get; } = new();

    /// <summary>Exit code returned for every call. Default: 0.</summary>
    public int ExitCode { get; set; } = 0;

    /// <summary>When set, delivered to the onOutput callback on every call.</summary>
    public string? OutputLine { get; set; }

    /// <summary>When set, delivered to the onError callback on every call.</summary>
    public string? ErrorLine { get; set; }

    public Task<int> RunAsync(
        string command,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add(new Call(command, workingDirectory));
        if (OutputLine != null) onOutput(OutputLine);
        if (ErrorLine != null) onError(ErrorLine);
        return Task.FromResult(ExitCode);
    }
}
