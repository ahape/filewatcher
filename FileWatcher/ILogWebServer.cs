using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

/// <summary>
/// Abstracts the optional web dashboard so the core watcher can run with or without
/// a concrete web-server plugin.
/// </summary>
public interface ILogWebServer
{
    /// <summary>Indicates whether this implementation actually exposes a dashboard.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Starts listening on <paramref name="port"/> and serves the log dashboard until
    /// <paramref name="token"/> is cancelled.
    /// </summary>
    Task StartAsync(int port, CancellationToken token);
}
