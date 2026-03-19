namespace FileWatcher.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public record Call(string Command, string WorkingDirectory);

    public List<Call> Calls { get; } = [];
    public int ExitCode { get; set; } = 0;
    public string? OutputLine { get; set; }
    public string? ErrorLine { get; set; }

    public Task<int> RunAsync(
        string command,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        Calls.Add(new Call(command, workingDirectory));
        if (OutputLine != null)
            onOutput(OutputLine);
        if (ErrorLine != null)
            onError(ErrorLine);
        return Task.FromResult(ExitCode);
    }
}
