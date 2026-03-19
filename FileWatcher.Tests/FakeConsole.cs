using System;
using System.Collections.Generic;

namespace FileWatcher.Tests;

internal sealed class FakeConsole : IConsole
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    public bool KeyAvailable => _keys.Count > 0;

    public void EnqueueKey(
        char keyChar,
        ConsoleKey key,
        bool shift = false,
        bool alt = false,
        bool control = false
    )
    {
        _keys.Enqueue(new ConsoleKeyInfo(keyChar, key, shift, alt, control));
    }

    public ConsoleKeyInfo ReadKey(bool intercept) => _keys.Dequeue();
}
