namespace CollectiblePluginFramework;

public sealed partial class CollectiblePluginCatalog
{
    private sealed record PendingEvent(PluginChangeKind Kind, PluginSnapshot? Current, PluginSnapshot? Previous);
}
