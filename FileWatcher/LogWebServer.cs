using System;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileWatcher;

public sealed class LogWebServer : ILogWebServer
{
    public bool IsEnabled => true;

    public async Task StartAsync(int port, CancellationToken ct)
    {
        try
        {
            var b = WebApplication.CreateBuilder();
            b.Logging.ClearProviders();
            b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
            var app = b.Build();

            app.MapGet("/", (HttpContext c) => WriteAssetAsync(c, "index.html"));
            app.MapGet("/logs", () => LogService.GetRecentLogs());
            app.MapGet("/stream", (HttpContext c) => StreamLogsAsync(c, ct));
            app.MapGet("/{asset}", (HttpContext c, string asset) => WriteAssetAsync(c, asset));

            await app.RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log(LogLevel.Error, $"Web server failed on {port}: {ex.Message}");
        }
    }

    private static async Task WriteAssetAsync(HttpContext context, string assetName)
    {
        var resource = $"FileWatcher.wwwroot.{assetName}";
        using var stream = typeof(LogWebServer).Assembly.GetManifestResourceStream(resource);
        if (stream == null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = assetName.EndsWith(".js") ? "text/javascript" : assetName.EndsWith(".css") ? "text/css" : "text/html";
        await stream.CopyToAsync(context.Response.Body);
    }

    private static async Task StreamLogsAsync(HttpContext c, CancellationToken ct)
    {
        c.Response.Headers.Append("Content-Type", "text/event-stream");
        c.Response.Headers.Append("Cache-Control", "no-cache");
        void onLog(LogEntry e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await c.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, s_opts)}\n\n", ct);
                    await c.Response.Body.FlushAsync(ct);
                }
                catch { }
            }, ct);
        }
        LogService.OnLog += onLog;
        try { await Task.Delay(-1, c.RequestAborted); }
        catch (OperationCanceledException) { }
        finally { LogService.OnLog -= onLog; }
    }

    private static readonly JsonSerializerOptions s_opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
}
