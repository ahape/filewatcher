using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

[Collection("Log tests")]
public class LogWebServerTests : IDisposable
{
    private readonly HttpClient _client = new();
    private const int TestPort = 45987;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private readonly TextWriter _originalOut;

    public LogWebServerTests()
    {
        LogService.Clear();
        _originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);

        var server = new DefaultLogWebServer();
        _serverTask = Task.Run(() => server.StartAsync(TestPort, _cts.Token));
        Thread.Sleep(1000); // Wait for Kestrel to bind
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _client.Dispose();
        try { _serverTask.Wait(); } catch { }
        Console.SetOut(_originalOut);
    }

    [Fact]
    public async Task WebServer_ServesDashboard_AtRootEndpoint()
    {
        var response = await _client.GetAsync($"http://localhost:{TestPort}/");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WebServer_ServesRecentLogs_AtLogsEndpoint()
    {
        LogService.Log(LogLevel.Info, "test log for web server");

        var json = await _client.GetStringAsync($"http://localhost:{TestPort}/logs");
        Assert.Contains("test log for web server", json);
    }

    [Fact]
    public async Task WebServer_StreamsLogs_AtStreamEndpoint()
    {
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var logTask = Task.Run(async () =>
        {
            while (!streamCts.IsCancellationRequested)
            {
                LogService.Log(LogLevel.Info, "streamed log event");
                await Task.Delay(50);
            }
        });

        var response = await _client.GetAsync(
            $"http://localhost:{TestPort}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            streamCts.Token
        );
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(streamCts.Token);
        using var reader = new StreamReader(stream);

        string? line;
        bool found = false;
        try
        {
            while ((line = await reader.ReadLineAsync(streamCts.Token)) != null)
            {
                if (line.Contains("streamed log event"))
                {
                    found = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        streamCts.Cancel();
        try { await logTask; } catch { }

        Assert.True(found, "Stream did not emit expected log event");
    }
}
