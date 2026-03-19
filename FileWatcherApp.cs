using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    IProcessRunner? processRunner = null,
    IFileSystem? fileSystem = null,
    ILogWebServer? webServer = null,
    IConsole? console = null
) : IDisposable
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
    private readonly IFileSystem _fs = fileSystem ?? new PhysicalFileSystem();
    private readonly ILogWebServer _webServer = webServer ?? new DefaultLogWebServer();
    private readonly IConsole _console = console ?? new SystemConsole();

    internal readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTokens = new(
        StringComparer.OrdinalIgnoreCase
    );
    internal readonly ConcurrentDictionary<string, IFileSystemWatcher> _directoryWatchers = new(
        StringComparer.OrdinalIgnoreCase
    );
    private ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>> _directoryEntries = new(
        StringComparer.OrdinalIgnoreCase
    );
    internal readonly ConcurrentDictionary<string, (DateTime LastWrite, long Size)> _fileStates =
        new(StringComparer.OrdinalIgnoreCase);
    private WatchConfig _config = new();
    private bool _disposed;

    internal WatchConfig Config
    {
        get => _config;
        set => _config = value;
    }

    /// <summary>
    /// Orchestrates the entire application lifecycle: loading configuration, setting up watchers, running startup hooks, starting the web server, and entering the console command loop.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        await LoadConfigurationAsync(token);
        SetupWatchers();

        int port =
            _config.Settings.DashboardPort > 0
                ? _config.Settings.DashboardPort
                : DefaultDashboardPort;
        _ = _webServer.StartAsync(port, token);
        PrintWelcome(port);

        await Task.WhenAll(RunStartupHooksAsync(token), RunConsoleLoopAsync(token));
    }

    /// <summary>
    /// Writes <paramref name="message"/> at <see cref="LogLevel.Debug"/> only when the configured
    /// log level is <c>Debug</c> or <c>Trace</c>, keeping noise out of normal output.
    /// </summary>
    private void LogDebug(string message)
    {
        if (
            string.Equals(_config.Settings.LogLevel, "Debug", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_config.Settings.LogLevel, "Trace", StringComparison.OrdinalIgnoreCase)
        )
        {
            LogService.Log(LogLevel.Debug, message);
        }
    }

    /// <summary>
    /// Polls the console for key presses at a fixed interval until the token is cancelled.
    /// <list type="bullet">
    ///   <item><c>r</c> — reload configuration in place.</item>
    ///   <item><c>q</c> — graceful exit (throws <see cref="OperationCanceledException"/>).</item>
    ///   <item>any other key — print a brief status summary.</item>
    /// </list>
    /// </summary>
    private async Task RunConsoleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_console.KeyAvailable)
            {
                ConsoleKeyInfo key = _console.ReadKey(true);
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

    /// <summary>
    /// Reloads <c>watchconfig.json</c>, recreates all file watchers, and re-runs startup hooks.
    /// On failure the error is logged and the previous configuration remains active.
    /// </summary>
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

    /// <summary>
    /// Reads and deserializes <c>watchconfig.json</c> from <see cref="_configPath"/>,
    /// replacing the current <see cref="Config"/>.
    /// Throws <see cref="FileNotFoundException"/> if the file is absent and
    /// <see cref="InvalidOperationException"/> if it is empty or unparseable.
    /// </summary>
    internal async Task LoadConfigurationAsync(CancellationToken token)
    {
        if (!_fs.FileExists(_configPath))
            throw new FileNotFoundException($"Configuration file not found: {_configPath}");

        await using Stream stream = _fs.OpenRead(_configPath);
        _config =
            await JsonSerializer.DeserializeAsync<WatchConfig>(stream, SerializerOptions, token)
            ?? throw new InvalidOperationException("Configuration file is empty or malformed.");
        _config.Settings ??= new WatchSettings();
        _config.Hooks ??= new WatchHooks();
    }

    /// <summary>
    /// Translates config entries into FileSystemWatchers, grouping multiple monitored files by their parent directory to minimize OS handles.
    /// </summary>
    internal void SetupWatchers()
    {
        DisposeWatchers();
        var grouped = new Dictionary<string, List<UpdateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateEntry? entry in (_config.Hooks?.OnUpdate ?? []).Where(e => e.Enabled))
        {
            if (string.IsNullOrWhiteSpace(entry.Source))
            {
                LogService.Log(
                    LogLevel.Warning,
                    $"Entry skipped: source is missing ({entry.Description})."
                );
                continue;
            }
            if (!_fs.FileExists(entry.Source))
            {
                LogService.Log(LogLevel.Warning, $"Source file not found: {entry.Source}");
                continue;
            }

            string directory = Path.GetDirectoryName(entry.Source)!;
            if (!grouped.TryGetValue(directory, out List<UpdateEntry>? list))
            {
                list = [];
                grouped[directory] = list;
            }
            list.Add(entry);
            _directoryWatchers.GetOrAdd(directory, CreateWatcher);
            LogDebug($"Mapped entry '{entry.Source}' (Enabled)");
        }
        _directoryEntries = new ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>>(
            grouped.ToDictionary(g => g.Key, g => (IReadOnlyList<UpdateEntry>)g.Value.AsReadOnly()),
            StringComparer.OrdinalIgnoreCase
        );
        LogDebug($"SetupWatchers completed with {_directoryWatchers.Count} active watchers");
    }

    /// <summary>
    /// Instantiates and wires an <see cref="IFileSystemWatcher"/> for <paramref name="directory"/>,
    /// routing <c>Changed</c> and <c>Created</c> events to <see cref="HandleFileEvent"/> and
    /// logging any watcher errors.
    /// </summary>
    private IFileSystemWatcher CreateWatcher(string directory)
    {
        LogDebug($"Creating OS FileSystemWatcher for directory: {directory}");
        IFileSystemWatcher watcher = _fs.CreateWatcher(
            directory,
            NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        );
        watcher.EnableRaisingEvents = true;
        watcher.Changed += (_, e) => HandleFileEvent(e);
        watcher.Created += (_, e) => HandleFileEvent(e);
        watcher.Error += (_, e) =>
            LogService.Log(
                LogLevel.Error,
                $"Watcher error in {directory}: {e.GetException().Message}"
            );
        return watcher;
    }

    /// <summary>
    /// Core event handler for OS-level file system events.
    /// It acts as a shield against rapid, duplicate, or spurious events emitted by the OS or other processes
    /// (such as our own File.Copy operations) by explicitly checking if the LastWriteTime or Size actually changed.
    /// </summary>
    internal void HandleFileEvent(FileSystemEventArgs args)
    {
        LogDebug($"[OS Event] {args.ChangeType} -> {args.FullPath}");

        string? directory = Path.GetDirectoryName(args.FullPath);
        if (
            directory == null
            || !_directoryEntries.TryGetValue(directory, out IReadOnlyList<UpdateEntry>? entries)
        )
            return;

        UpdateEntry? entry = entries.FirstOrDefault(e =>
            string.Equals(e.Source, args.FullPath, StringComparison.OrdinalIgnoreCase)
        );
        if (entry == null)
        {
            LogDebug($"Ignoring {args.FullPath} (Not mapped to an active source)");
            return;
        }

        try
        {
            (DateTime LastWriteTimeUtc, long Length) currentState = _fs.GetFileInfo(args.FullPath);
            if (
                _fileStates.TryGetValue(
                    args.FullPath,
                    out (DateTime LastWrite, long Size) previousState
                )
                && previousState == currentState
            )
            {
                LogDebug($"Ignoring {args.FullPath} (No size/timestamp change since last event)");
                return;
            }

            _fileStates[args.FullPath] = currentState;
            LogDebug(
                $"Updated tracked state for {args.FullPath} -> Size: {currentState.Length}, Time: {currentState.Item1}"
            );
        }
        catch (Exception)
        {
            // Ignore if file is locked; we'll retry or just proceed with scheduling
            return;
        }

        ScheduleActions(entry);
    }

    /// <summary>
    /// Debounces and queues the actual work (copying/command execution).
    /// A new event always cancels a pending operation for the same file, resetting the debounce timer.
    /// </summary>
    internal void ScheduleActions(UpdateEntry entry)
    {
        LogDebug($"Scheduling actions for {entry.Source}");
        if (_pendingTokens.TryRemove(entry.Source, out CancellationTokenSource? existing))
        {
            LogDebug($"Cancelling previous pending actions for {entry.Source}");
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingTokens[entry.Source] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                int delay = Math.Max(0, _config.Settings.DebounceMs);
                LogDebug($"Delaying {delay}ms for {entry.Source}");
                await Task.Delay(delay, cts.Token);

                LogDebug($"Debounce complete, executing actions for {entry.Source}");
                if (entry.CopyTo != null)
                {
                    LogDebug($"Copying {entry.Source} to {entry.CopyTo}");
                    string? destDir = Path.GetDirectoryName(entry.CopyTo);
                    if (!string.IsNullOrEmpty(destDir))
                        _fs.CreateDirectory(destDir);
                    _fs.CopyFile(entry.Source, entry.CopyTo, overwrite: true);
                    LogService.Log(
                        LogLevel.Copy,
                        $"Copied {Path.GetFileName(entry.Source)} to {entry.CopyTo}"
                    );
                }
                if (!string.IsNullOrWhiteSpace(entry.Command))
                {
                    LogDebug($"Running command: {entry.Command}");
                    await RunHookAsync(entry.Command, entry.Location, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                LogDebug($"Action cancelled for {entry.Source}");
            }
            catch (Exception ex)
            {
                LogService.Log(LogLevel.Error, $"Error processing {entry.Source}: {ex.Message}");
            }
            finally
            {
                _pendingTokens.TryRemove(
                    new KeyValuePair<string, CancellationTokenSource>(entry.Source, cts)
                );
                cts.Dispose();
                LogDebug($"Completed actions for {entry.Source} and cleaned up token");
            }
        });
    }

    /// <summary>Runs every <see cref="StartupEntry"/> command in order, awaiting each in turn.</summary>
    internal async Task RunStartupHooksAsync(CancellationToken token)
    {
        foreach (StartupEntry entry in _config.Hooks?.OnStartup ?? [])
            await RunHookAsync(entry.Command, entry.Location, token);
    }

    /// <summary>
    /// Executes a single shell <paramref name="command"/> in <paramref name="location"/>
    /// (or the current directory when blank), routing output to the log and surfacing a warning
    /// on non-zero exit codes.
    /// </summary>
    internal async Task RunHookAsync(string command, string location, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        try
        {
            string workingDirectory = string.IsNullOrWhiteSpace(location)
                ? Environment.CurrentDirectory
                : location;
            int exitCode = await _processRunner.RunAsync(
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

    /// <summary>Logs a one-line status summary showing the active watcher and pending-action counts.</summary>
    internal void ShowStatus()
    {
        LogService.Log(
            LogLevel.Info,
            $"\nStatus at {DateTime.Now:T}: {_directoryWatchers.Count} watchers, {_pendingTokens.Count} pending."
        );
    }

    /// <summary>Logs the startup banner with the dashboard URL and available key commands.</summary>
    internal static void PrintWelcome(int port)
    {
        LogService.Log(LogLevel.Success, $"Monitoring started. Dashboard: http://localhost:{port}");
        LogService.Log(LogLevel.Info, "Commands: [r]eload, [q]uit, any other key for status.");
    }

    /// <summary>Disposes all active <see cref="IFileSystemWatcher"/> instances and clears the registry.</summary>
    private void DisposeWatchers()
    {
        foreach (IFileSystemWatcher w in _directoryWatchers.Values)
            w.Dispose();
        _directoryWatchers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (CancellationTokenSource cts in _pendingTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
