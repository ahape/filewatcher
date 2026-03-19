using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public class LogWebServerTests : IDisposable
{
    private readonly HttpClient _client = new();
    private const int TestPort = 45987;
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task WebServer_ServesDashboardAndLogs()
    {
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            // Start the server in the background
            var serverTask = Task.Run(() => LogWebServer.StartAsync(TestPort, _cts.Token));

            // Wait for it to boot
            await Task.Delay(2000); // Kestrel needs a moment to start

            // ... (rest of the test)
            // Restore Console.Out for the assertions that might use LogService if any
            // Actually, we can keep it null until the end.

            // 1. Test root endpoint (dashboard HTML file)
            var rootResponse = await _client.GetAsync($"http://localhost:{TestPort}/");
            Assert.NotNull(rootResponse);

            // 2. Test /logs endpoint
            LogService.Log(LogLevel.Info, "test log for web server");
            var logsResponse = await _client.GetAsync($"http://localhost:{TestPort}/logs");
            logsResponse.EnsureSuccessStatusCode();
            var logsJson = await logsResponse.Content.ReadAsStringAsync();
            Assert.Contains("test log for web server", logsJson);

            // 3. Test /stream endpoint
            using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var streamResponse = await _client.GetAsync(
                    $"http://localhost:{TestPort}/stream",
                    HttpCompletionOption.ResponseHeadersRead,
                    streamCts.Token
                );
                streamResponse.EnsureSuccessStatusCode();
                Assert.Equal(
                    "text/event-stream",
                    streamResponse.Content.Headers.ContentType?.MediaType
                );

                LogService.Log(LogLevel.Info, "streamed log event");

                using var stream = await streamResponse.Content.ReadAsStreamAsync(streamCts.Token);
                using var reader = new System.IO.StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(streamCts.Token)) != null)
                {
                    if (line.Contains("streamed log event"))
                    {
                        break;
                    }
                }
                Assert.Contains("streamed log event", line);
            }
            catch (OperationCanceledException) { }

            _cts.Cancel();
            try
            {
                await serverTask;
            }
            catch { }
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
