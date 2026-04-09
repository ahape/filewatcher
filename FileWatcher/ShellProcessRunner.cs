using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

/// <summary>
/// Production implementation of <see cref="IProcessRunner"/> that spawns a shell process
/// (<c>cmd.exe /c</c> on Windows, <c>sh -c</c> elsewhere) and streams its stdout and stderr
/// line-by-line via callbacks.
/// </summary>
internal sealed class ShellProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(
        string cmd,
        string dir,
        Action<string> onOut,
        Action<string> onErr,
        CancellationToken token,
        Action<int>? onStarted = null
    )
    {
        using var p = new Process { StartInfo = CreateStartInfo(cmd, dir) };
        AttachHandlers(p, onOut, onErr);
        StartProcess(p, onStarted);
        return await WaitForExitAsync(p, token);
    }

    private static ProcessStartInfo CreateStartInfo(string cmd, string dir)
    {
        (string fn, string args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c \"{cmd}\"")
            : ("sh", $"-c \"{cmd}\"");
        return new ProcessStartInfo
        {
            FileName = fn,
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static void AttachHandlers(Process process, Action<string> onOut, Action<string> onErr)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                onOut(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                onErr(e.Data);
        };
    }

    private static void StartProcess(Process process, Action<int>? onStarted)
    {
        process.Start();
        onStarted?.Invoke(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static async Task<int> WaitForExitAsync(Process process, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore errors during kill
        }
    }
}
