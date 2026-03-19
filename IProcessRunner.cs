namespace FileWatcher;

internal interface IProcessRunner
{
    Task<int> RunAsync(string command, string workingDirectory, Action<string> onOutput, Action<string> onError, CancellationToken token);
}
