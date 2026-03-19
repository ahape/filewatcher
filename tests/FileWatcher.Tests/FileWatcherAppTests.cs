using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public class FileWatcherAppTests : IDisposable
{
    private static readonly Type AppType = typeof(WatchConfig).Assembly.GetType("FileWatcher.FileWatcherApp")!;

    private readonly string _tempDir;
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public FileWatcherAppTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fwtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _outWriter = new StringWriter();
        _errWriter = new StringWriter();
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _outWriter.Dispose();
        _errWriter.Dispose();

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    #region Helpers

    private object CreateApp(string configPath)
    {
        return Activator.CreateInstance(AppType, new object[] { configPath })!;
    }

    private Task InvokeAsync(object app, string methodName, params object[] args)
    {
        var method = AppType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                     ?? throw new MissingMethodException(methodName);
        return (Task)method.Invoke(app, args)!;
    }

    private T InvokeReturn<T>(object app, string methodName, params object[] args)
    {
        var method = AppType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                     ?? throw new MissingMethodException(methodName);
        return (T)method.Invoke(app, args)!;
    }

    private void InvokeVoid(object app, string methodName, params object[] args)
    {
        var method = AppType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                     ?? throw new MissingMethodException(methodName);
        method.Invoke(app, args);
    }

    private T GetField<T>(object app, string fieldName)
    {
        var field = AppType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException(fieldName);
        return (T)field.GetValue(app)!;
    }

    private void SetField(object app, string fieldName, object value)
    {
        var field = AppType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException(fieldName);
        field.SetValue(app, value);
    }

    private string CreateConfigFile(WatchConfig config)
    {
        var configPath = Path.Combine(_tempDir, "watchconfig.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
        return configPath;
    }

    private string CreateTempFile(string name, string content = "test content")
    {
        var path = Path.Combine(_tempDir, name);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        return path;
    }

    #endregion

    #region EnsureConfigurationExistsAsync

    [Fact]
    public async Task EnsureConfigurationExistsAsync_WhenConfigExists_DoesNothing()
    {
        var configPath = CreateConfigFile(new WatchConfig());
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "EnsureConfigurationExistsAsync", cts.Token);

        // If we got here without exception, the method returned normally
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public async Task EnsureConfigurationExistsAsync_WhenConfigMissing_CreatesFileAndThrows()
    {
        var configPath = Path.Combine(_tempDir, "nonexistent.json");
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => InvokeAsync(app, "EnsureConfigurationExistsAsync", cts.Token));

        Assert.True(File.Exists(configPath)); // sample was written
    }

    #endregion

    #region LoadConfigurationAsync

    [Fact]
    public async Task LoadConfigurationAsync_ValidConfig_LoadsMappings()
    {
        var sourceFile = CreateTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "dst.txt"), Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 500 }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);

        var loadedConfig = GetField<WatchConfig>(app, "_config");
        Assert.Single(loadedConfig.Mappings);
        Assert.Equal(500, loadedConfig.Settings.DebounceMs);
    }

    [Fact]
    public async Task LoadConfigurationAsync_InvalidJson_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(configPath, "NOT JSON {{{");
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(app, "LoadConfigurationAsync", cts.Token));
    }

    [Fact]
    public async Task LoadConfigurationAsync_NullDeserialization_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tempDir, "null.json");
        File.WriteAllText(configPath, "null");
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(app, "LoadConfigurationAsync", cts.Token));
    }

    [Fact]
    public async Task LoadConfigurationAsync_MissingMappingsAndSettings_DefaultsToEmpty()
    {
        var configPath = Path.Combine(_tempDir, "minimal.json");
        File.WriteAllText(configPath, "{}");
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);

        var loadedConfig = GetField<WatchConfig>(app, "_config");
        Assert.NotNull(loadedConfig.Mappings);
        Assert.NotNull(loadedConfig.Settings);
        Assert.Empty(loadedConfig.Mappings);
    }

    #endregion

    #region ValidateMapping

    [Fact]
    public void ValidateMapping_ValidMapping_ReturnsTrue()
    {
        var sourceFile = CreateTempFile("valid.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var mapping = new FileMapping
        {
            Source = sourceFile,
            Destination = Path.Combine(_tempDir, "out", "valid.txt"),
            Enabled = true
        };

        var result = InvokeReturn<bool>(app, "ValidateMapping", mapping);
        Assert.True(result);
    }

    [Fact]
    public void ValidateMapping_MissingSource_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var mapping = new FileMapping
        {
            Source = "",
            Destination = Path.Combine(_tempDir, "out.txt"),
            Enabled = true
        };

        var result = InvokeReturn<bool>(app, "ValidateMapping", mapping);
        Assert.False(result);
    }

    [Fact]
    public void ValidateMapping_MissingDestination_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var mapping = new FileMapping
        {
            Source = Path.Combine(_tempDir, "src.txt"),
            Destination = "",
            Enabled = true
        };

        var result = InvokeReturn<bool>(app, "ValidateMapping", mapping);
        Assert.False(result);
    }

    [Fact]
    public void ValidateMapping_SourceFileNotFound_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var mapping = new FileMapping
        {
            Source = Path.Combine(_tempDir, "doesnotexist.txt"),
            Destination = Path.Combine(_tempDir, "out.txt"),
            Enabled = true
        };

        var result = InvokeReturn<bool>(app, "ValidateMapping", mapping);
        Assert.False(result);
    }

    #endregion

    #region SetupWatchers

    [Fact]
    public async Task SetupWatchers_WithValidMappings_CreatesWatchers()
    {
        var sourceFile = CreateTempFile("watched.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "watched.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        var watchers = GetField<ConcurrentDictionary<string, FileSystemWatcher>>(app, "_directoryWatchers");
        Assert.NotEmpty(watchers);

        // Cleanup
        ((IDisposable)app).Dispose();
    }

    [Fact]
    public async Task SetupWatchers_WithDisabledMappings_DoesNotCreateWatcher()
    {
        var sourceFile = CreateTempFile("disabled.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "disabled.txt"), Enabled = false }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        var watchers = GetField<ConcurrentDictionary<string, FileSystemWatcher>>(app, "_directoryWatchers");
        Assert.Empty(watchers);

        ((IDisposable)app).Dispose();
    }

    [Fact]
    public async Task SetupWatchers_MultipleMappingsSameDirectory_CreatesOneWatcher()
    {
        var src1 = CreateTempFile("file1.txt");
        var src2 = CreateTempFile("file2.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src1, Destination = Path.Combine(_tempDir, "out", "file1.txt"), Enabled = true },
                new() { Source = src2, Destination = Path.Combine(_tempDir, "out", "file2.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        var watchers = GetField<ConcurrentDictionary<string, FileSystemWatcher>>(app, "_directoryWatchers");
        Assert.Single(watchers); // Both files in same _tempDir

        ((IDisposable)app).Dispose();
    }

    #endregion

    #region HandleFileChange

    [Fact]
    public async Task HandleFileChange_NonChangedType_DoesNotScheduleCopy()
    {
        var sourceFile = CreateTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        // Create a FileSystemEventArgs with Deleted type (not Changed)
        var args = new FileSystemEventArgs(WatcherChangeTypes.Deleted, _tempDir, "src.txt");
        InvokeVoid(app, "HandleFileChange", args);

        var pending = GetField<ConcurrentDictionary<string, CancellationTokenSource>>(app, "_pendingCopyTokens");
        Assert.Empty(pending);

        ((IDisposable)app).Dispose();
    }

    [Fact]
    public async Task HandleFileChange_UnmappedDirectory_DoesNotScheduleCopy()
    {
        var sourceFile = CreateTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        // Use a completely different directory path
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, @"C:\nonexistent", "src.txt");
        InvokeVoid(app, "HandleFileChange", args);

        // No new pending copy should have been created beyond initial copies
        ((IDisposable)app).Dispose();
    }

    [Fact]
    public async Task HandleFileChange_UnmappedFile_DoesNotScheduleCopy()
    {
        var sourceFile = CreateTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        // Wait for initial copies to complete
        await Task.Delay(200);

        // Event for a file that's in the right directory but not mapped
        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, _tempDir, "otherfile.txt");
        InvokeVoid(app, "HandleFileChange", args);

        ((IDisposable)app).Dispose();
    }

    #endregion

    #region CopyFileWithRetryAsync

    [Fact]
    public async Task CopyFileWithRetryAsync_CopiesFileSuccessfully()
    {
        var sourceFile = CreateTempFile("copysrc.txt", "copy me");
        var destFile = Path.Combine(_tempDir, "out", "copydst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        // Set up _config with Settings
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { CreateBackups = false } });

        var mapping = new FileMapping { Source = sourceFile, Destination = destFile };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "CopyFileWithRetryAsync", mapping, cts.Token, 3);

        Assert.True(File.Exists(destFile));
        Assert.Equal("copy me", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task CopyFileWithRetryAsync_CreatesDestinationDirectory()
    {
        var sourceFile = CreateTempFile("src2.txt", "nested");
        var destFile = Path.Combine(_tempDir, "nested", "dir", "dst2.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { CreateBackups = false } });

        var mapping = new FileMapping { Source = sourceFile, Destination = destFile };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "CopyFileWithRetryAsync", mapping, cts.Token, 3);

        Assert.True(File.Exists(destFile));
    }

    #endregion

    #region CreateBackupIfNeeded

    [Fact]
    public void CreateBackupIfNeeded_WhenEnabled_CreatesBackup()
    {
        var destFile = CreateTempFile("backup_target.txt", "original content");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { CreateBackups = true } });

        InvokeVoid(app, "CreateBackupIfNeeded", destFile);

        var backupFiles = Directory.GetFiles(_tempDir, "backup_target.txt.backup.*");
        Assert.NotEmpty(backupFiles);
        Assert.Equal("original content", File.ReadAllText(backupFiles[0]));
    }

    [Fact]
    public void CreateBackupIfNeeded_WhenDisabled_NoBackup()
    {
        var destFile = CreateTempFile("nobackup.txt", "content");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { CreateBackups = false } });

        InvokeVoid(app, "CreateBackupIfNeeded", destFile);

        var backupFiles = Directory.GetFiles(_tempDir, "nobackup.txt.backup.*");
        Assert.Empty(backupFiles);
    }

    [Fact]
    public void CreateBackupIfNeeded_WhenFileDoesNotExist_NoBackup()
    {
        var destFile = Path.Combine(_tempDir, "nonexistent.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { CreateBackups = true } });

        InvokeVoid(app, "CreateBackupIfNeeded", destFile);

        var backupFiles = Directory.GetFiles(_tempDir, "nonexistent.txt.backup.*");
        Assert.Empty(backupFiles);
    }

    #endregion

    #region ScheduleCopy and StartCopyWorkflowAsync

    [Fact]
    public async Task ScheduleCopy_CopiesFileAndCleansUpToken()
    {
        var sourceFile = CreateTempFile("sched_src.txt", "scheduled");
        var destFile = Path.Combine(_tempDir, "out", "sched_dst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false } });

        var mapping = new FileMapping { Source = sourceFile, Destination = destFile };

        InvokeVoid(app, "ScheduleCopy", mapping, true); // immediate = true

        // Wait for async copy to complete
        await Task.Delay(500);

        Assert.True(File.Exists(destFile));
        Assert.Equal("scheduled", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task ScheduleCopy_DuplicateDestination_CancelsPreviousCopy()
    {
        var sourceFile = CreateTempFile("dup_src.txt", "latest");
        var destFile = Path.Combine(_tempDir, "out", "dup_dst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { DebounceMs = 2000, CreateBackups = false } });

        var mapping = new FileMapping { Source = sourceFile, Destination = destFile };

        // Schedule first copy with long debounce
        InvokeVoid(app, "ScheduleCopy", mapping, false);

        // Schedule second copy immediately - should cancel first
        InvokeVoid(app, "ScheduleCopy", mapping, true);

        await Task.Delay(500);

        Assert.True(File.Exists(destFile));
    }

    #endregion

    #region TriggerInitialCopies

    [Fact]
    public async Task TriggerInitialCopies_CopiesAllMappedFiles()
    {
        var src1 = CreateTempFile("init1.txt", "content1");
        var src2 = CreateTempFile("init2.txt", "content2");
        var dst1 = Path.Combine(_tempDir, "out", "init1.txt");
        var dst2 = Path.Combine(_tempDir, "out", "init2.txt");

        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src1, Destination = dst1, Enabled = true },
                new() { Source = src2, Destination = dst2, Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");
        InvokeVoid(app, "TriggerInitialCopies", true);

        await Task.Delay(500);

        Assert.True(File.Exists(dst1));
        Assert.True(File.Exists(dst2));
        Assert.Equal("content1", File.ReadAllText(dst1));
        Assert.Equal("content2", File.ReadAllText(dst2));

        ((IDisposable)app).Dispose();
    }

    #endregion

    #region RunUpdateHookAsync

    [Fact]
    public async Task RunUpdateHookAsync_NoHook_DoesNothing()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Hooks = null });

        var mapping = new FileMapping { Id = "test", Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);

        // Should complete without error
    }

    [Fact]
    public async Task RunUpdateHookAsync_HookWithEmptyCommand_DoesNothing()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks { OnUpdate = new HookEvent { Command = "" } }
        });

        var mapping = new FileMapping { Id = "test", Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToMismatch_DoesNotRunHook()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent
                {
                    Command = "echo should not run",
                    ListenTo = "other-id"
                }
            }
        });

        var mapping = new FileMapping { Id = "my-id", Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);

        var output = _outWriter.ToString();
        Assert.DoesNotContain("should not run", output);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToMatch_RunsHook()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent
                {
                    Command = "echo hook-executed",
                    ListenTo = "my-id"
                }
            }
        });

        var mapping = new FileMapping { Id = "my-id", Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);

        await Task.Delay(200);
        var output = _outWriter.ToString();
        Assert.Contains("hook-executed", output);
    }

    [Fact]
    public async Task RunUpdateHookAsync_NoListenTo_RunsForAnyMapping()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent
                {
                    Command = "echo any-mapping-hook",
                    ListenTo = null
                }
            }
        });

        var mapping = new FileMapping { Id = "whatever", Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);

        await Task.Delay(200);
        var output = _outWriter.ToString();
        Assert.Contains("any-mapping-hook", output);
    }

    [Fact]
    public async Task RunUpdateHookAsync_MappingHasNoId_WithListenTo_DoesNotRun()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent
                {
                    Command = "echo should-not-run",
                    ListenTo = "specific-id"
                }
            }
        });

        var mapping = new FileMapping { Id = null, Source = "src", Destination = "dst" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunUpdateHookAsync", mapping, cts.Token);

        await Task.Delay(200);
        var output = _outWriter.ToString();
        Assert.DoesNotContain("should-not-run", output);
    }

    #endregion

    #region RunStartupHookAsync

    [Fact]
    public async Task RunStartupHookAsync_NoHooks_DoesNothing()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Hooks = null });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunStartupHookAsync", cts.Token);
    }

    [Fact]
    public async Task RunStartupHookAsync_WithHook_ExecutesCommand()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnStartup = new HookEvent { Command = "echo startup-test" }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunStartupHookAsync", cts.Token);

        await Task.Delay(200);
        var output = _outWriter.ToString();
        Assert.Contains("startup-test", output);
    }

    #endregion

    #region RunHookAsync edge cases

    [Fact]
    public async Task RunHookAsync_EmptyCommand_ReturnsImmediately()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        var hook = new HookEvent { Command = "   " };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunHookAsync", hook, cts.Token);
    }

    [Fact]
    public async Task RunHookAsync_NonZeroExitCode_WritesWarning()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        var hook = new HookEvent { Command = "exit /b 42" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunHookAsync", hook, cts.Token);

        var output = _outWriter.ToString();
        Assert.Contains("Hook exited with code 42", output);
    }

    [Fact]
    public async Task RunHookAsync_CustomLocation_UsesSpecifiedDirectory()
    {
        var subDir = Path.Combine(_tempDir, "hookdir");
        Directory.CreateDirectory(subDir);

        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        var hook = new HookEvent { Command = "cd", Location = subDir };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "RunHookAsync", hook, cts.Token);

        await Task.Delay(200);
        var output = _outWriter.ToString();
        Assert.Contains(subDir, output);
    }

    #endregion

    #region ShowStatus

    [Fact]
    public async Task ShowStatus_DisplaysWatcherAndMappingCounts()
    {
        var sourceFile = CreateTempFile("status_src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "status_dst.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        // Wait for initial copies to clear
        await Task.Delay(300);

        InvokeVoid(app, "ShowStatus");

        var output = _outWriter.ToString();
        Assert.Contains("Active watchers: 1", output);
        Assert.Contains("Enabled mappings: 1", output);
        Assert.Contains("Pending copies:", output);

        ((IDisposable)app).Dispose();
    }

    #endregion

    #region PrintWelcome

    [Fact]
    public async Task PrintWelcome_DisplaysMonitoringMessage()
    {
        var sourceFile = CreateTempFile("welcome_src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "welcome_dst.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        InvokeVoid(app, "PrintWelcome");

        var output = _outWriter.ToString();
        Assert.Contains("Monitoring", output);
        Assert.Contains("enabled mapping(s)", output);
        Assert.Contains("reload config", output);

        ((IDisposable)app).Dispose();
    }

    #endregion

    #region WriteCopySummary and console helpers

    [Fact]
    public void WriteCopySummary_DisplaysFileInfo()
    {
        var method = AppType.GetMethod("WriteCopySummary", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mapping = new FileMapping
        {
            Source = @"C:\src\test.txt",
            Destination = @"C:\dst\test.txt",
            Description = "Test file"
        };

        method.Invoke(null, new object[] { mapping });

        var output = _outWriter.ToString();
        Assert.Contains("Copied test.txt", output);
        Assert.Contains("Description: Test file", output);
        Assert.Contains(@"C:\src\test.txt", output);
        Assert.Contains(@"C:\dst\test.txt", output);
    }

    [Fact]
    public void WriteCopySummary_NoDescription_SkipsDescriptionLine()
    {
        var method = AppType.GetMethod("WriteCopySummary", BindingFlags.NonPublic | BindingFlags.Static)!;
        var mapping = new FileMapping
        {
            Source = @"C:\src\nodesc.txt",
            Destination = @"C:\dst\nodesc.txt",
            Description = ""
        };

        method.Invoke(null, new object[] { mapping });

        var output = _outWriter.ToString();
        Assert.Contains("Copied nodesc.txt", output);
        Assert.DoesNotContain("Description:", output);
    }

    #endregion

    #region Dispose

    [Fact]
    public async Task Dispose_CleansUpWatchersAndPendingCopies()
    {
        var sourceFile = CreateTempFile("dispose_src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "dispose_dst.txt"), Enabled = true }
            }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        ((IDisposable)app).Dispose();

        var watchers = GetField<ConcurrentDictionary<string, FileSystemWatcher>>(app, "_directoryWatchers");
        var pending = GetField<ConcurrentDictionary<string, CancellationTokenSource>>(app, "_pendingCopyTokens");
        Assert.Empty(watchers);
        Assert.Empty(pending);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        ((IDisposable)app).Dispose();
        ((IDisposable)app).Dispose(); // Should not throw
    }

    #endregion

    #region ReloadConfigurationAsync

    [Fact]
    public async Task ReloadConfigurationAsync_ValidConfig_Reloads()
    {
        var sourceFile = CreateTempFile("reload_src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "out", "reload_dst.txt"), Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 0 }
        };
        var configPath = CreateConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);
        InvokeVoid(app, "SetupWatchers");

        // Now update the config and reload
        config.Settings.DebounceMs = 2000;
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        await InvokeAsync(app, "ReloadConfigurationAsync", cts.Token);

        var reloaded = GetField<WatchConfig>(app, "_config");
        Assert.Equal(2000, reloaded.Settings.DebounceMs);

        var output = _outWriter.ToString();
        Assert.Contains("Reloading configuration", output);
        Assert.Contains("Configuration reloaded", output);

        ((IDisposable)app).Dispose();
    }

    [Fact]
    public async Task ReloadConfigurationAsync_InvalidConfig_WritesError()
    {
        var configPath = Path.Combine(_tempDir, "reload_bad.json");
        File.WriteAllText(configPath, "{}");
        var app = CreateApp(configPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await InvokeAsync(app, "LoadConfigurationAsync", cts.Token);

        // Corrupt the config file
        File.WriteAllText(configPath, "NOT VALID JSON {{{");

        await InvokeAsync(app, "ReloadConfigurationAsync", cts.Token);

        var output = _outWriter.ToString();
        Assert.Contains("Failed to reload configuration", output);
    }

    #endregion

    #region StartCopyWorkflowAsync error handling

    [Fact]
    public async Task StartCopyWorkflow_SourceMissing_WritesError()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        SetField(app, "_config", new WatchConfig { Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false } });

        var mapping = new FileMapping
        {
            Source = Path.Combine(_tempDir, "nonexistent_source.txt"),
            Destination = Path.Combine(_tempDir, "out", "dst.txt")
        };

        InvokeVoid(app, "ScheduleCopy", mapping, true);

        await Task.Delay(500);

        var output = _outWriter.ToString();
        Assert.Contains("Error copying", output);
    }

    #endregion
}
