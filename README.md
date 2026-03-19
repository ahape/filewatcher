# Filewatcher

Filewatcher is a simple, agnostic file system watcher that triggers actions on file events (`Created` and `Changed`). It is designed around a straightforward "onEvent -> someAction" model, allowing you to copy changed files to another location (proxy/persistence) or run arbitrary subprocess commands that pipe their output to the main thread.

## Why you might want it
- Watch any number of individual files across different folders.
- Multiple entries can target the same source file with independent actions.
- Debounced execution prevents spamming actions on rapid, repeated saves.
- Run arbitrary commands (e.g., `npm run build`, `systemctl restart service`) whenever specific files change.
- Per-entry `logLevel` control — set to `"None"` to silence noisy long-running processes.
- Simple console UI: press `r` to reload the config, `q` to quit, anything else for a status snapshot.
- **Web Dashboard**: View real-time logs in your browser via a built-in lightweight web server.
- **Smart Event Handling**: Automatically ignores duplicate, OS-level "spurious" events by comparing exact file sizes and modification timestamps. Zero-byte files (mid-write truncations) are also filtered out.
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
While the app is running:
- `r` reloads `watchconfig.json` without restarting the watcher or dashboard.
- `q` exits cleanly.
- Any other key prints the current watcher/status summary.

Keep the console window open; Filewatcher writes a short log each time it triggers an action or hits an error. Each line is prefixed with its severity (`[INFO]`, `[WARNING]`, `[ERROR]`, etc.). You can also view these logs in real-time by navigating to the **Web Dashboard** (e.g., `http://localhost:5002`).

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
      { "location": "./scripts", "command": "npm install", "logLevel": "None" }
    ],
    "onUpdate": [
      {
        "source": "./src/app/bundle.js",
        "copyTo": "./dist/app.js",
        "command": "echo 'Bundle updated.'",
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
  - `dashboardPort`: The TCP port for the real-time Web Dashboard UI.

- **`hooks.onStartup`**: Commands executed once when the application starts or after a config reload.
  - `command`: The shell command to execute.
  - `location` (optional): The working directory for the command.
  - `logLevel` (optional): Log level for this hook's output. Set to `"None"` to suppress all stdout. Defaults to `"Info"`.

- **`hooks.onUpdate`**: Actions executed after a watched file changes. Multiple entries can target the same source file; each gets independent debounce timers and actions.
  - `source`: Full or relative path to the file you want to watch.
  - `copyTo` (optional): Path to copy the source file to on change (directories are created if missing).
  - `command` (optional): Shell command to run after a change (and after copying, if applicable).
  - `location` (optional): Working directory for the command.
  - `enabled` (optional): Toggle to `false` to skip the entry without deleting it (defaults to `true`).
  - `description` (optional): Label that appears in the console log.
  - `logLevel` (optional): Log level for this hook's output. Set to `"None"` to suppress all stdout. Defaults to `"Info"`.

## Troubleshooting
- **File not found error on startup**: Ensure `watchconfig.json` exists in your working directory.
- **"Source file not found" warning**: Confirm the `source` path exists; the app skips invalid entries gracefully.
- **Nothing happens on change**: Verify the entry is `enabled`, the file path is correct, and reload the config (`r`).
- **Permissions issues**: Make sure you have write access to the destination folder or permission to execute the configured commands.
