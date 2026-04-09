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

        using var job = OperatingSystem.IsWindows() ? CreateJob() : null;

        StartProcess(p, onStarted);

        if (OperatingSystem.IsWindows() && job != null)
        {
            AddProcessToJob(job, p.Id);
        }

        return await WaitForExitAsync(p, token);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowsJobObject CreateJob() => new($"FileWatcher_{Guid.NewGuid()}");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddProcessToJob(WindowsJobObject job, int pid)
    {
        try { job.AddProcess(pid); } catch { /* Ignore */ }
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

        // Report PID immediately
        onStarted?.Invoke(process.Id);

        if (OperatingSystem.IsWindows() && onStarted != null)
        {
            // Try to find the actual workload PID after a short delay
            int rootPid = process.Id;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Wait for shell/script to spawn workload
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        int? workloadId = ProcessTreeDiscovery.FindWorkloadChild(rootPid);
                        if (workloadId.HasValue)
                            onStarted(workloadId.Value);
                    }
                }
                catch { /* Ignore discovery errors */ }
            });
        }

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
            // entireProcessTree: true is usually enough, but combined with 
            // the Job Object (via using declaration in RunAsync), it's bulletproof.
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore errors during kill
        }
    }
}
