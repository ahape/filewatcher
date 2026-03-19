using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

internal interface ILogWebServer
{
    Task StartAsync(int port, CancellationToken token);
}
