using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

    private FileWatcherApp CreateApp(WatchConfig c, FakeProcessRunner? r = null)
    {
        var app = new FileWatcherApp(WriteCfg(c), r ?? new FakeProcessRunner(), _fs, _webServer, _console);
        return app;
    }

    private async Task<FileWatcherApp> CreateReadyApp(WatchConfig c, FakeProcessRunner? r = null)
    {
        var app = CreateApp(c, r);
        await app.LoadConfigurationAsync(default);
        app.SetupWatchers();
        return app;
    }

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
        var app = CreateApp(new() { Settings = new() { DebounceMs = 500 } });
        await app.LoadConfigurationAsync(default);
        Assert.Equal(500, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task LoadConfiguration_MissingFile_ThrowsFileNotFound()
    {
        var app = new FileWatcherApp("/none.json", new FakeProcessRunner(), _fs, _webServer, _console);
        await Assert.ThrowsAsync<FileNotFoundException>(() => app.LoadConfigurationAsync(default));
    }

    [Fact]
    public async Task SetupWatchers_EnabledEntry_CreatesWatcher()
    {
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Source = WriteFile("a.txt"), Enabled = true }] } });
        Assert.Single(app._directoryWatchers);
    }

    [Fact]
    public async Task SetupWatchers_TwoFilesSameDir_OneWatcher()
    {
        var app = await CreateReadyApp(new()
        {
            Hooks = new() { OnUpdate = [
                new() { Source = WriteFile("dir/1.txt"), Enabled = true },
                new() { Source = WriteFile("dir/2.txt"), Enabled = true }
            ] }
        });
        Assert.Single(app._directoryWatchers);
    }

    [Fact]
    public async Task HandleFileEvent_ValidFile_SchedulesActions()
    {
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Source = WriteFile("s.txt"), Enabled = true }] } });

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "s.txt"));

        Assert.Single(app._pendingTokens);
    }

    [Fact]
    public async Task HandleFileEvent_SpuriousEvent_SameState_DoesNotScheduleActions()
    {
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Source = WriteFile("spurious.txt", "content"), Enabled = true }] } });

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "spurious.txt"));
        app._pendingTokens.Clear();
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "spurious.txt"));

        Assert.Empty(app._pendingTokens);
    }

    [Fact]
    public async Task HandleFileEvent_SpuriousEvent_FileChanged_SchedulesActions()
    {
        var src = WriteFile("modified.txt", "content 1");
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Source = src, Enabled = true }] } });

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "modified.txt"));
        app._pendingTokens.Clear();

        _fs.AddFile(src, "new content", DateTime.UtcNow.AddMinutes(1)); // Change timestamp & size
        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/", "modified.txt"));

        Assert.Single(app._pendingTokens);
    }

    [Fact]
    public async Task ScheduleActions_CopiesFile()
    {
        var dst = "/d.txt";
        var app = await CreateReadyApp(new() { Settings = new() { DebounceMs = 0 } });

        app.ScheduleActions(new() { Source = WriteFile("s.txt", "content"), CopyTo = dst });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!app._pendingTokens.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal("content", _fs.Files[dst]);
    }

    [Fact]
    public async Task RunHookAsync_InvokesRunner()
    {
        var r = new FakeProcessRunner();
        var app = CreateApp(new WatchConfig(), r);

        await app.RunHookAsync("cmd", "/tmp", LogLevel.Info, "", default);

        Assert.Equal("cmd", r.Calls.Single().Command);
    }

    [Fact]
    public async Task RunStartupHooksAsync_RunsInParallel()
    {
        var r = new FakeProcessRunner { DelayMs = 100 };
        var app = await CreateReadyApp(new()
        {
            Hooks = new()
            {
                OnStartup = [new() { Command = "h1" }, new() { Command = "h2" }, new() { Command = "h3" }],
            },
        }, r);

        await app.RunStartupHooksAsync(default);

        Assert.Equal(3, r.Calls.Count);
        Assert.Equal(3, r.MaxConcurrentCalls);
    }

    [Fact]
    public async Task ReloadConfigurationAsync_CancelsOldHooks()
    {
        var r = new FakeProcessRunner { DelayMs = 1000 };
        var app = await CreateReadyApp(new() { Hooks = new() { OnStartup = [new() { Command = "long-hook" }] } }, r);

        // Start first hooks
        var task1 = app.RunStartupHooksAsync(default);

        // Wait a bit for it to start
        await Task.Delay(100);
        Assert.Equal(1, r.MaxConcurrentCalls);

        // Reload - this should cancel the old hook and start new ones
        _fs.AddFile(
            "/cfg.json",
            "{\"hooks\": {\"onStartup\": [{\"command\": \"new-hook\"}]}}",
            DateTime.UtcNow
        );
        await app.ReloadConfigurationAsync(default);

        Assert.Equal(2, r.Calls.Count);
        Assert.Equal("long-hook", r.Calls[0].Command);
        Assert.Equal("new-hook", r.Calls[1].Command);

        // task1 should be finished (cancelled)
        await task1;
    }

    [Fact]
    public async Task ReloadConfiguration_UpdatesSettings()
    {
        var app = await CreateReadyApp(new() { Settings = new() { DebounceMs = 1 } });

        _fs.AddFile("/cfg.json", JsonSerializer.Serialize(new WatchConfig { Settings = new() { DebounceMs = 9 } }), DateTime.UtcNow);
        await app.ReloadConfigurationAsync(default);

        Assert.Equal(9, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task RunAsync_StartsWebServerAndRunsHooks()
    {
        var app = CreateApp(new() { Settings = new() { DashboardPort = 1234 } });
        _console.EnqueueKey('q', ConsoleKey.Q);

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(default));

        Assert.Equal(1, _webServer.StartCount);
        Assert.Equal(1234, _webServer.LastPort);
    }

    [Fact]
    public async Task RunAsync_WithoutWebServer_PrintsDisabledMessage()
    {
        var app = new FileWatcherApp(WriteCfg(new()), new FakeProcessRunner(), _fs, console: _console);
        _console.EnqueueKey('q', ConsoleKey.Q);

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(default));

        Assert.Contains("Web dashboard disabled", _out.ToString());
    }

    [Fact]
    public async Task RunConsoleLoopAsync_R_ReloadsConfiguration()
    {
        var app = await CreateReadyApp(new() { Settings = new() { DebounceMs = 1 } });

        _fs.AddFile("/cfg.json", JsonSerializer.Serialize(new WatchConfig { Settings = new() { DebounceMs = 9 } }), DateTime.UtcNow);
        _console.EnqueueKey('r', ConsoleKey.R);
        _console.EnqueueKey('q', ConsoleKey.Q);

        await Assert.ThrowsAsync<OperationCanceledException>(() => app.RunAsync(default));

        Assert.Equal(9, app.Config.Settings.DebounceMs);
    }

    [Fact]
    public async Task ReloadConfiguration_Fails_LogsError()
    {
        var app = await CreateReadyApp(new() { Settings = new() { DebounceMs = 1 } });

        _fs.AddFile("/cfg.json", "invalid json", DateTime.UtcNow);
        await app.ReloadConfigurationAsync(default);

        Assert.Contains(LogService.GetRecentLogs(), l => l.Level == LogLevel.Error && l.Message.Contains("Failed to reload"));
    }

    [Fact]
    public async Task SetupWatchers_MissingSource_SkipsAndLogsWarning()
    {
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Description = "test", Enabled = true }] } });

        Assert.Contains(LogService.GetRecentLogs(), l => l.Level == LogLevel.Warning && l.Message.Contains("source is missing"));
    }

    [Fact]
    public async Task SetupWatchers_SourceNotFound_LogsWarning()
    {
        var app = await CreateReadyApp(new() { Hooks = new() { OnUpdate = [new() { Source = "/missing.txt", Enabled = true }] } });

        Assert.Contains(LogService.GetRecentLogs(), l => l.Level == LogLevel.Warning && l.Message.Contains("Source file not found"));
    }

    [Fact]
    public void HandleFileEvent_UnwatchedDirectory_ReturnsEarly()
    {
        var app = CreateApp(new());

        app.HandleFileEvent(new(WatcherChangeTypes.Changed, "/unwatched", "file.txt"));

        Assert.Empty(app._pendingTokens);
    }

    [Fact]
    public async Task RunHookAsync_Exception_LogsError()
    {
        var app = CreateApp(new(), new FakeProcessRunner { ShouldThrow = true });

        await app.RunHookAsync("cmd", "/tmp", LogLevel.Info, "", default);

        Assert.Contains(LogService.GetRecentLogs(), l => l.Level == LogLevel.Error && l.Message.Contains("Hook failed"));
    }

    [Fact]
    public void Dispose_ClearsResources()
    {
        var app = CreateApp(new());
        app._directoryWatchers.TryAdd("dir", new FakeFileSystemWatcher("dir", NotifyFilters.LastWrite));

        app.Dispose();

        Assert.Empty(app._directoryWatchers);
    }
}
