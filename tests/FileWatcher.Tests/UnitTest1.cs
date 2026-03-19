using System.Text.Json;

namespace FileWatcher.Tests;

public class WatchConfigTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [Fact]
    public void CreateSample_ReturnsMappingsAndSettings()
    {
        var sample = WatchConfig.CreateSample();
        Assert.NotNull(sample);
        Assert.NotNull(sample.Settings);
        Assert.NotEmpty(sample.Mappings);
        Assert.Contains(sample.Mappings, mapping => mapping.Enabled);
    }

    [Fact]
    public void SampleConfig_SerializesAndDeserializes()
    {
        var sample = WatchConfig.CreateSample();
        var json = JsonSerializer.Serialize(sample, SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<WatchConfig>(json, SerializerOptions);
        Assert.NotNull(roundTrip);
        Assert.Equal(sample.Mappings.Count, roundTrip!.Mappings.Count);
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
        Assert.NotEmpty(config!.Mappings);
        Assert.All(config.Mappings, mapping => Assert.False(string.IsNullOrWhiteSpace(mapping.Source)));
    }

    private static string GetRepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
