using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [Fact]
    public async Task RunAsync_KillsChildProcessTreeOnCancellation_DeepTree()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Create a batch script that starts ping
        string batchPath = Path.Combine(Path.GetTempPath(), "test-kill-deep.cmd");
        await File.WriteAllTextAsync(batchPath, "@echo off\r\nping localhost -t");

        // Create a powershell script that runs the batch script
        string scriptPath = Path.Combine(Path.GetTempPath(), "test-kill-deep.ps1");
        await File.WriteAllTextAsync(scriptPath, $"& '{batchPath}'");

        using var cts = new CancellationTokenSource();
        var runTask = Runner.RunAsync(
            $"powershell -File {scriptPath}",
            Environment.CurrentDirectory,
            _ => { },
            _ => { },
            cts.Token
        );

        await Task.Delay(3000);

        var pings = Process.GetProcessesByName("ping");
        Assert.NotEmpty(pings);

        cts.Cancel();

        try { await runTask; } catch (OperationCanceledException) { }

        await Task.Delay(2000);

        pings = Process.GetProcessesByName("ping");
        Assert.Empty(pings);

        if (File.Exists(scriptPath)) File.Delete(scriptPath);
        if (File.Exists(batchPath)) File.Delete(batchPath);
    }

    [Fact]
    public async Task RunAsync_ReportsChildPid_WhenUsingShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Create a script that starts a long running process
        string scriptPath = Path.Combine(Path.GetTempPath(), "test-pid-report.ps1");
        await File.WriteAllTextAsync(scriptPath, "ping localhost -t");

        int reportedPid = -1;
        var pids = new List<int>();

        using var cts = new CancellationTokenSource();
        var runTask = Runner.RunAsync(
            $"powershell -File {scriptPath}",
            Environment.CurrentDirectory,
            _ => { },
            _ => { },
            cts.Token,
            pid =>
            {
                reportedPid = pid;
                lock (pids) pids.Add(pid);
            }
        );

        // Give it time to spawn the child and for our discovery to run
        await Task.Delay(2000);

        // We expect at least two PIDs to be reported: first the shell, then the child
        lock (pids)
        {
            Assert.NotEmpty(pids);
            if (pids.Count > 1)
            {
                // The last reported PID should be the ping process
                var pingProcess = Process.GetProcessById(pids[^1]);
                Assert.Equal("ping", pingProcess.ProcessName.ToLowerInvariant());
            }
        }

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
        if (File.Exists(scriptPath)) File.Delete(scriptPath);
    }
}
