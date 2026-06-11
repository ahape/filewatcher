using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

public static class Program
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    public static async Task Main(string[] args)
    {
        var configPath = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "watchconfig.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config not found: {configPath}");
            return;
        }

        var baseDir = Path.GetDirectoryName(configPath)!;
        var config = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(configPath), s_json) ?? new();

        // Expand "{{key}}" template variables from the config's env map. Keys prefixed with
        // "path:" get smart-joined so a trailing separator on the value can't collide with a
        // leading separator in the template (e.g. "/src/" + "/foo" -> "/src/foo", not "/src//foo").
        string Expand(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            foreach (var (key, value) in config.Env)
            {
                var token = $"{{{{{key}}}}}";
                var isPath = key.StartsWith("path:", StringComparison.Ordinal);
                int idx;
                while ((idx = input.IndexOf(token, StringComparison.Ordinal)) >= 0)
                {
                    var after = idx + token.Length;
                    var replacement = value;
                    if (isPath && replacement.Length > 0 && (replacement[^1] == '/' || replacement[^1] == '\\')
                        && after < input.Length && (input[after] == '/' || input[after] == '\\'))
                        replacement = replacement[..^1];
                    input = string.Concat(input.AsSpan(0, idx), replacement, input.AsSpan(after));
                }
            }
            return input;
        }

        foreach (var hook in config.Hooks.OnStartup.Concat(config.Hooks.OnUpdate))
        {
            hook.Command = Expand(hook.Command);
            hook.Source = Expand(hook.Source);
            hook.Location = Expand(hook.Location);
            if (hook.CopyTo != null) hook.CopyTo = Expand(hook.CopyTo);
        }

        foreach (var hook in config.Hooks.OnStartup.Concat(config.Hooks.OnUpdate))
            hook.Location = string.IsNullOrWhiteSpace(hook.Location) ? baseDir : Path.GetFullPath(hook.Location, baseDir);
        foreach (var hook in config.Hooks.OnUpdate)
        {
            hook.Source = Path.GetFullPath(hook.Source, baseDir);
            if (!string.IsNullOrWhiteSpace(hook.CopyTo)) hook.CopyTo = Path.GetFullPath(hook.CopyTo, baseDir);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var exitAfterStartup = args.Any(a => string.Equals(a, "--exit-after-startup", StringComparison.OrdinalIgnoreCase));
        var pending = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        var states = new ConcurrentDictionary<string, (DateTime Time, long Size)>(StringComparer.OrdinalIgnoreCase);
        var startupHooks = config.Hooks.OnStartup.Where(h => h.Enabled).ToArray();
        var updateHooks = config.Hooks.OnUpdate.Where(h => h.Enabled).ToArray();
        var hooksBySource = updateHooks.ToLookup(h => h.Source, StringComparer.OrdinalIgnoreCase);

        void Log(string tag, string msg, ConsoleColor c)
        {
            lock (Console.Out)
            {
                Console.ForegroundColor = c;
                Console.Write($"[{tag}] ");
                Console.ResetColor();
                Console.WriteLine(msg);
            }
        }

        async Task Run(Hook hook, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(hook.CopyTo) && File.Exists(hook.Source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hook.CopyTo)!);
                File.Copy(hook.Source, hook.CopyTo, true);
                Log("COPY", $"Copied {Path.GetFileName(hook.Source)}", ConsoleColor.Green);
            }
            if (string.IsNullOrWhiteSpace(hook.Command)) return;

            var name = string.IsNullOrWhiteSpace(hook.Name) ? "Hook" : hook.Name;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(
                    OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
                    OperatingSystem.IsWindows() ? $"/c \"{hook.Command}\"" : $"-c \"{hook.Command}\"")
                {
                    WorkingDirectory = hook.Location,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Failed to start process.");
                process.OutputDataReceived += (_, e) => { if (e.Data != null) Log(name, e.Data, ConsoleColor.Cyan); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(name, e.Data, ConsoleColor.Red); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { await process.WaitForExitAsync(token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(name, ex.Message, ConsoleColor.Red);
            }
        }

        void HandleChange(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || states.TryGetValue(path, out var last) && last == (info.LastWriteTimeUtc, info.Length)) return;
                states[path] = (info.LastWriteTimeUtc, info.Length);
            }
            catch { return; }

            foreach (var hook in hooksBySource[path])
            {
                var key = $"{hook.Source}|{hook.Command}";
                if (pending.TryRemove(key, out var old)) old.Cancel();
                var next = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                pending[key] = next;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(config.Settings.DebounceMs, next.Token);
                        await Run(hook, next.Token);
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        pending.TryRemove(key, out _);
                        next.Dispose();
                    }
                });
            }
        }

        if (exitAfterStartup)
        {
            await Task.WhenAll(startupHooks.Select(h => Run(h, cts.Token)));
            return;
        }

        var watchers = updateHooks.GroupBy(h => Path.GetDirectoryName(h.Source)!).Select(g =>
        {
            var watcher = new FileSystemWatcher(g.Key) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            watcher.Changed += (_, e) => HandleChange(e.FullPath);
            watcher.Created += (_, e) => HandleChange(e.FullPath);
            watcher.Renamed += (_, e) => HandleChange(e.FullPath);
            return watcher;
        }).ToArray();
        var startupTasks = startupHooks.Select(h => Run(h, cts.Token)).ToArray();

        Log("SYS", "Monitoring started. Press Ctrl+C to quit.", ConsoleColor.Green);
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
            foreach (var source in pending.Values) source.Cancel();
            await Task.WhenAll(startupTasks);
        }
    }
}
