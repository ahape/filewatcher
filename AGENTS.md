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

## Project Layout
```
FileWatcher.slnx            ← Solution file
FileWatcher/                 ← Main application project
  FileWatcher.csproj
  Program.cs
  FileWatcherApp.cs          ← Core orchestrator
  HookEntry.cs               ← Abstract base for StartupEntry/UpdateEntry
  StartupEntry.cs
  UpdateEntry.cs
  LogService.cs / LogLevel.cs / LogEntry.cs
  DefaultLogWebServer.cs     ← Kestrel SSE dashboard server
  ShellProcessRunner.cs
  PhysicalFileSystem.cs / PhysicalFileSystemWatcher.cs
  dashboard.html
  ...interfaces (IConsole, IFileSystem, ILogWebServer, IProcessRunner)
FileWatcher.Tests/           ← xUnit test project
  FileWatcher.Tests.csproj
  ...
```

## Core Guidelines
- **Project Structure**: Maintain a strict **one-class-per-file** structure.
- **Formatting**: You can format all of the csharp files via the command: `dnx -y csharpier format .`.
- **Dependencies**: The project depends on `Microsoft.AspNetCore.App` for the built-in web server. Unless completely unavoidable, rely solely on built-in .NET SDK packages.
- **Testing**: Maintain high test coverage using xUnit in the `FileWatcher.Tests` project. To run tests, simply execute `dotnet test`.

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
- Both `StartupEntry` and `UpdateEntry` extend the abstract `HookEntry` base record, which provides shared `Command`, `Location`, and `LogLevel` properties.
- Per-entry `logLevel` (defaulting to `Info`) controls the log level of hook stdout. Set to `None` to suppress all output from a hook — useful for long-running background processes like compilers in watch mode.
- Multiple `onUpdate` entries can watch the same source file; each gets independent debounce timers and actions.
- It features a built-in lightweight `Kestrel` web server (`DefaultLogWebServer.cs`) that broadcasts logs in real-time via Server-Sent Events (SSE) to a single-file `dashboard.html`.
- Handles spurious OS-level events by explicitly validating state changes (Size, LastWriteTime) against an internal `_fileStates` dictionary. Zero-byte files (mid-write truncations) are also filtered out.
- **Non-blocking Lifecycle**: The web server and file monitoring start immediately. Startup hooks run in parallel to avoid blocking the main application loop, which is critical for long-running watch processes in hooks.
