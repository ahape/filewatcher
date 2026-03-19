using System;
using System.IO;

namespace FileWatcher;

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
