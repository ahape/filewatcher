using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

internal sealed class NullLogWebServer : ILogWebServer
{
    public bool IsEnabled => false;

    public Task StartAsync(int port, CancellationToken token) => Task.CompletedTask;
}
