using System.Text.Json;

namespace FileWatcher.Tests;

public sealed class FileWatcherAppTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"fwtest_{Guid.NewGuid():N}"
    );
    private readonly StringWriter _out = new();

    public FileWatcherAppTests()
    {
        Directory.CreateDirectory(_tempDir);
        Console.SetOut(_out);
    }

    public void Dispose()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private FileWatcherApp CreateApp(string cfg, FakeProcessRunner? r = null) =>
        new(cfg, r ?? new FakeProcessRunner());

    private string WriteCfg(WatchConfig c)
    {
        var p = Path.Combine(_tempDir, "cfg.json");
        File.WriteAllText(p, JsonSerializer.Serialize(c));
        return p;
    }

    private string WriteFile(string n, string c = "test")
    {
        var p = Path.Combine(_tempDir, n);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, c);
        return p;
    }

    [Fact]
    public async Task LoadConfiguration_ValidFile_PopulatesConfig()
    {
        var app = CreateApp(WriteCfg(new() { Settings = new() { DebounceMs = 500 } }));
        await app.LoadConfigurationAsync(default);
        Assert.Equal(500, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task LoadConfiguration_MissingFile_ThrowsFileNotFound()
    {
        var app = CreateApp(Path.Combine(_tempDir, "none.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => app.LoadConfigurationAsync(default));
    }

    [Fact]
    public async Task SetupWatchers_EnabledEntry_CreatesWatcher()
    {
        var src = WriteFile("a.txt");
        var app = CreateApp(
            WriteCfg(
                new() { Hooks = new() { OnUpdate = [new() { Source = src, Enabled = true }] } }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        Assert.Single(app._directoryWatchers);
    }

    [Fact]
    public async Task SetupWatchers_TwoFilesSameDir_OneWatcher()
    {
        var s1 = WriteFile("1.txt");
        var s2 = WriteFile("2.txt");
        var app = CreateApp(
            WriteCfg(
                new()
                {
                    Hooks = new()
                    {
                        OnUpdate =
                        [
                            new() { Source = s1, Enabled = true },
                            new() { Source = s2, Enabled = true },
                        ],
                    },
                }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        Assert.Single(app._directoryWatchers);
    }

    [Fact]
    public async Task HandleFileEvent_ValidFile_SchedulesActions()
    {
        var src = WriteFile("s.txt");
        var app = CreateApp(
            WriteCfg(
                new() { Hooks = new() { OnUpdate = [new() { Source = src, Enabled = true }] } }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, _tempDir, "s.txt"));
        Assert.Single(app._pendingTokens);
    }

    [Fact]
    public async Task HandleFileEvent_SpuriousEvent_SameState_DoesNotScheduleActions()
    {
        var src = WriteFile("spurious.txt", "same content");
        var app = CreateApp(
            WriteCfg(
                new() { Hooks = new() { OnUpdate = [new() { Source = src, Enabled = true }] } }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();

        // First event: processes and schedules actions
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, _tempDir, "spurious.txt"));
        Assert.Single(app._pendingTokens);

        // Clear the pending tokens to act like the action finished
        app._pendingTokens.Clear();

        // Second event: same exact file size and write time. Should be ignored.
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, _tempDir, "spurious.txt"));
        Assert.Empty(app._pendingTokens);
    }

    [Fact]
    public async Task HandleFileEvent_SpuriousEvent_FileChanged_SchedulesActions()
    {
        var src = WriteFile("modified.txt", "content 1");
        var app = CreateApp(
            WriteCfg(
                new() { Hooks = new() { OnUpdate = [new() { Source = src, Enabled = true }] } }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();

        // First event
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, _tempDir, "modified.txt"));
        Assert.Single(app._pendingTokens);
        app._pendingTokens.Clear();

        // Modify the file to change its state (size/timestamp)
        File.WriteAllText(src, "new content");

        // Second event: file actually changed, should process
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, _tempDir, "modified.txt"));
        Assert.Single(app._pendingTokens);
    }

    [Fact]
    public async Task ScheduleActions_CopiesFile()
    {
        var src = WriteFile("s.txt", "content");
        var dst = Path.Combine(_tempDir, "d.txt");
        var app = CreateApp(WriteCfg(new() { Settings = new() { DebounceMs = 0 } }));
        await app.LoadConfigurationAsync(default);
        app.ScheduleActions(new() { Source = src, CopyTo = dst });
        await Task.Delay(200);
        Assert.Equal("content", File.ReadAllText(dst));
    }

    [Fact]
    public async Task RunHookAsync_InvokesRunner()
    {
        var r = new FakeProcessRunner();
        var app = CreateApp("cfg.json", r);
        await app.RunHookAsync("cmd", _tempDir, default);
        Assert.Single(r.Calls);
        Assert.Equal("cmd", r.Calls[0].Command);
    }

    [Fact]
    public async Task RunStartupHooksAsync_InvokesRunner()
    {
        var r = new FakeProcessRunner();
        var app = CreateApp("cfg.json", r);
        app.Config = new() { Hooks = new() { OnStartup = [new() { Command = "start" }] } };
        await app.RunStartupHooksAsync(default);
        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task ReloadConfiguration_UpdatesSettings()
    {
        var p = WriteCfg(new() { Settings = new() { DebounceMs = 1 } });
        var app = CreateApp(p);
        await app.LoadConfigurationAsync(default);
        File.WriteAllText(
            p,
            JsonSerializer.Serialize(new WatchConfig { Settings = new() { DebounceMs = 9 } })
        );
        await app.ReloadConfigurationAsync(default);
        Assert.Equal(9, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public void Dispose_ClearsResources()
    {
        var app = CreateApp("cfg.json");
        app._directoryWatchers.TryAdd("dir", new FileSystemWatcher(_tempDir));
        app.Dispose();
        Assert.Empty(app._directoryWatchers);
    }
}
