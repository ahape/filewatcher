using System;

namespace FileWatcher;

/// <summary>
/// Abstracts console keyboard input so that the interactive command loop can be
/// driven by pre-queued keys in tests without blocking on real stdin.
/// </summary>
internal interface IConsole
{
    /// <summary>Gets a value indicating whether a key press is waiting in the input buffer.</summary>
    bool KeyAvailable { get; }

    /// <summary>
    /// Reads the next key from the input buffer.
    /// When <paramref name="intercept"/> is <c>true</c> the key is not echoed to the console.
    /// </summary>
    ConsoleKeyInfo ReadKey(bool intercept);
}
