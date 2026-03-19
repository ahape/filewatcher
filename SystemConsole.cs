using System;

namespace FileWatcher;

internal sealed class SystemConsole : IConsole
{
    public bool KeyAvailable => Console.KeyAvailable;
    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}
