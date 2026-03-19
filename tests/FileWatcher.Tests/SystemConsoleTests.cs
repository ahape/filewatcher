using System;
using Xunit;

namespace FileWatcher.Tests;

public class SystemConsoleTests
{
    [Fact]
    public void SystemConsole_Instance_Exists()
    {
        var console = new SystemConsole();
        Assert.NotNull(console);
        // We can't easily test KeyAvailable or ReadKey without hanging or input
    }
}
