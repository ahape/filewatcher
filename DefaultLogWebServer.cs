using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileWatcher;

internal sealed class DefaultLogWebServer : ILogWebServer
{
    public async Task StartAsync(int port, CancellationToken token)
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Error);
        b.WebHost.SuppressStatusMessages(true);
        b.Services.AddCors();
        b.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = b.Build();
        app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        app.MapGet(
            "/",
            async c =>
            {
                c.Response.ContentType = "text/html";
                var htmlPath = Path.Combine(AppContext.BaseDirectory, "dashboard.html");
                await c.Response.SendFileAsync(htmlPath, token);
            }
        );
        app.MapGet("/logs", LogService.GetRecentLogs);
        app.MapGet(
            "/stream",
            async c =>
            {
                c.Response.Headers.Append("Content-Type", "text/event-stream");
                c.Response.Headers.Append("Cache-Control", "no-cache");
                var opts = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                };
                opts.Converters.Add(new JsonStringEnumConverter());
                LogService.OnLog += syncOnLog;
                try
                {
                    while (
                        !c.RequestAborted.IsCancellationRequested && !token.IsCancellationRequested
                    )
                        await Task.Delay(1000, c.RequestAborted);
                }
                finally
                {
                    LogService.OnLog -= syncOnLog;
                }
                void syncOnLog(LogEntry e) => _ = onLog(e);
                async Task onLog(LogEntry e)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(e, opts);
                        await c.Response.WriteAsync($"data: {json}\n\n", token);
                        await c.Response.Body.FlushAsync(token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Serialization or write failure must not crash the stream handler;
                        // log the problem and continue so other clients are unaffected.
                        LogService.Log(
                            LogLevel.Error,
                            $"[Stream] Failed to send log entry: {ex.Message}"
                        );
                    }
                }
            }
        );
        await app.RunAsync(token);
    }
}
