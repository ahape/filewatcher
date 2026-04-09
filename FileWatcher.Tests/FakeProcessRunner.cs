using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public record Call(string Command, string WorkingDirectory);

    public List<Call> Calls { get; } = [];
    public int ExitCode { get; set; } = 0;
    public string? OutputLine { get; set; }
    public string? ErrorLine { get; set; }
    public int ProcessId { get; set; } = 4242;
    public bool ShouldThrow { get; set; }
    public int DelayMs { get; set; } = 0;
    public int MaxConcurrentCalls => _maxConcurrentCalls;

    private int _concurrentCalls,
        _maxConcurrentCalls;

    public async Task<int> RunAsync(
        string command,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken token,
        Action<int>? onStarted = null
    )
    {
        if (ShouldThrow)
            throw new Exception("fail");

        int current = Interlocked.Increment(ref _concurrentCalls);
        lock (this)
        {
            if (current > _maxConcurrentCalls)
                _maxConcurrentCalls = current;
        }

        try
        {
            lock (Calls)
            {
                Calls.Add(new Call(command, workingDirectory));
            }

            onStarted?.Invoke(ProcessId);
            if (DelayMs > 0)
                await Task.Delay(DelayMs, token);

            token.ThrowIfCancellationRequested();
            if (OutputLine != null)
                onOutput(OutputLine);
            if (ErrorLine != null)
                onError(ErrorLine);
            return ExitCode;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCalls);
        }
    }
}
