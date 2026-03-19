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
- **Minimize Source Lines of Code (SLoC)**: Avoid unnecessary classes, abstractions, or structural bloat. Keep DTOs/records grouped in single lines if possible.
- **Dependencies**: The project depends on `Microsoft.AspNetCore.App` for the built-in web server. Unless completely unavoidable, rely solely on built-in .NET SDK packages.
- **Testing**: Maintain high test coverage using xUnit in the `FileWatcher.Tests` project. To run tests, simply execute `dotnet test`.

## Features
- The project implements a file system watcher with debounce, retry logic, and backup capabilities.
- It features a built-in lightweight `Kestrel` web server (`LogWebServer.cs`) that broadcasts logs in real-time via Server-Sent Events (SSE) to a single-file, minified HTML/JS dashboard.

## When modifying files
Always ensure that changes conform to the existing ultra-compact formatting, run the tests to confirm regressions are avoided, and verify no warnings (including nullable reference warnings) are introduced.