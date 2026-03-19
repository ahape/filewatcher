using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher.Tests;

internal sealed class FakeLogWebServer : ILogWebServer
{
    public int StartCount { get; private set; }
    public int LastPort { get; private set; }

    public Task StartAsync(int port, CancellationToken token)
    {
        StartCount++;
        LastPort = port;
        return Task.CompletedTask;
    }
}
