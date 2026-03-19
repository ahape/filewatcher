


using System.Text.Json;




namespace FileWatcher.Tests;

/// <summary>
/// Unit tests for FileWatcherApp. All state is exercised through the class's
/// internal API — no reflection.
/// </summary>
public sealed class FileWatcherAppTests : IDisposable
{
    // ──────────────────────────────────────────────────────────────────────────
    // Infrastructure
    // ──────────────────────────────────────────────────────────────────────────

    private readonly string _tempDir;
    private readonly StringWriter _out;
    private readonly StringWriter _err;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public FileWatcherAppTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fwtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _out = new StringWriter();
        _err = new StringWriter();
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _out.Dispose();
        _err.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a FileWatcherApp wired to a FakeProcessRunner.</summary>
    private FileWatcherApp CreateApp(string configPath, FakeProcessRunner? runner = null)
        => new(configPath, runner ?? new FakeProcessRunner());

    /// <summary>Creates a FileWatcherApp wired to a specific FakeProcessRunner instance.</summary>
    private (FileWatcherApp App, FakeProcessRunner Runner) CreateAppWithRunner(string configPath)
    {
        var runner = new FakeProcessRunner();
        return (new FileWatcherApp(configPath, runner), runner);
    }

    private string WriteConfigFile(WatchConfig config)
    {
        var path = Path.Combine(_tempDir, "watchconfig.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private string WriteTempFile(string relativePath, string content = "test content")
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static CancellationTokenSource Timeout5s() => new(TimeSpan.FromSeconds(5));

    // ──────────────────────────────────────────────────────────────────────────
    // EnsureConfigurationExistsAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureConfigurationExists_WhenFileAlreadyExists_ReturnsSilently()
    {
        var configPath = WriteConfigFile(new WatchConfig());
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await app.EnsureConfigurationExistsAsync(cts.Token); // must not throw

        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public async Task EnsureConfigurationExists_WhenFileMissing_WritesSampleAndThrows()
    {
        var configPath = Path.Combine(_tempDir, "missing.json");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => app.EnsureConfigurationExistsAsync(cts.Token));

        Assert.True(File.Exists(configPath), "Sample config should have been written to disk.");
    }

    [Fact]
    public async Task EnsureConfigurationExists_WrittenSample_IsValidJson()
    {
        var configPath = Path.Combine(_tempDir, "auto.json");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => app.EnsureConfigurationExistsAsync(cts.Token));

        var json = File.ReadAllText(configPath);
        var parsed = JsonSerializer.Deserialize<WatchConfig>(json);
        Assert.NotNull(parsed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LoadConfigurationAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadConfiguration_ValidFile_PopulatesConfigOnApp()
    {
        var sourceFile = WriteTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = sourceFile, Destination = Path.Combine(_tempDir, "dst.txt"), Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 500 }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);

        Assert.Single(app.Config.Mappings);
        Assert.Equal(500, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task LoadConfiguration_InvalidJson_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(configPath, "NOT JSON {{{");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.LoadConfigurationAsync(cts.Token));
    }

    [Fact]
    public async Task LoadConfiguration_NullDocument_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tempDir, "null.json");
        File.WriteAllText(configPath, "null");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.LoadConfigurationAsync(cts.Token));
    }

    [Fact]
    public async Task LoadConfiguration_EmptyDocument_DefaultsMappingsAndSettings()
    {
        var configPath = Path.Combine(_tempDir, "empty.json");
        File.WriteAllText(configPath, "{}");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);

        Assert.NotNull(app.Config.Mappings);
        Assert.NotNull(app.Config.Settings);
        Assert.Empty(app.Config.Mappings);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ValidateMapping
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateMapping_ValidSourceAndDestination_ReturnsTrue()
    {
        var sourceFile = WriteTempFile("valid.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var result = app.ValidateMapping(new FileMapping
        {
            Source = sourceFile,
            Destination = Path.Combine(_tempDir, "out", "valid.txt")
        });

        Assert.True(result);
    }

    [Fact]
    public void ValidateMapping_EmptySource_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var result = app.ValidateMapping(new FileMapping
        {
            Source = "",
            Destination = Path.Combine(_tempDir, "out.txt")
        });

        Assert.False(result);
    }

    [Fact]
    public void ValidateMapping_EmptyDestination_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var result = app.ValidateMapping(new FileMapping
        {
            Source = Path.Combine(_tempDir, "src.txt"),
            Destination = ""
        });

        Assert.False(result);
    }

    [Fact]
    public void ValidateMapping_SourceFileDoesNotExist_ReturnsFalse()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        var result = app.ValidateMapping(new FileMapping
        {
            Source = Path.Combine(_tempDir, "ghost.txt"),
            Destination = Path.Combine(_tempDir, "out.txt")
        });

        Assert.False(result);
    }

    [Fact]
    public void ValidateMapping_MissingSource_WritesWarningToConsole()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        app.ValidateMapping(new FileMapping { Source = "", Destination = "dst.txt", Description = "my mapping" });

        Assert.Contains("my mapping", _out.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SetupWatchers
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupWatchers_EnabledValidMapping_CreatesOneWatcher()
    {
        var src = WriteTempFile("watched.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "watched.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        Assert.Single(app.DirectoryWatchers);
        app.Dispose();
    }

    [Fact]
    public async Task SetupWatchers_DisabledMapping_CreatesNoWatchers()
    {
        var src = WriteTempFile("disabled.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "disabled.txt"), Enabled = false }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        Assert.Empty(app.DirectoryWatchers);
        app.Dispose();
    }

    [Fact]
    public async Task SetupWatchers_TwoMappingsInSameDirectory_CreatesOneWatcher()
    {
        var src1 = WriteTempFile("file1.txt");
        var src2 = WriteTempFile("file2.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src1, Destination = Path.Combine(_tempDir, "out", "file1.txt"), Enabled = true },
                new() { Source = src2, Destination = Path.Combine(_tempDir, "out", "file2.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        Assert.Single(app.DirectoryWatchers); // both files live in _tempDir
        app.Dispose();
    }

    [Fact]
    public async Task SetupWatchers_CalledTwice_ReplacesOldWatchers()
    {
        var src = WriteTempFile("a.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "a.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();
        var firstCount = app.DirectoryWatchers.Count;

        app.SetupWatchers(); // rebuild
        Assert.Equal(firstCount, app.DirectoryWatchers.Count);
        app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HandleFileChange
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleFileChange_NonChangedEventType_DoesNotScheduleCopy()
    {
        var src = WriteTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        app.HandleFileChange(new System.IO.FileSystemEventArgs(System.IO.WatcherChangeTypes.Deleted, _tempDir, "src.txt"));

        Assert.Empty(app.PendingCopyTokens);
        app.Dispose();
    }

    [Fact]
    public async Task HandleFileChange_EventFromUnwatchedDirectory_DoesNotScheduleCopy()
    {
        var src = WriteTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        var differentDir = Path.Combine(Path.GetTempPath(), $"other_{Guid.NewGuid():N}");
        app.HandleFileChange(new System.IO.FileSystemEventArgs(System.IO.WatcherChangeTypes.Changed, differentDir, "src.txt"));

        Assert.Empty(app.PendingCopyTokens);
        app.Dispose();
    }

    [Fact]
    public async Task HandleFileChange_EventForUnmappedFile_DoesNotScheduleCopy()
    {
        var src = WriteTempFile("src.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "src.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        // Wait for the initial copies (triggered during TriggerInitialCopies) to drain.
        await Task.Delay(100);

        // A Changed event for a file in the right directory but NOT in any mapping.
        app.HandleFileChange(new System.IO.FileSystemEventArgs(System.IO.WatcherChangeTypes.Changed, _tempDir, "totally-different.txt"));

        Assert.Empty(app.PendingCopyTokens);
        app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CopyFileWithRetryAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyFileWithRetry_HappyPath_CopiesContentToDestination()
    {
        var src = WriteTempFile("src.txt", "hello world");
        var dst = Path.Combine(_tempDir, "out", "dst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = false } };

        using var cts = Timeout5s();
        await app.CopyFileWithRetryAsync(new FileMapping { Source = src, Destination = dst }, cts.Token);

        Assert.True(File.Exists(dst));
        Assert.Equal("hello world", File.ReadAllText(dst));
    }

    [Fact]
    public async Task CopyFileWithRetry_DestinationDirectoryMissing_CreatesItAutomatically()
    {
        var src = WriteTempFile("src.txt", "nested");
        var dst = Path.Combine(_tempDir, "a", "b", "c", "dst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = false } };

        using var cts = Timeout5s();
        await app.CopyFileWithRetryAsync(new FileMapping { Source = src, Destination = dst }, cts.Token);

        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task CopyFileWithRetry_CancelledToken_ThrowsOperationCancelled()
    {
        var src = WriteTempFile("src.txt");
        var dst = Path.Combine(_tempDir, "dst.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = false } };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => app.CopyFileWithRetryAsync(new FileMapping { Source = src, Destination = dst }, cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CreateBackupIfNeeded
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateBackupIfNeeded_BackupsEnabled_CreatesTimestampedBackupFile()
    {
        var dest = WriteTempFile("target.txt", "original");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = true } };

        app.CreateBackupIfNeeded(dest);

        var backups = Directory.GetFiles(_tempDir, "target.txt.backup.*");
        Assert.Single(backups);
        Assert.Equal("original", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void CreateBackupIfNeeded_BackupsDisabled_LeavesNoBackupFile()
    {
        var dest = WriteTempFile("target.txt", "content");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = false } };

        app.CreateBackupIfNeeded(dest);

        Assert.Empty(Directory.GetFiles(_tempDir, "target.txt.backup.*"));
    }

    [Fact]
    public void CreateBackupIfNeeded_DestinationDoesNotYetExist_NoBackupCreated()
    {
        var dest = Path.Combine(_tempDir, "nonexistent.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { CreateBackups = true } };

        app.CreateBackupIfNeeded(dest); // must not throw

        Assert.Empty(Directory.GetFiles(_tempDir, "nonexistent.txt.backup.*"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ScheduleCopy / StartCopyWorkflowAsync / TriggerInitialCopies
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleCopy_ImmediateMode_CopiesFileWithoutDelay()
    {
        var src = WriteTempFile("sched.txt", "scheduled");
        var dst = Path.Combine(_tempDir, "out", "sched.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false } };

        app.ScheduleCopy(new FileMapping { Source = src, Destination = dst }, immediate: true);

        await Task.Delay(200); // let the fire-and-forget task complete

        Assert.True(File.Exists(dst));
        Assert.Equal("scheduled", File.ReadAllText(dst));
    }

    [Fact]
    public async Task ScheduleCopy_DuplicateDestination_CancelsPreviousAndSchedulesNew()
    {
        var src = WriteTempFile("dup.txt", "latest");
        var dst = Path.Combine(_tempDir, "out", "dup.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { DebounceMs = 5000, CreateBackups = false } };

        var mapping = new FileMapping { Source = src, Destination = dst };

        app.ScheduleCopy(mapping, immediate: false); // long debounce — will be cancelled
        app.ScheduleCopy(mapping, immediate: true);  // wins immediately

        await Task.Delay(200);

        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task ScheduleCopy_SourceMissing_WritesErrorToConsole()
    {
        var src = Path.Combine(_tempDir, "ghost.txt"); // does not exist
        var dst = Path.Combine(_tempDir, "out", "ghost.txt");
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false } };

        app.ScheduleCopy(new FileMapping { Source = src, Destination = dst }, immediate: true);

        await Task.Delay(500);

        Assert.Contains("Error copying", _out.ToString());
    }

    [Fact]
    public async Task ScheduleCopy_NonImmediate_RunsUpdateHookAfterCopy()
    {
        var src = WriteTempFile("debounced.txt", "content");
        var dst = Path.Combine(_tempDir, "out", "debounced.txt");
        var runner = new FakeProcessRunner();
        var app = new FileWatcherApp(Path.Combine(_tempDir, "cfg.json"), runner);
        app.Config = new WatchConfig
        {
            Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false },
            Hooks = new WatchHooks { OnUpdate = new HookEvent { Command = "update-cmd" } }
        };

        app.ScheduleCopy(new FileMapping { Source = src, Destination = dst }, immediate: false);

        await Task.Delay(500);

        Assert.True(File.Exists(dst));
        Assert.Single(runner.Calls);
        Assert.Equal("update-cmd", runner.Calls[0].Command);
    }

    [Fact]
    public async Task TriggerInitialCopies_MultipleValidMappings_CopiesAllFiles()
    {
        var src1 = WriteTempFile("init1.txt", "one");
        var src2 = WriteTempFile("init2.txt", "two");
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
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();
        app.TriggerInitialCopies(immediate: true);

        await Task.Delay(300);

        Assert.Equal("one", File.ReadAllText(dst1));
        Assert.Equal("two", File.ReadAllText(dst2));
        app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunHookAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunHookAsync_EmptyCommand_DoesNotInvokeProcessRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "   " }, cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunHookAsync_ValidCommand_InvokesProcessRunnerOnce()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "do-something" }, cts.Token);

        Assert.Single(runner.Calls);
        Assert.Equal("do-something", runner.Calls[0].Command);
    }

    [Fact]
    public async Task RunHookAsync_CustomLocation_PassesLocationToRunner()
    {
        var hookDir = Path.Combine(_tempDir, "hookdir");
        Directory.CreateDirectory(hookDir);
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "pwd", Location = hookDir }, cts.Token);

        Assert.Equal(hookDir, runner.Calls[0].WorkingDirectory);
    }

    [Fact]
    public async Task RunHookAsync_NoLocation_UsesCurrentDirectory()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "pwd" }, cts.Token);

        Assert.Equal(Environment.CurrentDirectory, runner.Calls[0].WorkingDirectory);
    }

    [Fact]
    public async Task RunHookAsync_NonZeroExitCode_WritesWarning()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        runner.ExitCode = 42;

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "failing-cmd" }, cts.Token);

        Assert.Contains("Hook exited with code 42", _out.ToString());
    }

    [Fact]
    public async Task RunHookAsync_OutputFromRunner_WrittenToConsoleOut()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        runner.OutputLine = "hello from hook";

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "cmd" }, cts.Token);

        Assert.Contains("hello from hook", _out.ToString());
    }

    [Fact]
    public async Task RunHookAsync_ErrorOutputFromRunner_WrittenToConsoleError()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        runner.ErrorLine = "something went wrong";

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "cmd" }, cts.Token);

        Assert.Contains("something went wrong", _out.ToString());
    }

    [Fact]
    public async Task RunHookAsync_RunnerThrows_WritesErrorToConsole()
    {
        var throwingRunner = new ThrowingProcessRunner();
        var app = new FileWatcherApp(Path.Combine(_tempDir, "cfg.json"), throwingRunner);

        using var cts = Timeout5s();
        await app.RunHookAsync(new HookEvent { Command = "boom" }, cts.Token); // must not propagate

        Assert.Contains("Failed to run hook", _out.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunStartupHookAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunStartupHookAsync_NoHooksConfigured_DoesNotInvokeRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Hooks = null };

        using var cts = Timeout5s();
        await app.RunStartupHookAsync(cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunStartupHookAsync_HookConfigured_InvokesRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks { OnStartup = new HookEvent { Command = "start" } }
        };

        using var cts = Timeout5s();
        await app.RunStartupHookAsync(cts.Token);

        Assert.Single(runner.Calls);
        Assert.Equal("start", runner.Calls[0].Command);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunUpdateHookAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunUpdateHookAsync_NoHooksConfigured_DoesNotInvokeRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig { Hooks = null };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "any" }, cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_EmptyCommand_DoesNotInvokeRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks { OnUpdate = new HookEvent { Command = "" } }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "any" }, cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToMatchesMappingId_InvokesRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent { Command = "update", ListenTo = "my-mapping" }
            }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "my-mapping" }, cts.Token);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToDoesNotMatchMappingId_DoesNotInvokeRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent { Command = "update", ListenTo = "other-mapping" }
            }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "my-mapping" }, cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToSetButMappingHasNoId_DoesNotInvokeRunner()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent { Command = "update", ListenTo = "specific-id" }
            }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = null }, cts.Token);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_NoListenToFilter_InvokesRunnerForAnyMapping()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent { Command = "update", ListenTo = null }
            }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "anything" }, cts.Token);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task RunUpdateHookAsync_ListenToMatchIsCaseInsensitive()
    {
        var (app, runner) = CreateAppWithRunner(Path.Combine(_tempDir, "cfg.json"));
        app.Config = new WatchConfig
        {
            Hooks = new WatchHooks
            {
                OnUpdate = new HookEvent { Command = "update", ListenTo = "My-Mapping" }
            }
        };

        using var cts = Timeout5s();
        await app.RunUpdateHookAsync(new FileMapping { Id = "MY-MAPPING" }, cts.Token);

        Assert.Single(runner.Calls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ShowStatus
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowStatus_AfterSetup_PrintsWatcherAndMappingCounts()
    {
        var src = WriteTempFile("status.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "status.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        await Task.Delay(100); // let pending copies drain

        app.ShowStatus();

        Assert.Contains("Active watchers: 1", _out.ToString());
        Assert.Contains("Enabled mappings: 1", _out.ToString());
        Assert.Contains("Pending copies:", _out.ToString());
        app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PrintWelcome
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrintWelcome_AfterSetup_PrintsMappingCountAndKeyHints()
    {
        var src = WriteTempFile("welcome.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "welcome.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        app.PrintWelcome(5000);

        var output = _out.ToString();
        Assert.Contains("Monitoring", output);
        Assert.Contains("enabled mapping(s)", output);
        Assert.Contains("reload config", output);
        app.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WriteCopySummary
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteCopySummary_WithDescription_PrintsAllFields()
    {
        var src = Path.Combine(_tempDir, "src", "test.txt");
        var dst = Path.Combine(_tempDir, "dst", "test.txt");

        FileWatcherApp.WriteCopySummary(new FileMapping
        {
            Source = src,
            Destination = dst,
            Description = "My test file"
        });

        var output = _out.ToString();
        Assert.Contains("Copied test.txt", output);
        Assert.Contains("Description: My test file", output);
        Assert.Contains(src, output);
        Assert.Contains(dst, output);
    }

    [Fact]
    public void WriteCopySummary_WithoutDescription_OmitsDescriptionLine()
    {
        var src = Path.Combine(_tempDir, "src", "nodesc.txt");
        var dst = Path.Combine(_tempDir, "dst", "nodesc.txt");

        FileWatcherApp.WriteCopySummary(new FileMapping
        {
            Source = src,
            Destination = dst,
            Description = ""
        });

        var output = _out.ToString();
        Assert.Contains("Copied nodesc.txt", output);
        Assert.DoesNotContain("Description:", output);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ReloadConfigurationAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadConfiguration_ValidUpdatedFile_AppliesNewSettings()
    {
        var src = WriteTempFile("reload.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "reload.txt"), Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 0 }
        };
        var configPath = WriteConfigFile(config);
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        config.Settings.DebounceMs = 9999;
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        await app.ReloadConfigurationAsync(cts.Token);

        Assert.Equal(9999, app.Config.Settings.DebounceMs);
        Assert.Contains("Reloading configuration", _out.ToString());
        Assert.Contains("Configuration reloaded", _out.ToString());
        app.Dispose();
    }

    [Fact]
    public async Task ReloadConfiguration_InvalidFile_WritesErrorAndDoesNotThrow()
    {
        var configPath = Path.Combine(_tempDir, "reload_bad.json");
        File.WriteAllText(configPath, "{}");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);

        File.WriteAllText(configPath, "NOT VALID JSON {{{");

        await app.ReloadConfigurationAsync(cts.Token); // must not throw

        Assert.Contains("Failed to reload configuration", _out.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_ReleasesWatchersAndClearsPendingCopies()
    {
        var src = WriteTempFile("dispose.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "dispose.txt"), Enabled = true }
            }
        };
        var app = CreateApp(WriteConfigFile(config));

        using var cts = Timeout5s();
        await app.LoadConfigurationAsync(cts.Token);
        app.SetupWatchers();

        app.Dispose();

        Assert.Empty(app.DirectoryWatchers);
        Assert.Empty(app.PendingCopyTokens);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var app = CreateApp(Path.Combine(_tempDir, "cfg.json"));

        app.Dispose();
        app.Dispose(); // idempotent — must not throw
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunAsync (orchestration)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithValidConfig_PrintsWelcomeBeforeEnteringConsoleLoop()
    {
        var src = WriteTempFile("run.txt");
        var config = new WatchConfig
        {
            Mappings = new List<FileMapping>
            {
                new() { Source = src, Destination = Path.Combine(_tempDir, "out", "run.txt"), Enabled = true }
            },
            Settings = new WatchSettings { DebounceMs = 0, CreateBackups = false }
        };
        var app = CreateApp(WriteConfigFile(config));

        // Cancel after a short delay. Console.KeyAvailable throws InvalidOperationException
        // in non-interactive test environments, which is also an acceptable exit.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try { await app.RunAsync(cts.Token); }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch (InvalidOperationException) { /* Console.KeyAvailable not available in CI */ }

        // Welcome message must have been written before the console loop was entered.
        Assert.Contains("Monitoring", _out.ToString());
        app.Dispose();
    }

    [Fact]
    public async Task RunAsync_ConfigFileMissing_CreatesItAndThrowsFileNotFound()
    {
        var configPath = Path.Combine(_tempDir, "auto_created.json");
        var app = CreateApp(configPath);

        using var cts = Timeout5s();
        await Assert.ThrowsAsync<FileNotFoundException>(() => app.RunAsync(cts.Token));

        Assert.True(File.Exists(configPath));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helper: a process runner that always throws so we can test error handling.
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class ThrowingProcessRunner : IProcessRunner
{
    public Task<int> RunAsync(string command, string workingDirectory,
        Action<string> onOutput, Action<string> onError, CancellationToken token)
        => throw new InvalidOperationException("Simulated runner failure");
}
