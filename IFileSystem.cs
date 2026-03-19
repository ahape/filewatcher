using System.IO;

namespace FileWatcher;

internal interface IFileSystem
{
    bool FileExists(string path);
    Stream OpenRead(string path);
    void CopyFile(string source, string dest, bool overwrite);
    void CreateDirectory(string path);
    (DateTime LastWriteTimeUtc, long Length) GetFileInfo(string path);
    IFileSystemWatcher CreateWatcher(string directory, NotifyFilters filters);
}

internal interface IFileSystemWatcher : System.IDisposable
{
    event FileSystemEventHandler? Changed;
    event FileSystemEventHandler? Created;
    event ErrorEventHandler? Error;
    bool EnableRaisingEvents { get; set; }
}
