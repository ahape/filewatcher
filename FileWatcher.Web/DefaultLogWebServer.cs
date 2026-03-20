using System;
using System.Collections.Generic;
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
/// Serves embedded dashboard assets, recent logs at <c>/logs</c>, and a live SSE stream at
/// <c>/stream</c>.
/// </summary>
public sealed class DefaultLogWebServer : ILogWebServer
{
    public bool IsEnabled => true;

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
        b.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())
        );
        b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        return b.Build();
    }

    private static void MapEndpoints(WebApplication app, CancellationToken token)
    {
        app.MapGet("/", c => WriteAssetAsync(c, "index.html"));
        app.MapGet("/logs", LogService.GetRecentLogs);
        app.MapGet("/stream", c => StreamLogsAsync(c, token));
        app.MapGet("/{asset}", (HttpContext c, string asset) => WriteAssetAsync(c, asset));
    }

    private static async Task WriteAssetAsync(HttpContext context, string assetName)
    {
        if (GetAssetStream(assetName) is not Stream asset)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using Stream stream = asset;
        context.Response.ContentType = GetContentType(assetName);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static Stream? GetAssetStream(string assetName)
    {
        string? resourceName = s_assetNames.GetValueOrDefault(assetName);
        return resourceName == null
            ? null
            : typeof(DefaultLogWebServer).Assembly.GetManifestResourceStream(resourceName);
    }

    private static async Task StreamLogsAsync(HttpContext c, CancellationToken token)
    {
        c.Response.Headers.Append("Content-Type", "text/event-stream");
        c.Response.Headers.Append("Cache-Control", "no-cache");

        void SyncOnLog(LogEntry e) => _ = SendLogEntryAsync(c, e, token);
        LogService.OnLog += SyncOnLog;
        try
        {
            while (!c.RequestAborted.IsCancellationRequested && !token.IsCancellationRequested)
                await Task.Delay(1000, c.RequestAborted);
        }
        finally
        {
            LogService.OnLog -= SyncOnLog;
        }
    }

    private static async Task SendLogEntryAsync(HttpContext c, LogEntry e, CancellationToken token)
    {
        try
        {
            string json = JsonSerializer.Serialize(e, s_streamJsonOptions);
            await c.Response.WriteAsync($"data: {json}\n\n", token);
            await c.Response.Body.FlushAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Log(LogLevel.Error, $"[Stream] Failed to send log entry: {ex.Message}");
        }
    }

    private static string GetContentType(string assetName) =>
        assetName switch
        {
            "dashboard.js" => "application/javascript; charset=utf-8",
            "styles.css" => "text/css; charset=utf-8",
            _ => "text/html; charset=utf-8",
        };

    private static readonly IReadOnlyDictionary<string, string> s_assetNames = new Dictionary<
        string,
        string
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["index.html"] = "FileWatcher.Web.wwwroot.index.html",
        ["styles.css"] = "FileWatcher.Web.wwwroot.styles.css",
        ["dashboard.js"] = "FileWatcher.Web.wwwroot.dashboard.js",
    };

    private static readonly JsonSerializerOptions s_streamJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
