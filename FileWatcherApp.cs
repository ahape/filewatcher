using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("FileWatcher.Tests")]

namespace FileWatcher;

internal sealed class FileWatcherApp(string configPath, IProcessRunner? processRunner = null)
    : IDisposable
{
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(75);
    private const int DefaultDashboardPort = 5002;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _configPath = configPath;
    private readonly IProcessRunner _processRunner = processRunner ?? new ShellProcessRunner();
    internal readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTokens = new(
        StringComparer.OrdinalIgnoreCase
    );
    internal readonly ConcurrentDictionary<string, FileSystemWatcher> _directoryWatchers = new(
        StringComparer.OrdinalIgnoreCase
    );
    private ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>> _directoryEntries = new(
        StringComparer.OrdinalIgnoreCase
    );
    private WatchConfig _config = new();
    private bool _disposed;

    internal WatchConfig Config
    {
        get => _config;
        set => _config = value;
    }

    public async Task RunAsync(CancellationToken token)
    {
        await LoadConfigurationAsync(token);
        SetupWatchers();
        await RunStartupHooksAsync(token);

        var port =
            _config.Settings.DashboardPort > 0
                ? _config.Settings.DashboardPort
                : DefaultDashboardPort;
        _ = LogWebServer.StartAsync(port, token);

        PrintWelcome(port);
        await RunConsoleLoopAsync(token);
    }

    private async Task RunConsoleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (char.ToLowerInvariant(key.KeyChar))
                {
                    case 'r':
                        await ReloadConfigurationAsync(token);
                        break;
                    case 'q':
                        throw new OperationCanceledException(token);
                    default:
                        ShowStatus();
                        break;
                }
            }
            await Task.Delay(KeyPollInterval, token);
        }
    }

    internal async Task ReloadConfigurationAsync(CancellationToken token)
    {
        try
        {
            LogService.Log(LogLevel.Info, "Reloading configuration...");
            await LoadConfigurationAsync(token);
            SetupWatchers();
            await RunStartupHooksAsync(token);
            LogService.Log(LogLevel.Success, "Configuration reloaded.");
        }
        catch (Exception ex)
        {
            LogService.Log(LogLevel.Error, $"Failed to reload: {ex.Message}");
        }
    }

    internal async Task LoadConfigurationAsync(CancellationToken token)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException($"Configuration file not found: {_configPath}");

        await using var stream = File.OpenRead(_configPath);
        _config =
            await JsonSerializer.DeserializeAsync<WatchConfig>(stream, SerializerOptions, token)
            ?? throw new InvalidOperationException("Configuration file is empty or malformed.");
        _config.Settings ??= new WatchSettings();
        _config.Hooks ??= new WatchHooks();
    }

    internal void SetupWatchers()
    {
        DisposeWatchers();
        var grouped = new Dictionary<string, List<UpdateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (_config.Hooks?.OnUpdate ?? []).Where(e => e.Enabled))
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || !File.Exists(entry.Source))
                continue;

            var directory = Path.GetDirectoryName(entry.Source)!;
            if (!grouped.TryGetValue(directory, out var list))
            {
                list = [];
                grouped[directory] = list;
            }
            list.Add(entry);
            _directoryWatchers.GetOrAdd(directory, CreateWatcher);
        }
        _directoryEntries = new ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>>(
            grouped.ToDictionary(g => g.Key, g => (IReadOnlyList<UpdateEntry>)g.Value.AsReadOnly()),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, e) => HandleFileEvent(e);
        watcher.Created += (_, e) => HandleFileEvent(e);
        watcher.Error += (_, e) =>
            LogService.Log(
                LogLevel.Error,
                $"Watcher error in {directory}: {e.GetException().Message}"
            );
        return watcher;
    }

    internal void HandleFileEvent(FileSystemEventArgs args)
    {
        var directory = Path.GetDirectoryName(args.FullPath);
        if (directory == null || !_directoryEntries.TryGetValue(directory, out var entries))
            return;

        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.Source, args.FullPath, StringComparison.OrdinalIgnoreCase)
        );
        if (entry == null)
            return;

        ScheduleActions(entry);
    }

    internal void ScheduleActions(UpdateEntry entry)
    {
        if (_pendingTokens.TryRemove(entry.Source, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingTokens[entry.Source] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, _config.Settings.DebounceMs), cts.Token);
                if (entry.CopyTo != null)
                {
                    var destDir = Path.GetDirectoryName(entry.CopyTo);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    File.Copy(entry.Source, entry.CopyTo, overwrite: true);
                    LogService.Log(
                        LogLevel.Copy,
                        $"Copied {Path.GetFileName(entry.Source)} to {entry.CopyTo}"
                    );
                }
                if (!string.IsNullOrWhiteSpace(entry.Command))
                {
                    await RunHookAsync(entry.Command, entry.Location, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.Log(LogLevel.Error, $"Error processing {entry.Source}: {ex.Message}");
            }
            finally
            {
                _pendingTokens.TryRemove(entry.Source, out _);
                cts.Dispose();
            }
        });
    }

    internal async Task RunStartupHooksAsync(CancellationToken token)
    {
        foreach (var entry in _config.Hooks?.OnStartup ?? [])
            await RunHookAsync(entry.Command, entry.Location, token);
    }

    internal async Task RunHookAsync(string command, string location, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        try
        {
            var workingDirectory = string.IsNullOrWhiteSpace(location)
                ? Environment.CurrentDirectory
                : location;
            var exitCode = await _processRunner.RunAsync(
                command,
                workingDirectory,
                line => LogService.Log(LogLevel.Info, $"[Hook] {line}"),
                line => LogService.Log(LogLevel.Error, $"[Hook Error] {line}"),
                token
            );
            if (exitCode != 0)
                LogService.Log(LogLevel.Warning, $"Hook exited with code {exitCode}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Log(LogLevel.Error, $"Hook failed: {ex.Message}");
        }
    }

    internal void ShowStatus()
    {
        LogService.Log(
            LogLevel.Info,
            $"\nStatus at {DateTime.Now:T}: {_directoryWatchers.Count} watchers, {_pendingTokens.Count} pending."
        );
    }

    internal static void PrintWelcome(int port)
    {
        LogService.Log(LogLevel.Success, $"Monitoring started. Dashboard: http://localhost:{port}");
        LogService.Log(LogLevel.Info, "Commands: [r]eload, [q]uit, any other key for status.");
    }

    private void DisposeWatchers()
    {
        foreach (var w in _directoryWatchers.Values)
            w.Dispose();
        _directoryWatchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var cts in _pendingTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
