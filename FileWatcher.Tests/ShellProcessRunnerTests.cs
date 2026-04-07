using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

/// <summary>
/// Integration tests for ShellProcessRunner.
/// These tests spawn real OS processes to verify that stdout/stderr are piped
/// and that exit codes are captured correctly.
/// </summary>
public sealed class ShellProcessRunnerTests : IDisposable
{
    private readonly StringWriter _out;
    private readonly StringWriter _err;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public ShellProcessRunnerTests()
    {
        _out = new StringWriter();
        _err = new StringWriter();
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _out.Dispose();
        _err.Dispose();
    }

    private static readonly ShellProcessRunner Runner = new();

    // Both shells (cmd.exe and sh) understand these forms, so no per-OS branching is needed.
    private static string StdoutCommand(string message) => $"echo {message}";

    private static string StderrCommand(string message) => $"echo {message} 1>&2";

    private static string ExitCodeCommand(int code) =>
        OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    private static CancellationTokenSource Timeout5s() => new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RunAsync_CommandWithOutput_DeliveredToOnOutputCallback()
    {
        List<string> received = [];

        using var cts = Timeout5s();
        await Runner.RunAsync(
            StdoutCommand("hello-stdout"),
            Environment.CurrentDirectory,
            received.Add,
            _ => { },
            cts.Token
        );

        Assert.Contains(received, line => line.Contains("hello-stdout"));
    }

    [Fact]
    public async Task RunAsync_CommandWithStderr_DeliveredToOnErrorCallback()
    {
        List<string> received = [];

        using var cts = Timeout5s();
        await Runner.RunAsync(
            StderrCommand("hello-stderr"),
            Environment.CurrentDirectory,
            _ => { },
            received.Add,
            cts.Token
        );

        Assert.Contains(received, line => line.Contains("hello-stderr"));
    }

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsExitCodeZero()
    {
        using var cts = Timeout5s();
        var exitCode = await Runner.RunAsync(
            StdoutCommand("ok"),
            Environment.CurrentDirectory,
            _ => { },
            _ => { },
            cts.Token
        );

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_FailingCommand_ReturnsNonZeroExitCode()
    {
        using var cts = Timeout5s();
        var exitCode = await Runner.RunAsync(
            ExitCodeCommand(3),
            Environment.CurrentDirectory,
            _ => { },
            _ => { },
            cts.Token
        );

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ThrowsOperationCanceledException()
    {
        string command = OperatingSystem.IsWindows()
            ? "ping localhost -n 10 > nul"
            : "sleep 10";

        using var cts = new CancellationTokenSource();
        var runTask = Runner.RunAsync(
            command,
            Environment.CurrentDirectory,
            _ => { },
            _ => { },
            cts.Token
        );

        await Task.Delay(200);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }
}
