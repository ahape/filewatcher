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
    private static readonly JsonSerializerOptions s_jsonOpts = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    public static async Task Main(string[] args)
    {
        var configPath = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "watchconfig.json";
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config not found: {configPath}");
            return;
        }

        var config = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(configPath), s_jsonOpts)!;
        foreach (var u in config.Hooks.OnUpdate ?? [])
        {
            u.Source = Path.GetFullPath(u.Source);
            if (u.CopyTo != null) u.CopyTo = Path.GetFullPath(u.CopyTo);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        var pending = new ConcurrentDictionary<string, CancellationTokenSource>();
        var states = new ConcurrentDictionary<string, (DateTime, long)>();

        async Task Run(Hook h, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(h.CopyTo) && File.Exists(h.Source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(h.CopyTo)!);
                File.Copy(h.Source, h.CopyTo, true);
                Log("COPY", $"Copied {Path.GetFileName(h.Source)}", ConsoleColor.Green);
            }
            if (string.IsNullOrWhiteSpace(h.Command)) return;
            var name = string.IsNullOrEmpty(h.Name) ? "Hook" : h.Name;
            var (fn, procArgs) = OperatingSystem.IsWindows() ? ("cmd.exe", $"/c \"{h.Command}\"") : ("sh", $"-c \"{h.Command}\"");
            var p = Process.Start(new ProcessStartInfo(fn, procArgs)
            {
                WorkingDirectory = string.IsNullOrEmpty(h.Location) ? Environment.CurrentDirectory : h.Location,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(name, e.Data, ConsoleColor.Cyan); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(name, e.Data, ConsoleColor.Red); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            try { await p.WaitForExitAsync(ct); } catch { p.Kill(true); }
        }

        void Log(string tag, string msg, ConsoleColor c)
        {
            lock (Console.Out) { Console.ForegroundColor = c; Console.Write($"[{tag}] "); Console.ResetColor(); Console.WriteLine(msg); }
        }

        await Task.WhenAll((config.Hooks.OnStartup ?? []).Where(h => h.Enabled).Select(h => Run(h, cts.Token)));
        if (args.Contains("--exit-after-startup")) return;

        var updateHooks = (config.Hooks.OnUpdate ?? []).Where(h => h.Enabled).ToList();
        var watchers = updateHooks.GroupBy(h => Path.GetDirectoryName(h.Source)!).Select(g =>
        {
            var w = new FileSystemWatcher(g.Key) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            void OnChanged(object s, FileSystemEventArgs e)
            {
                try
                {
                    var fi = new FileInfo(e.FullPath);
                    if (!fi.Exists || fi.Length == 0 || (states.TryGetValue(e.FullPath, out var last) && last == (fi.LastWriteTimeUtc, fi.Length))) return;
                    states[e.FullPath] = (fi.LastWriteTimeUtc, fi.Length);
                }
                catch { return; }
                foreach (var hook in updateHooks.Where(hu => string.Equals(hu.Source, e.FullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    var key = $"{hook.Source}|{hook.Command}";
                    if (pending.TryRemove(key, out var old)) { old.Cancel(); old.Dispose(); }
                    var ncts = new CancellationTokenSource(); pending[key] = ncts;
                    Task.Run(async () =>
                    {
                        try { await Task.Delay(config.Settings.DebounceMs, ncts.Token); await Run(hook, ncts.Token); }
                        catch (OperationCanceledException) { }
                        finally { pending.TryRemove(key, out _); ncts.Dispose(); }
                    });
                }
            }
            w.Changed += OnChanged; w.Created += OnChanged; w.Renamed += (s, e) => OnChanged(s, e);
            return w;
        }).ToList();

        Log("SYS", "Monitoring started. Press Ctrl+C to quit.", ConsoleColor.Green);
        try { await Task.Delay(-1, cts.Token); } catch (OperationCanceledException) { }
        foreach (var w in watchers) w.Dispose();
    }
}
