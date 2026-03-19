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
    public List<FakeFileSystemWatcher> Watchers { get; } = new();

    public bool FileExists(string path) => Files.ContainsKey(path);

    public Stream OpenRead(string path)
    {
        if (!Files.TryGetValue(path, out var content))
            throw new FileNotFoundException("Not found", path);
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    public void CopyFile(string source, string dest, bool overwrite)
    {
        if (!Files.ContainsKey(source))
            throw new FileNotFoundException();
        Files[dest] = Files[source];
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

internal class FakeFileSystemWatcher : IFileSystemWatcher
{
    public string Directory { get; }
    public NotifyFilters Filters { get; }
    public bool EnableRaisingEvents { get; set; }

    public event FileSystemEventHandler? Changed;
    public event FileSystemEventHandler? Created;
    public event ErrorEventHandler? Error;

    public FakeFileSystemWatcher(string directory, NotifyFilters filters)
    {
        Directory = directory;
        Filters = filters;
    }

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
