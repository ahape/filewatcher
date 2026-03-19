using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FileWatcher.Tests;

public class HookTests : IDisposable
{
    private readonly StringWriter _outWriter;
    private readonly StringWriter _errWriter;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public HookTests()
    {
        _outWriter = new StringWriter();
        _errWriter = new StringWriter();

        _originalOut = Console.Out;
        _originalErr = Console.Error;

        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    [Fact]
    public async Task RunHookAsync_PipesStandardOutputAndErrorToConsole()
    {
        // Arrange
        // Create an empty dummy config file just to instantiate the app
        var dummyConfigPath = Path.GetTempFileName();
        File.WriteAllText(dummyConfigPath, "{}");

        object appInstance;
        try
        {
            var appType = typeof(WatchConfig).Assembly.GetType("FileWatcher.FileWatcherApp")
                          ?? throw new InvalidOperationException("FileWatcherApp not found");
            appInstance = Activator.CreateInstance(appType, new object[] { dummyConfigPath })
                          ?? throw new InvalidOperationException("Could not create FileWatcherApp");
        }
        finally
        {
            File.Delete(dummyConfigPath);
        }

        var hookEvent = new HookEvent
        {
            // Windows specific command to print to stdout and stderr
            Command = "echo Standard Output Message && echo Standard Error Message 1>&2",
            Location = Environment.CurrentDirectory
        };

        var methodInfo = appInstance.GetType().GetMethod("RunHookAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                         ?? throw new InvalidOperationException("RunHookAsync method not found");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var task = (Task)methodInfo.Invoke(appInstance, new object[] { hookEvent, cts.Token })!;
        await task;

        // Give the process a moment to flush buffers to the StringWriter
        await Task.Delay(100);

        var output = _outWriter.ToString();
        var error = _errWriter.ToString();

        // Assert
        Assert.Contains("Standard Output Message", output);
        Assert.Contains("Standard Error Message", error);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        
        _outWriter.Dispose();
        _errWriter.Dispose();
    }
}
