using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace FileWatcher;

internal static class LogWebServerPluginLoader
{
    public static ILogWebServer Load(bool disableWeb = false, string? pluginPath = null)
    {
        if (disableWeb)
            return new NullLogWebServer();

        string path = pluginPath ?? Path.Combine(AppContext.BaseDirectory, Constants.PluginAssemblyName);
        if (!File.Exists(path))
            return new NullLogWebServer();

        try
        {
            return CreateServer(path) ?? new NullLogWebServer();
        }
        catch (Exception ex)
        {
            LogService.Log(LogLevel.Warning, $"Web plugin unavailable: {ex.Message}");
            return new NullLogWebServer();
        }
    }

    private static ILogWebServer? CreateServer(string pluginPath)
    {
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.GetFullPath(pluginPath)
        );
        Type? type = assembly
            .GetTypes()
            .FirstOrDefault(t =>
                !t.IsAbstract
                && typeof(ILogWebServer).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null
            );
        return type == null ? null : (ILogWebServer?)Activator.CreateInstance(type);
    }

}
