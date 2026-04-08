using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// Orchestrates the entire application lifecycle: loading configuration, setting up
    /// watchers, running startup hooks, starting the web server, and entering the console
    /// command loop.
    /// </summary>
    public async Task RunAsync(CancellationToken token, bool exitAfterStartup = false)
    {
        await LoadConfigurationAsync(token);
        SetupWatchers();

        int port = GetDashboardPort();
        if (_webServer.IsEnabled)
            _ = _webServer.StartAsync(port, token);
        PrintWelcome(_webServer.IsEnabled, port);

        if (exitAfterStartup)
        {
            await RunStartupHooksAsync(token);
        }
        else
        {
            await Task.WhenAll(RunStartupHooksAsync(token), RunConsoleLoopAsync(token));
        }
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
        _hooksCts?.Cancel();
        _hooksCts?.Dispose();
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTokens = new(
        StringComparer.OrdinalIgnoreCase
    );
    internal readonly ConcurrentDictionary<string, IFileSystemWatcher> _directoryWatchers = new(
        StringComparer.OrdinalIgnoreCase
    );
    internal readonly ConcurrentDictionary<string, (DateTime LastWrite, long Size)> _fileStates =
        new(StringComparer.OrdinalIgnoreCase);

    internal WatchConfig Config { get; set; } = new();

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
    /// </summary>
    internal async Task LoadConfigurationAsync(CancellationToken token)
    {
        if (!_fs.FileExists(_configPath))
            throw new FileNotFoundException($"Configuration file not found: {_configPath}");

        await using Stream stream = _fs.OpenRead(_configPath);
        Config =
            await JsonSerializer.DeserializeAsync<WatchConfig>(stream, s_serializerOptions, token)
            ?? throw new InvalidOperationException("Configuration file is empty or malformed.");
        Config.Settings ??= new WatchSettings();
        Config.Hooks ??= new WatchHooks();
    }

    /// <summary>
    /// Translates config entries into FileSystemWatchers, grouping multiple monitored
    /// files by their parent directory to minimize OS handles.
    /// </summary>
    internal void SetupWatchers()
    {
        DisposeWatchers();
        var grouped = new Dictionary<string, List<UpdateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateEntry entry in (Config.Hooks?.OnUpdate ?? []).Where(e => e.Enabled))
            TryMapEntry(entry, grouped);
        _directoryEntries = new ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>>(
            grouped.ToDictionary(g => g.Key, g => (IReadOnlyList<UpdateEntry>)g.Value.AsReadOnly()),
            StringComparer.OrdinalIgnoreCase
        );
        LogDebug($"SetupWatchers completed with {_directoryWatchers.Count} active watchers");
    }

    /// <summary>
    /// Core event handler for OS-level file system events. Filters spurious/duplicate
    /// events and schedules actions for all matching entries.
    /// </summary>
    /// <remarks>
    /// <b>OS quirk — duplicate events:</b> Windows (and some Linux inotify drivers) frequently
    /// fire multiple <c>Changed</c> events for a single logical save. The method delegates to
    /// <see cref="TryUpdateAndValidateState"/> to deduplicate them by comparing (Size, LastWriteTime)
    /// against <see cref="_fileStates"/>.
    /// <para/>
    /// <b>OS quirk — atomic/safe save:</b> Editors like Visual Studio, VS Code, and JetBrains
    /// IDEs use an "atomic save" strategy: write to a temp file, rename the original to a backup,
    /// then rename the temp to the original filename. The watched file is never <em>modified</em> —
    /// it is <em>replaced</em> via rename. Without listening for <c>Renamed</c> events, these saves
    /// would be silently missed. <see cref="RenamedEventArgs"/> extends <see cref="FileSystemEventArgs"/>,
    /// so the <c>FullPath</c> property gives us the new (target) filename, which is the one we match
    /// against configured sources.
    /// </remarks>
    internal void HandleFileEvent(FileSystemEventArgs args)
    {
        LogDebug($"[OS Event] {args.ChangeType} -> {args.FullPath}");

        List<UpdateEntry>? matching = FindMatchingEntries(args.FullPath);
        if (matching == null || !TryUpdateAndValidateState(args.FullPath))
            return;

        foreach (UpdateEntry entry in matching)
            ScheduleActions(entry);
    }

    /// <summary>
    /// Debounces and queues the actual work (copying/command execution).
    /// A new event always cancels a pending operation for the same entry,
    /// resetting the debounce timer.
    /// </summary>
    internal void ScheduleActions(UpdateEntry entry)
    {
        string key = EntryKey(entry);
        string label = EntryLabel(entry);
        LogDebug($"Scheduling actions for [{label}]");
        if (_pendingTokens.TryRemove(key, out CancellationTokenSource? existing))
        {
            LogDebug($"Cancelling previous pending actions for [{label}]");
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingTokens[key] = cts;
        _ = Task.Run(() => ExecuteEntryActionsAsync(entry, key, label, cts));
    }

    /// <summary>
    /// Runs all startup hooks in parallel.
    /// Cancels any previous startup hooks if this is a reload.
    /// Returns a task that completes when all hooks have finished or were cancelled.
    /// </summary>
    internal async Task RunStartupHooksAsync(CancellationToken token)
    {
        _hooksCts?.Cancel();
        _hooksCts?.Dispose();
        _hooksCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken linkedToken = _hooksCts.Token;

        StartupEntry[] hooks = [.. Config.Hooks?.OnStartup ?? []];
        if (hooks.Length == 0)
            return;

        IEnumerable<Task> tasks = hooks.Select(async entry =>
        {
            string name = string.IsNullOrWhiteSpace(entry.Name) ? Constants.AnonymousHookName : entry.Name;
            Task hookTask = RunHookAsync(entry.Command, entry.Location, entry.LogLevel, name, linkedToken);

            int timeout = Config.Settings.StartupTimeoutMs > 0 ? Config.Settings.StartupTimeoutMs : Constants.DefaultStartupTimeoutMs;
            Task delayTask = Task.Delay(timeout, linkedToken);

            Task completedTask = await Task.WhenAny(hookTask, delayTask);

            if (completedTask == hookTask)
            {
                if (!linkedToken.IsCancellationRequested)
                    LogService.Log(LogLevel.Info, $"[{name}] startup hook executed");
            }
            else
            {
                if (!linkedToken.IsCancellationRequested)
                    LogService.Log(LogLevel.Info, $"[{name}] startup hook running in background");
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Executes a single shell <paramref name="command"/> in <paramref name="location"/>
    /// (or the current directory when blank), routing output to the log and surfacing a
    /// warning on non-zero exit codes.
    /// </summary>
    internal async Task RunHookAsync(
        string command,
        string location,
        LogLevel hookLogLevel,
        string name,
        CancellationToken token
    )
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        try
        {
            string workingDirectory = string.IsNullOrWhiteSpace(location)
                ? Environment.CurrentDirectory
                : location;
            bool silent = hookLogLevel == LogLevel.None;
            string tag = string.IsNullOrWhiteSpace(name) ? "Hook" : name;
            int exitCode = await _processRunner.RunAsync(
                command,
                workingDirectory,
                silent ? _ => { }
            : line => LogService.Log(hookLogLevel, $"[{tag}] {line}"),
                silent ? _ => { }
            : line => LogService.Log(LogLevel.Error, $"[{tag} Error] {line}"),
                token
            );
            if (exitCode != 0 && !silent)
                LogService.Log(LogLevel.Warning, $"[{tag}] exited with code {exitCode}");
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

    private readonly string _configPath = configPath;
    private readonly IProcessRunner _processRunner = processRunner ?? new ShellProcessRunner();
    private readonly IFileSystem _fs = fileSystem ?? new PhysicalFileSystem();
    private readonly ILogWebServer _webServer = webServer ?? new NullLogWebServer();
    private readonly IConsole _console = console ?? new SystemConsole();
    private CancellationTokenSource? _hooksCts;
    private ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>> _directoryEntries = new(
        StringComparer.OrdinalIgnoreCase
    );
    private bool _disposed;

    /// <summary>
    /// Writes <paramref name="message"/> at <see cref="LogLevel.Debug"/> only when the
    /// configured log level is <c>Debug</c> or <c>Trace</c>.
    /// </summary>
    private void LogDebug(string message)
    {
        if (
            string.Equals(Config.Settings.LogLevel, "Debug", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Config.Settings.LogLevel, "Trace", StringComparison.OrdinalIgnoreCase)
        )
        {
            LogService.Log(LogLevel.Debug, message);
        }
    }

    /// <summary>
    /// Polls the console for key presses at a fixed interval until the token is cancelled.
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
            await Task.Delay(s_keyPollInterval, token);
        }
    }

    /// <summary>
    /// Instantiates and wires an <see cref="IFileSystemWatcher"/> for
    /// <paramref name="directory"/>, routing events to <see cref="HandleFileEvent"/>.
    /// </summary>
    /// <remarks>
    /// Three event types are subscribed:
    /// <list type="bullet">
    ///   <item><c>Changed</c> — covers normal in-place writes (e.g., <c>notepad</c>, build tools).</item>
    ///   <item><c>Created</c> — covers files that are deleted and recreated rather than overwritten.</item>
    ///   <item><c>Renamed</c> — covers editors that use "atomic save" (write-temp-then-rename),
    ///         including Visual Studio, VS Code, JetBrains IDEs, and <c>vim</c>. Without this,
    ///         saves from these editors would be silently missed because the watched file is never
    ///         modified — it is replaced via an OS rename operation.</item>
    /// </list>
    /// </remarks>
    private IFileSystemWatcher CreateWatcher(string directory)
    {
        LogDebug($"Creating OS FileSystemWatcher for directory: {directory}");
        IFileSystemWatcher watcher = _fs.CreateWatcher(
            directory,
            Constants.WatcherNotifyFilters
        );
        watcher.EnableRaisingEvents = true;
        watcher.Changed += (_, e) => HandleFileEvent(e);
        watcher.Created += (_, e) => HandleFileEvent(e);
        watcher.Renamed += (_, e) => HandleFileEvent(e);
        watcher.Error += (_, e) =>
            LogService.Log(
                LogLevel.Error,
                $"Watcher error in {directory}: {e.GetException().Message}"
            );
        return watcher;
    }

    /// <summary>Returns all entries matching <paramref name="fullPath"/>, or null if none.</summary>
    private List<UpdateEntry>? FindMatchingEntries(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (
            directory == null
            || !_directoryEntries.TryGetValue(directory, out IReadOnlyList<UpdateEntry>? entries)
        )
            return null;

        List<UpdateEntry> matching =
        [
            .. entries.Where(e =>
                string.Equals(e.Source, fullPath, StringComparison.OrdinalIgnoreCase)
            ),
        ];
        if (matching.Count == 0)
        {
            LogDebug($"Ignoring {fullPath} (Not mapped to an active source)");
            return null;
        }
        return matching;
    }

    /// <summary>
    /// Validates that the file at <paramref name="fullPath"/> actually changed (size or
    /// timestamp) and is non-empty. Updates <see cref="_fileStates"/> on success.
    /// </summary>
    /// <remarks>
    /// <b>OS quirk — duplicate/spurious events:</b> Windows commonly fires 2–4 <c>Changed</c>
    /// events for a single save operation. This method deduplicates them by comparing the current
    /// (LastWriteTime, Size) tuple against the previously recorded state in <see cref="_fileStates"/>.
    /// If nothing actually changed, the event is discarded.
    /// <para/>
    /// <b>OS quirk — mid-write truncation:</b> Many editors and build tools truncate a file to
    /// zero bytes before writing new content. The OS fires a <c>Changed</c> event at the zero-byte
    /// stage. If we acted on it, we would copy an empty file or lint empty source. A zero-byte
    /// file is almost certainly a transient mid-write state, so we discard it and wait for the
    /// next event that carries the real content.
    /// <para/>
    /// <b>OS quirk — locked files:</b> The file may be locked by the writing process at the
    /// moment we try to read its metadata. Rather than crashing, we silently return <c>false</c>
    /// and let the next OS event retry.
    /// </remarks>
    private bool TryUpdateAndValidateState(string fullPath)
    {
        try
        {
            (DateTime LastWriteTimeUtc, long Length) currentState = _fs.GetFileInfo(fullPath);
            if (
                _fileStates.TryGetValue(fullPath, out (DateTime LastWrite, long Size) previousState)
                && previousState == currentState
            )
            {
                LogDebug($"Ignoring {fullPath} (No size/timestamp change since last event)");
                return false;
            }

            _fileStates[fullPath] = currentState;
            LogDebug(
                $"Updated tracked state for {fullPath} -> Size: {currentState.Length}, Time: {currentState.LastWriteTimeUtc}"
            );

            if (currentState.Length == 0)
            {
                LogDebug($"Ignoring {fullPath} (File is empty, likely mid-write)");
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Awaits the debounce delay, then runs copy/command actions for the entry.</summary>
    private async Task ExecuteEntryActionsAsync(
        UpdateEntry entry,
        string key,
        string label,
        CancellationTokenSource cts
    )
    {
        try
        {
            int delay = Math.Max(0, Config.Settings.DebounceMs);
            LogDebug($"Delaying {delay}ms for [{label}]");
            await Task.Delay(delay, cts.Token);

            LogDebug($"Debounce complete, executing actions for [{label}]");
            await RunEntryAsync(entry, cts.Token);
        }
        catch (OperationCanceledException)
        {
            LogDebug($"Action cancelled for [{label}]");
        }
        catch (Exception ex)
        {
            LogService.Log(LogLevel.Error, $"Error processing [{label}]: {ex.Message}");
        }
        finally
        {
            _pendingTokens.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            cts.Dispose();
            LogDebug($"Completed actions for [{label}] and cleaned up token");
        }
    }

    /// <summary>Runs the copy and/or command actions for a single entry.</summary>
    private async Task RunEntryAsync(UpdateEntry entry, CancellationToken token)
    {
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
            await RunHookAsync(entry.Command, entry.Location, entry.LogLevel, entry.Name, token);
        }
    }

    /// <summary>Validates and maps a single config entry into the grouped dictionary.</summary>
    private void TryMapEntry(UpdateEntry entry, Dictionary<string, List<UpdateEntry>> grouped)
    {
        if (string.IsNullOrWhiteSpace(entry.Source))
        {
            LogService.Log(
                LogLevel.Warning,
                $"Entry skipped: source is missing ({entry.Description})."
            );
            return;
        }
        if (!_fs.FileExists(entry.Source))
        {
            LogService.Log(LogLevel.Warning, $"Source file not found: {entry.Source}");
            return;
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

    /// <summary>Disposes all active <see cref="IFileSystemWatcher"/> instances and clears the registry.</summary>
    private void DisposeWatchers()
    {
        foreach (IFileSystemWatcher w in _directoryWatchers.Values)
            w.Dispose();
        _directoryWatchers.Clear();
    }

    private int GetDashboardPort() =>
        Config.Settings.DashboardPort > 0 ? Config.Settings.DashboardPort : Constants.DefaultDashboardPort;

    /// <summary>Logs the startup banner with the dashboard state and available key commands.</summary>
    internal static void PrintWelcome(bool webServerEnabled, int port)
    {
        string message = webServerEnabled
            ? $"Monitoring started. Dashboard: http://localhost:{port}"
            : "Monitoring started. Web dashboard disabled.";
        LogService.Log(LogLevel.Success, message);
        LogService.Log(LogLevel.Info, "Commands: [r]eload, [q]uit, any other key for status.");
    }

    private static readonly TimeSpan s_keyPollInterval = Constants.KeyPollInterval;
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Returns a key that uniquely identifies this entry's action, so that multiple
    /// entries watching the same source file get independent debounce timers.
    /// </summary>
    private static string EntryKey(UpdateEntry entry) =>
        $"{entry.Source}|{entry.CopyTo}|{entry.Command}";

    private static string EntryLabel(UpdateEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Description)
            ? Path.GetFileName(entry.Source)
            : entry.Description;
}
