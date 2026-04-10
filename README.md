# FileWatcher

A minimalist, "one-stop-shop" developer orchestrator. It spins up your background services (compilers, databases, servers) and reacts to file changes (linting, copying built assets) so you can focus on code, not terminal management.

## Features
- **Parallel Startup:** Runs all your required background processes simultaneously on launch.
- **Robust File Watching:** Handles "atomic saves" from modern IDEs (VS Code, JetBrains, etc.) and avoids redundant triggers.
- **Unified Hook System:** A simple "Source -> Copy -> Command" pipeline.
- **Config-Relative Paths:** `source`, `copyTo`, and `location` resolve from the config file directory, not the shell's current directory.
- **Forgiving Config Loading:** Missing `settings` or `hooks` sections fall back to sensible defaults.
- **Zero Dependencies:** Built entirely on the modern .NET 10 Base Class Library.

## Getting Started

### 1. Build the project
```bash
dotnet build
```

### 2. Configure hooks
Create a `watchconfig.json` in your project root.

```json
{
  "settings": {
    "debounceMs": 1000
  },
  "hooks": {
    "onStartup": [
      {
        "name": "Azurite",
        "command": "azurite --silent --location ./data",
        "enabled": true
      },
      {
        "name": "TSC",
        "command": "npx tsc -w",
        "location": "./WebFrontEnd"
      }
    ],
    "onUpdate": [
      {
        "name": "Linter",
        "source": "src/utils.ts",
        "command": "npm run lint"
      },
      {
        "name": "Asset Deploy",
        "source": "dist/bundle.js",
        "copyTo": "C:/deploy/wwwroot/bundle.js"
      }
    ]
  }
}
```

### 3. Run
```bash
# Run with default watchconfig.json
dotnet run --project FileWatcher/FileWatcher.csproj

# Run with a specific config
dotnet run --project FileWatcher/FileWatcher.csproj -- my-project.json

# Run startup hooks and exit immediately (CI/Setup mode)
dotnet run --project FileWatcher/FileWatcher.csproj -- --exit-after-startup
```

## Configuration Reference

### `settings`
- `debounceMs`: Milliseconds to wait after a file change before triggering the action.

If `settings` is omitted, FileWatcher uses the default debounce window of `1000` ms.

### `hooks` (Startup & Update)
- `name`: Label shown in log prefixes.
- `command`: Shell command to execute.
- `source`: (Update only) Path to the file to watch.
- `copyTo`: (Update only) Destination to copy the source file to on change.
- `location`: Working directory for the command.
- `logLevel`: Reserved compatibility field on the unified `Hook` model. Current console coloring is based on stdout/stderr stream type, not this value.
- `enabled`: Set to `false` to skip the hook without removing it.

Relative paths are resolved against the directory containing the config file.
If `hooks` is omitted, FileWatcher starts with no startup hooks and no update hooks.

## Architecture
This tool is designed for extreme maintainability. The entire orchestration logic resides in just two files:
- `Models.cs`: Declarative data structures for configuration.
- `Program.cs`: The core loop handling process management and file system events.
