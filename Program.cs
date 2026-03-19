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
// Encapsulates the long-running watcher workflow so Program.cs stays thin and testable.
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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCopyTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _directoryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, IReadOnlyList<FileMapping>> _directoryMappings = new(StringComparer.OrdinalIgnoreCase);
    private WatchConfig _config = new();
    private bool _disposed;
    // Internal observable state — used by tests without reflection.
    internal WatchConfig Config { get => _config; set => _config = value; }
    internal ConcurrentDictionary<string, FileSystemWatcher> DirectoryWatchers => _directoryWatchers;
    internal ConcurrentDictionary<string, CancellationTokenSource> PendingCopyTokens => _pendingCopyTokens;
    // Entry point used by Program. Handles the happy-path lifecycle.
    public async Task RunAsync(CancellationToken token)
    {
        await EnsureConfigurationExistsAsync(token);
        await LoadConfigurationAsync(token);
        SetupWatchers();
        TriggerInitialCopies(immediate: true);
        await RunStartupHookAsync(token);
        // Start web dashboard
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
            await RunStartupHookAsync(token);
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
            {
                return Console.ReadKey(true);
            }
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
        if (File.Exists(_configPath))
        {
            return;
        }
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
            _config.Mappings ??= new List<FileMapping>();
            _config.Settings ??= new WatchSettings();
            WriteInfo($"Loaded {_config.Mappings.Count} mapping(s) from {_configPath}.");
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
        var groupedMappings = new Dictionary<string, List<FileMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in _config.Mappings.Where(m => m.Enabled))
        {
            if (!ValidateMapping(mapping))
            {
                continue;
            }
            var directory = Path.GetDirectoryName(mapping.Source)!;
            if (!groupedMappings.TryGetValue(directory, out var list))
            {
                list = new List<FileMapping>();
                groupedMappings[directory] = list;
            }
            list.Add(mapping);
            _directoryWatchers.GetOrAdd(directory, CreateWatcher);
        }
        var latestMappings = new ConcurrentDictionary<string, IReadOnlyList<FileMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in groupedMappings)
        {
            latestMappings[pair.Key] = pair.Value.AsReadOnly();
        }
        _directoryMappings = latestMappings;
    }
    internal bool ValidateMapping(FileMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.Source) || string.IsNullOrWhiteSpace(mapping.Destination))
        {
            WriteWarning($"Mapping skipped: Source or destination missing ({mapping.Description}).");
            return false;
        }
        if (!File.Exists(mapping.Source))
        {
            WriteWarning($"Source file not found: {mapping.Source}");
            return false;
        }
        if (Path.GetDirectoryName(mapping.Source) is null)
        {
            WriteWarning($"Unable to determine directory for source: {mapping.Source}");
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
    // Filters raw file system events down to the mapping that needs copying.
    internal void HandleFileChange(FileSystemEventArgs args)
    {
        if (args.ChangeType != WatcherChangeTypes.Changed)
        {
            return;
        }
        var directory = Path.GetDirectoryName(args.FullPath);
        if (directory is null)
        {
            WriteWarning($"Unable to determine directory for path: {args.FullPath}");
            return;
        }
        if (!_directoryMappings.TryGetValue(directory, out var mappings))
        {
            return;
        }
        var mapping = mappings.FirstOrDefault(m => string.Equals(m.Source, args.FullPath, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            return;
        }
        ScheduleCopy(mapping);
    }
    // Ensures there is at most one pending copy per destination, with optional debounce.
    internal void ScheduleCopy(FileMapping mapping, bool immediate = false)
    {
        var tokenSource = new CancellationTokenSource();
        if (_pendingCopyTokens.TryGetValue(mapping.Destination, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
        _pendingCopyTokens[mapping.Destination] = tokenSource;
        _ = StartCopyWorkflowAsync(mapping, tokenSource, immediate);
    }
    // Handles the debounce wait, retries, and logging for a single copy operation.
    private async Task StartCopyWorkflowAsync(FileMapping mapping, CancellationTokenSource cts, bool immediate)
    {
        try
        {
            if (!immediate)
            {
                var delay = Math.Max(0, _config.Settings.DebounceMs);
                await Task.Delay(delay, cts.Token);
            }
            await CopyFileWithRetryAsync(mapping, cts.Token);
            WriteCopySummary(mapping);
            if (!immediate)
            {
                await RunUpdateHookAsync(mapping, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer change cancels this copy.
        }
        catch (Exception ex)
        {
            WriteError($"Error copying {mapping.Source}: {ex.Message}");
        }
        finally
        {
            _pendingCopyTokens.TryRemove(mapping.Destination, out _);
            cts.Dispose();
        }
    }
    internal async Task CopyFileWithRetryAsync(FileMapping mapping, CancellationToken token, int maxRetries = 3)
    {
        var destinationDirectory = Path.GetDirectoryName(mapping.Destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                CreateBackupIfNeeded(mapping.Destination);
                File.Copy(mapping.Source, mapping.Destination, overwrite: true);
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
        if (!_config.Settings.CreateBackups || !File.Exists(destination))
        {
            return;
        }
        var backupFile = $"{destination}.backup.{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(destination, backupFile, overwrite: true);
    }
    // Primes every mapping so destinations are up-to-date even before the first change event.
    internal void TriggerInitialCopies(bool immediate)
    {
        foreach (var mapping in _directoryMappings.Values.SelectMany(list => list))
        {
            ScheduleCopy(mapping, immediate);
        }
    }
    internal async Task RunStartupHookAsync(CancellationToken token)
    {
        var hook = _config.Hooks?.OnStartup;
        if (hook != null)
        {
            WriteInfo("Executing onStartup hook...");
            await RunHookAsync(hook, token);
        }
    }
    internal async Task RunUpdateHookAsync(FileMapping mapping, CancellationToken token)
    {
        var hook = _config.Hooks?.OnUpdate;
        if (hook == null || string.IsNullOrWhiteSpace(hook.Command)) return;
        if (!string.IsNullOrEmpty(hook.ListenTo))
        {
            if (string.IsNullOrEmpty(mapping.Id) || !string.Equals(hook.ListenTo, mapping.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        WriteInfo($"Executing onUpdate hook for mapping '{mapping.Source}'...");
        await RunHookAsync(hook, token);
    }
    internal async Task RunHookAsync(HookEvent hook, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(hook.Command)) return;
        try
        {
            var workingDirectory = string.IsNullOrWhiteSpace(hook.Location)
                ? Environment.CurrentDirectory
                : hook.Location;
            var exitCode = await _processRunner.RunAsync(
                hook.Command,
                workingDirectory,
                line => LogService.Log(LogLevel.Info, $"[Hook] {line}"),
                line => LogService.Log(LogLevel.Error, $"[Hook Error] {line}"),
                token);
            if (exitCode != 0)
            {
                WriteWarning($"Hook exited with code {exitCode}");
            }
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
        LogService.Log(LogLevel.Info, $"Enabled mappings: {_directoryMappings.Values.Sum(list => list.Count)}");
        LogService.Log(LogLevel.Info, $"Pending copies: {_pendingCopyTokens.Count}");
        if (_pendingCopyTokens.Count > 0)
        {
            LogService.Log(LogLevel.Info, "Pending destinations:");
            foreach (var destination in _pendingCopyTokens.Keys)
            {
                LogService.Log(LogLevel.Info, $"  - {destination}");
            }
        }
    }
    internal void PrintWelcome(int port)
    {
        LogService.Log(LogLevel.Info, "");
        LogService.Log(LogLevel.Success, $"Monitoring {_directoryMappings.Values.Sum(list => list.Count)} enabled mapping(s).");
        LogService.Log(LogLevel.Info, $"Dashboard available at http://localhost:{port}");
        LogService.Log(LogLevel.Info, "Press 'r' to reload config, 'q' to quit, any other key for status.");
    }
    internal static void WriteCopySummary(FileMapping mapping)
    {
        LogService.Log(LogLevel.Copy, $"✓ Copied {Path.GetFileName(mapping.Source)} at {DateTime.Now:HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(mapping.Description))
        {
            LogService.Log(LogLevel.Info, $"  Description: {mapping.Description}");
        }
        LogService.Log(LogLevel.Info, $"  From: {mapping.Source}");
        LogService.Log(LogLevel.Info, $"  To:   {mapping.Destination}");
    }
    private static void WriteWarning(string message) => LogService.Log(LogLevel.Warning, message);
    private static void WriteError(string message) => LogService.Log(LogLevel.Error, message);
    private static void WriteInfo(string message) => LogService.Log(LogLevel.Info, message);
    private static void WriteSuccess(string message) => LogService.Log(LogLevel.Success, message);
    private void DisposeWatchers()
    {
        foreach (var watcher in _directoryWatchers.Values)
        {
            watcher.Dispose();
        }
        _directoryWatchers.Clear();
    }
    private void CancelPendingCopies()
    {
        foreach (var tokenSource in _pendingCopyTokens.Values)
        {
            tokenSource.Cancel();
            tokenSource.Dispose();
        }
        _pendingCopyTokens.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        // Cancel in-flight copies before disposing watchers so that any running
        // CopyFileWithRetryAsync tasks receive the cancellation signal and exit
        // cleanly rather than racing against a disposed FileSystemWatcher.
        CancelPendingCopies();
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this); // No finalizer — tells the GC to skip the finalizer queue.
    }
}

/// <summary>Root configuration object deserialised from <c>watchconfig.json</c>.</summary>
public sealed record WatchConfig
{
    /// <summary>Source → destination file mappings to watch and sync.</summary>
    public List<FileMapping> Mappings { get; set; } = [];
    /// <summary>Global settings such as debounce delay and dashboard port.</summary>
    public WatchSettings Settings { get; set; } = new();
    /// <summary>Optional lifecycle hooks executed on startup or file update.</summary>
    public WatchHooks? Hooks { get; set; }
    /// <summary>Returns a minimal sample configuration suitable for first-run seeding.</summary>
    public static WatchConfig CreateSample() => new()
    {
        Settings = new() { DebounceMs = 1000, DashboardPort = 5000 },
        Mappings = [new() { Source = "src/file.js", Destination = "out/file.js", Description = "App script" }]
    };
}

/// <summary>Describes a single source → destination file sync pair.</summary>
public sealed record FileMapping
{
    /// <summary>Optional unique identifier used to bind hooks to a specific mapping.</summary>
    public string? Id { get; set; }
    /// <summary>Absolute or relative path to the source file.</summary>
    public string Source { get; set; } = "";
    /// <summary>Absolute or relative path to the destination file.</summary>
    public string Destination { get; set; } = "";
    /// <summary>When <c>false</c> the mapping is skipped entirely.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Human-readable description shown in copy summaries and warnings.</summary>
    public string Description { get; set; } = "";
}

/// <summary>Global application settings read from the <c>settings</c> object in the config file.</summary>
public sealed record WatchSettings
{
    /// <summary>Milliseconds to wait after a file change before copying, to coalesce rapid saves.</summary>
    public int DebounceMs { get; set; } = 1000;
    /// <summary>When <c>true</c>, a timestamped backup of the destination is created before each overwrite.</summary>
    public bool CreateBackups { get; set; } = false;
    /// <summary>Reserved for future log-verbosity filtering; not yet enforced.</summary>
    public string LogLevel { get; set; } = "Info";
    /// <summary>TCP port for the web dashboard. Defaults to 5000 if zero or omitted.</summary>
    public int DashboardPort { get; set; } = 5000;
}

/// <summary>Container for the optional lifecycle hook commands.</summary>
public sealed record WatchHooks
{
    /// <summary>Hook executed once when the application starts (or after a config reload).</summary>
    public HookEvent? OnStartup { get; set; }
    /// <summary>Hook executed after each successful file copy.</summary>
    public HookEvent? OnUpdate { get; set; }
}

/// <summary>
/// A single hook command with an optional working directory and mapping filter.
/// <para><see cref="Location"/> may be empty or omitted; the process then runs in
/// <see cref="Environment.CurrentDirectory"/>.</para>
/// </summary>
public sealed record HookEvent
{
    /// <summary>Working directory for the command. Empty or whitespace uses the current directory.</summary>
    public string Location { get; set; } = "";
    /// <summary>Shell command to execute.</summary>
    public string Command { get; set; } = "";
    /// <summary>
    /// When set, the hook only fires for the mapping whose <see cref="FileMapping.Id"/> matches.
    /// When <c>null</c> or empty, the hook fires for every mapping update.
    /// </summary>
    public string? ListenTo { get; set; }
}
