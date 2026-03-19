using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

/// <summary>
/// Abstracts the embedded web server so that the dashboard can be verified in tests
/// without binding a real TCP port.
/// </summary>
internal interface ILogWebServer
{
    /// <summary>
    /// Starts listening on <paramref name="port"/> and serves the log dashboard until
    /// <paramref name="token"/> is cancelled.
    /// </summary>
    Task StartAsync(int port, CancellationToken token);
}
