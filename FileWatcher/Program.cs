using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcher;

/// <summary>
/// Application entry point. Wires up graceful Ctrl+C shutdown and delegates
/// the full lifecycle to <see cref="FileWatcherApp"/>.
/// </summary>
internal static class Program
{
    private const string ConfigFileName = "watchconfig.json";
    private static readonly CancellationTokenSource s_shutdownTokenSource = new();

    private static async Task Main(string[] args)
    {
        ProgramOptions options = ProgramOptions.Parse(args);
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            s_shutdownTokenSource.Cancel();
        };

        try
        {
            using var app = new FileWatcherApp(
                ConfigFileName,
                webServer: LogWebServerPluginLoader.Load(options.DisableWeb)
            );
            await app.RunAsync(s_shutdownTokenSource.Token, options.ExitAfterStartup);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            s_shutdownTokenSource.Dispose();
        }
    }
}
