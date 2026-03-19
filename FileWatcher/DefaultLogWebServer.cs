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

/// <summary>
/// ASP.NET Core / Kestrel implementation of <see cref="ILogWebServer"/>.
/// Serves the bundled <c>dashboard.html</c> at <c>/</c>, the recent log history at <c>/logs</c>
/// (JSON array), and a Server-Sent Events stream of live entries at <c>/stream</c>.
/// </summary>
internal sealed class DefaultLogWebServer : ILogWebServer
{
    public async Task StartAsync(int port, CancellationToken token)
    {
        try
        {
            WebApplication app = BuildApp(port);
            MapEndpoints(app, token);
            await app.RunAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log(
                LogLevel.Error,
                $"Failed to start web server on port {port}: {ex.Message}"
            );
        }
    }

    private static WebApplication BuildApp(int port)
    {
        WebApplicationBuilder b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Error);
        b.Services.AddCors();
        b.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        WebApplication app = b.Build();
        app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        return app;
    }

    private static void MapEndpoints(WebApplication app, CancellationToken token)
    {
        app.MapGet("/", c => ServeDashboardAsync(c, token));
        app.MapGet("/logs", LogService.GetRecentLogs);
        app.MapGet("/stream", c => StreamLogsAsync(c, token));
    }

    private static async Task ServeDashboardAsync(HttpContext c, CancellationToken token)
    {
        c.Response.ContentType = "text/html";
        string htmlPath = Path.Combine(AppContext.BaseDirectory, "dashboard.html");
        if (File.Exists(htmlPath))
        {
            await c.Response.SendFileAsync(htmlPath, token);
        }
        else
        {
            c.Response.StatusCode = 404;
            await c.Response.WriteAsync("dashboard.html not found in output directory.", token);
        }
    }

    private static async Task StreamLogsAsync(HttpContext c, CancellationToken token)
    {
        c.Response.Headers.Append("Content-Type", "text/event-stream");
        c.Response.Headers.Append("Cache-Control", "no-cache");
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new JsonStringEnumConverter());

        void syncOnLog(LogEntry e) => _ = SendLogEntryAsync(c, e, opts, token);
        LogService.OnLog += syncOnLog;
        try
        {
            while (!c.RequestAborted.IsCancellationRequested && !token.IsCancellationRequested)
                await Task.Delay(1000, c.RequestAborted);
        }
        finally
        {
            LogService.OnLog -= syncOnLog;
        }
    }

    private static async Task SendLogEntryAsync(
        HttpContext c,
        LogEntry e,
        JsonSerializerOptions opts,
        CancellationToken token
    )
    {
        try
        {
            string json = JsonSerializer.Serialize(e, opts);
            await c.Response.WriteAsync($"data: {json}\n\n", token);
            await c.Response.Body.FlushAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log(LogLevel.Error, $"[Stream] Failed to send log entry: {ex.Message}");
        }
    }
}
