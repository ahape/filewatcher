using System;
using System.IO;

namespace FileWatcher;

/// <summary>
/// Abstracts file system operations so that production code can be exercised
/// in tests without touching the real disk.
/// </summary>
internal interface IFileSystem
{
    /// <summary>Returns <c>true</c> if the file at <paramref name="path"/> exists.</summary>
    bool FileExists(string path);

    /// <summary>Opens the file at <paramref name="path"/> for reading and returns the stream.</summary>
    Stream OpenRead(string path);

    /// <summary>Copies <paramref name="source"/> to <paramref name="dest"/>, optionally overwriting.</summary>
    void CopyFile(string source, string dest, bool overwrite);

    /// <summary>Creates <paramref name="path"/> and any intermediate directories that do not already exist.</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Returns the last-write timestamp (UTC) and byte length of the file at <paramref name="path"/>.
    /// Throws <see cref="FileNotFoundException"/> if the file does not exist.
    /// </summary>
    (DateTime LastWriteTimeUtc, long Length) GetFileInfo(string path);

    /// <summary>
    /// Creates and returns a watcher that monitors <paramref name="directory"/> for the
    /// specified <paramref name="filters"/>. The caller is responsible for disposing it.
    /// </summary>
    IFileSystemWatcher CreateWatcher(string directory, NotifyFilters filters);
}

/// <summary>
/// Abstracts <see cref="FileSystemWatcher"/> so that the event pipeline can be
/// exercised in tests without requiring real OS file events.
/// </summary>
internal interface IFileSystemWatcher : IDisposable
{
    /// <summary>Raised when a watched file is modified.</summary>
    event FileSystemEventHandler? Changed;

    /// <summary>Raised when a new file appears in the watched directory.</summary>
    event FileSystemEventHandler? Created;

    /// <summary>
    /// Raised when a file in the watched directory is renamed. Required to detect saves from
    /// editors that use "atomic save" (write-temp-then-rename) such as Visual Studio, VS Code,
    /// JetBrains IDEs, and vim.
    /// </summary>
    event RenamedEventHandler? Renamed;

    /// <summary>Raised when the internal OS buffer overflows or an error occurs.</summary>
    event ErrorEventHandler? Error;

    /// <summary>Gets or sets whether the watcher is actively raising events.</summary>
    bool EnableRaisingEvents { get; set; }
}
