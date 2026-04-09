using System.Threading;

namespace FileWatcher;

internal sealed class ActiveTaskInfo(string label)
{
    internal string Label { get; } = label;
    internal int? ProcessId => GetProcessId();

    internal void SetProcessId(int processId) => Interlocked.Exchange(ref _processId, processId);

    private int _processId = -1;

    private int? GetProcessId()
    {
        int processId = Volatile.Read(ref _processId);
        return processId > 0 ? processId : null;
    }
}
