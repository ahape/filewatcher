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
        CancellationToken token
    )
    {
        using var p = new Process { StartInfo = CreateStartInfo(cmd, dir) };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                onOut(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                onErr(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(token);
        return p.ExitCode;
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
}
