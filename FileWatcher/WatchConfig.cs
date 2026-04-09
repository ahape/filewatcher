namespace FileWatcher;

public sealed record WatchConfig(
    WatchSettings Settings,
    WatchHooks? Hooks = null
)
{
    public WatchConfig() : this(new WatchSettings()) { }
}
