using System;

namespace FileWatcher;

internal interface IConsole
{
    bool KeyAvailable { get; }
    ConsoleKeyInfo ReadKey(bool intercept);
}
