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

## Features & Goals
- The core goal of this project is to provide a simple, agnostic "on[Event] -> someAction" pipeline.
- **Do not introduce opinionated, baked-in conventions** (no opinions outside the realm of executing user-configured commands or copies on file events).
- The system supports triggering file copies (`CopyTo`) and arbitrary subprocess commands (`Command`) with output piped to the main thread's stdout.
- It features a built-in lightweight `Kestrel` web server (`DefaultLogWebServer.cs`) that broadcasts logs in real-time via Server-Sent Events (SSE) to a single-file `dashboard.html`.
- Handles spurious OS-level events by explicitly validating state changes (Size, LastWriteTime) against an internal `_fileStates` dictionary.
- **Non-blocking Lifecycle**: The web server and file monitoring start immediately. Startup hooks run in parallel to avoid blocking the main application loop, which is critical for long-running watch processes in hooks.
