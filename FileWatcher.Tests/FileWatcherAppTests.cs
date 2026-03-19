using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

[Collection("Log tests")]
public sealed class FileWatcherAppTests : IDisposable
{
    private readonly FakeFileSystem _fs = new();
    private readonly StringWriter _out = new();
    private readonly TextWriter _originalOut;

    public FileWatcherAppTests()
    {
        LogService.Clear();
        _originalOut = Console.Out;
        Console.SetOut(_out);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _out.Dispose();
    }

    private readonly FakeLogWebServer _webServer = new();
    private readonly FakeConsole _console = new();

    private FileWatcherApp CreateApp(string cfgPath, FakeProcessRunner? r = null) =>
        new(cfgPath, r ?? new FakeProcessRunner(), _fs, _webServer, _console);

    private string WriteCfg(WatchConfig c)
    {
        var p = "/cfg.json";
        _fs.AddFile(p, JsonSerializer.Serialize(c), DateTime.UtcNow);
        return p;
    }

    private string WriteFile(string n, string c = "test")
    {
        var p = "/" + n;
        _fs.AddFile(p, c, DateTime.UtcNow);
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
        var app = CreateApp("/none.json");
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
        var s1 = WriteFile("dir/1.txt");
        var s2 = WriteFile("dir/2.txt");
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
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "s.txt"));
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

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "spurious.txt"));
        Assert.Single(app._pendingTokens);

        app._pendingTokens.Clear();

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "spurious.txt"));
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

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "modified.txt"));
        Assert.Single(app._pendingTokens);
        app._pendingTokens.Clear();

        _fs.AddFile(src, "new content", DateTime.UtcNow.AddMinutes(1)); // Change timestamp & size

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "modified.txt"));
        Assert.Single(app._pendingTokens);
    }

    [Fact]
    public async Task ScheduleActions_CopiesFile()
    {
        var src = WriteFile("s.txt", "content");
        var dst = "/d.txt";
        var app = CreateApp(WriteCfg(new() { Settings = new() { DebounceMs = 0 } }));
        await app.LoadConfigurationAsync(default);
        app.ScheduleActions(new() { Source = src, CopyTo = dst });

        // The pending token is removed in the finally block of the Task.Run inside ScheduleActions.
        // Polling for its removal is more reliable than an arbitrary fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (app._pendingTokens.Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(_fs.FileExists(dst));
        Assert.Equal("content", _fs.Files[dst]);
    }

    [Fact]
    public async Task RunHookAsync_InvokesRunner()
    {
        var r = new FakeProcessRunner();
        var app = CreateApp("/cfg.json", r);
        await app.RunHookAsync("cmd", "/tmp", LogLevel.Info, default);
        Assert.Single(r.Calls);
        Assert.Equal("cmd", r.Calls[0].Command);
    }

    [Fact]
    public async Task RunStartupHooksAsync_InvokesRunner()
    {
        var r = new FakeProcessRunner();
        var app = CreateApp("/cfg.json", r);
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

        _fs.AddFile(
            p,
            JsonSerializer.Serialize(new WatchConfig { Settings = new() { DebounceMs = 9 } }),
            DateTime.UtcNow
        );

        await app.ReloadConfigurationAsync(default);
        Assert.Equal(9, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task RunAsync_StartsWebServerAndRunsHooks()
    {
        var cfg = new WatchConfig { Settings = new() { DashboardPort = 1234 } };
        var app = CreateApp(WriteCfg(cfg));
        _console.EnqueueKey('q', ConsoleKey.Q);

        try
        {
            await app.RunAsync(default);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(1, _webServer.StartCount);
        Assert.Equal(1234, _webServer.LastPort);
    }

    [Fact]
    public async Task RunConsoleLoopAsync_R_ReloadsConfiguration()
    {
        var p = WriteCfg(new() { Settings = new() { DebounceMs = 1 } });
        var app = CreateApp(p);
        await app.LoadConfigurationAsync(default);

        _fs.AddFile(
            p,
            JsonSerializer.Serialize(new WatchConfig { Settings = new() { DebounceMs = 9 } }),
            DateTime.UtcNow
        );

        _console.EnqueueKey('r', ConsoleKey.R);
        _console.EnqueueKey('q', ConsoleKey.Q);

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(default));
        Assert.Equal(9, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task ReloadConfiguration_Fails_LogsError()
    {
        var p = WriteCfg(new() { Settings = new() { DebounceMs = 1 } });
        var app = CreateApp(p);
        await app.LoadConfigurationAsync(default);

        _fs.AddFile(p, "invalid json", DateTime.UtcNow);

        await app.ReloadConfigurationAsync(default);
        var logs = LogService.GetRecentLogs().ToList();
        Assert.Contains(
            logs,
            l => l.Level == LogLevel.Error && l.Message.Contains("Failed to reload")
        );
    }

    [Fact]
    public async Task SetupWatchers_MissingSource_SkipsAndLogsWarning()
    {
        var app = CreateApp(
            WriteCfg(
                new()
                {
                    Hooks = new() { OnUpdate = [new() { Description = "test", Enabled = true }] },
                }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        var logs = LogService.GetRecentLogs().ToList();
        Assert.Contains(
            logs,
            l =>
                l.Level == LogLevel.Warning
                && l.Message.Contains("Entry skipped: source is missing (test).")
        );
    }

    [Fact]
    public async Task SetupWatchers_SourceNotFound_LogsWarning()
    {
        var app = CreateApp(
            WriteCfg(
                new()
                {
                    Hooks = new()
                    {
                        OnUpdate = [new() { Source = "/missing.txt", Enabled = true }],
                    },
                }
            )
        );
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        var logs = LogService.GetRecentLogs().ToList();
        Assert.Contains(
            logs,
            l =>
                l.Level == LogLevel.Warning
                && l.Message.Contains("Source file not found: /missing.txt")
        );
    }

    [Fact]
    public void HandleFileEvent_UnwatchedDirectory_ReturnsEarly()
    {
        // No watchers set up → no entry for the file's directory → should silently return.
        var app = CreateApp(WriteCfg(new()));
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/unwatched", "file.txt"));
        Assert.Empty(app._pendingTokens);
    }

    [Fact]
    public async Task RunHookAsync_Exception_LogsError()
    {
        var r = new FakeProcessRunner();
        r.ShouldThrow = true;
        var app = CreateApp("/cfg.json", r);
        await app.RunHookAsync("cmd", "/tmp", LogLevel.Info, default);
        var logs = LogService.GetRecentLogs().ToList();
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("Hook failed"));
    }

    [Fact]
    public void Dispose_ClearsResources()
    {
        var app = CreateApp("/cfg.json");
        app._directoryWatchers.TryAdd(
            "dir",
            new FakeFileSystemWatcher("dir", NotifyFilters.LastWrite)
        );
        app.Dispose();
        Assert.Empty(app._directoryWatchers);
    }
}
