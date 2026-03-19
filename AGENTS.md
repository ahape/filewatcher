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

## Core Guidelines
- **Project Structure**: Maintain a strict **one-class-per-file** structure.
- **Formatting**: You can format all of the csharp files via the command: `dnx -y csharpier format .`.
- **Dependencies**: The project depends on `Microsoft.AspNetCore.App` for the built-in web server. Unless completely unavoidable, rely solely on built-in .NET SDK packages.
- **Testing**: Maintain high test coverage using xUnit in the `FileWatcher.Tests` project. To run tests, simply execute `dotnet test`.

## Member Ordering
All top-level `*.cs` files must order their members using a three-level sort:

1. **Static before instance**
2. **Access level** (within static/instance): `public > internal > protected > private`
3. **Member kind** (within each access level): `Const > Field > Ctor > Dtor > Delegate > Event > Enum > Interface > Prop > Indexer > Method > Struct > Class`

Use section comments (e.g. `// ── Static, Private ──`) to visually separate groups.

## Method Size Limit
No method may span more than **35 lines** (measured from the method signature to its closing brace, inclusive). When a method exceeds this limit, extract well-named private helpers. Prefer extracting cohesive chunks of logic (validation, I/O, mapping) rather than arbitrary splits.

## Features & Goals
- The core goal of this project is to provide a simple, agnostic "on[Event] -> someAction" pipeline.
- **Do not introduce opinionated, baked-in conventions** (no opinions outside the realm of executing user-configured commands or copies on file events).
- The system supports triggering file copies (`CopyTo`) and arbitrary subprocess commands (`Command`) with output piped to the main thread's stdout.
- It features a built-in lightweight `Kestrel` web server (`DefaultLogWebServer.cs`) that broadcasts logs in real-time via Server-Sent Events (SSE) to a single-file `dashboard.html`.
- Handles spurious OS-level events by explicitly validating state changes (Size, LastWriteTime) against an internal `_fileStates` dictionary.
- **Non-blocking Lifecycle**: The web server and file monitoring start immediately. Startup hooks run in parallel to avoid blocking the main application loop, which is critical for long-running watch processes in hooks.
