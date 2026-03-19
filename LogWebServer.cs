using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileWatcher;

public static class LogWebServer
{
    public static async Task StartAsync(int port, CancellationToken token)
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddCors();
        b.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = b.Build();
        app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        app.MapGet(
            "/",
            async c =>
            {
                c.Response.ContentType = "text/html";
                await c.Response.WriteAsync(Html, token);
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
        await HostingAbstractionsHostExtensions.RunAsync(app, token);
    }

    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>FileWatcher Dashboard</title>
          <style>
            body {
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
              background: #121212;
              color: #e0e0e0;
              margin: 0;
              padding: 20px;
            }
            .container { max-width: 1200px; margin: 0 auto; }
            header {
              display: flex;
              justify-content: space-between;
              align-items: center;
              border-bottom: 1px solid #333;
              padding-bottom: 10px;
              margin-bottom: 20px;
            }
            h1 { margin: 0; font-size: 1.5rem; color: #00bcd4; }
            #logs {
              font-family: "JetBrains Mono", "Fira Code", monospace;
              font-size: .9rem;
              line-height: 1.4;
              overflow-wrap: break-word;
              max-height: 80vh;
              overflow-y: auto;
            }
            .log-entry { margin-bottom: 4px; border-radius: 4px; padding: 2px 8px; }
            .timestamp { color: #666; margin-right: 10px; user-select: none; }
            .level {
              font-weight: bold;
              margin-right: 10px;
              text-transform: uppercase;
              display: inline-block;
              width: 60px;
            }
            .level-info    { color: #00bcd4; }
            .level-success { color: #4caf50; }
            .level-warning { color: #ffeb3b; }
            .level-error   { color: #f44336; }
            .level-copy    { color: #4caf50; }
            .message { color: #eee; }
            .status { font-size: .8rem; background: #333; padding: 4px 8px; border-radius: 4px; }
            .status-online  { color: #4caf50; }
            .status-offline { color: #f44336; }
            /* Visually hidden but readable by screen readers. */
            .sr-only {
              position: absolute;
              width: 1px; height: 1px;
              padding: 0; margin: -1px;
              overflow: hidden;
              clip: rect(0, 0, 0, 0);
              white-space: nowrap;
              border: 0;
            }
            /* Keyboard focus indicator for interactive elements. */
            :focus-visible { outline: 2px solid #00bcd4; outline-offset: 2px; }
          </style>
        </head>
        <body>
          <div class="container">
            <header role="banner">
              <h1>FileWatcher Dashboard</h1>
              <!--
                role="status" + aria-live="polite" announces text changes to screen readers
                without interrupting whatever they are currently reading.
              -->
              <div id="status"
                   class="status status-offline"
                   role="status"
                   aria-live="polite"
                   aria-atomic="true"
                   aria-label="Connection status">
                Connecting&hellip;
              </div>
            </header>

            <main>
              <section aria-labelledby="logs-heading">
                <h2 id="logs-heading" class="sr-only">Application Logs</h2>
                <!--
                  role="log" carries an implicit aria-live="polite" (per ARIA spec) so new
                  entries are announced to screen readers without interruption.
                  tabindex="0" makes the panel keyboard-focusable for arrow-key scrolling.
                -->
                <div id="logs"
                     role="log"
                     aria-label="Application log entries"
                     tabindex="0"></div>
              </section>
            </main>
          </div>

          <script>
            const logsDiv   = document.getElementById('logs');
            const statusDiv = document.getElementById('status');
            const MAX_VISIBLE_LOGS = 500;

            /**
             * Appends a log entry to the top of the log panel using safe DOM APIs
             * (textContent) instead of innerHTML to prevent XSS injection via log messages.
             */
            function addLog(e) {
              const entry = document.createElement('div');
              entry.className = 'log-entry';

              const ts = document.createElement('span');
              ts.className = 'timestamp';
              ts.setAttribute('aria-hidden', 'true'); // timestamp is decorative; time is in the message
              ts.textContent = '[' + new Date(e.timestamp).toLocaleTimeString() + ']';

              const lvl = document.createElement('span');
              lvl.className = 'level level-' + e.level.toLowerCase();
              lvl.textContent = e.level;

              const msg = document.createElement('span');
              msg.className = 'message';
              msg.textContent = e.message;

              entry.appendChild(ts);
              entry.appendChild(lvl);
              entry.appendChild(msg);

              logsDiv.insertBefore(entry, logsDiv.firstChild);
              if (logsDiv.children.length > MAX_VISIBLE_LOGS) {
                logsDiv.removeChild(logsDiv.lastChild);
              }
            }

            /** Updates the connection-status indicator and notifies screen readers. */
            function setStatus(online) {
              statusDiv.textContent = online ? 'ONLINE' : 'OFFLINE';
              statusDiv.className   = 'status ' + (online ? 'status-online' : 'status-offline');
            }

            // Keyboard navigation: arrow keys scroll the log panel when it has focus.
            logsDiv.addEventListener('keydown', function (e) {
              if      (e.key === 'ArrowDown') { logsDiv.scrollTop += 40; e.preventDefault(); }
              else if (e.key === 'ArrowUp')   { logsDiv.scrollTop -= 40; e.preventDefault(); }
            });

            async function init() {
              try {
                const res     = await fetch('/logs');
                const entries = await res.json();
                // Render oldest-first so the most recent entry ends up at the top.
                entries.slice().reverse().forEach(addLog);
              } catch (err) {
                console.error('Failed to fetch initial logs', err);
              }

              const source      = new EventSource('/stream');
              source.onmessage  = function (e) { addLog(JSON.parse(e.data)); };
              source.onopen     = function ()  { setStatus(true);  };
              source.onerror    = function ()  { setStatus(false); };
            }

            init();
          </script>
        </body>
        </html>
        """;
}
