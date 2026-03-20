using System;

namespace FileWatcher;

/// <summary>
/// Production implementation of <see cref="IConsole"/> that delegates to the
/// <see cref="Console"/> static API.
/// </summary>
internal sealed class SystemConsole : IConsole
{
    public bool KeyAvailable => Console.KeyAvailable;

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}
