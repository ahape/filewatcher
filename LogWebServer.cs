using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileWatcher;

public static class LogWebServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task StartAsync(int port, CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCors();
        
        // Ensure Kestrel uses the provided port and cancellation token
        builder.WebHost.ConfigureKestrel(options => 
        {
            options.ListenAnyIP(port);
        });

        var app = builder.Build();
        app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

        app.MapGet("/", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(DashboardHtml);
        });

        app.MapGet("/logs", () => LogService.GetRecentLogs());

        app.MapGet("/stream", async (HttpContext context) =>
        {
            context.Response.Headers.Append("Content-Type", "text/event-stream");
            context.Response.Headers.Append("Cache-Control", "no-cache");
            context.Response.Headers.Append("Connection", "keep-alive");

            var onLog = async (LogEntry entry) =>
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n");
                await context.Response.Body.FlushAsync();
            };

            LogService.OnLog += (entry) => { _ = onLog(entry); };

            try
            {
                // Keep the connection alive until the client disconnects or token is cancelled
                while (!context.RequestAborted.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000, context.RequestAborted);
                }
            }
            finally
            {
                LogService.OnLog -= (entry) => { _ = onLog(entry); };
            }
        });

        await app.RunAsync(token);
    }

    private const string DashboardHtml = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>FileWatcher Dashboard</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; background: #121212; color: #e0e0e0; margin: 0; padding: 20px; }
        .container { max-width: 1200px; margin: 0 auto; }
        header { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #333; padding-bottom: 10px; margin-bottom: 20px; }
        h1 { margin: 0; font-size: 1.5rem; color: #00bcd4; }
        #logs { font-family: ""JetBrains Mono"", ""Fira Code"", monospace; font-size: 0.9rem; line-height: 1.4; overflow-wrap: break-word; }
        .log-entry { margin-bottom: 4px; border-radius: 4px; padding: 2px 8px; }
        .timestamp { color: #666; margin-right: 10px; user-select: none; }
        .level { font-weight: bold; margin-right: 10px; text-transform: uppercase; display: inline-block; width: 60px; }
        .level-info { color: #00bcd4; }
        .level-success { color: #4caf50; }
        .level-warning { color: #ffeb3b; }
        .level-error { color: #f44336; }
        .level-copy { color: #4caf50; }
        .message { color: #eee; }
        .status { font-size: 0.8rem; background: #333; padding: 4px 8px; border-radius: 4px; }
        .status-online { color: #4caf50; }
        .status-offline { color: #f44336; }
    </style>
</head>
<body>
    <div class=""container"">
        <header>
            <h1>FileWatcher Dashboard</h1>
            <div id=""status"" class=""status status-offline"">Connecting...</div>
        </header>
        <div id=""logs""></div>
    </div>

    <script>
        const logsDiv = document.getElementById('logs');
        const statusDiv = document.getElementById('status');

        function addLog(entry) {
            const row = document.createElement('div');
            row.className = 'log-entry';
            
            const ts = new Date(entry.timestamp).toLocaleTimeString();
            const levelClass = 'level-' + entry.level.toLowerCase();
            
            row.innerHTML = `
                <span class=""timestamp"">[${ts}]</span>
                <span class=""level ${levelClass}"">${entry.level}</span>
                <span class=""message"">${entry.message}</span>
            `;
            
            logsDiv.insertBefore(row, logsDiv.firstChild);
            if (logsDiv.children.length > 500) {
                logsDiv.removeChild(logsDiv.lastChild);
            }
        }

        async function init() {
            // Load initial logs
            try {
                const res = await fetch('/logs');
                const initialLogs = await res.json();
                initialLogs.forEach(addLog);
            } catch (e) {
                console.error('Failed to fetch initial logs', e);
            }

            // Start SSE stream
            const evtSource = new EventSource(""/stream"");
            
            evtSource.onmessage = (event) => {
                const entry = JSON.parse(event.data);
                addLog(entry);
            };

            evtSource.onopen = () => {
                statusDiv.textContent = 'ONLINE';
                statusDiv.className = 'status status-online';
            };

            evtSource.onerror = () => {
                statusDiv.textContent = 'OFFLINE';
                statusDiv.className = 'status status-offline';
            };
        }

        init();
    </script>
</body>
</html>
";
}
