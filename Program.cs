namespace FileWatcher;

/// <summary>
/// Application entry point. Wires up graceful Ctrl+C shutdown and delegates
/// the full lifecycle to <see cref="FileWatcherApp"/>.
/// </summary>
internal static class Program
{
    private const string ConfigFileName = "watchconfig.json";
    private static readonly CancellationTokenSource ShutdownTokenSource = new();

    private static async Task Main()
    {
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            ShutdownTokenSource.Cancel();
        };

        try
        {
            using var app = new FileWatcherApp(ConfigFileName);
            await app.RunAsync(ShutdownTokenSource.Token);
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
            ShutdownTokenSource.Dispose();
        }
    }
}
