using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

/// <summary>
/// Coordinates configuration loading, watcher lifecycle, and the console UI loop.
/// </summary>
/// <summary>
/// Encapsulates the long-running watcher workflow so Program.cs stays thin and testable.
/// </summary>
internal sealed class FileWatcherApp : IDisposable
{
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(75);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly JsonSerializerOptions SerializerOptionsIndented = CreateSerializerOptions(writeIndented: true);

    private readonly string _configPath;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCopyTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _directoryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, IReadOnlyList<FileMapping>> _directoryMappings = new(StringComparer.OrdinalIgnoreCase);
    private WatchConfig _config = new();
    private bool _disposed;

    public FileWatcherApp(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Entry point used by Program. Handles the happy-path lifecycle.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        await EnsureConfigurationExistsAsync(token);
        await LoadConfigurationAsync(token);
        SetupWatchers();
        TriggerInitialCopies(immediate: true);
        PrintWelcome();
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

    /// <summary>
    /// Reloads the JSON file while keeping the process alive.
    /// </summary>
    private async Task ReloadConfigurationAsync(CancellationToken token)
    {
        try
        {
            WriteInfo("\nReloading configuration...");
            await LoadConfigurationAsync(token);
            SetupWatchers();
            TriggerInitialCopies(immediate: true);
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
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = writeIndented
    };

    /// <summary>
    /// Guarantees there is a config on disk by seeding a sample if needed.
    /// </summary>
    private async Task EnsureConfigurationExistsAsync(CancellationToken token)
    {
        if (File.Exists(_configPath))
        {
            return;
        }

        var sample = JsonSerializer.Serialize(WatchConfig.CreateSample(), SerializerOptionsIndented);
        await File.WriteAllTextAsync(_configPath, sample, token);
        throw new FileNotFoundException($"Created sample config '{_configPath}'. Please customize it and run again.");
    }

    /// <summary>
    /// Reads and deserializes watchconfig.json with friendly error messages.
    /// </summary>
    private async Task LoadConfigurationAsync(CancellationToken token)
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

    /// <summary>
    /// Rebuilds the watcher set to match the current configuration.
    /// </summary>
    private void SetupWatchers()
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

    private bool ValidateMapping(FileMapping mapping)
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

    /// <summary>
    /// Filters raw file system events down to the mapping that needs copying.
    /// </summary>
    private void HandleFileChange(FileSystemEventArgs args)
    {
        if (args.ChangeType != WatcherChangeTypes.Changed)
        {
            return;
        }

        var directory = Path.GetDirectoryName(args.FullPath) ?? string.Empty;
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

    /// <summary>
    /// Ensures there is at most one pending copy per destination, with optional debounce.
    /// </summary>
    private void ScheduleCopy(FileMapping mapping, bool immediate = false)
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

    /// <summary>
    /// Handles the debounce wait, retries, and logging for a single copy operation.
    /// </summary>
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

    private async Task CopyFileWithRetryAsync(FileMapping mapping, CancellationToken token, int maxRetries = 3)
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
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), token);
            }
        }
    }

    /// <summary>
    /// Writes a timestamped safety copy if backups are enabled.
    /// </summary>
    private void CreateBackupIfNeeded(string destination)
    {
        if (!_config.Settings.CreateBackups || !File.Exists(destination))
        {
            return;
        }

        var backupFile = $"{destination}.backup.{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(destination, backupFile, overwrite: true);
    }

    /// <summary>
    /// Primes every mapping so destinations are up-to-date even before the first change event.
    /// </summary>
    private void TriggerInitialCopies(bool immediate)
    {
        foreach (var mapping in _directoryMappings.Values.SelectMany(list => list))
        {
            ScheduleCopy(mapping, immediate);
        }
    }

    private void ShowStatus()
    {
        WriteInfo($"\n=== Status at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Console.WriteLine($"Active watchers: {_directoryWatchers.Count}");
        Console.WriteLine($"Enabled mappings: {_directoryMappings.Values.Sum(list => list.Count)}");
        Console.WriteLine($"Pending copies: {_pendingCopyTokens.Count}");

        if (_pendingCopyTokens.Count > 0)
        {
            Console.WriteLine("Pending destinations:");
            foreach (var destination in _pendingCopyTokens.Keys)
            {
                Console.WriteLine($"  - {destination}");
            }
        }
    }

    private void PrintWelcome()
    {
        Console.WriteLine();
        WriteSuccess($"Monitoring {_directoryMappings.Values.Sum(list => list.Count)} enabled mapping(s).");
        Console.WriteLine("Press 'r' to reload config, 'q' to quit, any other key for status.");
    }

    private static void WriteCopySummary(FileMapping mapping)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Copied {Path.GetFileName(mapping.Source)} at {DateTime.Now:HH:mm:ss}");
        Console.ResetColor();
        if (!string.IsNullOrWhiteSpace(mapping.Description))
        {
            Console.WriteLine($"  Description: {mapping.Description}");
        }
        Console.WriteLine($"  From: {mapping.Source}");
        Console.WriteLine($"  To:   {mapping.Destination}");
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

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
        if (_disposed)
        {
            return;
        }

        CancelPendingCopies();
        DisposeWatchers();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public sealed record class WatchConfig
{
    [JsonPropertyName("mappings")]
    public List<FileMapping> Mappings { get; set; } = new();

    [JsonPropertyName("settings")]
    public WatchSettings Settings { get; set; } = new();

    public static WatchConfig CreateSample() => new()
    {
        Settings = new WatchSettings
        {
            DebounceMs = 1000,
            CreateBackups = false,
            LogLevel = "Info"
        },
        Mappings = new List<FileMapping>
        {
            new()
            {
                Source = @"C:\\src\\projects\\example\\file1.js",
                Destination = @"C:\\deploy\\example\\file1.js",
                Enabled = true,
                Description = "Main application script"
            },
            new()
            {
                Source = @"C:\\src\\projects\\example\\styles.css",
                Destination = @"C:\\deploy\\example\\styles.css",
                Enabled = false,
                Description = "Stylesheet (currently disabled)"
            }
        }
    };
}

public sealed record class FileMapping
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public sealed record class WatchSettings
{
    [JsonPropertyName("debounceMs")]
    public int DebounceMs { get; set; } = 1000;

    [JsonPropertyName("createBackups")]
    public bool CreateBackups { get; set; } = false;

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Info";
}
