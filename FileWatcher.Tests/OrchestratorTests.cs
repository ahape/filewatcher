using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public sealed class OrchestratorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _configPath;
    private static readonly JsonSerializerOptions s_jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DevOrchestrator_E2E_Workflow()
    {
        var srcDir = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(srcDir);

        var sourceFile = Path.Combine(srcDir, "utils.ts");
        File.WriteAllText(sourceFile, "initial content");
        var config = new
        {
            Settings = new { DebounceMs = 200 },
            Hooks = new
            {
                OnStartup = new[] { new { Name = "compiler", Command = "echo startup", Enabled = true } },
                OnUpdate = new[] {
                    new {
                        Name = "linter",
                        Source = Path.GetRelativePath(_testDir, sourceFile),
                        CopyTo = Path.Combine("dest", "utils_copied.ts"),
                        Command = "echo update",
                        Enabled = true
                    },
                    new {
                        Name = "disabled",
                        Source = Path.GetRelativePath(_testDir, sourceFile),
                        CopyTo = Path.Combine("dest", "disabled.ts"),
                        Command = "echo disabled",
                        Enabled = false
                    }
                }
            }
        };

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, s_jsonOpts));
        var originalDir = Environment.CurrentDirectory;
        var foreignDir = Path.Combine(_testDir, "cwd");
        Directory.CreateDirectory(foreignDir);

        try
        {
            Environment.CurrentDirectory = foreignDir;
            var runTask = Task.Run(() => Program.Main([_configPath]));

            await Task.Delay(1000);
            File.WriteAllText(sourceFile, "updated content");
            await Task.Delay(1000);

            Assert.False(runTask.IsFaulted);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
        }

        Assert.True(File.Exists(Path.Combine(_testDir, "dest", "utils_copied.ts")), "Enabled hook should copy on update.");
        Assert.False(File.Exists(Path.Combine(_testDir, "dest", "disabled.ts")), "Disabled hook should be ignored.");
    }

    [Fact]
    public async Task ExitAfterStartup_RunsStartupHooks_WithoutEnteringWatchLoop()
    {
        var srcDir = Path.Combine(_testDir, "src");
        Directory.CreateDirectory(srcDir);

        var sourceFile = Path.Combine(srcDir, "utils.ts");
        var startupMarker = Path.Combine(_testDir, "startup.txt");
        var copiedFile = Path.Combine(_testDir, "dest", "utils_copied.ts");
        File.WriteAllText(sourceFile, "initial content");

        var config = new
        {
            Settings = new { DebounceMs = 100 },
            Hooks = new
            {
                OnStartup = new[] { new { Name = "startup", Command = $"echo started > \"{startupMarker}\"", Enabled = true } },
                OnUpdate = new[] { new { Name = "copy", Source = sourceFile, CopyTo = copiedFile, Enabled = true } }
            }
        };

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, s_jsonOpts));

        await Program.Main([_configPath, "--exit-after-startup"]);
        File.WriteAllText(sourceFile, "updated content");
        await Task.Delay(300);

        Assert.True(File.Exists(startupMarker), "Startup hooks should run before exiting.");
        Assert.False(File.Exists(copiedFile), "Exit-after-startup should skip the watch loop.");
    }
}
