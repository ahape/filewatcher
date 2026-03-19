using System.Diagnostics;

namespace FileWatcher;

internal sealed class ShellProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string cmd, string dir, Action<string> onOut, Action<string> onErr, CancellationToken token)
    {
        var (fn, args) = OperatingSystem.IsWindows() ? ("cmd.exe", $"/c \"{cmd}\"") : ("sh", $"-c \"{cmd}\"");
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fn,
                Arguments = args,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOut(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(token);
        return p.ExitCode;
    }
}
