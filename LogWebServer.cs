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
        app.MapGet("/", async c => { c.Response.ContentType = "text/html"; await c.Response.WriteAsync(Html, token); });
        app.MapGet("/logs", LogService.GetRecentLogs);
        app.MapGet("/stream", async c =>
        {
            c.Response.Headers.Append("Content-Type", "text/event-stream");
            c.Response.Headers.Append("Cache-Control", "no-cache");
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var onLog = async (LogEntry e) => { await c.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, opts)}\n\n", token); await c.Response.Body.FlushAsync(token); };
            Action<LogEntry> syncOnLog = e => _ = onLog(e);
            LogService.OnLog += syncOnLog;
            try { while (!c.RequestAborted.IsCancellationRequested && !token.IsCancellationRequested) await Task.Delay(1000, c.RequestAborted); }
            finally { LogService.OnLog -= syncOnLog; }
        });
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, token);
    }

    private const string Html = """<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><title>FileWatcher Dashboard</title><style>body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;background:#121212;color:#e0e0e0;margin:0;padding:20px}.container{max-width:1200px;margin:0 auto}header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #333;padding-bottom:10px;margin-bottom:20px}h1{margin:0;font-size:1.5rem;color:#00bcd4}#logs{font-family:"JetBrains Mono","Fira Code",monospace;font-size:.9rem;line-height:1.4;overflow-wrap:break-word}.log-entry{margin-bottom:4px;border-radius:4px;padding:2px 8px}.timestamp{color:#666;margin-right:10px;user-select:none}.level{font-weight:bold;margin-right:10px;text-transform:uppercase;display:inline-block;width:60px}.level-info{color:#00bcd4}.level-success{color:#4caf50}.level-warning{color:#ffeb3b}.level-error{color:#f44336}.level-copy{color:#4caf50}.message{color:#eee}.status{font-size:.8rem;background:#333;padding:4px 8px;border-radius:4px}.status-online{color:#4caf50}.status-offline{color:#f44336}</style></head><body><div class="container"><header><h1>FileWatcher Dashboard</h1><div id="status" class="status status-offline">Connecting...</div></header><div id="logs"></div></div><script>const logsDiv=document.getElementById('logs'),statusDiv=document.getElementById('status');function addLog(e){const t=document.createElement('div'),s=new Date(e.timestamp).toLocaleTimeString(),n='level-'+e.level.toLowerCase();t.className='log-entry',t.innerHTML=`<span class="timestamp">[${s}]</span> <span class="level ${n}">${e.level}</span> <span class="message">${e.message}</span>`,logsDiv.insertBefore(t,logsDiv.firstChild),logsDiv.children.length>500&&logsDiv.removeChild(logsDiv.lastChild)}async function init(){try{const e=await fetch('/logs');(await e.json()).forEach(addLog)}catch(e){console.error('Failed to fetch initial logs',e)}const e=new EventSource("/stream");e.onmessage=e=>{addLog(JSON.parse(e.data))},e.onopen=()=>{statusDiv.textContent='ONLINE',statusDiv.className='status status-online'},e.onerror=()=>{statusDiv.textContent='OFFLINE',statusDiv.className='status status-offline'}}init();</script></body></html>""";
}