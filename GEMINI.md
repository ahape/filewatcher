# Filewatcher Project Instructions

This document provides context and guidelines for interacting with the `filewatcher` repository.

## Stack & Architecture
- **Language**: C# 13, running on .NET 10.
- **Style**: Extremely concise, modern C#. We heavily favor features that reduce boilerplate, such as:
  - Implicit usings
  - Nullable reference types
  - Primary constructors
  - Collection expressions (`[]`)
  - Expression-bodied members
  - Raw string literals (`"""..."""`)
  - File-scoped namespaces

## Core Guidelines
- **Project Structure**: Maintain a strict **one-class-per-file** structure.
- **Formatting**: The project uses `dotnet format` under the hood. You can trigger it via the npm script: `npm run format`. This ensures correct formatting for C# 13 constructs without relying on unmaintained npm packages.
- **Dependencies**: The project depends on `Microsoft.AspNetCore.App` for the built-in web server. Unless completely unavoidable, rely solely on built-in .NET SDK packages.
- **Testing**: Maintain high test coverage using xUnit in the `FileWatcher.Tests` project. To run tests, simply execute `dotnet test`.

## Features & Goals
- The core goal of this project is to provide a simple, agnostic "on[Event] -> someAction" pipeline.
- **Do not introduce opinionated, baked-in logic** (like retry loops, file backups, or auto-generating missing configuration files).
- The system supports triggering file copies (`CopyTo`) and arbitrary subprocess commands (`Command`) with output piped to the main thread's stdout.
- It features a built-in lightweight `Kestrel` web server (`LogWebServer.cs`) that broadcasts logs in real-time via Server-Sent Events (SSE) to a single-file HTML/JS dashboard (stored as a readable, multi-line raw string literal in `LogWebServer.cs`).

## When modifying files
Always ensure changes conform to the existing ultra-compact formatting and the one-class-per-file rule. Run the tests to confirm regressions are avoided, and verify no warnings (including nullable reference warnings) are introduced. Avoid adding opinionated logic outside the realm of simply executing user-configured commands or copies on file events.
