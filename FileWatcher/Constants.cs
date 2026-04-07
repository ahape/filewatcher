using System;
using System.IO;

namespace FileWatcher;

/// <summary>Application-wide constants and default values.</summary>
internal static class Constants
{
    public const string ConfigFileName = "watchconfig.json";
    public const string PluginAssemblyName = "FileWatcher.Web.dll";

    public const int DefaultDashboardPort = 5002;
    public const int DefaultDebounceMs = 1000;
    public const int DefaultStartupTimeoutMs = 2000;
    public const int KeyPollIntervalMs = 75;
    public const int MaxLogEntries = 500;

    public const string DefaultLogLevel = "Info";

    public const string ArgNoWeb = "--no-web";
    public const string ArgExitAfterStartup = "--exit-after-startup";

    public const string AnonymousHookName = "<Anonymous>";

    public static readonly NotifyFilters WatcherNotifyFilters =
        NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;

    public static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(KeyPollIntervalMs);
}
