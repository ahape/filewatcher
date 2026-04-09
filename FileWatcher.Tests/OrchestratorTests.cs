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
        var destDir = Path.Combine(_testDir, "dest");
        Directory.CreateDirectory(srcDir);

        var sourceFile = Path.Combine(srcDir, "utils.ts");
        File.WriteAllText(sourceFile, "initial content");

        // The unified model: Hook
        var config = new
        {
            Settings = new { DebounceMs = 200 },
            Hooks = new
            {
                OnStartup = new[] { new { Name = "compiler", Command = "echo startup", Enabled = true } },
                OnUpdate = new[] {
                    new {
                        Name = "linter",
                        Source = sourceFile,
                        CopyTo = Path.Combine(destDir, "utils_copied.ts"),
                        Command = "echo update",
                        Enabled = true
                    }
                }
            }
        };

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, s_jsonOpts));

        // We run the actual Program.Main. 
        // Note: Top-level statements result in a Program class in the global namespace or matching project name.
        var runTask = Task.Run(() => Program.Main([_configPath]));

        await Task.Delay(1000); // Wait for startup and file watching to settle

        File.WriteAllText(sourceFile, "updated content");
        await Task.Delay(1000); // Wait for debounce and execution

        Assert.True(File.Exists(Path.Combine(destDir, "utils_copied.ts")), "File should have been copied on update.");

        // We can't easily cancel the Main loop here without a CancellationToken or similar, 
        // but our test can just finish and let the process dispose.
    }
}
