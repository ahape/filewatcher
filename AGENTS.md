# FileWatcher: AI Agent Instructions

This document provides context and guidelines for AI agents interacting with the `FileWatcher` repository.

## Architectural Vision: Minimalism as a Feature
This project has been intentionally consolidated to achieve maximum robustness with minimum code. It rejects "Enterprise C#" patterns (DI containers, heavy abstractions, multi-layered services) in favor of declarative, procedural orchestration using modern C# 13 and the .NET 10 Base Class Library.

### Critical Guidelines for Agents
- **Minimalism First:** Do NOT introduce new files, layers, or abstractions unless absolutely unavoidable. The goal is to keep the entire system's logic in `Program.cs` and `Models.cs`.
- **Zero Dependencies:** Favor the .NET BCL over third-party NuGet packages. This project aims for zero-maintenance and high future-proofing.
- **Declarative Models:** All configuration should reside in the unified `Hook` record in `Models.cs`.
- **Behavioral Testing:** When making changes, verify them using the high-level `OrchestratorTests.cs`. We prioritize end-to-end behavioral parity over implementation-specific unit tests.

## Technical Knowledge Base

### 1. Robust File Watching
OS-level file events are notoriously noisy. FileWatcher uses three layers of validation to ensure stability:
- **State Check:** Compares `FileInfo.Length` and `LastWriteTimeUtc` against a local cache to filter spurious OS "Modified" events where the file hasn't actually changed.
- **Zero-Byte Filter:** Ignores zero-byte files (mid-write truncations).
- **Atomic Save Detection:** Listens for `Renamed` events alongside `Changed` and `Created` to support modern IDEs (VS Code, JetBrains) that write to a temporary swap file and then rename it.

### 2. Async Debouncing
To prevent rapid-fire triggers (e.g., during a batch file copy), the system uses a `ConcurrentDictionary` of `CancellationTokenSource`. 
- Every event cancels the previous pending action for that specific `Source|Command` key.
- A new task is spawned with a `Task.Delay(DebounceMs)`.
- If no further events occur within the window, the command is executed.

### 3. Unified Hook Pipeline
The system uses a single `Hook` model for both `onStartup` and `onUpdate`. Every hook follows a consistent execution pipeline:
1. **Source Check:** If a `Source` is provided (Update Hook), it ensures the file exists.
2. **Copy Phase:** If `CopyTo` is defined, the file is copied to the destination, creating directories if needed.
3. **Execution Phase:** If `Command` is defined, it spawns a subprocess and pipes `stdout`/`stderr` to the console with color-coded prefixes.

## Modern C# Usage
- **Primary Constructors:** Used in `Models.cs` for extreme brevity.
- **Collection Expressions (`[]`):** Preferred for all array/list initializations.
- **Local Functions:** Used in `Program.cs` for logic encapsulation without the overhead of private methods.
- **Raw String Literals:** Used for multi-line strings and JSON samples.

## Validation & Testing
- To run the full system verification: `dotnet test`.
- To test the app manually: `dotnet run --project FileWatcher/FileWatcher.csproj -- [config.json] [--exit-after-startup]`.
- Always normalize paths to absolute versions during config loading to ensure reliable matching across different working directories.
