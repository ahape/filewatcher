namespace FileWatcher.Tests;

/// <summary>
/// Unit tests for <see cref="LogService"/>.
/// Each test calls <see cref="LogService.Clear"/> in its setup to guarantee isolation
/// from other tests that write to the same static queue.
/// </summary>
[Collection("Log tests")]
public sealed class LogServiceTests : IDisposable
{
    private readonly StringWriter _out;
    private readonly TextWriter _originalOut;

    public LogServiceTests()
    {
        LogService.Clear();
        _out = new StringWriter();
        _originalOut = Console.Out;
        Console.SetOut(_out);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _out.Dispose();
        LogService.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Console prefix (colour-blind accessibility)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LogLevel.Info, "[INFO]")]
    [InlineData(LogLevel.Success, "[SUCCESS]")]
    [InlineData(LogLevel.Warning, "[WARNING]")]
    [InlineData(LogLevel.Error, "[ERROR]")]
    [InlineData(LogLevel.Copy, "[COPY]")]
    public void Log_NonEmptyMessage_PrefixesOutputWithLevelName(
        LogLevel level,
        string expectedPrefix
    )
    {
        LogService.Log(level, "test message");

        Assert.Contains(expectedPrefix, _out.ToString());
    }

    [Theory]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Error)]
    public void Log_EmptyMessage_WritesBlankLineWithoutPrefix(LogLevel level)
    {
        LogService.Log(level, "");

        // A blank line should contain no level prefix — it is used as a visual spacer.
        var output = _out.ToString();
        Assert.DoesNotContain("[INFO]", output);
        Assert.DoesNotContain("[ERROR]", output);
        Assert.DoesNotContain("[WARNING]", output);
        // The output should still end with a newline (blank line written).
        Assert.EndsWith(Environment.NewLine, output);
    }

    [Fact]
    public void Log_Message_IsIncludedInConsoleOutput()
    {
        LogService.Log(LogLevel.Info, "hello from test");

        Assert.Contains("hello from test", _out.ToString());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // In-memory queue
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Log_SingleEntry_IsReturnedByGetRecentLogs()
    {
        LogService.Log(LogLevel.Info, "queued entry");

        var logs = LogService.GetRecentLogs().ToList();
        Assert.Single(logs);
        Assert.Equal("queued entry", logs[0].Message);
        Assert.Equal(LogLevel.Info, logs[0].Level);
    }

    [Fact]
    public void Log_ExceedsMaxEntries_CapsQueueAtMaxEntries()
    {
        for (var i = 0; i < LogService.MaxLogEntries + 50; i++)
            LogService.Log(LogLevel.Info, $"entry {i}");

        Assert.Equal(LogService.MaxLogEntries, LogService.GetRecentLogs().Count());
    }

    [Fact]
    public void Log_ExceedsMaxEntries_OldestEntriesAreEvicted()
    {
        for (var i = 0; i < LogService.MaxLogEntries + 1; i++)
            LogService.Log(LogLevel.Info, $"entry {i}");

        // The very first entry ("entry 0") should have been evicted.
        var messages = LogService.GetRecentLogs().Select(e => e.Message).ToList();
        Assert.DoesNotContain("entry 0", messages);
        Assert.Contains($"entry {LogService.MaxLogEntries}", messages);
    }

    [Fact]
    public void Clear_RemovesAllRetainedEntries()
    {
        LogService.Log(LogLevel.Info, "before clear");
        LogService.Clear();

        Assert.Empty(LogService.GetRecentLogs());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OnLog event
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Log_WithSubscriber_RaisesOnLogWithCorrectEntry()
    {
        LogEntry? received = null;
        LogService.OnLog += e => received = e;

        LogService.Log(LogLevel.Warning, "event test");

        Assert.NotNull(received);
        Assert.Equal(LogLevel.Warning, received.Level);
        Assert.Equal("event test", received.Message);
    }

    [Fact]
    public void Log_AfterUnsubscribe_DoesNotRaiseEventToRemovedHandler()
    {
        var callCount = 0;
        Action<LogEntry> handler = _ => callCount++;
        LogService.OnLog += handler;
        LogService.OnLog -= handler;

        LogService.Log(LogLevel.Info, "should not reach handler");

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Log_MultipleSubscribers_AllReceiveTheEntry()
    {
        var count1 = 0;
        var count2 = 0;
        LogService.OnLog += _ => count1++;
        LogService.OnLog += _ => count2++;

        LogService.Log(LogLevel.Info, "multi-subscriber");

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Timestamp
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Log_Timestamp_IsApproximatelyNow()
    {
        var before = DateTime.Now;
        LogService.Log(LogLevel.Info, "timing test");
        var after = DateTime.Now;

        var entry = LogService.GetRecentLogs().Single();
        Assert.InRange(entry.Timestamp, before, after);
    }
}
