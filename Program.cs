using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyNamespace
{
    public class WatchConfig
    {
        [JsonPropertyName("mappings")]
        public List<FileMapping> Mappings { get; set; } = new();
        
        [JsonPropertyName("settings")]
        public WatchSettings Settings { get; set; } = new();
    }

    public class FileMapping
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";
        
        [JsonPropertyName("destination")]
        public string Destination { get; set; } = "";
        
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
    }

    public class WatchSettings
    {
        [JsonPropertyName("debounceMs")]
        public int DebounceMs { get; set; } = 1000;
        
        [JsonPropertyName("createBackups")]
        public bool CreateBackups { get; set; } = false;
        
        [JsonPropertyName("logLevel")]
        public string LogLevel { get; set; } = "Info";
    }

    class EnhancedFileWatcher
    {
        private static WatchConfig _config;
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingWrites = new();
        private static readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
        private static readonly Dictionary<string, List<FileMapping>> _watcherMappings = new();

        static void Main()
        {
            try
            {
                LoadConfiguration();
                SetupWatchers();
                
                Console.WriteLine($"Monitoring {_config.Mappings.Count(m => m.Enabled)} file mappings.");
                Console.WriteLine("Press 'r' to reload config, 'q' to quit, or any other key for status.");
                
                ConsoleKeyInfo key;
                do
                {
                    key = Console.ReadKey(true);
                    switch (key.KeyChar)
                    {
                        case 'r':
                        case 'R':
                            ReloadConfiguration();
                            break;
                        case 'q':
                        case 'Q':
                            break;
                        default:
                            ShowStatus();
                            break;
                    }
                } while (key.KeyChar != 'q' && key.KeyChar != 'Q');
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
            finally
            {
                Cleanup();
            }
        }

        private static void LoadConfiguration()
        {
            const string configFile = "watchconfig.json";
            
            if (!File.Exists(configFile))
            {
                CreateSampleConfig(configFile);
                throw new FileNotFoundException($"Created sample config file '{configFile}'. Please edit it and restart.");
            }

            try
            {
                var json = File.ReadAllText(configFile);
                _config = JsonSerializer.Deserialize<WatchConfig>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (_config?.Mappings == null)
                    throw new InvalidOperationException("Invalid configuration format");

                Console.WriteLine($"Loaded configuration with {_config.Mappings.Count} mappings");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON in config file: {ex.Message}");
            }
        }

        private static void CreateSampleConfig(string configFile)
        {
            var sampleConfig = new WatchConfig
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
                        Source = @"C:\src\projects\example\file1.js",
                        Destination = @"C:\deploy\example\file1.js",
                        Enabled = true,
                        Description = "Main application script"
                    },
                    new()
                    {
                        Source = @"C:\src\projects\example\styles.css",
                        Destination = @"C:\deploy\example\styles.css",
                        Enabled = false,
                        Description = "Stylesheet (currently disabled)"
                    }
                }
            };

            var json = JsonSerializer.Serialize(sampleConfig, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            File.WriteAllText(configFile, json);
        }

        private static void SetupWatchers()
        {
            Cleanup();
            _watcherMappings.Clear();

            var enabledMappings = _config.Mappings.Where(m => m.Enabled).ToList();
            
            foreach (var mapping in enabledMappings)
            {
                try
                {
                    var sourceInfo = new FileInfo(mapping.Source);
                    
                    if (!sourceInfo.Exists)
                    {
                        Console.WriteLine($"Warning: Source file not found: {mapping.Source}");
                        continue;
                    }

                    var watchDir = sourceInfo.DirectoryName;
                    
                    if (!_watcherMappings.ContainsKey(watchDir))
                        _watcherMappings[watchDir] = new List<FileMapping>();
                    
                    _watcherMappings[watchDir].Add(mapping);

                    if (!_watchers.ContainsKey(watchDir))
                    {
                        var watcher = CreateWatcher(watchDir);
                        _watchers.TryAdd(watchDir, watcher);
                        Console.WriteLine($"Created watcher for: {watchDir}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error setting up watcher for {mapping.Source}: {ex.Message}");
                }
            }
        }

        private static FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            
            watcher.Changed += OnChanged;
            watcher.Error += OnError;
            
            return watcher;
        }

        private static void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
                return;

            var watcher = (FileSystemWatcher)sender;
            var watchDir = watcher.Path;

            if (!_watcherMappings.TryGetValue(watchDir, out var mappings))
                return;

            var relevantMapping = mappings.FirstOrDefault(m => 
                string.Equals(m.Source, e.FullPath, StringComparison.OrdinalIgnoreCase));

            if (relevantMapping == null)
                return;

            ProcessFileChange(relevantMapping, e.FullPath);
        }

        private static void ProcessFileChange(FileMapping mapping, string changedFile)
        {
            var cts = new CancellationTokenSource();
            var destination = mapping.Destination;

            // Cancel any pending write for this destination
            if (_pendingWrites.TryGetValue(destination, out var existingCts))
            {
                existingCts.Cancel();
            }
            _pendingWrites.AddOrUpdate(destination, cts, (key, oldValue) => cts);

            // Debounced copy operation
            Task.Delay(_config.Settings.DebounceMs, cts.Token)
                .ContinueWith(async x =>
                {
                    if (cts.Token.IsCancellationRequested)
                        return;

                    try
                    {
                        await CopyFileWithRetry(mapping.Source, destination);
                        
                        Console.WriteLine($"\n=== [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===");
                        Console.WriteLine($"✓ Copied: {Path.GetFileName(mapping.Source)}");
                        if (!string.IsNullOrEmpty(mapping.Description))
                            Console.WriteLine($"  Description: {mapping.Description}");
                        Console.WriteLine($"  From: {mapping.Source}");
                        Console.WriteLine($"  To:   {destination}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n✗ Error copying {mapping.Source}: {ex.Message}");
                    }
                    finally
                    {
                        _pendingWrites.TryRemove(destination, out _);
                    }
                }, cts.Token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }

        private static async Task CopyFileWithRetry(string source, string destination, int maxRetries = 3)
        {
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Create backup if enabled
                    if (_config.Settings.CreateBackups && File.Exists(destination))
                    {
                        var backupPath = $"{destination}.backup.{DateTime.Now:yyyyMMdd-HHmmss}";
                        File.Copy(destination, backupPath, true);
                    }

                    File.Copy(source, destination, true);
                    return;
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    await Task.Delay(100 * attempt); // Progressive delay
                }
            }
        }

        private static void ReloadConfiguration()
        {
            try
            {
                Console.WriteLine("\nReloading configuration...");
                LoadConfiguration();
                SetupWatchers();
                Console.WriteLine("Configuration reloaded successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to reload configuration: {ex.Message}");
            }
        }

        private static void ShowStatus()
        {
            Console.WriteLine($"\n=== Status at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Console.WriteLine($"Active watchers: {_watchers.Count}");
            Console.WriteLine($"Enabled mappings: {_config.Mappings.Count(m => m.Enabled)}");
            Console.WriteLine($"Pending operations: {_pendingWrites.Count}");
            
            if (_pendingWrites.Any())
            {
                Console.WriteLine("Pending writes:");
                foreach (var dest in _pendingWrites.Keys)
                {
                    Console.WriteLine($"  - {dest}");
                }
            }
        }

        private static void OnError(object sender, ErrorEventArgs e)
        {
            Console.WriteLine($"FileSystemWatcher error: {e.GetException().Message}");
        }

        private static void Cleanup()
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher?.Dispose();
            }
            _watchers.Clear();

            foreach (var cts in _pendingWrites.Values)
            {
                cts?.Cancel();
            }
            _pendingWrites.Clear();
        }
    }
}
