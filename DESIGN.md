# Design Philosophy: FileWatcher

FileWatcher is a minimalist developer orchestrator designed with a single goal: **to manage the "glue" of local development with zero maintenance overhead.**

## Ideology: Minimalism as a Feature

This project intentionally rejects the "Enterprise C#" pattern of heavy abstractions, Dependency Injection containers, and multi-layered architectures. Instead, it embraces the expressive power of modern .NET to provide a robust solution in the smallest possible footprint.

### 1. Zero-Maintenance Codebase
The codebase is restricted to just two core files: `Models.cs` for the contract and `Program.cs` for the orchestration. This ensures that any developer can understand the entire system in under five minutes. If a bug occurs, there are no "layers" to peel back—only straightforward, procedural logic.

### 2. Declarative Orchestration
The system treats your development environment as a state machine:
- **Startup Hooks:** Declare the background services that *must* be running.
- **Update Hooks:** Declare the reactive pipeline (Source → Copy → Command).

By consolidating these into a single unified `Hook` model, we ensure a consistent interface for every action the tool performs.

### 3. SRP at Scale
While we've moved away from class-level SRP (Single Responsibility Principle), we apply it at the **Process Level**. This tool does not try to be a compiler, a linter, or a deployment script. It is strictly the *orchestrator* that knows when and how to trigger those specialized tools.

## Usage Goals

### The "One-Stop-Shop"
Modern development often requires four or five terminal tabs: one for the database, one for the frontend watcher, one for the backend API, and one for background workers. 
**FileWatcher reduces this to one command.** It aggregates the output, manages the process lifetimes, and ensures your environment is always in sync with your source code.

### Robustness Over Complexity
OS-level file events are notoriously noisy and platform-dependent. FileWatcher achieves robustness not through complex library dependencies, but through:
- **State Validation:** Comparing file size and `LastWriteTime` to filter spurious events.
- **Atomic Save Detection:** Monitoring `Renamed` events to support modern IDEs that use temporary swap files.
- **Async Debouncing:** Using `CancellationTokenSource` and `ConcurrentDictionary` to collapse rapid-fire events into a single execution.

## Future-Proofing
By relying solely on the .NET 10 Base Class Library (BCL), FileWatcher avoids the "dependency rot" that plagues many developer tools. As long as the .NET runtime exists, this orchestrator will continue to function without requiring package updates or security patches for third-party libraries.
