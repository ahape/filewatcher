using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ProgramOptions.Parse(args);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            ILogWebServer server = options.DisableWeb ? new NullLogWebServer() : new LogWebServer();
            using var app = new FileWatcherApp(options.ConfigPath ?? Constants.ConfigFileName, webServer: server);
            await app.RunAsync(cts.Token, options.ExitAfterStartup);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Log(LogLevel.Error, $"Fatal: {ex.Message}");
        }
    }
}
