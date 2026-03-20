# Filewatcher

Filewatcher is a simple, agnostic file system watcher that triggers actions on file events (`Created`, `Changed`, and `Renamed`). It is designed around a straightforward "onEvent -> someAction" model, allowing you to copy changed files to another location (proxy/persistence) or run arbitrary subprocess commands that pipe their output to the main thread.

## Why you might want it
- Watch any number of individual files across different folders.
- Multiple entries can target the same source file with independent actions.
- Debounced execution prevents spamming actions on rapid, repeated saves.
- Run arbitrary commands (e.g., `npm run build`, `systemctl restart service`) whenever specific files change.
- Per-entry `name` for log prefixes — know exactly which subprocess produced each line of output.
- Per-entry `logLevel` control — set to `"None"` to silence noisy long-running processes.
- Simple console UI: press `r` to reload the config, `q` to quit, anything else for a status snapshot.
- **Optional Web Dashboard**: View real-time logs in your browser via an installable HTMX + Tailwind CSS dashboard plugin served by Kestrel.
- **Smart Event Handling**: Automatically ignores duplicate, OS-level "spurious" events by comparing exact file sizes and modification timestamps. Zero-byte files (mid-write truncations) are also filtered out. Detects saves from editors that use "atomic save" (write-temp-then-rename) such as Visual Studio, VS Code, and JetBrains IDEs.
- **Non-blocking Startup**: The web server and file monitoring start immediately, even if you have long-running startup hooks (like a compiler in watch mode).

## Requirements
- [.NET SDK 10.0+](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux. (The app detects your OS to run commands via `cmd.exe` or `sh`).

## First-time setup
1. **Clone or download** this repo.
2. **Create a config**: Copy `watchconfig.example.json` to `watchconfig.json` in the run directory.
3. **Edit the config** to define what files to watch (`source`), where to copy them (`copyTo`), and/or what command to run (`command`).

## Running the watcher
```powershell
dotnet run --project FileWatcher
```
This runs the lean core watcher only. If `FileWatcher.Web.dll` is present beside the executable, the dashboard plugin is loaded automatically. Pass `--no-web` to force the dashboard off even when the plugin DLL is present.

While the app is running:
- `r` reloads `watchconfig.json` without restarting the watcher or dashboard plugin.
- `q` exits cleanly.
- Any other key prints the current watcher/status summary.

Keep the console window open; Filewatcher writes a short log each time it triggers an action or hits an error. Each line is prefixed with its severity (`[INFO]`, `[WARNING]`, `[ERROR]`, etc.). If the web plugin is installed, you can also view these logs in real-time in the dashboard (for example `http://localhost:5002`).

## Configuration reference (`watchconfig.json`)

```json
{
  "settings": {
    "debounceMs": 500,
    "logLevel": "Debug",
    "dashboardPort": 5002
  },
  "hooks": {
    "onStartup": [
      { "command": "echo 'Starting up!'" },
      { "name": "tsc-watcher", "location": "./scripts", "command": "npx tsc -w", "logLevel": "None" }
    ],
    "onUpdate": [
      {
        "name": "lint",
        "source": "./src/app/bundle.js",
        "copyTo": "./dist/app.js",
        "command": "npm run lint",
        "location": "./src/app",
        "description": "Main application javascript bundle"
      },
      {
        "source": "./config/app.env",
        "command": "systemctl restart my-app",
        "description": "Restart service when config changes (no file copy needed)"
      }
    ]
  }
}
```

- **`settings`**:
  - `debounceMs`: Wait time (in ms) after a change before executing actions. Combines rapid sequential saves.
  - `logLevel`: Controls debug output visibility. Set to `"Debug"` or `"Trace"` to enable verbose diagnostic logging of internal file events, debounce timers, and command hooks. Defaults to `"Info"`.
  - `dashboardPort`: The TCP port used by the web dashboard plugin when it is enabled.

- **`hooks.onStartup`**: Commands executed once when the application starts or after a config reload.
  - `name` (optional): Label shown in log prefixes to identify this hook's output (e.g., `[tsc-watcher]`). Defaults to `[Hook]`.
  - `command`: The shell command to execute.
  - `location` (optional): The working directory for the command.
  - `logLevel` (optional): Log level for this hook's output. Set to `"None"` to suppress all stdout. Defaults to `"Info"`.

- **`hooks.onUpdate`**: Actions executed after a watched file changes. Multiple entries can target the same source file; each gets independent debounce timers and actions.
  - `name` (optional): Label shown in log prefixes to identify this hook's output (e.g., `[lint]`). Defaults to `[Hook]`.
  - `source`: Full or relative path to the file you want to watch.
  - `copyTo` (optional): Path to copy the source file to on change (directories are created if missing).
  - `command` (optional): Shell command to run after a change (and after copying, if applicable).
  - `location` (optional): Working directory for the command.
  - `enabled` (optional): Toggle to `false` to skip the entry without deleting it (defaults to `true`).
  - `description` (optional): Label that appears in the console log.
  - `logLevel` (optional): Log level for this hook's output. Set to `"None"` to suppress all stdout. Defaults to `"Info"`.

## Web Dashboard Plugin

The dashboard now lives in the `FileWatcher.Web/` plugin. Its static assets are embedded into `FileWatcher.Web.dll`, so the plugin can be copied in or left out entirely without affecting the core watcher.

To build the plugin:

```powershell
dotnet build FileWatcher.Web/FileWatcher.Web.csproj
```

`FileWatcher.Web.dll` is optional at runtime. If it sits beside the main executable, the dashboard loads automatically. If it is missing, or if you run the app with `--no-web`, the watcher stays console-only.

## Troubleshooting
- **File not found error on startup**: Ensure `watchconfig.json` exists in your working directory.
- **"Source file not found" warning**: Confirm the `source` path exists; the app skips invalid entries gracefully.
- **Nothing happens on change**: Verify the entry is `enabled`, the file path is correct, and reload the config (`r`). If you are using an editor with "atomic save" (Visual Studio, VS Code, JetBrains), the `Renamed` event handler should catch it — check debug logs for details.
- **Permissions issues**: Make sure you have write access to the destination folder or permission to execute the configured commands.
- **Dashboard plugin missing**: Build `FileWatcher.Web` and place `FileWatcher.Web.dll` beside the main executable, or run without it if you do not need the dashboard.
