using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileWatcher.Tests;

internal class FakeFileSystem : IFileSystem
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (DateTime LastWriteTimeUtc, long Length)> FileStates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<FakeFileSystemWatcher> Watchers { get; } = [];

    public bool FileExists(string path) => Files.ContainsKey(path);

    public Stream OpenRead(string path)
    {
        if (!Files.TryGetValue(path, out var content))
            throw new FileNotFoundException("Not found", path);
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    public void CopyFile(string source, string dest, bool overwrite)
    {
        if (!Files.TryGetValue(source, out string? value))
            throw new FileNotFoundException();
        Files[dest] = value;
    }

    public void CreateDirectory(string path) => Directories.Add(path);

    public (DateTime LastWriteTimeUtc, long Length) GetFileInfo(string path)
    {
        if (!FileStates.TryGetValue(path, out var state))
            throw new FileNotFoundException();
        return state;
    }

    public IFileSystemWatcher CreateWatcher(string directory, NotifyFilters filters)
    {
        var watcher = new FakeFileSystemWatcher(directory, filters);
        Watchers.Add(watcher);
        return watcher;
    }

    public void AddFile(string path, string content, DateTime lastWrite)
    {
        Files[path] = content;
        FileStates[path] = (lastWrite, Encoding.UTF8.GetByteCount(content));
    }
}

internal class FakeFileSystemWatcher(string directory, NotifyFilters filters) : IFileSystemWatcher
{
    public string Directory { get; } = directory;
    public NotifyFilters Filters { get; } = filters;
    public bool EnableRaisingEvents { get; set; }

    public event FileSystemEventHandler? Changed;
    public event FileSystemEventHandler? Created;
#pragma warning disable CS0067 // Unused
    public event RenamedEventHandler? Renamed;
#pragma warning restore CS0067
    public event ErrorEventHandler? Error;

    public void TriggerChanged(string path) =>
        Changed?.Invoke(
            this,
            new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                Path.GetDirectoryName(path) ?? "",
                Path.GetFileName(path)
            )
        );

    public void TriggerCreated(string path) =>
        Created?.Invoke(
            this,
            new FileSystemEventArgs(
                WatcherChangeTypes.Created,
                Path.GetDirectoryName(path) ?? "",
                Path.GetFileName(path)
            )
        );

    public void TriggerError(Exception ex) => Error?.Invoke(this, new ErrorEventArgs(ex));

    public void Dispose() { }
}
