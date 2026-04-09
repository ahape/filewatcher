using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public class OrchestratorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _configPath;

    public OrchestratorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FileWatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _configPath = Path.Combine(_testDir, "watchconfig.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DevOrchestrator_E2E_Workflow()
    {
        // 1. Setup abstract configuration based on PRD
        var srcDir = Path.Combine(_testDir, "src");
        var destDir = Path.Combine(_testDir, "dest");
        Directory.CreateDirectory(srcDir);

        var sourceFile = Path.Combine(srcDir, "utils.ts");
        File.WriteAllText(sourceFile, "initial content");

        var config = new WatchConfig
        {
            Settings = new WatchSettings { DebounceMs = 200, DashboardPort = 0 }, // Fast debounce for testing
            Hooks = new WatchHooks
            {
                OnStartup = [
                    new StartupEntry("npm run build:watch", "compiler")
                ],
                OnUpdate = [
                    new UpdateEntry(sourceFile, "npm run lint", "linter")
                    {
                        CopyTo = Path.Combine(destDir, "utils_copied.ts")
                    }
                ]
            }
        };

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var processRunner = new MockProcessRunner();
        var console = new MockConsole();
        
        using var cts = new CancellationTokenSource();
        using var app = new FileWatcherApp(_configPath, runner: processRunner, console: console);

        // 2. Start the application
        var runTask = app.RunAsync(cts.Token);

        // Wait for startup hooks
        await Task.Delay(500);

        Assert.Contains(processRunner.ExecutedCommands, cmd => cmd == "npm run build:watch");

        // 3. Simulate developer saving a file
        File.WriteAllText(sourceFile, "updated content");

        // Wait for debounce delay and hook execution
        await Task.Delay(1000);

        // 4. Verify behaviors
        Assert.True(File.Exists(Path.Combine(destDir, "utils_copied.ts")), "File should have been copied on update.");
        Assert.Contains(processRunner.ExecutedCommands, cmd => cmd == "npm run lint");

        // 5. Graceful shutdown
        cts.Cancel();
        await runTask;
    }

    private class MockProcessRunner : IProcessRunner
    {
        public ConcurrentBag<string> ExecutedCommands { get; } = new();

        public Task<int> RunAsync(string command, string workingDirectory, Action<string> onOutput, Action<string> onError, CancellationToken token, Action<int>? onProcessStarted = null)
        {
            ExecutedCommands.Add(command);
            onOutput?.Invoke($"Mock output for {command}");
            onProcessStarted?.Invoke(new Random().Next(1000, 9999));
            return Task.FromResult(0); // Exit code 0
        }
    }

    private class MockConsole : IConsole
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey(bool intercept) => default;
    }
}
