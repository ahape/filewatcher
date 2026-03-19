using System;
using System.IO;

namespace FileWatcher;

internal sealed class PhysicalFileSystemWatcher : IFileSystemWatcher
{
    private readonly FileSystemWatcher _watcher;

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
}
