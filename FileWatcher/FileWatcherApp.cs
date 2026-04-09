using System;
using System.Collections.Concurrent;

using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("FileWatcher.Tests")]

namespace FileWatcher;

internal sealed class FileWatcherApp(
    string configPath,
    IProcessRunner? runner = null,
    IFileSystem? fs = null,
    ILogWebServer? webServer = null,
    IConsole? console = null
) : IDisposable
{
    private readonly string _configPath = configPath;
    private readonly IProcessRunner _runner = runner ?? new ShellProcessRunner();
    private readonly IFileSystem _fs = fs ?? new PhysicalFileSystem();
    private readonly ILogWebServer _web = webServer ?? new NullLogWebServer();
    private readonly IConsole _con = console ?? new SystemConsole();
    internal readonly ConcurrentDictionary<string, IFileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Time, long Size)> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, (string Label, int? Pid)> _tasks = new();
    internal WatchConfig _config = new();
    private CancellationTokenSource? _hooksCts;

    public async Task RunAsync(CancellationToken token, bool exitAfterStartup = false)
    {
        await LoadConfigAsync(token);
        SetupWatchers();
        if (_web.IsEnabled) _ = _web.StartAsync(_config.Settings.DashboardPort, token);
        LogService.Log(LogLevel.Success, $"Monitoring started. Dashboard: http://localhost:{_config.Settings.DashboardPort}");
        if (exitAfterStartup) await RunHooksAsync(token);
        else await Task.WhenAll(RunHooksAsync(token), RunConsoleLoopAsync(token));
    }

    public void Dispose()
    {
        _hooksCts?.Cancel();
        foreach (var w in _watchers.Values) w.Dispose();
        foreach (var cts in _pending.Values) cts.Cancel();
    }

    internal async Task LoadConfigAsync(CancellationToken token)
    {
        if (!_fs.FileExists(_configPath)) throw new FileNotFoundException(_configPath);
        await using var stream = _fs.OpenRead(_configPath);
        _config = await JsonSerializer.DeserializeAsync<WatchConfig>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }, token) ?? new();
        foreach (var u in _config.Hooks?.OnUpdate ?? [])
        {
            u.Source = Path.GetFullPath(u.Source);
            if (u.CopyTo != null) u.CopyTo = Path.GetFullPath(u.CopyTo);
        }
    }

    internal void SetupWatchers()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        var hooks = _config.Hooks?.OnUpdate?.Where(e => e.Enabled) ?? [];
        foreach (var entry in hooks)
        {
            var dir = Path.GetDirectoryName(entry.Source)!;
            _watchers.GetOrAdd(dir, d =>
            {
                var w = _fs.CreateWatcher(d, Constants.WatcherNotifyFilters);
                w.EnableRaisingEvents = true;
                w.Changed += (_, e) => HandleEvent(e); w.Created += (_, e) => HandleEvent(e); w.Renamed += (_, e) => HandleEvent(e);
                return w;
            });
        }
    }

    internal void HandleEvent(FileSystemEventArgs e)
    {
        var entries = _config.Hooks?.OnUpdate?.Where(u => u.Enabled && string.Equals(u.Source, e.FullPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries == null || entries.Count == 0 || !ShouldTrigger(e.FullPath)) return;
        foreach (var entry in entries) ScheduleAction(entry);
    }

    private bool ShouldTrigger(string path)
    {
        try
        {
            var state = _fs.GetFileInfo(path);
            if (state.Length == 0 || (_states.TryGetValue(path, out var prev) && prev == state)) return false;
            _states[path] = state; return true;
        }
        catch { return false; }
    }

    internal void ScheduleAction(UpdateEntry entry)
    {
        var key = $"{entry.Source}|{entry.Command}";
        if (_pending.TryRemove(key, out var old)) { old.Cancel(); old.Dispose(); }
        var cts = new CancellationTokenSource(); _pending[key] = cts;
        var task = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_config.Settings.DebounceMs, cts.Token);
                await RunHookAsync(entry, cts.Token);
            }
            finally { _pending.TryRemove(key, out _); cts.Dispose(); }
        });
        Track(task, entry.Name ?? Path.GetFileName(entry.Source));
    }

    internal async Task RunHooksAsync(CancellationToken token)
    {
        _hooksCts?.Cancel(); _hooksCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hooks = _config.Hooks?.OnStartup?.Where(h => h.Enabled != false) ?? [];
        await Task.WhenAll(hooks.Select(h => RunHookAsync(h, _hooksCts.Token)));
    }

    internal async Task RunHookAsync(HookEntry h, CancellationToken token)
    {
        if (h.CopyTo != null && h is UpdateEntry u)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(h.CopyTo)!);
            File.Copy(u.Source, h.CopyTo, true);
            LogService.Log(LogLevel.Copy, $"Copied {Path.GetFileName(u.Source)}");
        }
        if (string.IsNullOrWhiteSpace(h.Command)) return;
        var tag = string.IsNullOrWhiteSpace(h.Name) ? "Hook" : h.Name;
        Task? t = null;
        var task = _runner.RunAsync(h.Command, h.Location,
            l => LogService.Log(h.LogLevel, $"[{tag}] {l}"),
            l => LogService.Log(LogLevel.Error, $"[{tag}] {l}"),
            token, pid => { if (t != null) _tasks[t] = (tag, pid); });
        t = task; Track(t, tag);
        await t;
    }

    private void Track(Task t, string label)
    {
        _tasks[t] = (label, null);
        t.ContinueWith(task => _tasks.TryRemove(task, out _));
    }

    private async Task RunConsoleLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_con.KeyAvailable)
                {
                    switch (char.ToLower(_con.ReadKey(true).KeyChar))
                    {
                        case 'r': await LoadConfigAsync(token); SetupWatchers(); await RunHooksAsync(token); break;
                        case 't': ShowTasks(); break;
                        case 'q': return;
                        default: LogService.Log(LogLevel.Info, $"Status: {_watchers.Count} watchers, {_tasks.Count} tasks."); break;
                    }
                }
                await Task.Delay(Constants.KeyPollInterval, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void ShowTasks()
    {
        if (_tasks.IsEmpty) { LogService.Log(LogLevel.Info, "No active tasks."); return; }
        LogService.Log(LogLevel.Info, "Active Tasks:\nPID\tTask\n" + string.Join("\n", _tasks.Values.Select(v => $"{v.Pid?.ToString() ?? "-"}\t{v.Label}")));
    }
}


