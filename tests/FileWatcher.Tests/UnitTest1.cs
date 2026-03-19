using System.Text.Json;

namespace FileWatcher.Tests;

public class WatchConfigTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [Fact]
    public void CreateSample_ReturnsHooksAndSettings()
    {
        var sample = WatchConfig.CreateSample();
        Assert.NotNull(sample);
        Assert.NotNull(sample.Settings);
        Assert.NotNull(sample.Hooks);
        Assert.NotEmpty(sample.Hooks!.OnUpdate);
        Assert.NotEmpty(sample.Hooks.OnStartup);
    }

    [Fact]
    public void SampleConfig_SerializesAndDeserializes()
    {
        var sample = WatchConfig.CreateSample();
        var json = JsonSerializer.Serialize(sample, SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<WatchConfig>(json, SerializerOptions);
        Assert.NotNull(roundTrip);
        Assert.Equal(sample.Hooks!.OnUpdate.Count, roundTrip!.Hooks!.OnUpdate.Count);
        Assert.Equal(sample.Settings.CreateBackups, roundTrip.Settings.CreateBackups);
    }

    [Fact]
    public void ExampleConfig_FileDeserializesSuccessfully()
    {
        var examplePath = Path.Combine(GetRepoRoot(), "watchconfig.example.json");
        Assert.True(File.Exists(examplePath));
        var json = File.ReadAllText(examplePath);
        var config = JsonSerializer.Deserialize<WatchConfig>(json, SerializerOptions);
        Assert.NotNull(config);
        Assert.NotEmpty(config!.Hooks!.OnUpdate);
        Assert.All(config.Hooks.OnUpdate, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Source)));
    }

    private static string GetRepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
