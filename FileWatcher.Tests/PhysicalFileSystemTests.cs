using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public class PhysicalFileSystemTests : IDisposable
{
    private readonly string _testDir;
    private readonly PhysicalFileSystem _fs;

    public PhysicalFileSystemTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pfs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _fs = new PhysicalFileSystem();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void FileExists_WhenFileExists_ReturnsTrue()
    {
        var path = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(path, "content");
        Assert.True(_fs.FileExists(path));
    }

    [Fact]
    public void FileExists_WhenFileMissing_ReturnsFalse()
    {
        Assert.False(_fs.FileExists(Path.Combine(_testDir, "missing.txt")));
    }

    [Fact]
    public void OpenRead_ReturnsStream()
    {
        var path = Path.Combine(_testDir, "read.txt");
        File.WriteAllText(path, "abc");
        using var stream = _fs.OpenRead(path);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void CopyFile_CopiesContent()
    {
        var src = Path.Combine(_testDir, "src.txt");
        var dst = Path.Combine(_testDir, "dst.txt");
        File.WriteAllText(src, "data");
        _fs.CopyFile(src, dst, true);
        Assert.Equal("data", File.ReadAllText(dst));
    }

    [Fact]
    public void CreateDirectory_CreatesFolder()
    {
        var subDir = Path.Combine(_testDir, "sub");
        _fs.CreateDirectory(subDir);
        Assert.True(Directory.Exists(subDir));
    }

    [Fact]
    public void GetFileInfo_ReturnsCorrectSize()
    {
        var path = Path.Combine(_testDir, "info.txt");
        File.WriteAllText(path, "12345"); // 5 bytes
        var (LastWriteTimeUtc, Length) = _fs.GetFileInfo(path);
        Assert.Equal(5, Length);
        Assert.NotEqual(default, LastWriteTimeUtc);
    }

    [Fact]
    public void GetFileInfo_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _fs.GetFileInfo(Path.Combine(_testDir, "none.txt"))
        );
    }

    [Fact]
    public async Task PhysicalFileSystemWatcher_TriggersEvents()
    {
        using var watcher = _fs.CreateWatcher(
            _testDir,
            NotifyFilters.FileName | NotifyFilters.LastWrite
        );

        var tcsCreated = new TaskCompletionSource<bool>();
        var tcsChanged = new TaskCompletionSource<bool>();

        watcher.Created += (s, e) =>
        {
            if (e.Name == "watch.txt")
                tcsCreated.TrySetResult(true);
        };
        watcher.Changed += (s, e) =>
        {
            if (e.Name == "watch.txt")
                tcsChanged.TrySetResult(true);
        };
        watcher.Error += (s, e) => { }; // just for coverage
        watcher.EnableRaisingEvents = true;

        var path = Path.Combine(_testDir, "watch.txt");
        File.WriteAllText(path, "test");

        var created = await Task.WhenAny(tcsCreated.Task, Task.Delay(2000)) == tcsCreated.Task;
        Assert.True(created, "Created event did not fire");

        // trigger change
        File.AppendAllText(path, "more");
        var changed = await Task.WhenAny(tcsChanged.Task, Task.Delay(2000)) == tcsChanged.Task;
        Assert.True(changed, "Changed event did not fire");

        watcher.EnableRaisingEvents = false;
        Assert.False(watcher.EnableRaisingEvents);
    }

    [Fact]
    public void PhysicalFileSystemWatcher_RemoveEvents_Works()
    {
        using var watcher = _fs.CreateWatcher(_testDir, NotifyFilters.FileName);
        static void h1(object s, FileSystemEventArgs e) { }
        static void h2(object s, ErrorEventArgs e) { }

        watcher.Changed += h1;
        watcher.Changed -= h1;

        watcher.Created += h1;
        watcher.Created -= h1;

        watcher.Error += h2;
        watcher.Error -= h2;

        // No exceptions means it's working (as we can't easily verify internal delegate list)
        Assert.NotNull(watcher);
    }
}
