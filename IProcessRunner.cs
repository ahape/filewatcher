namespace FileWatcher;

/// <summary>
/// Abstracts shell-process execution so that hooks can be verified in tests
/// without spawning real OS processes.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> in a shell process rooted at <paramref name="workingDirectory"/>,
    /// streaming stdout lines to <paramref name="onOutput"/> and stderr lines to <paramref name="onError"/>.
    /// Returns the process exit code when it completes or the <paramref name="token"/> is cancelled.
    /// </summary>
    Task<int> RunAsync(
        string command,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken token
    );
}
