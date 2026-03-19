using System;
using System.IO;

namespace FileWatcher;

/// <summary>
/// Production implementation of <see cref="IFileSystem"/> that delegates directly to
/// <see cref="System.IO.File"/>, <see cref="System.IO.Directory"/>, and
/// <see cref="System.IO.FileInfo"/>.
/// </summary>
internal sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public void CopyFile(string source, string dest, bool overwrite) =>
        File.Copy(source, dest, overwrite);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public (DateTime LastWriteTimeUtc, long Length) GetFileInfo(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("File not found", path);
        return (info.LastWriteTimeUtc, info.Length);
    }

    public IFileSystemWatcher CreateWatcher(string directory, NotifyFilters filters) =>
        new PhysicalFileSystemWatcher(directory, filters);
}
