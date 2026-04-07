# Filewatcher Project Instructions

This document provides context and guidelines for interacting with the `filewatcher` repository.

## Stack & Architecture
- **Language**: C# 13, running on .NET 10.
- **Style**: Extremely concise, modern C#. We heavily favor features that reduce boilerplate, such as:
  - Nullable reference types
  - Primary constructors
  - Collection expressions (`[]`)
  - Expression-bodied members
  - Raw string literals (`"""..."""`)
  - File-scoped namespaces
- **Web Frontend**: TypeScript, HTMX, and Tailwind CSS v4. Built with esbuild and the Tailwind CLI.

## Project Layout
```
FileWatcher.slnx            ← Solution file
FileWatcher/                 ← Main application project
  FileWatcher.csproj         ← Triggers WebFrontEnd npm build via MSBuild targets
  Program.cs
  FileWatcherApp.cs          ← Core orchestrator
  HookEntry.cs               ← Abstract base for StartupEntry/UpdateEntry
  StartupEntry.cs
  UpdateEntry.cs
  LogService.cs / LogLevel.cs / LogEntry.cs
  DefaultLogWebServer.cs     ← Kestrel server; serves wwwroot/ via static file middleware
  ShellProcessRunner.cs
  PhysicalFileSystem.cs / PhysicalFileSystemWatcher.cs
  ...interfaces (IConsole, IFileSystem, ILogWebServer, IProcessRunner)
WebFrontEnd/                 ← HTMX + Tailwind CSS dashboard
  package.json               ← esbuild, @tailwindcss/cli, htmx.org
  tsconfig.json              ← IDE support (esbuild does the actual compilation)
  src/
    index.html               ← Dashboard page (references styles.css, htmx, dashboard.js)
    input.css                ← Tailwind v4 entry point with component styles
    dashboard.ts             ← SSE streaming, log rendering, status indicator
  wwwroot/                   ← Build output (gitignored); copied to bin/wwwroot by MSBuild
FileWatcher.Tests/           ← xUnit test project
  FileWatcher.Tests.csproj
  ...
```

## Build Pipeline
Building `FileWatcher.csproj` automatically builds the web frontend:
1. MSBuild `BuildWebFrontEnd` target runs `npm ci` + `npm run build` in `WebFrontEnd/`.
2. `npm run build` compiles Tailwind CSS, bundles TypeScript via esbuild, and copies `index.html` + `htmx.min.js` into `WebFrontEnd/wwwroot/`.
3. MSBuild `CopyWwwRoot` target copies `wwwroot/**` into the output directory.
4. At runtime, `DefaultLogWebServer` serves the dashboard via `UseDefaultFiles()` + `UseStaticFiles()`.

To rebuild just the frontend during development: `cd WebFrontEnd && npm run build`.

## Core Guidelines
- **Project Structure**: Maintain a strict **one-class-per-file** structure.
- **Formatting**: You can format all of the csharp files via the command: `dnx -y csharpier format .`.
- **Dependencies**: The project depends on `Microsoft.AspNetCore.App` for the built-in web server. Unless completely unavoidable, rely solely on built-in .NET SDK packages. The web frontend uses npm packages (`esbuild`, `@tailwindcss/cli`, `htmx.org`) but these are dev/build-time only.
- **Testing**: Maintain high test coverage using xUnit in the `FileWatcher.Tests` project. To run tests, simply execute `dotnet test`.
- **Manual Testing**: When testing the application manually (e.g., via `dotnet run`), always append `--exit-after-startup` so you don't wait on the process indefinitely.

## Member Ordering
All top-level `*.cs` files must order their members using a three-level sort:

1. **Static after instance**
2. **Access level** (within static/instance): `public > internal > protected > private`
3. **Member kind** (within each access level): `Const > Field > Ctor > Dtor > Delegate > Event > Enum > Interface > Prop > Indexer > Method > Struct > Class`

Do **not** add decorative separator comments (e.g. `// ── Static, Private ──`). The ordering itself provides sufficient structure.

## Method Size Limit
No method may span more than **35 lines** (measured from the method signature to its closing brace, inclusive). When a method exceeds this limit, extract well-named private helpers. Prefer extracting cohesive chunks of logic (validation, I/O, mapping) rather than arbitrary splits.

## Features & Goals
- The core goal of this project is to provide a simple, agnostic "on[Event] -> someAction" pipeline.
- **Do not introduce opinionated, baked-in conventions** (no opinions outside the realm of executing user-configured commands or copies on file events).
- The system supports triggering file copies (`CopyTo`) and arbitrary subprocess commands (`Command`) with output piped to the main thread's stdout.
- Both `StartupEntry` and `UpdateEntry` extend the abstract `HookEntry` base record, which provides shared `Name`, `Command`, `Location`, and `LogLevel` properties.
- Per-entry `name` (optional) is shown in log prefixes to identify which subprocess produced output (e.g., `[tsc-watcher]` instead of `[Hook]`).
- Per-entry `logLevel` (defaulting to `Info`) controls the log level of hook stdout. Set to `None` to suppress all output from a hook — useful for long-running background processes like compilers in watch mode.
- Multiple `onUpdate` entries can watch the same source file; each gets independent debounce timers and actions.
- It features a built-in lightweight Kestrel web server (`DefaultLogWebServer.cs`) that serves an HTMX + Tailwind CSS dashboard from `wwwroot/` and broadcasts logs in real-time via Server-Sent Events (SSE).
- Handles spurious OS-level events by explicitly validating state changes (Size, LastWriteTime) against an internal `_fileStates` dictionary. Zero-byte files (mid-write truncations) are also filtered out.
- Detects saves from editors that use "atomic save" (write-temp-then-rename) such as Visual Studio, VS Code, JetBrains IDEs, and vim by listening for `Renamed` events in addition to `Changed` and `Created`.
- **Non-blocking Lifecycle**: The web server and file monitoring start immediately. Startup hooks run in parallel to avoid blocking the main application loop, which is critical for long-running watch processes in hooks.
