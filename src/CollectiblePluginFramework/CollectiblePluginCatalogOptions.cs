namespace CollectiblePluginFramework;

public sealed class CollectiblePluginCatalogOptions
{
    public string SearchPattern { get; set; } = "*.dll";
    public bool EnableWatcher { get; set; } = true;
    public TimeSpan WatcherDebounce { get; set; } = TimeSpan.FromMilliseconds(500);
    public Func<string?, bool>? IsRelevantPath { get; set; }
}
