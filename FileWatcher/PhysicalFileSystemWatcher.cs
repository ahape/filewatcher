using System.IO;

namespace FileWatcher;

/// <summary>
/// Thin adapter that wraps a <see cref="FileSystemWatcher"/> and exposes it
/// through the <see cref="IFileSystemWatcher"/> interface.
/// </summary>
internal sealed class PhysicalFileSystemWatcher : IFileSystemWatcher
{
    public PhysicalFileSystemWatcher(string directory, NotifyFilters filters)
    {
        _watcher = new FileSystemWatcher(directory) { NotifyFilter = filters };
    }

    public event FileSystemEventHandler? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }

    public event FileSystemEventHandler? Created
    {
        add => _watcher.Created += value;
        remove => _watcher.Created -= value;
    }

    public event RenamedEventHandler? Renamed
    {
        add => _watcher.Renamed += value;
        remove => _watcher.Renamed -= value;
    }

    public event ErrorEventHandler? Error
    {
        add => _watcher.Error += value;
        remove => _watcher.Error -= value;
    }

    public bool EnableRaisingEvents
    {
        get => _watcher.EnableRaisingEvents;
        set => _watcher.EnableRaisingEvents = value;
    }

    public void Dispose() => _watcher.Dispose();

    private readonly FileSystemWatcher _watcher;
}
