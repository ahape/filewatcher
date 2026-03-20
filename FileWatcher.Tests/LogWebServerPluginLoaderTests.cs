using Xunit;

namespace FileWatcher.Tests;

public sealed class LogWebServerPluginLoaderTests
{
    [Fact]
    public void Load_MissingPlugin_ReturnsDisabledServer()
    {
        ILogWebServer server = LogWebServerPluginLoader.Load(pluginPath: "/missing/FileWatcher.Web.dll");
        Assert.False(server.IsEnabled);
    }

    [Fact]
    public void Load_DisabledFlag_ReturnsDisabledServer()
    {
        ILogWebServer server = LogWebServerPluginLoader.Load(disableWeb: true);
        Assert.False(server.IsEnabled);
    }
}
