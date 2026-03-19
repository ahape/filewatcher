using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("FileWatcher.Tests")]
namespace FileWatcher;

internal static class Program
{
    private const string ConfigFileName = "watchconfig.json";
    private static readonly CancellationTokenSource ShutdownTokenSource = new();
    private static async Task Main()
    {
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            ShutdownTokenSource.Cancel();
        };
        try
        {
            using var app = new FileWatcherApp(ConfigFileName);
            await app.RunAsync(ShutdownTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown triggered by Ctrl+C or the interactive loop.
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
        }
        finally
        {
            ShutdownTokenSource.Dispose();
        }
    }
}
internal interface IProcessRunner { Task<int> RunAsync(string command, string workingDirectory, Action<string> onOutput, Action<string> onError, CancellationToken token); }
internal sealed class ShellProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string cmd, string dir, Action<string> onOut, Action<string> onErr, CancellationToken token)
    {
        var (fn, args) = OperatingSystem.IsWindows() ? ("cmd.exe", $"/c \"{cmd}\"") : ("sh", $"-c \"{cmd}\"");
        using var p = new Process { StartInfo = new ProcessStartInfo { FileName = fn, Arguments = args, WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOut(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr(e.Data); };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync(token);
        return p.ExitCode;
    }
}
// Coordinates configuration loading, watcher lifecycle, and the console UI loop.
internal sealed class FileWatcherApp(string configPath, IProcessRunner? processRunner = null) : IDisposable
{
    // 75 ms gives ~13 polls/s — responsive enough for key input without busy-waiting.
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(75);
    // Base delay for exponential back-off retries when a file is briefly locked.
    private const int CopyRetryBackoffMs = 150;
    // Fallback port when the config does not specify one.
    private const int DefaultDashboardPort = 5000;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly JsonSerializerOptions SerializerOptionsIndented = CreateSerializerOptions(writeIndented: true);
    private readonly string _configPath = configPath;
    private readonly IProcessRunner _processRunner = processRunner ?? new ShellProcessRunner();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _directoryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>> _directoryEntries = new(StringComparer.OrdinalIgnoreCase);
    private WatchConfig _config = new();
    private bool _disposed;
    // Internal observable state — used by tests without reflection.
    internal WatchConfig Config { get => _config; set => _config = value; }
    internal ConcurrentDictionary<string, FileSystemWatcher> DirectoryWatchers => _directoryWatchers;
    internal ConcurrentDictionary<string, CancellationTokenSource> PendingTokens => _pendingTokens;
    // Entry point used by Program. Handles the happy-path lifecycle.
    public async Task RunAsync(CancellationToken token)
    {
        await EnsureConfigurationExistsAsync(token);
        await LoadConfigurationAsync(token);
        SetupWatchers();
        TriggerInitialCopies(immediate: true);
        await RunStartupHooksAsync(token);
        var port = _config.Settings.DashboardPort > 0 ? _config.Settings.DashboardPort : DefaultDashboardPort;
        _ = LogWebServer.StartAsync(port, token);
        PrintWelcome(port);
        await RunConsoleLoopAsync(token);
    }
    private async Task RunConsoleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var key = await ReadKeyAsync(token);
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
    }
    // Reloads the JSON file while keeping the process alive.
    internal async Task ReloadConfigurationAsync(CancellationToken token)
    {
        try
        {
            WriteInfo("\nReloading configuration...");
            await LoadConfigurationAsync(token);
            SetupWatchers();
            TriggerInitialCopies(immediate: true);
            await RunStartupHooksAsync(token);
            WriteSuccess("Configuration reloaded.");
        }
        catch (Exception ex)
        {
            WriteError($"Failed to reload configuration: {ex.Message}");
        }
    }
    private static async Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
                return Console.ReadKey(true);
            await Task.Delay(KeyPollInterval, token);
        }
        throw new OperationCanceledException(token);
    }
    private static JsonSerializerOptions CreateSerializerOptions(bool writeIndented = false) => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = writeIndented
    };
    // Guarantees there is a config on disk by seeding a sample if needed.
    internal async Task EnsureConfigurationExistsAsync(CancellationToken token)
    {
        if (File.Exists(_configPath)) return;
        var sample = JsonSerializer.Serialize(WatchConfig.CreateSample(), SerializerOptionsIndented);
        await File.WriteAllTextAsync(_configPath, sample, token);
        throw new FileNotFoundException($"Created sample config '{_configPath}'. Please customize it and run again.");
    }
    // Reads and deserializes watchconfig.json with friendly error messages.
    internal async Task LoadConfigurationAsync(CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(_configPath);
            _config = await JsonSerializer.DeserializeAsync<WatchConfig>(stream, SerializerOptions, token)
                ?? throw new InvalidOperationException("Configuration file is empty or malformed.");
            _config.Settings ??= new WatchSettings();
            _config.Hooks ??= new WatchHooks();
            WriteInfo($"Loaded {_config.Hooks.OnUpdate.Count} onUpdate entry(ies) from {_configPath}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in configuration: {ex.Message}", ex);
        }
    }
    // Rebuilds the watcher set to match the current configuration.
    internal void SetupWatchers()
    {
        DisposeWatchers();
        var grouped = new Dictionary<string, List<UpdateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (_config.Hooks?.OnUpdate ?? []).Where(e => e.Enabled))
        {
            if (!ValidateEntry(entry)) continue;
            var directory = Path.GetDirectoryName(entry.Source)!;
            if (!grouped.TryGetValue(directory, out var list))
            {
                list = [];
                grouped[directory] = list;
            }
            list.Add(entry);
            _directoryWatchers.GetOrAdd(directory, CreateWatcher);
        }
        var latest = new ConcurrentDictionary<string, IReadOnlyList<UpdateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in grouped)
            latest[pair.Key] = pair.Value.AsReadOnly();
        _directoryEntries = latest;
    }
    internal bool ValidateEntry(UpdateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Source))
        {
            WriteWarning($"Entry skipped: source is missing ({entry.Description}).");
            return false;
        }
        if (!File.Exists(entry.Source))
        {
            WriteWarning($"Source file not found: {entry.Source}");
            return false;
        }
        if (Path.GetDirectoryName(entry.Source) is null)
        {
            WriteWarning($"Unable to determine directory for source: {entry.Source}");
            return false;
        }
        return true;
    }
    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Changed += (_, args) => HandleFileChange(args);
        watcher.Error += (_, args) => WriteError($"Watcher error in {directory}: {args.GetException().Message}");
        WriteInfo($"Watching {directory}");
        return watcher;
    }
    // Filters raw file system events to the matching entry.
    internal void HandleFileChange(FileSystemEventArgs args)
    {
        if (args.ChangeType != WatcherChangeTypes.Changed) return;
        var directory = Path.GetDirectoryName(args.FullPath);
        if (directory is null)
        {
            WriteWarning($"Unable to determine directory for path: {args.FullPath}");
            return;
        }
        if (!_directoryEntries.TryGetValue(directory, out var entries)) return;
        var entry = entries.FirstOrDefault(e => string.Equals(e.Source, args.FullPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        ScheduleActions(entry);
    }
    // Ensures at most one pending action per source, with optional debounce.
    internal void ScheduleActions(UpdateEntry entry, bool immediate = false)
    {
        var tokenSource = new CancellationTokenSource();
        if (_pendingTokens.TryGetValue(entry.Source, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
        _pendingTokens[entry.Source] = tokenSource;
        _ = StartActionsWorkflowAsync(entry, tokenSource, immediate);
    }
    // Handles debounce, copy, and command execution for a single entry.
    private async Task StartActionsWorkflowAsync(UpdateEntry entry, CancellationTokenSource cts, bool immediate)
    {
        try
        {
            if (!immediate)
            {
                var delay = Math.Max(0, _config.Settings.DebounceMs);
                await Task.Delay(delay, cts.Token);
            }
            if (entry.CopyTo != null)
            {
                await CopyFileWithRetryAsync(entry.Source, entry.CopyTo, cts.Token);
                WriteCopySummary(entry);
            }
            if (!immediate && !string.IsNullOrWhiteSpace(entry.Command))
            {
                WriteInfo($"Executing onUpdate command for '{entry.Source}'...");
                await RunHookAsync(entry.Command, entry.Location, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer change cancels this action.
        }
        catch (Exception ex)
        {
            WriteError($"Error processing {entry.Source}: {ex.Message}");
        }
        finally
        {
            _pendingTokens.TryRemove(entry.Source, out _);
            cts.Dispose();
        }
    }
    internal async Task CopyFileWithRetryAsync(string source, string destination, CancellationToken token, int maxRetries = 3)
    {
        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                CreateBackupIfNeeded(destination);
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(CopyRetryBackoffMs * attempt), token);
            }
        }
    }
    // Writes a timestamped safety copy if backups are enabled.
    internal void CreateBackupIfNeeded(string destination)
    {
        if (!_config.Settings.CreateBackups || !File.Exists(destination)) return;
        File.Copy(destination, $"{destination}.backup.{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
    }
    // Primes every entry that has a copyTo so destinations are up-to-date before the first change event.
    internal void TriggerInitialCopies(bool immediate)
    {
        foreach (var entry in _directoryEntries.Values.SelectMany(list => list).Where(e => e.CopyTo != null))
            ScheduleActions(entry, immediate);
    }
    internal async Task RunStartupHooksAsync(CancellationToken token)
    {
        foreach (var entry in _config.Hooks?.OnStartup ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Command)) continue;
            WriteInfo("Executing onStartup hook...");
            await RunHookAsync(entry.Command, entry.Location, token);
        }
    }
    internal async Task RunHookAsync(string command, string location, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            var workingDirectory = string.IsNullOrWhiteSpace(location) ? Environment.CurrentDirectory : location;
            var exitCode = await _processRunner.RunAsync(
                command,
                workingDirectory,
                line => LogService.Log(LogLevel.Info, $"[Hook] {line}"),
                line => LogService.Log(LogLevel.Error, $"[Hook Error] {line}"),
                token);
            if (exitCode != 0)
                WriteWarning($"Hook exited with code {exitCode}");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            WriteError($"Failed to run hook: {ex.Message}");
        }
    }
    internal void ShowStatus()
    {
        LogService.Log(LogLevel.Info, $"\n=== Status at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        LogService.Log(LogLevel.Info, $"Active watchers: {_directoryWatchers.Count}");
        LogService.Log(LogLevel.Info, $"Active entries: {_directoryEntries.Values.Sum(list => list.Count)}");
        LogService.Log(LogLevel.Info, $"Pending actions: {_pendingTokens.Count}");
        if (_pendingTokens.Count > 0)
        {
            LogService.Log(LogLevel.Info, "Pending sources:");
            foreach (var src in _pendingTokens.Keys)
                LogService.Log(LogLevel.Info, $"  - {src}");
        }
    }
    internal void PrintWelcome(int port)
    {
        LogService.Log(LogLevel.Info, "");
        LogService.Log(LogLevel.Success, $"Monitoring {_directoryEntries.Values.Sum(list => list.Count)} enabled entry(ies).");
        LogService.Log(LogLevel.Info, $"Dashboard available at http://localhost:{port}");
        LogService.Log(LogLevel.Info, "Press 'r' to reload config, 'q' to quit, any other key for status.");
    }
    internal static void WriteCopySummary(UpdateEntry entry)
    {
        LogService.Log(LogLevel.Copy, $"✓ Copied {Path.GetFileName(entry.Source)} at {DateTime.Now:HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(entry.Description))
            LogService.Log(LogLevel.Info, $"  Description: {entry.Description}");
        LogService.Log(LogLevel.Info, $"  From: {entry.Source}");
        LogService.Log(LogLevel.Info, $"  To:   {entry.CopyTo}");
    }
    private static void WriteWarning(string message) => LogService.Log(LogLevel.Warning, message);
    private static void WriteError(string message) => LogService.Log(LogLevel.Error, message);
    private static void WriteInfo(string message) => LogService.Log(LogLevel.Info, message);
    private static void WriteSuccess(string message) => LogService.Log(LogLevel.Success, message);
    private void DisposeWatchers()
    {
        foreach (var watcher in _directoryWatchers.Values)
            watcher.Dispose();
        _directoryWatchers.Clear();
    }
    private void CancelPendingActions()
    {
        foreach (var tokenSource in _pendingTokens.Values)
        {
            tokenSource.Cancel();
            tokenSource.Dispose();
        }
        _pendingTokens.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        CancelPendingActions();
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Root configuration object deserialised from <c>watchconfig.json</c>.</summary>
public sealed record WatchConfig
{
    /// <summary>Global settings such as debounce delay and dashboard port.</summary>
    public WatchSettings Settings { get; set; } = new();
    /// <summary>Lifecycle hooks: arrays of startup commands and per-file update entries.</summary>
    public WatchHooks? Hooks { get; set; }
    /// <summary>Returns a minimal sample configuration suitable for first-run seeding.</summary>
    public static WatchConfig CreateSample() => new()
    {
        Settings = new() { DebounceMs = 1000, DashboardPort = 5000 },
        Hooks = new()
        {
            OnStartup = [new() { Command = "echo started" }],
            OnUpdate = [new() { Source = "src/file.js", CopyTo = "out/file.js", Command = "echo updated", Description = "App script" }]
        }
    };
}

/// <summary>Global application settings read from the <c>settings</c> object in the config file.</summary>
public sealed record WatchSettings
{
    /// <summary>Milliseconds to wait after a file change before acting, to coalesce rapid saves.</summary>
    public int DebounceMs { get; set; } = 1000;
    /// <summary>When <c>true</c>, a timestamped backup of the destination is created before each overwrite.</summary>
    public bool CreateBackups { get; set; } = false;
    /// <summary>Reserved for future log-verbosity filtering; not yet enforced.</summary>
    public string LogLevel { get; set; } = "Info";
    /// <summary>TCP port for the web dashboard. Defaults to 5000 if zero or omitted.</summary>
    public int DashboardPort { get; set; } = 5000;
}

/// <summary>Container for lifecycle hook arrays.</summary>
public sealed record WatchHooks
{
    /// <summary>Commands executed once when the application starts (or after a config reload).</summary>
    public List<StartupEntry> OnStartup { get; set; } = [];
    /// <summary>Entries executed after each debounced file change.</summary>
    public List<UpdateEntry> OnUpdate { get; set; } = [];
}

/// <summary>A command to run on application startup or config reload.</summary>
public sealed record StartupEntry
{
    /// <summary>Shell command to execute.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working directory for the command. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";
}

/// <summary>
/// Watches a source file and, on change, optionally copies it to <see cref="CopyTo"/>
/// and/or executes <see cref="Command"/>. Both actions are opt-in.
/// </summary>
public sealed record UpdateEntry
{
    /// <summary>Absolute or relative path to the source file to watch.</summary>
    public string Source { get; set; } = "";
    /// <summary>When <c>false</c> the entry is skipped entirely.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Destination path to copy the source file to on change. Omit to skip copying.</summary>
    public string? CopyTo { get; set; }
    /// <summary>Shell command to run after a change (and after copying, if applicable). Omit to skip.</summary>
    public string? Command { get; set; }
    /// <summary>Working directory for <see cref="Command"/>. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";
    /// <summary>Human-readable description shown in copy summaries and warnings.</summary>
    public string Description { get; set; } = "";
}
